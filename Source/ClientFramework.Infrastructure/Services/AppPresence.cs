using Avalonia.Controls;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///    应用在用户侧的在场形态：用户能感知的"应用现在是什么样子"。
///    采用扁平枚举而非"工作台可见性 + AI 开关"两个正交字段：可达的业务组合远少于
///    笛卡尔积（边栏只可能在 AI 对话打开时出现），扁平枚举让不存在的业务场景不可表达。
/// </summary>
public enum AppPresence
{
    /// <summary>还没有应用。</summary>
    NotStarted,

    /// <summary>只有工作台。</summary>
    WorkBenchOnly,

    /// <summary>工作台与 AI 对话并列。</summary>
    AiChatAlongside,

    /// <summary>工作台收为边栏，AI 对话独占视野。</summary>
    WorkBenchFolded,

    /// <summary>让出前台。</summary>
    Minimized,

    /// <summary>后台驻留，只剩托盘图标。</summary>
    InBackground,

    /// <summary>正在退出。</summary>
    Exiting
}

/// <summary>业务事件：用户或应用生命周期"想做什么"。措辞不涉及任何窗口。</summary>
public enum AppSignal
{
    /// <summary>启动完成，已注册的 AI 对话面随工作台展开。</summary>
    AppReady,

    /// <summary>切换 AI 对话。</summary>
    ToggleAiChat,

    /// <summary>关闭 AI 对话面板。</summary>
    CloseAiChat,

    /// <summary>收起/展开工作台。</summary>
    ToggleWorkBenchFold,

    /// <summary>收起应用到后台。</summary>
    CollapseToBackground,

    /// <summary>从后台唤起应用。</summary>
    ActivateApp,

    /// <summary>让出前台。</summary>
    AppDeactivated,

    /// <summary>回到前台。</summary>
    AppActivated,

    /// <summary>退出应用。</summary>
    ExitApp
}

/// <summary>业务事件及其随行事实。</summary>
public readonly record struct AppSignalArgs(AppSignal Signal, bool AiChatPreferredAtBoot = false);

/// <summary>业务结果：应用应当呈现成什么样。仍然不含任何窗口操作。</summary>
[Flags]
public enum AppPresentation
{
    None = 0,

    /// <summary>AI 对话应当呈现。</summary>
    PresentAiChat = 1 << 0,

    /// <summary>AI 对话应当隐去。</summary>
    ConcealAiChat = 1 << 1,

    /// <summary>工作台应当收为边栏。</summary>
    FoldWorkBench = 1 << 2,

    /// <summary>工作台应当展开。</summary>
    UnfoldWorkBench = 1 << 3,

    /// <summary>两个面应当并列贴合。</summary>
    AlignFaces = 1 << 4,

    /// <summary>应用应当退到后台驻留。</summary>
    SendToBackground = 1 << 5,

    /// <summary>应用应当回到前台。</summary>
    BringToForeground = 1 << 6,

    /// <summary>应用应当拆解全部面。</summary>
    TearDown = 1 << 7
}

/// <summary>
///    宿主提供的应用实体服务：应用是一个实体，工作台与 AI 对话是它的两个并列的面。
///     插件只通过它投递业务事件、登记自己的面，不裁决跨窗口行为。
/// </summary>
public interface IClientAppPresence
{
    /// <summary>当前在场形态。</summary>
    AppPresence Presence { get; }

    /// <summary>AI 对话是否打开。业务事实由形态派生，取代自行维护的可见性布尔。</summary>
    bool IsAiChatOpen { get; }

    /// <summary>投递业务事件。可从任意线程调用，实现负责切回 UI 线程。</summary>
    void Signal(AppSignal signal);

    /// <summary>
    ///     登记 AI 面。工厂在 UI 线程上首次需要呈现该面时调用一次；返回的句柄撤销登记，
    ///     插件停止时用它摘掉自己的面。
    /// </summary>
    IDisposable RegisterAiChatFace(Func<IClientAppFace> factory);
}

/// <summary>应用的一个面。窗口词汇只出现在这一层。</summary>
public interface IClientAppFace
{
    /// <summary>该面所属的顶层窗口。</summary>
    Window Face { get; }

    /// <summary>呈现该面。<paramref name="owner" /> 为实体主面，用于让系统保证单一任务栏身份。</summary>
    void Present(Window? owner);

    /// <summary>隐去该面。隐去不等于拆解。</summary>
    void Conceal();

    /// <summary>同步"工作台是否已收为边栏"，供该面自己的服饰（如边栏按钮图标）取用。</summary>
    void SetWorkBenchFolded(bool folded);
}
