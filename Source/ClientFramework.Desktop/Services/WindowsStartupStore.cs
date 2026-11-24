using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Abstraction for per-user login startup registration and legacy shortcut inspection.</summary>
public interface IClientStartupStore
{
    string? ReadRun(string name);
    void WriteRun(string name, string command);
    void DeleteRun(string name);
    IEnumerable<(string Path, string Target)> ReadShortcuts();
    void DeleteShortcut(string path);
    bool ExecutableExists(string path);
    bool MarkerExists(string path);
    void WriteMarker(string path);
}

/// <summary>Default Windows HKCU Run registry and Startup folder store.</summary>
public sealed class WindowsStartupStore : IClientStartupStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly IntPtr HkeyCurrentUser = new(unchecked((int)0x80000001));
    private const uint KeyQueryValue = 0x0001;
    private const uint KeySetValue = 0x0002;
    private const uint KeyCreateSubKey = 0x0004;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const int ErrorFileNotFound = 2;

    public string? ReadRun(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (RegOpenKeyExW(HkeyCurrentUser, RunKeyPath, 0, KeyQueryValue, out var key) != 0)
            return null;
        try
        {
            uint size = 0;
            var status = RegQueryValueExW(key, name, IntPtr.Zero, out var type, null, ref size);
            if (status == ErrorFileNotFound || status != 0 || size == 0 || (type != RegSz && type != RegExpandSz))
                return null;
            var buffer = new byte[size];
            status = RegQueryValueExW(key, name, IntPtr.Zero, out type, buffer, ref size);
            if (status != 0 || (type != RegSz && type != RegExpandSz))
                return null;
            return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public void WriteRun(string name, string command)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var openStatus = RegCreateKeyExW(HkeyCurrentUser, RunKeyPath, 0, null, 0, KeySetValue | KeyCreateSubKey,
            IntPtr.Zero, out var key, out _);
        if (openStatus != 0)
            throw new InvalidOperationException("Cannot open Windows Run startup registry key.", new Win32Exception(openStatus));
        try
        {
            var bytes = Encoding.Unicode.GetBytes(command + "\0");
            var setStatus = RegSetValueExW(key, name, 0, RegSz, bytes, (uint)bytes.Length);
            if (setStatus != 0)
                throw new Win32Exception(setStatus);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public void DeleteRun(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (RegOpenKeyExW(HkeyCurrentUser, RunKeyPath, 0, KeySetValue, out var key) != 0)
            return;
        try
        {
            RegDeleteValueW(key, name);
        }
        finally
        {
            RegCloseKey(key);
        }
    }

    public IEnumerable<(string Path, string Target)> ReadShortcuts()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<(string, string)>();
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (!Directory.Exists(directory)) return Array.Empty<(string, string)>();
        var results = new List<(string, string)>();
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            foreach (var path in Directory.EnumerateFiles(directory, "*.lnk"))
            {
                object? shortcut = null;
                try
                {
                    shortcut = ((dynamic)shell!).CreateShortcut(path);
                    results.Add((path, (string)((dynamic)shortcut).TargetPath));
                }
                finally
                {
                    if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                }
            }
        }
        finally
        {
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
        return results;
    }

    public void DeleteShortcut(string path) => File.Delete(path);
    public bool ExecutableExists(string path) => File.Exists(path);
    public bool MarkerExists(string path) => File.Exists(path);

    public void WriteMarker(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, uint ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(IntPtr hKey, string lpSubKey, uint reserved, string? lpClass,
        uint dwOptions, uint samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out uint lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved,
        out uint lpType, byte[]? lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(IntPtr hKey, string lpValueName, uint reserved,
        uint dwType, byte[] lpData, uint cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValueW(IntPtr hKey, string lpValueName);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
