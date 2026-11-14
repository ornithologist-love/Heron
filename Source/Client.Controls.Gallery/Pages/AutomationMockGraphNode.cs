using Heron.ClientControls.Graphs;
using FluentIcons.Common;

namespace Heron.ClientControls.Gallery.Pages;

public enum MockStepState { Plan, Pending, Running, Review, Succeeded, Failed, Skipped, Cancelled }

public sealed class MockGraphNode : IGraphItem
{
    public MockGraphNode(string key, string title, MockStepState state, IReadOnlyList<string>? dependsOn = null)
    {
        Key = key;
        Title = title;
        State = state;
        DependsOn = dependsOn ?? [];
    }

    public string Key { get; }
    public string Title { get; }
    public IReadOnlyList<string>? DependsOn { get; }
    public MockStepState State { get; }
    /// <summary>状态之外的补充：耗时、跳过原因、错误摘要。与状态拼成一行，不增加节点高度。</summary>
    public string Detail { get; init; } = "";
    public string Duration { get; init; } = "";
    public double Progress { get; init; } = -1;
    public bool HasLog { get; init; }
    /// <summary>是否为子图调用节点；只提供入口，不展示内部步骤数。</summary>
    public bool HasSubgraph { get; init; }
    /// <summary>Stage 的进入条件说明（例如收尾 Stage），只以文字呈现，不再画条件虚线。</summary>
    public string Condition { get; init; } = "";
    public IReadOnlyList<MockGraphNode> Children { get; init; } = [];

    public bool IsStage => Children.Count > 0;
    public bool HasCondition => Condition.Length > 0;
    public bool HasStageNote => HasCondition || IsParallel;
    /// <summary>HumanGate 待审核时节点右侧提供「审核」主操作。</summary>
    public bool CanReview => IsReview && !IsStage;
    public bool HasActions => HasLog || HasSubgraph || CanReview;
    public bool IsParallel => Children.Count(child => child.DependsOn is { Count: 0 }) > 1;
    public string ParallelCaption => $"{Children.Count(child => child.DependsOn is { Count: 0 })} 路并行";

    public bool IsPlan => State == MockStepState.Plan;
    public bool IsPending => State == MockStepState.Pending;
    public bool IsRunning => State == MockStepState.Running;
    public bool IsReview => State == MockStepState.Review;
    public bool IsSucceeded => State == MockStepState.Succeeded;
    public bool IsFailed => State == MockStepState.Failed;
    public bool IsSkipped => State == MockStepState.Skipped;
    public bool IsCancelled => State == MockStepState.Cancelled;
    public bool ShowProgress => IsRunning && Progress >= 0;

    public Icon StateIcon => State switch
    {
        MockStepState.Succeeded => Icon.CheckmarkCircle,
        MockStepState.Failed => Icon.DismissCircle,
        MockStepState.Running => Icon.ArrowSync,
        MockStepState.Review => Icon.PersonClock,
        MockStepState.Skipped => Icon.SubtractCircle,
        MockStepState.Cancelled => Icon.RecordStop,
        MockStepState.Pending => Icon.Clock,
        _ => Icon.Circle
    };

    public string StateText => State switch
    {
        MockStepState.Plan => "",
        MockStepState.Pending => "等待",
        MockStepState.Running => Progress >= 0 ? $"进行中 {Progress:F0}%" : "进行中",
        MockStepState.Review => "待审核",
        MockStepState.Succeeded => Duration.Length > 0 ? Duration : "已完成",
        MockStepState.Failed => Duration.Length > 0 ? $"失败 · {Duration}" : "失败",
        MockStepState.Skipped => "已跳过",
        MockStepState.Cancelled => "已取消",
        _ => ""
    };

    /// <summary>节点第二行：状态 + 补充信息，单行省略，完整内容放 Tooltip。</summary>
    public string MetaLine => (StateText, Detail) switch
    {
        ("", var detail) => detail,
        (var state, "") => state,
        var (state, detail) => $"{state} · {detail}"
    };

    public bool HasMeta => MetaLine.Length > 0;

    public string ToolTipText => Detail.Length > 0 ? $"{Title}\n{Detail}" : Title;
}
