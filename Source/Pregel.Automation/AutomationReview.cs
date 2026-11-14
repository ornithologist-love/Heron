namespace Heron.Pregel.Automation;

/// <summary>Immutable, user-visible content. An approval is bound to both identity and exact content hash.</summary>
public sealed record AutomationReview(string Id, int Revision, string Title, string Content, string ContentHash,
    DateTimeOffset CreatedAt, string Detail = "")
{
    public bool CanRevise { get; init; } = true;
    public string ApproveLabel { get; init; } = "批准并继续";
    public string Hint { get; init; } = "确认后继续执行流程的后续步骤。";
    public string ReviseLabel { get; init; } = "带意见继续";
    public string FeedbackPlaceholder { get; init; } = "补充要求、指出问题，或提出下一轮问题";
    public string ContinuationDetail { get; init; } = "";
    public AutomationChatSession? ChatSession { get; init; }
    public IReadOnlyList<AutomationReviewMessage> Conversation { get; init; } = [];
}

/// <summary>Human-visible turns, preserved with the review across continuation, archive and restart.</summary>
public sealed record AutomationReviewMessage(int Round, string Role, string Content, DateTimeOffset CreatedAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Caption => $"第 {Round} 轮 · {(Role == "user" ? "你的要求" : "Agent 结果")}";
}

/// <summary>Runtime-owned conversation identity; the host can transfer it without rerunning business nodes.</summary>
public sealed record AutomationChatSession(string Provider, string SessionId, string Workspace, string Model, string Title);

public enum AutomationReviewAction { Approve, Revise, Abandon, TransferToChat }

public sealed record AutomationReviewDecision(string ReviewId, string ContentHash, AutomationReviewAction Action,
    string Feedback = "")
{
    public string? RemoteActionId { get; init; }
}

/// <summary>Optional capability. The host owns the approval UI; the template owns durable graph state and artifacts.</summary>
public interface IAutomationReviewTemplate : IAutomationTemplate
{
    /// <summary>Allows templates to resolve session metadata for reviews saved before ChatSession was recorded.</summary>
    bool SupportsChatTransfer => false;
    Task<AutomationChatSession?> GetChatSessionAsync(AutomationReview review, CancellationToken token) =>
        Task.FromResult(review.ChatSession);

    Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, AutomationReview review,
        AutomationReviewDecision decision, CancellationToken token);
}
