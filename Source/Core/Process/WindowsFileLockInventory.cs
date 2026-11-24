using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Heron.Core.Process;

public sealed record WindowsFileLockOwner(int Id, string ApplicationName, string ServiceName, uint SessionId);

/// <summary>Queries the applications and services using one file; never shuts them down.</summary>
public static class WindowsFileLockInventory
{
    public static IReadOnlyList<WindowsFileLockOwner> Read(string filePath, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("文件占用检查仅支持 Windows。");
        token.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(filePath);
        // Restart Manager accepts files, not directories (RmGetList returns access denied for directories).
        if (Directory.Exists(path)) throw new ArgumentException("Restart Manager 只能查询具体文件。", nameof(filePath));
        Check(RmStartSession(out var session, 0, new StringBuilder(33)), "启动占用查询");
        try
        {
            token.ThrowIfCancellationRequested();
            Check(RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero), "登记待查询文件");
            uint count = 0;
            var result = RmGetList(session, out var needed, ref count, null, out _);
            if (result != 234) Check(result, "查询占用进程");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (needed == 0) return [];
                var processes = new ProcessInfo[checked((int)needed)];
                count = (uint)processes.Length;
                result = RmGetList(session, out needed, ref count, processes, out _);
                if (result == 234) continue;
                Check(result, "读取占用进程");
                token.ThrowIfCancellationRequested();
                return processes.Take(checked((int)count)).Select(process => new WindowsFileLockOwner(
                    checked((int)process.Process.Id), process.ApplicationName, process.ServiceName, process.SessionId)).ToArray();
            }
            throw new IOException("占用进程列表在查询期间持续变化，请重试。");
        }
        finally { RmEndSession(session); }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
            throw new Win32Exception((int)result, $"Restart Manager {operation}失败（Windows 错误 {result}）："
                + new Win32Exception((int)result).Message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public uint Id;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartedAt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint ApplicationType;
        public uint Status;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmRegisterResources(uint session, uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
        uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[]? processes, out uint rebootReasons);
    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint RmEndSession(uint session);
}
