using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     呈现编排层：唯一知道窗口、停靠、裁剪、托盘的地方。业务状态机从不接触它，
///     只把"应用应当呈现成什么样"（<see cref="AppPresentation" />）交给它执行。
/// </summary>
public sealed class FacePresenter
{
    private readonly WorkBenchFold _fold = new();
    private readonly FaceDock _dock = new();
    private readonly ILogger<FacePresenter> _logger;

    private Window? _workBench;
    private IClientAppFace? _aiChat;
    private Func<IClientAppFace>? _aiChatFactory;

    public FacePresenter(ILogger<FacePresenter> logger)
    {
        _logger = logger;
        _fold.Settled += OnFoldSettled;
    }

    /// <summary>工作台就位。实体身份由它承载，因此呈现层必须在任何形态生效前拿到它。</summary>
    public void AttachWorkBench(Window workBench) => _workBench = workBench;

    /// <summary>是否已有插件登记 AI 面。没有时不能声称该面已呈现。</summary>
    public bool HasAiChatFace => _aiChatFactory != null;

    /// <summary>登记 AI 面的工厂。第一次需要呈现它时才真正创建窗口。</summary>
    public void SetAiChatFactory(Func<IClientAppFace>? factory) => _aiChatFactory = factory;

    /// <summary>执行一次业务结果。顺序固定：先撤销收起、再谈并列与显隐。</summary>
    public void Apply(AppPresentation presentation)
    {
        if (_workBench == null) return;

        if (presentation.HasFlag(AppPresentation.TearDown))
        {
            _fold.Release();
            _fold.Stop();
            _dock.Detach();
            _aiChat?.Conceal();
            _aiChat = null;
            return;
        }

        if (presentation.HasFlag(AppPresentation.SendToBackground))
        {
            SendToBackground();
            return;
        }

        // 收起/展开必须先于回到前台：处于收起态时展开的第一帧就是边条（全透明后
        // 渐显），若先显示完整窗口再收起，屏幕上会闪过一个整窗。
        if (presentation.HasFlag(AppPresentation.UnfoldWorkBench))
        {
            // 动画期间工作台是固定锚点，AI 面不能去追一个正在移动的边缘。
            _dock.Freeze();
            _fold.Unfold(UnfoldAnchor());
            NotifyFolded();
        }

        if (presentation.HasFlag(AppPresentation.FoldWorkBench))
        {
            _dock.Freeze();
            _fold.Fold(_workBench);
            NotifyFolded();
        }

        // 回到前台：窗口若还隐藏着，后面所有位置写入都会落在看不见的地方。
        if (presentation.HasFlag(AppPresentation.BringToForeground)) BringToForeground();

        if (presentation.HasFlag(AppPresentation.PresentAiChat)) PresentAiChatFace();
        if (presentation.HasFlag(AppPresentation.ConcealAiChat)) ConcealAiChatFace();
        // 收起/展开动画进行中不解冻：动画结束（Settled）才贴合，避免 AI 面追着移动边缘。
        // 收起稳定态同样不恢复：工作台已整窗移到屏幕之外，它的坐标会把 AI 面一起拖出去。
        if (presentation.HasFlag(AppPresentation.AlignFaces) && !_fold.IsAnimating && !_fold.IsCollapsed)
            _dock.Resume();
    }

    private void PresentAiChatFace()
    {
        if (_aiChat == null)
        {
            if (_aiChatFactory == null)
            {
                _logger.LogWarning("AI face presentation requested before a face was registered");
                return;
            }
            _aiChat = _aiChatFactory();
        }

        var face = _aiChat.Face;
        // 首次呈现先摆位再显示：不这样做，新窗口会先在默认位置冒出再跳到工作台旁边。
        if (!face.IsVisible && _fold.IsCollapsed == false) FaceDock.PlaceGeometry(_workBench!, face);
        _aiChat.Present(_workBench);
        _dock.Attach(_workBench!, face);
        _aiChat.SetWorkBenchFolded(_fold.IsCollapsed);
        // 工作台还在屏外或正在滑入时不能恢复跟随：此刻它的位置不是有效参照，贴合会把
        // 刚显示的 AI 面直接拖到屏幕外；等收起/展开稳定后由 Settled 统一贴合。
        if (_fold.IsCollapsed || _fold.IsAnimating) _dock.Suspend();
        else _dock.Resume();
    }

    private void ConcealAiChatFace()
    {
        _dock.Detach();
        _aiChat?.Conceal();
    }

    /// <summary>收起/展开动画结束：收起态继续保持暂停跟随（工作台在屏外，不是有效参照），
    ///     只有展开完成才恢复贴合。收起/展开时 AI 面不去追工作台正在移动的边缘。</summary>
    private void OnFoldSettled()
    {
        NotifyFolded();
        if (_fold.IsCollapsed)
        {
            _dock.Suspend();
            return;
        }
        _dock.Resume();
    }

    private void NotifyFolded()
    {
        if (_aiChat == null) return;
        _aiChat.SetWorkBenchFolded(_fold.IsCollapsed);
    }

    /// <summary>
    ///     展开锚点：AI 面还在屏幕上就取它的左缘（收起的工具条其实位于 AI 面左边）；
    ///     AI 面不在时就地展开，工作台回到收起前的位置。
    /// </summary>
    private PixelPoint UnfoldAnchor()
    {
        if (_aiChat is { Face.IsVisible: true }) return _aiChat.Face.Position;

        var workBench = _workBench!;
        // 收起稳定后工作台停在屏幕之外，它的实时坐标不是有效参照，必须用收起前的位置
        // 推算，否则"就地展开"会从屏外展开到屏外，用户看不到任何东西。
        var position = _fold.RestorePosition ?? workBench.Position;
        var scale = workBench.RenderScaling;
        return new PixelPoint(
            position.X + (int)Math.Round(workBench.Bounds.Width * scale),
            position.Y);
    }

    /// <summary>
    ///     退到后台驻留：两个面都让出屏幕，实体身份交给托盘图标。窗口本身保留，
    ///     所以从托盘回来是恢复而不是重建（AI 面存活，WebView 与 sidecar 得以复用）。
    /// </summary>
    private void SendToBackground()
    {
        _dock.Detach();
        _aiChat?.Conceal();
        if (_workBench is { IsVisible: true }) _workBench.Hide();
    }

    private void BringToForeground()
    {
        var workBench = _workBench!;
        if (workBench.WindowState == WindowState.Minimized) workBench.WindowState = WindowState.Normal;
        if (!workBench.IsVisible) workBench.Show();
        workBench.Activate();
    }
}
