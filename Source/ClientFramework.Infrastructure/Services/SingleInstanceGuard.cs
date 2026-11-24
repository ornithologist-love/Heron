using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     Single-instance lock with cross-process foreground activation.
///     Acquires the primary mutex (and optional legacy aliases), tracks a PID file, listens on a named
///     <see cref="EventWaitHandle" /> on Windows, and signals/activates the existing instance when already running.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const int SwRestore = 9;
    private const int SwShow = 5;

    private readonly SingleInstanceOptions _options;
    private readonly List<Mutex> _mutexes;
    private readonly ActivationChannel? _channel;
    private int _disposed;

    private SingleInstanceGuard(SingleInstanceOptions options, List<Mutex> mutexes, ActivationChannel? channel)
    {
        _options = options;
        _mutexes = mutexes;
        _channel = channel;
    }

    public SingleInstanceOptions Options => _options;

    /// <summary>
    ///     Attempts to become the single running instance for <paramref name="options" />.
    ///     When another instance holds the primary or any legacy mutex, signals it to come to the front and returns false.
    /// </summary>
    public static bool TryAcquire(SingleInstanceOptions options, out SingleInstanceGuard? guard)
    {
        ArgumentNullException.ThrowIfNull(options);
        guard = null;
        var held = new List<Mutex>();
        try
        {
            var primary = new Mutex(true, options.MutexName, out var createdPrimary);
            held.Add(primary);
            if (!createdPrimary)
            {
                BringExistingInstanceToFront(options, options.ActivationEventName);
                ReleaseAll(held);
                return false;
            }

            foreach (var alias in options.LegacyAliases)
            {
                var aliasMutex = new Mutex(true, alias.MutexName, out var createdAlias);
                held.Add(aliasMutex);
                if (!createdAlias)
                {
                    BringExistingInstanceToFront(options, alias.ActivationEventName);
                    ReleaseAll(held);
                    return false;
                }
            }

            WritePidFile(options.PidFilePath);
            ActivationChannel? channel = null;
            if (OperatingSystem.IsWindows())
            {
                try { channel = ActivationChannel.Start(options.ActivationEventName); }
                catch (Exception error)
                {
                    Serilog.Log.Warning(error, "Activation channel unavailable; using window fallback");
                }
            }

            guard = new SingleInstanceGuard(options, held, channel);
            return true;
        }
        catch
        {
            ReleaseAll(held);
            throw;
        }
    }

    /// <summary>Registers the callback invoked when a second launch asks this instance to activate its window.</summary>
    public void SetActivationCallback(Action onActivated)
    {
        ArgumentNullException.ThrowIfNull(onActivated);
        _channel?.SetCallback(onActivated);
    }

    /// <summary>Restores, shows, activates and brings <paramref name="window" /> to the foreground.</summary>
    public static bool BringWindowToFront(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();

        if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero)
            return SetForegroundWindow(handle);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel?.Dispose();
        CleanupPidFile(_options.PidFilePath);
        ReleaseAll(_mutexes);
    }

    private static void ReleaseAll(List<Mutex> mutexes)
    {
        foreach (var mutex in mutexes)
        {
            try { mutex.Dispose(); }
            catch { /* Best-effort cleanup */ }
        }
        mutexes.Clear();
    }

    private static void WritePidFile(string? pidFilePath)
    {
        if (string.IsNullOrWhiteSpace(pidFilePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(pidFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(pidFilePath, Environment.ProcessId.ToString());
        }
        catch
        {
            // Optional diagnostic/activation aid
        }
    }

    private static void CleanupPidFile(string? pidFilePath)
    {
        if (string.IsNullOrWhiteSpace(pidFilePath)) return;
        try
        {
            if (!File.Exists(pidFilePath)) return;
            var content = File.ReadAllText(pidFilePath).Trim();
            if (int.TryParse(content, out var pid) && pid == Environment.ProcessId)
                File.Delete(pidFilePath);
        }
        catch
        {
            // Ignore
        }
    }

    private static void BringExistingInstanceToFront(SingleInstanceOptions options, string activationEventName)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                BringExistingInstanceToFrontWindows(options, activationEventName);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                BringExistingInstanceToFrontMacOS(options);
        }
        catch
        {
            // Exiting shortly; ignore activation errors.
        }
    }

    private static void BringExistingInstanceToFrontWindows(SingleInstanceOptions options, string activationEventName)
    {
        using var pidProcess = GetProcessFromPidFile(options.PidFilePath);
        if (TryActivateWindowsProcess(pidProcess, options, activationEventName)) return;

        using var currentProcess = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(currentProcess.ProcessName))
        {
            using (process)
            {
                if (process.Id != currentProcess.Id && TryActivateWindowsProcess(process, options, activationEventName)) return;
            }
        }
    }

    private static bool TryActivateWindowsProcess(Process? process, SingleInstanceOptions options, string activationEventName)
    {
        if (process == null || process.HasExited) return false;
        var candidatePath = TryGetExecutablePath(process);
        if (!options.MatchesExecutablePath(candidatePath)) return false;

        if (RequestActivation(options, candidatePath, process.Id,
                pid => AllowSetForegroundWindow(pid), () => TrySignalExisting(activationEventName)))
            return true;

        if (string.IsNullOrEmpty(options.MainWindowTitle)) return false;
        var hWnd = FindTopLevelWindow(process.Id, options.MainWindowTitle);
        if (hWnd == IntPtr.Zero) return false;

        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        else ShowWindow(hWnd, SwShow);
        return SetForegroundWindow(hWnd);
    }

    internal static bool RequestActivation(SingleInstanceOptions options, string? executablePath, int pid,
        Func<int, bool> grantForeground, Func<bool> signal)
    {
        if (!options.MatchesExecutablePath(executablePath)) return false;
        if (!grantForeground(pid)) Debug.WriteLine($"Foreground permission grant denied for PID {pid}");
        return signal();
    }

    internal static bool TrySignalExisting(string activationEventName)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!EventWaitHandle.TryOpenExisting(activationEventName, out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr FindTopLevelWindow(int processId, string expectedTitle)
    {
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != processId) return true;

            var title = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            if (!IsMainWindowCandidate(title.ToString(), expectedTitle, GetWindow(hWnd, 4 /* GW_OWNER */) != IntPtr.Zero))
                return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    internal static bool IsMainWindowCandidate(string title, string? expectedTitle, bool hasOwner) =>
        !hasOwner && !string.IsNullOrEmpty(expectedTitle) && string.Equals(title, expectedTitle, StringComparison.Ordinal);

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static void BringExistingInstanceToFrontMacOS(SingleInstanceOptions options)
    {
        var pid = ReadPidFromFile(options.PidFilePath);
        if (pid <= 0) return;
        using var process = GetProcess(pid);
        if (process == null || !options.MatchesExecutablePath(TryGetExecutablePath(process))) return;
        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            Arguments = $"-e \"tell application \\\"System Events\\\" to set frontmost of first process whose unix id is {pid} to true\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        Process.Start(psi)?.Dispose();
    }

    private static Process? GetProcessFromPidFile(string? pidFilePath)
    {
        var pid = ReadPidFromFile(pidFilePath);
        return pid > 0 ? GetProcess(pid) : null;
    }

    private static Process? GetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }
    }

    private static int ReadPidFromFile(string? pidFilePath)
    {
        if (string.IsNullOrWhiteSpace(pidFilePath)) return -1;
        try
        {
            if (File.Exists(pidFilePath))
            {
                var content = File.ReadAllText(pidFilePath).Trim();
                if (int.TryParse(content, out var pid)) return pid;
            }
        }
        catch
        {
            // Ignore
        }
        return -1;
    }

    internal sealed class ActivationChannel : IDisposable
    {
        private readonly EventWaitHandle _handle;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ManualResetEventSlim _callbackReady = new(false);
        private readonly Thread _listener;
        private Action? _onActivated;
        private int _disposed;

        private ActivationChannel(string name)
        {
            _handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            _listener = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "Heron.SingleInstance.Activation",
            };
        }

        public static ActivationChannel Start(string eventName)
        {
            var channel = new ActivationChannel(eventName);
            channel._listener.Start();
            return channel;
        }

        public void SetCallback(Action onActivated)
        {
            Volatile.Write(ref _onActivated, onActivated);
            _callbackReady.Set();
        }

        private void ListenLoop()
        {
            try { _callbackReady.Wait(_cancellation.Token); }
            catch (OperationCanceledException) { return; }
            var waits = new[] { _handle, _cancellation.Token.WaitHandle };
            while (true)
            {
                try
                {
                    var index = WaitHandle.WaitAny(waits);
                    if (index != 0 || _cancellation.IsCancellationRequested) return;
                    if (Volatile.Read(ref _onActivated) is { } callback) callback();
                }
                catch (ObjectDisposedException) { return; }
                catch { /* Keep listening */ }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                _cancellation.Cancel();
                if (!_listener.Join(TimeSpan.FromSeconds(2))) { /* Do not block shutdown */ }
            }
            catch { /* Ignore */ }
            _callbackReady.Dispose();
            _handle.Dispose();
            _cancellation.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
}
