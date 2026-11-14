using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     工作台的边栏收起：先在屏幕内把工作台原位裁剪成右缘的一道细边并淡至全透明，
///     动画结束后再把它整体移出屏幕。
///     为什么收起动画必须"就地"：用户要的是抽屉，不是飞走。动画期间窗口位置不动，
///     可见区域从左侧抽向钉在 AI 面左缘的右缘，看起来就是往边栏里收进去；若整窗平移
///     出去，屏幕上看到的是窗口飘走。
///     为什么动画结束后还要移出屏幕：裁剪与淡出都带不走 DWM 的框架。Win11 的圆角、
///     系统边框与投影由 DWM 沿窗口的可见区域单独绘制，SetWindowRgn 只改窗口自己能画的
///     像素，Avalonia 的 Opacity 也只作用于窗口自绘内容，于是收到最右缘那 1 像素后，
///     DWM 仍会在它周围描出一圈灰黑边框与投影。只有让窗口整体离开屏幕，这套框架才
///     无迹可寻；这一步发生在窗口已全透明之后，用户看不到位移。
///     隐藏会连同实体在任务栏与 Alt+Tab 里的身份一起消失——屏幕上明明还显示着 AI 面，
///     用户却无法用 Alt+Tab 找回这个应用。所以收起只能改位置与视觉，不能改可见性。
/// </summary>
internal sealed class WorkBenchFold
{
    /// <summary>收起动画终点保留在屏幕上的可见宽度（物理像素）。窗口在动画结束后会被
    ///     移出屏幕，这个宽度只出现在动画期间，用来给"抽走"一个可见的收束点。</summary>
    private const int CollapsedWidthPx = 1;

    /// <summary>移出屏幕时相对整个虚拟桌面额外让出的余量（物理像素）。DWM 的投影绘制
    ///     在窗口区域之外，余量必须大于这部分外扩，否则屏幕边缘仍会露出一条阴影。</summary>
    private const int OffScreenMarginPx = 64;

    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan AnimationFrame = TimeSpan.FromMilliseconds(16);

    private DispatcherTimer? _timer;
    private Window? _window;
    private nint _hwnd;
    private int _fullWidthPx;
    private int _fromOffset;
    private int _toOffset;
    private double _fromOpacity;
    private double _toOpacity;
    private PixelPoint? _originPosition;
    private DateTime _startedAt;
    private bool _clockStarted;
    private bool _targetCollapsed;

    /// <summary>工作台当前是否收在边栏里。</summary>
    public bool IsCollapsed { get; private set; }

    /// <summary>
    ///     收起前工作台在屏幕上的位置。收起稳定后窗口停在屏幕之外，它的实时坐标不再是
    ///     有效参照，任何"就地展开"都必须以它为基准，否则会从屏外展开到屏外。
    /// </summary>
    public PixelPoint? RestorePosition => _originPosition;

    /// <summary>收起或展开动画是否在进行中。</summary>
    public bool IsAnimating => _timer?.IsEnabled == true;

    /// <summary>动画稳定后（在 UI 线程）触发一次。</summary>
    public event Action? Settled;

    /// <summary>把工作台收进边栏。</summary>
    public void Fold(Window window)
    {
        if (IsAnimating || IsCollapsed) return;
        if (!window.IsVisible || window.WindowState != WindowState.Normal) return;

        _window = window;
        _hwnd = ResolveHandle(window);
        _fullWidthPx = MeasureWidthPx(window);
        _originPosition = window.Position;

        if (_hwnd == 0 || _fullWidthPx <= CollapsedWidthPx)
        {
            // 取不到窗口句柄（非 Windows 平台）时无法裁剪；此时不做任何视觉改动，
            // 保持工作台完整可见，实体身份与可达性都不受影响。
            IsCollapsed = true;
            Settled?.Invoke();
            return;
        }

        // 防御：若窗口带着遗留的透明残留进入收起，先恢复全不透明再开始渐隐。
        if (window.Opacity < 0.01) window.Opacity = 1;
        Start(0, _fullWidthPx - CollapsedWidthPx, window.Opacity, 0, collapsed: true);
    }

