using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Opens a directory and activates its actual Explorer window, including an existing one.</summary>
public static class WindowsFolderService
{
    // Called on the Client's STA UI thread; Shell automation stays on that thread across awaits.
    public static async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(path);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        if (!OperatingSystem.IsWindows())
        {
            using var otherPlatform = Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            return;
        }

        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
            shell = Activator.CreateInstance(shellType)!;
            if (ActivateDirectory(shell, directory)) return;

            // explorer.exe can hand the request to an existing process and exit. Its
            // returned PID is not the folder window's PID, so locate the window by URL.
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(directory);
            using var launcher = Process.Start(start);
            for (var attempt = 0; attempt < 25; attempt++)
            {
                await Task.Delay(120, cancellationToken);
                if (ActivateDirectory(shell, directory)) return;
            }
            throw new InvalidOperationException("目录已请求打开，但未能激活对应的文件窗口，请重试。");
        }
        finally { Release(shell); }
    }

    private static bool ActivateDirectory(object shell, string directory)
    {
        object? windows = null;
        try
        {
            windows = ((dynamic)shell).Windows();
            var count = (int)((dynamic)windows).Count;
            for (var index = 0; index < count; index++)
            {
                object? browser = null;
                try
                {
                    browser = ((dynamic)windows).Item(index);
                    if (browser == null) continue;
                    var url = (string)((dynamic)browser).LocationURL;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile
                        || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath)).Equals(
                            Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase)) continue;
                    var window = new IntPtr(Convert.ToInt64(((dynamic)browser).HWND));
                    if (window == IntPtr.Zero) continue;
                    if (IsIconic(window)) ShowWindowAsync(window, 9 /* SW_RESTORE */);
                    if (GetForegroundWindow() == window || SetForegroundWindow(window)) return true;
                }
                catch (COMException) { /* A folder window can close while being enumerated. */ }
                finally { Release(browser); }
            }
            return false;
        }
        finally { Release(windows); }
    }

    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}
