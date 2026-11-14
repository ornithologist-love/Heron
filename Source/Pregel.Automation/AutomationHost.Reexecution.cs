using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>Result of the preflight shown before a derived run is created.</summary>
/// <param name="Problem">Why the run cannot be created; null when it can.</param>
/// <param name="ReusedNodeIds">Nodes whose source results are inherited or replayed instead of executed.</param>
public sealed record AutomationReexecutionCheck(string? Problem, IReadOnlyList<string> ReusedNodeIds)
{
    public bool CanStart => Problem == null;
    public static AutomationReexecutionCheck Refused(string problem) => new(problem, []);
}

public sealed partial class AutomationHost
{
    public bool SupportsReexecution(string templateId)
    {
        lock (_gate) return _templates.GetValueOrDefault(templateId) is IAutomationReexecutionTemplate { SupportsReexecution: true };
    }

    /// <summary>
    /// Host admission rules for a derived run. Cheap and synchronous, so a view can explain an unavailable entry;
    /// graph-level rules need <see cref="CheckReexecutionAsync"/>.
    /// </summary>
    public string? ReexecutionProblem(string taskId, string sourceRunId, PregelReexecutionMode mode)
    {
        lock (_gate) return ReexecutionProblemLocked(taskId, sourceRunId, mode, out _, out _);
    }

    /// <summary>Host rules followed by the template's read-only graph preview. Creates no record or run.</summary>
    public async Task<AutomationReexecutionCheck> CheckReexecutionAsync(string taskId, string sourceRunId,
        PregelReexecutionRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IAutomationTemplate? template;
        lock (_gate)
        {
            if (ReexecutionProblemLocked(taskId, sourceRunId, request.Mode, out _, out template) is { } problem)
                return AutomationReexecutionCheck.Refused(problem);
        }
        try
        {
            var preview = await Task.Run(() => ((IAutomationReexecutionTemplate)template!)
                .PreviewReexecutionAsync(sourceRunId, request, token), token).ConfigureAwait(false);
            return new(null, preview.InheritedNodes.Select(x => x.NodeId.Value)
                .Concat(preview.ReplayedNodes.Select(x => x.Value)).Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is PregelException or InvalidOperationException or InvalidDataException
            or IOException or System.Text.Json.JsonException)
        {
            return AutomationReexecutionCheck.Refused(ex.Message);
        }
    }

    /// <summary>
    /// Queues a run derived from <paramref name="sourceRunId"/> with its original parameter snapshot as a new history
    /// entry. Throws <see cref="InvalidOperationException"/> with the reason when the run cannot be admitted.
    /// </summary>
    public string StartReexecution(string taskId, string sourceRunId, PregelReexecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        return StartRunCore(taskId, "manual", null, sourceRunId, request with { NodePath = request.NodePath.ToArray() })
            ?? throw new InvalidOperationException("自动化宿主已停止或任务已被占用，请稍后重试");
    }

    // Callers hold _gate.
    private string? ReexecutionProblemLocked(string taskId, string sourceRunId, PregelReexecutionMode mode,
        out AutomationRunRecord? source, out IAutomationTemplate? template)
    {
        template = null;
        source = FindRun(taskId, sourceRunId);
        if (source == null) return "原运行记录已不存在";
        if (source.Subgraph != null) return "远程子图由父流程关联执行；请从父流程继续或重新运行。";
        if (!source.IsFinished) return "请先等待原运行及收尾结束";
        if (source.Status == AutomationRunStatus.Skipped) return "此记录没有执行流程，没有可用的检查点";
        if (mode == PregelReexecutionMode.Continue && source.Status is not (AutomationRunStatus.Failed or AutomationRunStatus.Cancelled))
            return "只有失败或取消的运行可以继续";
        if (source.TaskSnapshot is not { } snapshot) return "原运行缺少参数快照，无法重新运行";
        if (!_templates.TryGetValue(snapshot.TemplateId, out template)) return "模板未加载，无法重新运行";
        if (template is not IAutomationReexecutionTemplate { SupportsReexecution: true }) return "此模板不支持从检查点派生运行";
        // Frozen state may reference the previous execution's working files. Do not admit concurrent writers.
        if (IsRunning(taskId) || HasPendingReview(taskId)) return "任务正在执行或有待审核的运行，结束后才能重新运行";
        return Problem(snapshot, template);
    }
}