    /// <summary>
    ///     把工作台从边栏拉出来，锚定在 <paramref name="aiFaceLeft" />：AI 面左缘的屏幕位置。
    ///     工作台右缘贴住该锚点、顶部与 AI 面对齐，因此即使收起期间 AI 面被移动过，
    ///     展开后也会接回它旁边。
    /// </summary>
    public void Unfold(PixelPoint aiFaceLeft)
    {
        var window = _window;
        if (window == null || IsAnimating) return;

        if (!IsCollapsed)
        {
            // 已展开：清掉可能残留的裁剪与透明，并确保它在屏幕上。
            window.Opacity = 1;
            ClearRegion();
            if (!window.IsVisible) window.Show();
            Settled?.Invoke();
            return;
        }

        _fullWidthPx = MeasureWidthPx(window);
        var anchored = new PixelPoint(aiFaceLeft.X - _fullWidthPx, aiFaceLeft.Y);
        // 首帧就是收起态：先把窗口从屏外瞬移回锚点，再摆成"最右缘 1 像素 + 全透明"。
        // 此刻窗口仍带着收起态的裁剪与透明，所以这次瞬移看不见，动画也不会从整窗状态闪出。
        window.Position = anchored;
        ApplyFrame(window, _fullWidthPx - CollapsedWidthPx, 0);
        if (!window.IsVisible) window.Show();
        window.Position = anchored;
        Start(_fullWidthPx - CollapsedWidthPx, 0, 0, 1, collapsed: false);
    }

    /// <summary>
    ///     释放收起几何：清除裁剪与透明，并把窗口挪回收起前的位置。刻意不改变可见性——
    ///     调用它的场景（实体拆解）正要让窗口下屏，这里再显示一次会造成退出时的窗口闪现。
    /// </summary>
    public void Release()
    {
        _timer?.Stop();
        RestoreGeometry();
        IsCollapsed = false;
    }

    /// <summary>停止在途动画；实体拆解时调用。会把窗口从屏外挪回并清掉裁剪与透明。</summary>
    public void Stop()
    {
        _timer?.Stop();
        RestoreGeometry();
        _window = null;
        _hwnd = 0;
        _originPosition = null;
        IsCollapsed = false;
    }

    /// <summary>清除裁剪与透明，并把窗口放回收起前的位置。</summary>
    private void RestoreGeometry()
    {
        var window = _window;
        if (window == null) return;
        window.Opacity = 1;
        ClearRegion();
        if (_originPosition is { } origin && window.Position != origin)
            window.Position = origin;
    }

    private static int MeasureWidthPx(Window window) =>
        Math.Max(1, (int)Math.Round(MeasureLogical(window.Width, window.Bounds.Width) * window.RenderScaling));

    private static int MeasureHeightPx(Window window) =>
        Math.Max(1, (int)Math.Round(MeasureLogical(window.Height, window.Bounds.Height) * window.RenderScaling));

    /// <summary>取窗口的逻辑尺寸：优先显式尺寸，未设置时退回实际边界。</summary>
    private static double MeasureLogical(double requested, double actual) =>
        double.IsFinite(requested) && requested > 0 ? requested : actual;

    private static nint ResolveHandle(Window window) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? window.TryGetPlatformHandle()?.Handle ?? 0
            : 0;

    /// <summary>
    ///     计算屏外位置：挪到整个虚拟桌面之外，任何一块显示器上都看不到它。
    ///     取不到屏幕信息时返回 null，此时窗口留在原位，无法消除框架残留。
    /// </summary>
    private PixelPoint? ResolveOffscreenPosition()
    {
        var screens = _window?.Screens;
        if (screens == null) return null;
        var all = screens.All;
        if (all.Count == 0) return null;

        // 不用 Linq，避免依赖隐式 using。
        var left = int.MaxValue;
        var bottom = int.MinValue;
        foreach (var screen in all)
        {
            if (screen.Bounds.X < left) left = screen.Bounds.X;
            if (screen.Bounds.Bottom > bottom) bottom = screen.Bounds.Bottom;
        }
        return new PixelPoint(left - _fullWidthPx - OffScreenMarginPx, bottom + OffScreenMarginPx);
    }

