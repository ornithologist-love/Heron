using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Keep the native browser's DPI aligned with its Avalonia window.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeWebViewScaling
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext("SourceContext", "WebView");
    private readonly NativeWebView _view;
    private IControllerScale? _controller;
    private IControllerZoom? _zoom;
    private TopLevel? _topLevel;

    private NativeWebViewScaling(NativeWebView view)
    {
        _view = view;
        view.AdapterCreated += OnAdapterCreated;
        view.AdapterDestroyed += (_, _) => { _controller = null; _zoom = null; };
        view.AttachedToVisualTree += (_, _) => { BindWindow(TopLevel.GetTopLevel(view)); Synchronize(); };
        view.DetachedFromVisualTree += (_, _) => BindWindow(null);
    }

    internal static void Attach(NativeWebView view) => _ = new NativeWebViewScaling(view);

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs args)
    {
        _controller = null;
        _zoom = null;
        if (args.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle) return;
        var pointer = handle.CoreWebView2Controller;
        if (pointer == IntPtr.Zero) return;
        try
        {
            var controller = Marshal.GetObjectForIUnknown(pointer);
            _controller = (IControllerScale)controller;
            _zoom = (IControllerZoom)controller;
            BindWindow(TopLevel.GetTopLevel(_view));
            Synchronize();
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            Log.Warning("WebView DPI binding failed control={Control} HResult={HResult}", _view.Name, error.HResult);
        }
        finally { Marshal.Release(pointer); }
    }

    private void BindWindow(TopLevel? window)
    {
        if (ReferenceEquals(_topLevel, window)) return;
        if (_topLevel != null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = window;
        if (window != null) window.ScalingChanged += OnScalingChanged;
    }

    private void OnScalingChanged(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(Synchronize, DispatcherPriority.Loaded);

    private void Synchronize()
    {
        if (_controller is not { } controller || _topLevel is not { } window) return;
        try
        {
            var scale = window.RenderScaling;
            var previous = controller.RasterizationScale;
            // Avalonia 12.1 disables monitor detection and uses raw-pixel bounds,
            // but never updates RasterizationScale after initialization. Supply
            // the owning window's DPI; browser/user ZoomFactor stays independent.
            controller.ShouldDetectMonitorScaleChanges = 0;
            if (Math.Abs(previous - scale) > 0.001) controller.RasterizationScale = scale;
            Log.Information("WebView DPI synchronized control={Control} windowScale={WindowScale} previousRasterScale={PreviousRasterScale} rasterScale={RasterScale} zoom={Zoom}",
                _view.Name, scale, previous, controller.RasterizationScale, _zoom?.ZoomFactor);
        }
        catch (COMException error)
        {
            Log.Warning("WebView DPI synchronization failed control={Control} HResult={HResult}", _view.Name, error.HResult);
        }
    }

    // Microsoft's managed controller has no public COM-handle factory. These
    // narrow projections use the published WebView2 COM interfaces. Vtable gaps
    // skip unused inherited methods without duplicating their interop types.
    [ComImport, Guid("F9614724-5D2B-41DC-AEF7-73D62B51543B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IControllerScale
    {
        void _VtblGap1_25(); // ICoreWebView2Controller (23) + Controller2 (2).
        double RasterizationScale { get; set; }
        int ShouldDetectMonitorScaleChanges { get; set; }
    }

    [ComImport, Guid("4D00C0D1-9434-4EB6-8078-8697A560334F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IControllerZoom
    {
        void _VtblGap1_4(); // IsVisible and Bounds accessors.
        double ZoomFactor { get; }
    }
}
