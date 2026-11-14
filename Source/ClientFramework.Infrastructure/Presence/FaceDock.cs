using Avalonia;
using Avalonia.Controls;
using System.Runtime.InteropServices;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     两个面的并列几何。AI 面左缘贴工作台右缘、高度与工作台外框一致、顶部对齐；
///     工作台移动或改尺寸时 AI 面跟随。拖动任一面则整体平移：这是"一个实体"认知的
///     直接体现，用户拖的从来不是某一个窗口。
///     收起/展开动画期间停靠冻结（<see cref="Freeze" />），此时工作台是固定锚点，
///     AI 面不能去追一个正在移动的边缘。
/// </summary>
internal sealed class FaceDock
{
    private Window? _workBench;
    private Window? _aiChat;
    private PixelPoint _lastAiPosition;
    private bool _frozen;
    private bool _suspended;
    private bool _syncing;

    /// <summary>把两个面接上并列跟随。重复调用只更新引用。</summary>
    public void Attach(Window workBench, Window aiChat)
    {
        if (ReferenceEquals(_aiChat, aiChat) && ReferenceEquals(_workBench, workBench)) return;
        Detach();
        _workBench = workBench;
        _aiChat = aiChat;
        _lastAiPosition = aiChat.Position;
        workBench.PositionChanged += OnWorkBenchMoved;
        workBench.SizeChanged += OnWorkBenchResized;
        aiChat.PositionChanged += OnAiChatMoved;
        Win32Properties.AddWndProcHookCallback(workBench, OnWindowResized);
    }

    /// <summary>摘掉跟随。隐去 AI 面时调用：它停在哪里就留在哪里。</summary>
    public void Detach()
    {
        if (_workBench != null)
        {
            Win32Properties.RemoveWndProcHookCallback(_workBench, OnWindowResized);
            _workBench.PositionChanged -= OnWorkBenchMoved;
            _workBench.SizeChanged -= OnWorkBenchResized;
        }
        if (_aiChat != null) _aiChat.PositionChanged -= OnAiChatMoved;
        _workBench = null;
        _aiChat = null;
    }

    /// <summary>冻结跟随：工作台正在做收起/展开动画。</summary>
    public void Freeze() => _frozen = true;

    /// <summary>
    ///     暂停跟随：工作台整窗已被移到屏幕之外（收起稳定态），它的实时坐标不再是有效
    ///     参照。与 <see cref="Freeze" /> 的区别是它不随动画结束自动解除，必须由展开路径
    ///     显式恢复，否则 AI 面会被屏外的工作台一起拽出屏幕。
    /// </summary>
    public void Suspend() => _suspended = true;

    /// <summary>恢复跟随，并把 AI 面重新贴到工作台的当前边缘。</summary>
    public void Resume()
    {
        _frozen = false;
        _suspended = false;
        Align();
    }

    /// <summary>把 AI 面贴到工作台右缘。</summary>
    public void Align()
    {
        if (_frozen || _suspended || _syncing) return;
        var workBench = _workBench;
        var aiChat = _aiChat;
        if (workBench == null || aiChat == null) return;
        if (!workBench.IsVisible || workBench.WindowState == WindowState.Minimized) return;
        // 隐藏的工作台报告的边界是过期的，不能让它拖动 AI 面。
        if (!aiChat.IsVisible) return;

        _syncing = true;
        try
        {
            PlaceGeometry(workBench, aiChat);
            _lastAiPosition = aiChat.Position;
        }
        finally { _syncing = false; }
    }

