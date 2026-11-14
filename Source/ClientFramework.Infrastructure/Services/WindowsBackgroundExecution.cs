using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Scoped idle sleep prevention. Display timeout and explicit user sleep remain available.</summary>
public sealed class WindowsBackgroundExecution : IDisposable
{
    private readonly object _gate = new();
    private readonly string _reason;
    private readonly PowerCallback _callback;
    private readonly Action<bool>? _powerChanged;
    private SafeFileHandle? _request;
    private IntPtr _registration;
    private bool _disposed;

    private WindowsBackgroundExecution(string reason, Action<bool>? powerChanged)
    {
        _reason = reason;
        _powerChanged = powerChanged;
        _callback = OnPowerChanged;
        _request = CreateRequest();
        var subscription = new Subscription { Callback = _callback };
        // DEVICE_NOTIFY_CALLBACK: no HWND or UI message pump is required.
        var error = PowerRegisterSuspendResumeNotification(2, ref subscription, out _registration);
        if (error == 0) return;
        _request.Dispose();
        throw new Win32Exception((int)error);
    }

    public static IDisposable Acquire(string reason, Action<bool>? powerChanged = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new WindowsBackgroundExecution(reason, powerChanged);
    }

    private SafeFileHandle CreateRequest()
    {
        var context = new ReasonContext { Flags = 1, SimpleReasonString = _reason };
        var handle = PowerCreateRequest(ref context);
        // SystemRequired prevents idle sleep; ExecutionRequired protects the desktop process
        // during Modern Standby on AC power. Neither request keeps the display on.
        if (!handle.IsInvalid && PowerSetRequest(handle, 1) && PowerSetRequest(handle, 3)) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private uint OnPowerChanged(IntPtr context, uint type, IntPtr setting)
    {
        // PBT_APMSUSPEND / PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND.
        // User sleep terminates power requests, so obtain a fresh lease on resume.
        if (type is not (4 or 18 or 7)) return 0;
        lock (_gate)
        {
            if (_disposed) return 0;
            try
            {
                if (type != 4)
                {
                    var previous = _request;
                    _request = CreateRequest();
                    previous?.Dispose();
                    Serilog.Log.Information("[Automation] background execution renewed after resume");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Automation] background execution renewal failed");
            }
        }
        try { _powerChanged?.Invoke(type == 4); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] power notification failed"); }
        return 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _request?.Dispose();
            _request = null;
        }
        // The owner may hold its own gate while disposing, and a callback may be waiting
        // for that gate. Drain callbacks on a worker, retaining the delegate until done.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            PowerUnregisterSuspendResumeNotification(_registration);
            GC.KeepAlive(_callback);
        });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
        // The detailed-reason union is larger than its simple-string member.
        private IntPtr _reserved1, _reserved2, _reserved3;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PowerCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct Subscription
    {
        [MarshalAs(UnmanagedType.FunctionPtr)] public PowerCallback Callback;
        public IntPtr Context;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, int requestType);

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscription recipient,
        out IntPtr registration);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
}
