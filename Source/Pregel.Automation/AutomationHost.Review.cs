namespace Heron.Pregel.Automation;

public sealed partial class AutomationHost
{
    // Callers hold _gate. A waiting run does not hold a resource lease or a worker.
    private bool HasPendingReview(string taskId) =>
        HistorySummaries(taskId).Any(r => r.Status == AutomationRunStatus.AwaitingReview);

    private static AutomationRunResult? InterruptedReview(ActiveRun run, string summary) => run.PreviousReview is { } review
        ? AutomationRunResult.AwaitingReview(review, summary) : null;

    /// <summary>Queues a decision for exactly the visible run/revision. Never uses subsequently edited task settings.</summary>
    public void Review(string taskId, string runId, AutomationReviewDecision decision)
    {
        EnsureActive();
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            RefreshExternalRunsLocked();
            var task = _document.Tasks.FirstOrDefault(t => t.Id == taskId);
            var record = FindRun(taskId, runId);
            if (decision.RemoteActionId is { } actionId)
            {
                if (actionId.Length != 32 || !actionId.All(Uri.IsHexDigit))
                    throw new InvalidOperationException("远程处理请求身份无效");
                // Replaying a lost acknowledgement observes the same run instead of
                // applying an already queued review decision a second time.
                if (record?.LastRemoteActionId == actionId) return;
            }
            if (_stopped || task == null
                || HistorySummaries(taskId).Any(r => r.RunId == runId && IsLive(r)))
                throw new InvalidOperationException("任务正在执行，请稍后再试");
            if (record == null || record.Status != AutomationRunStatus.AwaitingReview || record.Review is not { } review)
                throw new InvalidOperationException("该运行已不在等待审核，请刷新后重试");
            if (record.Subgraph == null && !task.AllowParallel && IsRunning(taskId))
                throw new InvalidOperationException("任务正在执行，请稍后再试");
            if (review.Id != decision.ReviewId || review.ContentHash != decision.ContentHash)
                throw new InvalidOperationException("草稿版本已经变化，请阅读新版本后重新审核");
            if (!Enum.IsDefined(decision.Action)) throw new InvalidOperationException("无效审核动作");
            if (decision.Action == AutomationReviewAction.Revise && !review.CanRevise)
                throw new InvalidOperationException("此流程不支持修改意见");
            if (decision.Action == AutomationReviewAction.Revise && string.IsNullOrWhiteSpace(decision.Feedback))
                throw new InvalidOperationException("请填写继续处理的要求或问题");
            if (decision.Feedback.Length > 12000) throw new InvalidOperationException("反馈不能超过 12000 字符");
            var snapshot = record.TaskSnapshot ?? throw new InvalidOperationException("缺少本次运行配置，无法恢复审核");
            if (!_templates.TryGetValue(snapshot.TemplateId, out var template) || template is not IAutomationReviewTemplate)
                throw new InvalidOperationException("所属模板未加载，请加载后继续处理");
            if (record.Subgraph != null) template = BindSubgraph(template, record.Subgraph);
            if (decision.Action == AutomationReviewAction.TransferToChat && (_openChat == null ||
                (review.ChatSession == null && !((IAutomationReviewTemplate)template).SupportsChatTransfer)))
                throw new InvalidOperationException("本次运行无法转到 AI Chat");
            using var leaseGuard = new LeaseGuard(_store.TryAcquireTask(ExecutionLeaseKey(taskId, record.RemoteRequestId, record.Subgraph),
                record.Subgraph == null && task.AllowParallel)
                ?? throw new InvalidOperationException("任务正在执行，请稍后再试"));
            var queued = record with { Status = AutomationRunStatus.Queued, EndedAt = null,
                LastRemoteActionId = decision.RemoteActionId ?? record.LastRemoteActionId,
                Revision = record.Revision + 1,
                OwnerProcessId = Environment.ProcessId, OwnerStartedUtcTicks = ProcessStartedUtcTicks,
                Summary = decision.Action == AutomationReviewAction.TransferToChat ? "正在转到 AI Chat" : "等待处理审核意见" };
            var recorder = new AutomationRunRecorder(queued, template, snapshot, _evidenceRoot, OnProgress);
            var run = new ActiveRun(snapshot, template, recorder,
                decision.Action == AutomationReviewAction.TransferToChat ? [] : template.ResourceKeys(snapshot).ToArray(),
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token), decision, review) { Lease = leaseGuard.Lease };
            _store.Upsert(recorder.Record);
            _active[runId] = run;
            UpdateBackgroundExecutionLocked();
            Track(ExecuteAsync(run, record.Trigger));
            leaseGuard.Lease = null;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes a run that waits for a decision without running its Finally. A run whose graph can no longer be
    /// loaded cannot finish its own cleanup and would block its task forever; this is the way out. False while
    /// a worker still executes the run.
    /// </summary>
    public bool Discard(string taskId, string runId, string reason)
    {
        EnsureActive();
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            RefreshExternalRunsLocked();
            if (FindRun(taskId, runId) is not { } record) return false;
            if (record.IsFinished) return true;
            if (record.Status != AutomationRunStatus.AwaitingReview || _active.ContainsKey(runId)) return false;
            _store.Upsert(AutomationRunRecorder.Finish(record, AutomationRunResult.Cancelled(reason)));
        }
        Changed?.Invoke();
        return true;
    }

    private sealed class LeaseGuard(IDisposable lease) : IDisposable
    {
        public IDisposable? Lease { get; set; } = lease;
        public void Dispose() => Lease?.Dispose();
    }
}
