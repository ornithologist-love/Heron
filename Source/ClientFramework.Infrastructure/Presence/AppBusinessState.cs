namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     业务状态机：应用在场形态的唯一真相源。同步推进，不接触任何窗口对象，不做异步编排。
///     "应用现在什么形态"由此回答；"窗口该怎么摆"由呈现编排层回答。
/// </summary>
public sealed class AppBusinessState
{
    private AppPresence _presence = AppPresence.NotStarted;
    private AppPresence _resumePresence = AppPresence.WorkBenchOnly;

    /// <summary>当前在场形态。</summary>
    public AppPresence Presence => _presence;

/// <summary>
    ///     AI 面在用户意图上是否打开。业务事实由形态派生，取代自行维护的可见性布尔。
    ///     后台驻留与最小化只是让面下屏，不是用户把它关掉，因此按恢复形态回答——
    ///     该状态只用于本次运行中恢复窗口，不持久化到下次启动。
    /// </summary>
    public bool IsAiChatOpen => _presence switch
    {
        AppPresence.AiChatAlongside or AppPresence.WorkBenchFolded => true,
        AppPresence.Minimized or AppPresence.InBackground =>
            _resumePresence is AppPresence.AiChatAlongside or AppPresence.WorkBenchFolded,
        _ => false
    };

    /// <summary>形态推进后产生的呈现结果；业务上无变化时不触发。</summary>
    public event Action<AppPresentation>? PresentationChanged;

    /// <summary>推进一次状态机。纯同步、无副作用，展示动作交给订阅者。</summary>
    public void Signal(AppSignal signal, bool aiChatPreferredAtBoot = false)
    {
        var from = _presence;
        var (next, presentation) = Reduce(from, signal, aiChatPreferredAtBoot, _resumePresence);

        // 进入"不在前台"的形态前记录本次运行的形态，用于回到前台时恢复。
        if (next is AppPresence.Minimized or AppPresence.InBackground &&
            from is not (AppPresence.Minimized or AppPresence.InBackground))
            _resumePresence = from;

        if (next == from && presentation == AppPresentation.None) return;

        _presence = next;
        if (presentation != AppPresentation.None) PresentationChanged?.Invoke(presentation);
    }

    private static (AppPresence Next, AppPresentation Presentation) Reduce(
        AppPresence presence, AppSignal signal, bool aiChatPreferredAtBoot, AppPresence resume) =>
        (presence, signal) switch
        {
            // 启动：宿主带入 AI 面是否可用，可用时立即展开。
            (AppPresence.NotStarted, AppSignal.AppReady) => aiChatPreferredAtBoot
                ? (AppPresence.AiChatAlongside, AppPresentation.PresentAiChat | AppPresentation.AlignFaces)
                : (AppPresence.WorkBenchOnly, AppPresentation.None),

            // 只有工作台。
            (AppPresence.WorkBenchOnly, AppSignal.ToggleAiChat) =>
                (AppPresence.AiChatAlongside, AppPresentation.PresentAiChat | AppPresentation.AlignFaces),
            (AppPresence.WorkBenchOnly, AppSignal.AppDeactivated) =>
                (AppPresence.Minimized, AppPresentation.None),
            (AppPresence.WorkBenchOnly, AppSignal.CollapseToBackground) =>
                (AppPresence.InBackground, AppPresentation.SendToBackground),

            // 两个面并列。
            (AppPresence.AiChatAlongside, AppSignal.ToggleAiChat) =>
                (AppPresence.WorkBenchOnly, AppPresentation.ConcealAiChat),
            (AppPresence.AiChatAlongside, AppSignal.CloseAiChat) =>
                (AppPresence.WorkBenchOnly, AppPresentation.ConcealAiChat),
            (AppPresence.AiChatAlongside, AppSignal.ToggleWorkBenchFold) =>
                (AppPresence.WorkBenchFolded, AppPresentation.FoldWorkBench),
            (AppPresence.AiChatAlongside, AppSignal.AppDeactivated) =>
                (AppPresence.Minimized, AppPresentation.ConcealAiChat),
            (AppPresence.AiChatAlongside, AppSignal.CollapseToBackground) =>
                (AppPresence.InBackground, AppPresentation.SendToBackground),

            // 工作台在边栏。
            (AppPresence.WorkBenchFolded, AppSignal.ToggleWorkBenchFold) =>
                (AppPresence.AiChatAlongside, AppPresentation.UnfoldWorkBench | AppPresentation.AlignFaces),
            // AI 对话关掉时边栏随之消失，工作台必须先展开：展开必须发生在隐去之前，
            // 否则展开锚点会失去参照。
            (AppPresence.WorkBenchFolded, AppSignal.CloseAiChat) =>
                (AppPresence.WorkBenchOnly, AppPresentation.UnfoldWorkBench | AppPresentation.ConcealAiChat),
            (AppPresence.WorkBenchFolded, AppSignal.ToggleAiChat) =>
                (AppPresence.WorkBenchOnly, AppPresentation.UnfoldWorkBench | AppPresentation.ConcealAiChat),
            // 收起态被激活即自动展开，否则用户会以为应用卡死。
            (AppPresence.WorkBenchFolded, AppSignal.AppActivated) =>
                (AppPresence.AiChatAlongside, AppPresentation.UnfoldWorkBench | AppPresentation.AlignFaces),
            (AppPresence.WorkBenchFolded, AppSignal.AppDeactivated) =>
                (AppPresence.Minimized, AppPresentation.ConcealAiChat),
            (AppPresence.WorkBenchFolded, AppSignal.CollapseToBackground) =>
                (AppPresence.InBackground, AppPresentation.SendToBackground),

            // 不在前台：按记忆恢复。
            (AppPresence.Minimized, AppSignal.AppActivated) => Resume(resume),
            (AppPresence.Minimized, AppSignal.ActivateApp) => Resume(resume),
            (AppPresence.Minimized, AppSignal.CollapseToBackground) =>
                (AppPresence.InBackground, AppPresentation.SendToBackground),
            (AppPresence.InBackground, AppSignal.ActivateApp) => Resume(resume),

            // 终态。
            (_, AppSignal.ExitApp) => (AppPresence.Exiting, AppPresentation.TearDown),

            // 业务上不可达的组合：静默空转，结构性取代散落各处的防御式判断。
            _ => (presence, AppPresentation.None)
        };

    private static (AppPresence Next, AppPresentation Presentation) Resume(AppPresence resume)
    {
        // 回到前台时不还原"工作台已收为边栏"：只剩一个 AI 面板可看、工作台是一根看不出
        // 的边条，用户无法解释这个画面。业务上规范化为两个面并列。
        var normalized = resume == AppPresence.WorkBenchFolded
            ? AppPresence.AiChatAlongside
            : resume;

        return normalized == AppPresence.AiChatAlongside
            ? (AppPresence.AiChatAlongside, AppPresentation.BringToForeground
                | AppPresentation.UnfoldWorkBench | AppPresentation.PresentAiChat | AppPresentation.AlignFaces)
            : (AppPresence.WorkBenchOnly, AppPresentation.BringToForeground
                | AppPresentation.UnfoldWorkBench | AppPresentation.ConcealAiChat);
    }
}
