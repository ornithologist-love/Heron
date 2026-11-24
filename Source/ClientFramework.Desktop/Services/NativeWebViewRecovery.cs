using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Recover native browser failures without restarting the host or its sidecar.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeWebViewRecovery
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext("SourceContext", "WebView");
    private static long _failedUntil;
    private static bool _exceptionHandlerAttached;
    private readonly NativeWebView _view;
    private readonly Func<Uri>? _recoveryUri;
    private CoreWebView2? _core;
    private bool _recovering;
    private DateTime _lastRecovery;
    private int _consecutiveFailures;

    private NativeWebViewRecovery(NativeWebView view, Func<Uri>? recoveryUri)
    {
        _view = view;
        _recoveryUri = recoveryUri;
        view.AdapterCreated += OnAdapterCreated;
        view.AdapterDestroyed += (_, _) => Disconnect();
    }

    internal static void Attach(NativeWebView view, Func<Uri>? recoveryUri)
    {
        // WebView2 12.1 queues resize callbacks as async void. After a confirmed
        // browser failure, a queued callback can still target the closed controller.
        // Contain only that exact failure, during the bounded recovery interval.
        if (!_exceptionHandlerAttached)
        {
            _exceptionHandlerAttached = true;
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                if (Environment.TickCount64 > _failedUntil ||
                    args.Exception is not COMException { HResult: unchecked((int)0x8007139F) } error ||
                    error.StackTrace?.Contains("WebView2BaseAdapter.SizeChangedCore", StringComparison.Ordinal) != true)
                    return;
                Log.Warning("Discarded queued WebView resize after confirmed browser failure");
                args.Handled = true;
            };
        }
        _ = new NativeWebViewRecovery(view, recoveryUri);
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs args)
    {
        Disconnect();
        if (args.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle) return;
        var pointer = handle.CoreWebView2;
        try
        {
            _core = CoreWebView2.CreateFromComICoreWebView2(pointer);
            _core.ProcessFailed += OnProcessFailed;
            _core.NavigationCompleted += OnNavigationCompleted;
        }
        catch (COMException error)
        {
            Log.Warning("WebView failure monitor unavailable HResult={HResult}", error.HResult);
            _core = null;
        }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    }

    private void Disconnect()
    {
        var core = _core;
        _core = null;
        if (core == null) return;
        try { core.ProcessFailed -= OnProcessFailed; core.NavigationCompleted -= OnNavigationCompleted; }
        catch (Exception error) when (error is COMException or InvalidOperationException) { /* The browser may already be gone. */ }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess) Log.Warning("WebView navigation error control={Control} status={Status} http={HttpStatus}",
            _view.Name, args.WebErrorStatus, args.HttpStatusCode);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        var kind = args.ProcessFailedKind;
        Log.Error("WebView process failed control={Control} kind={Kind}", _view.Name, kind);
        // GPU/utility and subframe failures are recovered by WebView2 itself.
        if (kind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
        _failedUntil = Environment.TickCount64 + 10_000;
        if (_recovering) return;
        _recovering = true;
        // Never close or recreate a controller inside a WebView2 COM callback.
        Dispatcher.UIThread.Post(Recover, DispatcherPriority.Send);
    }

    private void Recover()
    {
        try
        {
            if (TopLevel.GetTopLevel(_view) == null) { _recovering = false; return; }
            var parent = _view.Parent as Control;
            if (parent is not Panel && parent is not ContentControl && parent is not Decorator) { _recovering = false; return; }
            _consecutiveFailures = DateTime.UtcNow - _lastRecovery < TimeSpan.FromMinutes(1)
                ? _consecutiveFailures + 1 : 1;
            _lastRecovery = DateTime.UtcNow;
            var index = parent is Panel panel ? panel.Children.IndexOf(_view) : 0;
            Disconnect();
            // Keep the same Avalonia control, namescope, handlers and last URI;
            // detaching destroys its failed native adapter, attaching creates it anew.
            try
            {
                switch (parent)
                {
                    case Panel p: p.Children.Remove(_view); break;
                    case ContentControl c: c.Content = null; break;
                    case Decorator d: d.Child = null; break;
                }
            }
            catch (COMException error) when (error.HResult == unchecked((int)0x8007139F))
            {
                Log.Warning("WebView controller already closed during recovery");
            }
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (TopLevel.GetTopLevel(parent) == null || _view.Parent != null) return;
                    // Browser crashes discard session cookies; local AI must repeat
                    // its authenticated bootstrap instead of navigating the cleaned URL.
                    if (_consecutiveFailures <= 2 && _recoveryUri != null) _view.Source = _recoveryUri();
                    Control content = _consecutiveFailures <= 2 ? _view : new TextBlock
                    {
                        Text = "网页进程连续退出，请重启客户端后重试。",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    };
                    Grid.SetRow(content, Grid.GetRow(_view));
                    Grid.SetColumn(content, Grid.GetColumn(_view));
                    switch (parent)
                    {
                        case Panel p: p.Children.Insert(Math.Min(index, p.Children.Count), content); break;
                        case ContentControl c when c.Content == null: c.Content = content; break;
                        case Decorator d when d.Child == null: d.Child = content; break;
                    }
                    Log.Information("WebView recovery attempted control={Control} attempt={Attempt}", _view.Name, _consecutiveFailures);
                }
                finally { _recovering = false; }
            }, DispatcherPriority.ContextIdle);
        }
        catch (Exception error)
        {
            _recovering = false;
            Log.Error("WebView recovery failed type={Type} HResult={HResult}", error.GetType().Name, error.HResult);
        }
    }
}
