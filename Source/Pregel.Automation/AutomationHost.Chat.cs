namespace Heron.Pregel.Automation;

public sealed partial class AutomationHost
{
    private Func<AutomationChatSession, string, CancellationToken, Task>? _openChat;
    public bool CanOpenChat { get { lock (_gate) return !_stopped && _openChat != null; } }

    public IDisposable RegisterChat(Func<AutomationChatSession, string, CancellationToken, Task> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        lock (_gate)
        {
            if (_stopped || _openChat != null) throw new InvalidOperationException("AI Chat 已登记或宿主已停止");
            _openChat = open;
        }
        Changed?.Invoke();
        return new Registration(() =>
        {
            lock (_gate)
            {
                _openChat = null;
                foreach (var run in _active.Values.Where(run => run.Decision?.Action == AutomationReviewAction.TransferToChat))
                    run.Cancellation.Cancel();
            }
            Changed?.Invoke();
        });
    }

    private async Task<AutomationRunResult> RunReviewAsync(ActiveRun run, AutomationRunContext context,
        AutomationReview review, AutomationReviewDecision decision, CancellationToken token)
    {
        if (decision.Action != AutomationReviewAction.TransferToChat)
            return await ((IAutomationReviewTemplate)run.Template).ReviewAsync(context, review, decision, token);
        Func<AutomationChatSession, string, CancellationToken, Task> open;
        lock (_gate) open = _openChat ?? throw new InvalidOperationException("AI Chat 未启用，原结果仍待审核");
        var session = await ((IAutomationReviewTemplate)run.Template).GetChatSessionAsync(review, token);
        await open(session ?? throw new InvalidOperationException("本次运行没有可接管的 AI 会话"),
            decision.Feedback, token);
        // Loading must succeed before the waiting run is closed. No template or graph is resumed.
        return AutomationRunResult.Cancelled("已转到 AI Chat") with { ArtifactPath = run.Record.ArtifactPath, Review = review };
    }
}
