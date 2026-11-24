using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Inspects and cleans up descendant <c>msedgewebview2.exe</c> processes owned by the host.</summary>
public static class WebView2ProcessLifetime
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static async Task StopOwnedProcessesAsync(CancellationToken cancellationToken = default)
    {
        var rootProcessId = Environment.ProcessId;

        // Give disposed controls a short opportunity to close their browser processes normally.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (FindOwnedWebViewProcessIds(rootProcessId).Count == 0) return;
            await Task.Delay(100, cancellationToken);
        }

        foreach (var processId in FindOwnedWebViewProcessIds(rootProcessId))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException)
            {
                // The process exited between the snapshot and the lookup.
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Serilog.Log.Warning("Timed out waiting for owned WebView2 process {ProcessId} to exit", processId);
            }
            catch (Exception error)
            {
                Serilog.Log.Warning(error, "Failed to stop owned WebView2 process {ProcessId}", processId);
            }
        }
    }

    public static IReadOnlyList<int> FindOwnedWebViewProcessIds(
        int rootProcessId,
        IReadOnlyCollection<ProcessEntry>? snapshot = null)
    {
        snapshot ??= CaptureProcessSnapshot();
        var descendantIds = FindDescendantProcessIds([rootProcessId], snapshot);
        return snapshot
            .Where(entry => descendantIds.Contains(entry.ProcessId)
                            && entry.ExecutableName.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.ProcessId)
            .ToArray();
    }

    public static IReadOnlySet<int> FindDescendantProcessIds(
        IReadOnlyCollection<int> rootProcessIds,
        IReadOnlyCollection<ProcessEntry>? snapshot = null)
    {
        snapshot ??= CaptureProcessSnapshot();
        var descendants = new HashSet<int>(rootProcessIds);
        var remaining = snapshot.ToList();
        var changed = true;

        while (changed)
        {
            changed = false;
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                var entry = remaining[i];
                if (!descendants.Contains(entry.ParentProcessId)) continue;
                descendants.Add(entry.ProcessId);
                remaining.RemoveAt(i);
                changed = true;
            }
        }

        descendants.ExceptWith(rootProcessIds);
        return descendants;
    }

    private static IReadOnlyCollection<ProcessEntry> CaptureProcessSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ProcessEntry>();
        var handle = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (handle == InvalidHandleValue) return Array.Empty<ProcessEntry>();

        try
        {
            var native = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(handle, ref native)) return Array.Empty<ProcessEntry>();
            var entries = new List<ProcessEntry>();
            do
            {
                entries.Add(new((int)native.ProcessId, (int)native.ParentProcessId,
                    native.ExecutableName ?? string.Empty));
                native.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(handle, ref native));
            return entries;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public sealed record ProcessEntry(int ProcessId, int ParentProcessId, string ExecutableName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExecutableName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