    /// <summary>
    ///     计算并写入并列几何：AI 面左缘贴工作台右缘、顶部对齐、高度与工作台外框一致。
    ///     与 <see cref="Align" /> 不同，它不要求两个面都已可见——首次呈现前先用它摆好
    ///     位置，新窗口就不会在默认位置闪一下。
    /// </summary>
    public static void PlaceGeometry(Window workBench, Window aiChat)
    {
        // Bounds is the content area in Avalonia 12. Position is the native outer origin,
        // which includes invisible resize borders, so they cannot describe one rectangle.
        if (OperatingSystem.IsWindows() &&
            TryGetVisibleFrame(workBench, out _, out var workFrame) &&
            TryGetVisibleFrame(aiChat, out var aiOuter, out var aiFrame) &&
            GetClientRect(aiChat.TryGetPlatformHandle()!.Handle, out var aiClient))
        {
            aiChat.Position = new PixelPoint(
                workFrame.Right - (aiFrame.Left - aiOuter.Left),
                workFrame.Top - (aiFrame.Top - aiOuter.Top));
            // Height sizes the client area. Account for the side window's visible border
            // and use its DPI rather than the owner's when the windows span monitors.
            if (TryGetVisibleFrame(aiChat, out var placedOuter, out var placedFrame) &&
                GetClientRect(aiChat.TryGetPlatformHandle()!.Handle, out var placedClient))
            {
                aiOuter = placedOuter;
                aiFrame = placedFrame;
                aiClient = placedClient;
            }
            var clientHeight = workFrame.Bottom - workFrame.Top -
                (aiFrame.Bottom - aiFrame.Top - (aiClient.Bottom - aiClient.Top));
            aiChat.Height = Math.Max(clientHeight / aiChat.RenderScaling, aiChat.MinHeight);
            aiChat.Position = new PixelPoint(
                workFrame.Right - (aiFrame.Left - aiOuter.Left),
                workFrame.Top - (aiFrame.Top - aiOuter.Top));
            return;
        }

        var scale = workBench.RenderScaling;
        var frameSize = workBench.FrameSize ?? workBench.ClientSize;
        var left = workBench.Position.X + (int)Math.Round(frameSize.Width * scale);
        var top = workBench.Position.Y;
        var aiFrameSize = aiChat.FrameSize ?? aiChat.ClientSize;
        var height = Math.Max(frameSize.Height * scale / aiChat.RenderScaling -
            (aiFrameSize.Height - aiChat.ClientSize.Height), aiChat.MinHeight);

        aiChat.Height = height;
        aiChat.Position = new PixelPoint(left, top);
    }

    private static bool TryGetVisibleFrame(Window window, out NativeRect outer, out NativeRect visible)
    {
        outer = visible = default;
        if (window.TryGetPlatformHandle()?.Handle is not { } handle ||
            !GetWindowRect(handle, out outer)) return false;
        // DWM excludes the invisible resize border that GetWindowRect includes.
        if (DwmGetWindowAttribute(handle, 9, out visible, Marshal.SizeOf<NativeRect>()) != 0)
            visible = outer;
        return visible.Right > visible.Left && visible.Bottom > visible.Top;
    }

    private void OnWorkBenchMoved(object? sender, PixelPointEventArgs e) => Align();

    private void OnWorkBenchResized(object? sender, SizeChangedEventArgs e) => Align();

    private nint OnWindowResized(nint handle, uint message, nint wParam, nint lParam, ref bool handled)
    {
        // WM_SIZE / SIZE_MAXIMIZED：在原始尺寸进入布局前只修正宽度。
        // 手动 Resize 同步产生的新通知继续走 Avalonia 的正常流程，原通知到此截断。
        if (message != 0x0005 || wParam != 2) return 0;
        var workBench = _workBench;
        var aiChat = _aiChat;
        if (!_frozen && !_suspended && workBench != null && aiChat is { IsVisible: true } &&
            workBench.Screens.ScreenFromWindow(workBench) is { } screen &&
            GetWindowRect(handle, out var rect))
        {
            var aiWidth = (int)Math.Round(aiChat.Bounds.Width * aiChat.RenderScaling);
            // 只改宽度：左侧沿用系统最大化的位置，右侧留出 AI 面，二者合计到工作区右缘。
            var width = Math.Max(1, screen.WorkingArea.Right - rect.Left - aiWidth);
            // 尺寸相等即放行，避免递归；不能用整段重入锁，因为 Avalonia 在首次
            // 最大化通知中还会再次调整工作区尺寸，那次通知也必须按相同规则收窄。
            if (width != rect.Right - rect.Left)
            {
                handled = SetWindowPos(handle, 0, 0, 0, width, rect.Bottom - rect.Top, 0x0016);
            }
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out NativeRect rect, int size);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    /// <summary>
    ///     用户挪动了 AI 面：整个实体一起走。工作台按相同偏移平移，随后的对齐会把
    ///     AI 面贴回工作台新边缘，两者的相对位置因此保持不变。
    /// </summary>
    private void OnAiChatMoved(object? sender, PixelPointEventArgs e)
    {
        if (_frozen || _suspended || _syncing) return;
        var workBench = _workBench;
        var aiChat = _aiChat;
        if (workBench == null || aiChat == null) return;
        if (!workBench.IsVisible || workBench.WindowState != WindowState.Normal) return;

        var current = aiChat.Position;
        var delta = new PixelPoint(current.X - _lastAiPosition.X, current.Y - _lastAiPosition.Y);
        _lastAiPosition = current;
        if (delta.X == 0 && delta.Y == 0) return;
        workBench.Position = new PixelPoint(workBench.Position.X + delta.X, workBench.Position.Y + delta.Y);
    }
}