    private void Start(int fromOffset, int toOffset, double fromOpacity, double toOpacity, bool collapsed)
    {
        _fromOffset = fromOffset;
        _toOffset = toOffset;
        _fromOpacity = fromOpacity;
        _toOpacity = toOpacity;
        _targetCollapsed = collapsed;
        _clockStarted = false;
        _startedAt = default;

        // 定时器必须在 UI 线程上创建；插件与宿主都只在 UI 线程触发收起/展开。
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = AnimationFrame };
            _timer.Tick += (_, _) => Advance();
        }
        _timer.Start();
    }

    private void Advance()
    {
        var window = _window;
        if (window == null)
        {
            _timer?.Stop();
            return;
        }

        // 动画途中窗口状态被用户改变（最大化/最小化）：放弃裁剪与透明，把窗口原样交还。
        if (window.WindowState != WindowState.Normal)
        {
            _timer?.Stop();
            window.Opacity = 1;
            ClearRegion();
            IsCollapsed = false;
            Settled?.Invoke();
            return;
        }

        // 动画时钟锚在真正跑起来的第一帧：显示窗口会唤醒宿主的 WebView，可能长时间
        // 阻塞 UI 线程，从调用方计时会直接跳过整段动画。
        if (!_clockStarted)
        {
            _clockStarted = true;
            _startedAt = DateTime.UtcNow;
            ApplyFrame(window, _fromOffset, _fromOpacity);
            return;
        }

        var progress = (DateTime.UtcNow - _startedAt).TotalMilliseconds / AnimationDuration.TotalMilliseconds;
        if (progress >= 1) progress = 1;
        // 收进边栏时加速钻入；拉出时缓入缓出。
        var eased = _targetCollapsed
            ? progress * progress * progress
            : 1 - Math.Pow(1 - progress, 3);
        ApplyFrame(
            window,
            (int)Math.Round(_fromOffset + (_toOffset - _fromOffset) * eased),
            _fromOpacity + (_toOpacity - _fromOpacity) * eased);

        if (progress < 1) return;

        _timer?.Stop();
        IsCollapsed = _targetCollapsed;
        if (_targetCollapsed)
        {
            // 收起终点：先收到最右缘 1 像素并全透明，随即把整个窗口移出屏幕。
            // DWM 沿可见区域绘制的边框与投影不随裁剪、透明消失，只有离开屏幕才彻底干净；
            // 此刻窗口已全透明，这一步的位移用户看不到，因此不会产生"飞走"观感。
            ApplyFrame(window, _toOffset, 0);
            if (ResolveOffscreenPosition() is { } offscreen) window.Position = offscreen;
        }
        else
        {
            // 展开收尾：彻底清掉裁剪与透明，并把原位更新到展开后的落点。
            window.Opacity = 1;
            ClearRegion();
            _originPosition = window.Position;
        }
        Settled?.Invoke();
    }

    /// <summary>
    ///     应用一帧：把窗口裁剪成从 <paramref name="offset" /> 起向右的一条带，并设置淡入
    ///     淡出。淡出与裁剪共用同一条缓动进度，窗口变窄的同时就变淡。
    /// </summary>
    private void ApplyFrame(Window window, int offset, double opacity)
    {
        if (_hwnd != 0)
        {
            var width = Math.Max(1, _fullWidthPx);
            var height = MeasureHeightPx(window);
            var left = Math.Clamp(offset, 0, width - CollapsedWidthPx);
            var region = CreateRectRgn(left, 0, width, height);
            if (region != 0) SetWindowRgn(_hwnd, region, true);
        }
        window.Opacity = Math.Clamp(opacity, 0, 1);
    }

    /// <summary>移除收起裁剪，让窗口完整绘制。</summary>
    private void ClearRegion()
    {
        if (_hwnd == 0) return;
        SetWindowRgn(_hwnd, 0, true);
    }

    [DllImport("user32.dll", SetLastError = false)]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("gdi32.dll", SetLastError = false)]
    private static extern nint CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);
}