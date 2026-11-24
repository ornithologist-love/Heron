using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     宿主中的应用实体服务。业务状态机是唯一真相源，呈现编排层是唯一执行者，
///     本类只负责把两者接起来，并把真实入口（窗口状态变化、激活、关闭）翻译成业务事件。
/// </summary>
public sealed class ClientAppPresence : IClientAppPresence, IDisposable
{
    private readonly AppBusinessState _state = new();
    private readonly FacePresenter _presenter;
    private readonly ILogger<ClientAppPresence> _logger;

    private Window? _workBench;
    private WindowState _lastWorkBenchState = WindowState.Normal;
    private bool _applying;
    private bool _ready;

    public ClientAppPresence(FacePresenter presenter, ILogger<ClientAppPresence> logger)
    {
        _presenter = presenter;
        _logger = logger;
        _state.PresentationChanged += OnPresentationChanged;
    }

    public AppPresence Presence => _state.Presence;

    public bool IsAiChatOpen => _state.IsAiChatOpen;

    public void Signal(AppSignal signal) => Dispatch(() => Advance(signal, false));

    public IDisposable RegisterAiChatFace(Func<IClientAppFace> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _presenter.SetAiChatFactory(factory);
        return new Registration(() => _presenter.SetAiChatFactory(null));
    }

    /// <summary>
    ///     工作台就位：实体身份有了承载者。此处只建立连接，不投递启动事件——AI 面要以
    ///     工作台为属主呈现，必须等它真正显示出来。
    /// </summary>
    public void AttachWorkBench(Window workBench)
    {
        ArgumentNullException.ThrowIfNull(workBench);
        _workBench = workBench;
        _presenter.AttachWorkBench(workBench);
        _lastWorkBenchState = workBench.WindowState;

        workBench.Activated += OnWorkBenchActivated;
        workBench.PropertyChanged += OnWorkBenchPropertyChanged;
    }

    /// <summary>
    ///     应用就绪：工作台已显示，此处投递 <see cref="AppSignal.AppReady" />。
    ///     AI 面默认不展开，由插件结合选中的功能页与该场景的用户选择决定显隐。
    ///     幂等：只生效一次。
    /// </summary>
    public void NotifyAppReady(bool aiChatPreferredAtBoot = false)
    {
        if (_ready) return;
        _ready = true;

        var openAiChat = aiChatPreferredAtBoot && _presenter.HasAiChatFace;
        _logger.LogInformation("App ready; open registered AI chat at boot={OpenAiChat}", openAiChat);
        Advance(AppSignal.AppReady, openAiChat);
    }

    /// <summary>投递到 UI 线程：入口可能来自托盘菜单或后台任务。</summary>
    private void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void Advance(AppSignal signal, bool aiChatPreferredAtBoot)
    {
        _state.Signal(signal, aiChatPreferredAtBoot);
    }

    private void OnPresentationChanged(AppPresentation presentation)
    {
        // 呈现过程本身会引发激活、可见性等回调；它们是执行的副产物，不是新的业务输入。
        _applying = true;
        try
        {
            _presenter.Apply(presentation);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "App presentation failed: {Presentation}", presentation);
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnWorkBenchActivated(object? sender, EventArgs e)
    {
        if (_applying) return;
        // 收起态被激活即自动展开（否则用户会以为应用卡死）；其余形态下是空转。
        Advance(AppSignal.AppActivated, false);
    }

    private void OnWorkBenchPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;

        var state = _workBench!.WindowState;
        var previous = _lastWorkBenchState;
        _lastWorkBenchState = state;
        if (_applying) return;

        // 最小化由操作系统驱动，但"让出前台时 AI 面不应留在前台"是一条业务规则，
        // 因此以业务事件形态进入状态机，而不是让窗口去监听窗口。
        if (state == WindowState.Minimized) Advance(AppSignal.AppDeactivated, false);
        else if (previous == WindowState.Minimized) Advance(AppSignal.AppActivated, false);
    }

    public void Dispose()
    {
        _state.PresentationChanged -= OnPresentationChanged;
        if (_workBench == null) return;
        _workBench.Activated -= OnWorkBenchActivated;
        _workBench.PropertyChanged -= OnWorkBenchPropertyChanged;
        _workBench = null;
    }

    private sealed class Registration(Action revoke) : IDisposable
    {
        private Action? _revoke = revoke;
        public void Dispose() => Interlocked.Exchange(ref _revoke, null)?.Invoke();
    }
}
