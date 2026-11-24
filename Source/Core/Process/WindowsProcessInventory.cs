using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Heron.Core.Process;

/// <summary><paramref name="Name"/> is the queried process name, known even when the image path is unreadable.</summary>
public sealed record WindowsProcessInfo(int Id, string Executable, string CommandLine, string Name = "");

/// <summary>Reads process identity in-process, without PowerShell or WMI providers.</summary>
public static class WindowsProcessInventory
{
    private static readonly Lazy<QueryProcessInformation?> CommandLineQuery = new(LoadCommandLineQuery);

    public static IReadOnlyList<WindowsProcessInfo> ReadByNames(IEnumerable<string> names, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("进程占用检查仅支持 Windows。");
        var result = new List<WindowsProcessInfo>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    token.ThrowIfCancellationRequested();
                    if (HasExited(process)) continue;
                    // Limited query access also works for processes without VM-read permission.
                    using var handle = OpenProcess(0x1000 | 0x100000, false, process.Id);
                    var executable = "";
                    var commandLine = "";
                    if (!handle.IsInvalid)
                    {
                        if (WaitForSingleObject(handle, 0) == 0) continue;
                        var path = new StringBuilder(32768);
                        var length = path.Capacity;
                        if (QueryFullProcessImageName(handle, 0, path, ref length)) executable = path.ToString();
                        commandLine = ReadCommandLine(handle);
                        if (WaitForSingleObject(handle, 0) == 0) continue;
                    }
                    else if (HasExited(process)) continue;
                    // Failed identity reads remain visible; callers must not mistake them for no occupant.
                    result.Add(new(process.Id, executable, commandLine, name));
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return result;
    }

    private static bool HasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    private static string ReadCommandLine(SafeProcessHandle handle)
    {
        var query = CommandLineQuery.Value;
        if (query == null) return "";
        const int commandLineInformation = 60; // ProcessCommandLineInformation, Windows 8.1+.
        const int lengthMismatch = unchecked((int)0xC0000004);
        const int bufferTooSmall = unchecked((int)0xC0000023);
        var status = query(handle, commandLineInformation, IntPtr.Zero, 0, out var required);
        var headerSize = Marshal.SizeOf<UnicodeString>();
        // Arguments may change between size and data reads; retry only bounded buffer growth.
        for (var attempt = 0; attempt < 3 && required >= headerSize && required <= 1024 * 1024; attempt++)
        {
            if (status != lengthMismatch && status != bufferTooSmall) return "";
            var size = required;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                status = query(handle, commandLineInformation, buffer, size, out required);
                if (status < 0) continue;
                var value = Marshal.PtrToStructure<UnicodeString>(buffer);
                var offset = value.Buffer.ToInt64() - buffer.ToInt64();
                if (value.Length == 0) return "";
                if (value.Length % 2 != 0 || value.Length > value.MaximumLength
                    || offset < headerSize || offset > size - value.Length) return "";
                return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return "";
    }

    private static QueryProcessInformation? LoadCommandLineQuery()
    {
        if (!NativeLibrary.TryLoad("ntdll.dll", typeof(WindowsProcessInventory).Assembly,
                DllImportSearchPath.System32, out var library)) return null;
        if (NativeLibrary.TryGetExport(library, "NtQueryInformationProcess", out var address))
            return Marshal.GetDelegateForFunctionPointer<QueryProcessInformation>(address);
        NativeLibrary.Free(library);
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryProcessInformation(SafeProcessHandle process, int informationClass,
        IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int id);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
