using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>单个本地磁盘的容量快照（GB）。</summary>
public sealed record DiskSpaceSnapshot(string Name, double TotalGb, double FreeGb);

/// <summary>
///     主显卡的显存快照。FreeGb 仅在 nvidia-smi 可用时可读（NVIDIA）；
///     DXGI 回退路径（AMD/Intel 独立显卡）只有总量，此时 FreeGb 为 null。
/// </summary>
public sealed record GpuMemorySnapshot(string Model, double? TotalGb, double? FreeGb)
{
    public static GpuMemorySnapshot Unknown { get; } = new("", null, null);
}

/// <summary>一次整机资源采样结果（CPU / 内存 / 显存 / 本地磁盘）。</summary>
public sealed record SystemResourceSnapshot(
    double? CpuUsagePercent,
    double? MemoryTotalGb,
    double? MemoryAvailableGb,
    GpuMemorySnapshot Gpu,
    IReadOnlyList<DiskSpaceSnapshot> Disks);

/// <summary>
///     CPU、内存、显存（VRAM）与本地磁盘的统一采样器，供 Footer 状态栏等场景复用。
///     CPU/内存走 P/Invoke（不依赖 WMI/性能计数器）；显存优先 nvidia-smi，
///     无 NVIDIA 工具时回退 DXGI 独立显卡枚举（覆盖 AMD/Intel），与 Editor 插件的预检实现同源；
///     磁盘按固定与可移动盘逐一采集，汇总口径由消费方决定。
///     采样放在后台线程执行（含最长 5 秒的 nvidia-smi 等待），调用方负责线程调度。
/// </summary>
public sealed class SystemResourceMonitor
{
    internal const int CpuSampleDelayMs = 250;
    private const int NvidiaSmiTimeoutMs = 5000;
    private const double Gigabyte = 1073741824d;

    /// <summary>采集一次快照；不抛异常，单项不可读以 null / unknown 表达。</summary>
    public async Task<SystemResourceSnapshot> SampleAsync(CancellationToken token)
    {
        var hardwareTask = Task.Run(ReadHardware, token);
        var cpu = await SampleCpuAsync(token).ConfigureAwait(false);
        var (memoryTotal, memoryAvailable) = ReadMemoryGb();
        var (gpu, disks) = await hardwareTask.ConfigureAwait(false);
        return new(cpu, memoryTotal, memoryAvailable, gpu, disks);
    }

    private static (GpuMemorySnapshot Gpu, List<DiskSpaceSnapshot> Disks) ReadHardware()
        => (ReadGpuMemory(), ReadDisks());

    private static async Task<double?> SampleCpuAsync(CancellationToken token)
    {
        var before = GetSystemTimes(out var idleBefore, out var kernelBefore, out var userBefore);
        await Task.Delay(CpuSampleDelayMs, token).ConfigureAwait(false);
        var after = GetSystemTimes(out var idleAfter, out var kernelAfter, out var userAfter);
        return before && after
            ? CalculateCpuUsage(idleBefore, kernelBefore, userBefore, idleAfter, kernelAfter, userAfter)
            : null;
    }

    private static (double? TotalGb, double? AvailableGb) ReadMemoryGb()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.TotalPhysical / Gigabyte, status.AvailablePhysical / Gigabyte)
            : (null, null);
    }

    private static List<DiskSpaceSnapshot> ReadDisks()
    {
        var disks = new List<DiskSpaceSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            try
            {
                if (!drive.IsReady) continue;
                disks.Add(new DiskSpaceSnapshot(
                    drive.Name.TrimEnd('\\'),
                    drive.TotalSize / Gigabyte,
                    drive.AvailableFreeSpace / Gigabyte));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 单个盘不可读时跳过；其余盘与总采样不受影响。
            }
        }
        return disks;
    }

    private static GpuMemorySnapshot ReadGpuMemory()
    {
        var nvidia = TryReadNvidiaMemory();
        if (nvidia != null) return nvidia;
        try
        {
            if (ReadLargestHardwareAdapter() is { } adapter)
                return new GpuMemorySnapshot(adapter.Name, adapter.TotalGb, null);
        }
        catch
        {
            // DXGI 不可用（远程会话、驱动异常等）：保持未知，绝不把未知当成 0。
        }
        return GpuMemorySnapshot.Unknown;
    }

    /// <summary>nvidia-smi 优先（NVIDIA 机器可读到实时余量）；缺失或失败返回 null 触发 DXGI 回退。</summary>
    private static GpuMemorySnapshot? TryReadNvidiaMemory()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.free,memory.total --format=csv,noheader,nounits",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null) return null;
            // 持续读取两条流，避免陈旧的 NVIDIA 安装写出大段诊断阻塞 WaitForExit。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(NvidiaSmiTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* 尽力清理；本次采样按未知处理 */ }
                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            return ParseNvidiaMemory(process.ExitCode, stdout);
        }
        catch (Exception)
        {
            // nvidia-smi 不存在或不可执行（AMD/Intel 机器）：交给 DXGI 回退。
            return null;
        }
    }

    /// <summary>
    ///     只接受命令成功且返回合法 CSV 的 nvidia-smi 输出。曾更换为 AMD 卡的机器可能残留
    ///     nvidia-smi，其失败信息必须触发回退，绝不能混入型号或显存。
    ///     多卡输出只取第一行（主卡），与 Editor 预检保持一致。
    /// </summary>
    internal static GpuMemorySnapshot? ParseNvidiaMemory(int exitCode, string? stdout)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
        var line = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line)) return null;
        var cells = line.Split(',', StringSplitOptions.TrimEntries);
        if (cells.Length < 3 || string.IsNullOrWhiteSpace(cells[0])) return null;
        var model = cells[0];
        if (System.Text.RegularExpressions.Regex.IsMatch(model,
                @"\b(?:failed|error|couldn't\s+communicate|not\s+found)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            return null;

        var freeParsed = double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var freeMb);
        var totalParsed = double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var totalMb);
        var valid = freeParsed && totalParsed
            && double.IsFinite(freeMb) && double.IsFinite(totalMb) && freeMb >= 0 && totalMb > 0 && freeMb <= totalMb;
        return valid ? new GpuMemorySnapshot(model, totalMb / 1024, freeMb / 1024) : null;
    }

    /// <summary>
    ///     枚举 DXGI 硬件适配器，取独立显存最大的独显（排除软件适配器）。
    ///     无 nvidia-smi 的 AMD/Intel 独显机器走此路径读取显存总量；实现与 Editor 预检同源。
    /// </summary>
    internal static (string Name, double TotalGb)? ReadLargestHardwareAdapter()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapters>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            (string Name, ulong Bytes)? best = null;
            for (uint index = 0; ; index++)
            {
                var hr = enumerate(factory, index, out var adapter);
                if (hr == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var describe = Marshal.GetDelegateForFunctionPointer<GetDesc>(
                        Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    Marshal.ThrowExceptionForHR(describe(adapter, out var desc));
                    if ((desc.Flags & 2) == 0 && (best == null || desc.DedicatedVideoMemory.ToUInt64() > best.Value.Bytes))
                        best = (desc.Description, desc.DedicatedVideoMemory.ToUInt64());
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
            return best is { } value && value.Bytes > 0 ? (value.Name, value.Bytes / Gigabyte) : null;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    internal static double? CalculateCpuUsage(ulong idleBefore, ulong kernelBefore, ulong userBefore, ulong idleAfter, ulong kernelAfter, ulong userAfter)
    {
        if (idleAfter < idleBefore || kernelAfter < kernelBefore || userAfter < userBefore) return null;
        var total = (double)(kernelAfter - kernelBefore) + (userAfter - userBefore);
        var idle = idleAfter - idleBefore;
        return total > 0 && idle <= total ? Math.Clamp((total - idle) / total * 100, 0, 100) : null;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc(IntPtr self, out AdapterDesc desc);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
}
