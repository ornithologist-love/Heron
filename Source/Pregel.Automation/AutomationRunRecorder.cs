using Heron.Pregel;
using Heron.Pregel.Tooling;
using System.Text.Json;

namespace Heron.Pregel.Automation;

/// <summary>
/// Turns Pregel node events into the user-level step timeline of one execution. Nodes are mapped to steps through
/// <see cref="IAutomationTemplate.PlanSteps"/>; nodes that belong to no step (validation, bookkeeping) are not shown
/// but still fail the step list when they fail.
/// </summary>
public sealed class AutomationRunRecorder : IPregelRunObserver
{
    private readonly object _gate = new();
    private readonly FilePregelRunObserver _files;
    private readonly Action<AutomationRunRecord, bool> _changed;
    private readonly Dictionary<string, AutomationStep> _stepByNode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _finishedNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _skippedNodes = new(StringComparer.Ordinal);
    private readonly List<AutomationStepRecord> _steps;
    private readonly List<string> _evidence = [];
    private AutomationRunRecord _record;
    private DateTimeOffset _lastProgressPublish;
    private volatile bool _isFinalizing;
    private bool _closed;
    private readonly AutomationRunRecord? _reuseSource;
    public bool IsFinalizing => _isFinalizing;

    public AutomationRunRecorder(AutomationRunRecord record, IAutomationTemplate template, AutomationTask task,
        string evidenceRoot, Action<AutomationRunRecord, bool> changed, AutomationRunRecord? reuseSource = null)
    {
        _reuseSource = reuseSource;
        _files = new FilePregelRunObserver(evidenceRoot);
        _changed = changed;
        var plan = template.PlanSteps(task);
        foreach (var step in plan)
        foreach (var node in step.NodeIds)
            _stepByNode[node] = step;
        _steps = record.Steps.Count > 0 ? record.Steps.ToList() : plan.Select(CreateStep).ToList();
        _evidence.AddRange(record.EvidenceDirectories);
        foreach (var step in _steps.Where(s => s.State is AutomationStepState.Succeeded or AutomationStepState.Skipped))
        {
            _finishedNodes[step.Key] = step.NodeIds.ToHashSet(StringComparer.Ordinal);
            if (step.State == AutomationStepState.Skipped) _skippedNodes[step.Key] = step.NodeIds.ToHashSet(StringComparer.Ordinal);
        }
        foreach (var stage in _steps.Where(s => s.IsStage && s.State is not (AutomationStepState.Succeeded or AutomationStepState.Skipped)))
        {
            var completed = stage.Children.Where(child => child.State is AutomationStepState.Succeeded or AutomationStepState.Skipped).ToArray();
            if (completed.Length == 0) continue;
            _finishedNodes[stage.Key] = completed.SelectMany(child => child.NodeIds).ToHashSet(StringComparer.Ordinal);
            _skippedNodes[stage.Key] = completed.Where(child => child.State == AutomationStepState.Skipped)
                .SelectMany(child => child.NodeIds).ToHashSet(StringComparer.Ordinal);
        }
        _record = record with { Steps = _steps.ToArray() };
    }

    public string GetRunDirectory(PregelRunId runId) => _files.GetRunDirectory(runId);

    public AutomationRunRecord Record
    {
        get { lock (_gate) return _record; }
    }

    /// <summary>Host-side status changes (queued → running).</summary>
    public AutomationRunRecord Update(Func<AutomationRunRecord, AutomationRunRecord> change)
    {
        lock (_gate) return _record = change(_record) with { Steps = _steps.ToArray(), EvidenceDirectories = _evidence.ToArray(),
            Revision = _record.Revision + 1 };
    }

    public void OnEvent(PregelRunEvent value)
    {
        // Evidence first: the file log must be complete even when a UI subscriber misbehaves.
        try { _files.OnEvent(value); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] evidence write failed"); }

        AutomationRunRecord? publish = null;
        var persist = false;
        lock (_gate)
        {
            if (_closed) return;
            value = NormalizeReuse(value);
            if (value.Kind != "subgraph.event" && Step(value) is { IsStage: true } stage)
                Update(stage.Key, s => s with { Children = ApplyChildEvent(s.Children, value) });
            switch (value.Kind)
            {
                case "mutation.committed" when value.Data is { } expansion && PregelMutationSnapshot.FromJson(expansion) is { } snapshot:
                    var expanded = MergeSteps(AutomationStepTopology.FromGraph(snapshot.Graph), _steps);
                    _steps.Clear(); _steps.AddRange(expanded);
                    _stepByNode.Clear();
                    foreach (var expandedStep in AutomationStepTopology.FromGraph(snapshot.Graph))
                    foreach (var expandedNode in expandedStep.NodeIds) _stepByNode[expandedNode] = expandedStep;
                    publish = Commit(persist = true);
                    break;
                case var warning when AutomationRuntime.WarningEvents.Contains(warning):
                    _record = _record with { Warning = value.Message };
                    publish = Commit(persist = true);
                    break;
                case "run.finalizing":
                    _isFinalizing = true;
                    _record = _record with { Status = AutomationRunStatus.Running, Summary = "正在收尾；再次取消将停止收尾" };
                    publish = Commit(persist = true);
                    break;
                case "subgraph.event" when Step(value) is { } parent && value.Data is { } data:
                    var child = data.Deserialize<PregelRunEvent>();
                    if (child != null)
                    {
                        Update(parent.Key, s => parent.IsStage
                            ? BubbleWaiting(s with { Children = ApplyChildEvent(s.Children, value) }, s.Children)
                            : ApplySubgraphEvent(s, child));
                        UpdateRunActivity();
                        var activity = Unwrap(child);
                        publish = activity.Kind is "node.started" or "node.finished" or "run.started" or "run.finished"
                            or "run.waiting" or "mutation.committed"
                            ? Commit(persist = true) : Throttled();
                    }
                    break;
                case "run.waiting":
                    var waitingDirectory = value.WorkingDirectory ?? _files.GetRunDirectory(value.RunId);
                    if (!_evidence.Contains(waitingDirectory)) _evidence.Add(waitingDirectory);
                    _record = _record with { Status = AutomationRunStatus.Queued, Summary = PregelRunLogFormatter.WaitingSummary(value) };
                    publish = Commit(persist = true);
                    break;
                case "run.started":
                    _record = _record with
                    {
                        Status = AutomationRunStatus.Running, StartedAt = _record.StartedAt ?? value.Timestamp,
                        Summary = "运行中"
                    };
                    var directory = value.WorkingDirectory ?? _files.GetRunDirectory(value.RunId);
                    if (!_evidence.Contains(directory)) _evidence.Add(directory);
                    persist = true;
                    publish = Commit(true);
                    break;
                case "node.started" when Step(value) is { } started:
                    Update(started.Key, s => Started(s, value) with { HasProgress = started.HasProgress });
                    publish = Commit(persist = true);
                    break;
                case "node.retry" when Step(value) is { IsStage: false } retrying:
                    Update(retrying.Key, s => Retrying(s, value));
                    publish = Commit(persist = true);
                    break;
                case "node.finished":
                    publish = NodeFinished(value, out persist);
                    if (IsReused(value) && Step(value) is { IsStage: false } reused)
                    {
                        Update(reused.Key, s => s with { IsReused = true });
                        publish = Commit(persist = true);
                    }
                    break;
                case "node.output" when Step(value) is { } logged:
                    Update(logged.Key, s => s with { Detail = Trim(value.Message) });
                    publish = Throttled();
                    break;
                default:
                    if (Step(value) is { } step && PregelProgress.TryRead(value, out var progress)
                        && _steps.First(s => s.Key == step.Key).State == AutomationStepState.Running)
                    {
                        var first = !_steps.First(s => s.Key == step.Key).HasProgress
                            || _steps.First(s => s.Key == step.Key).Progress == null;
                        Update(step.Key, s => s with { HasProgress = true, Progress = progress.Fraction, Detail = progress.Detail });
                        publish = first ? Commit(false) : Throttled();
                    }
                    break;
            }
            var observation = Unwrap(value);
            if (observation.Kind == ArtifactNodes.RegisteredEvent && observation.Data is { } artifactData)
            {
                var artifact = artifactData.Deserialize<ArtifactRecord>();
                if (artifact is { Id.Length: > 0, Name.Length: > 0, SourcePath.Length: > 0 })
                {
                    var artifacts = _record.Artifacts.ToList();
                    var index = artifacts.FindIndex(item => item.Id == artifact.Id);
                    if (index < 0) artifacts.Add(artifact);
                    else artifacts[index] = artifacts[index].Merge(artifact);
                    _record = _record with { Artifacts = artifacts.ToArray() };
                    publish = Commit(persist = true);
                }
            }
        }
        if (publish != null) _changed(publish, persist);
    }

    /// <summary>Closes the timeline: running steps take the final status, untouched steps become 「未执行」.</summary>
    public AutomationRunRecord Finish(AutomationRunResult result)
    {
        lock (_gate)
        {
            _closed = true;
            _record = Finish(_record with { Steps = _steps.ToArray(), EvidenceDirectories = _evidence.ToArray() }, result);
            _steps.Clear();
            _steps.AddRange(_record.Steps);
            return _record;
        }
    }

    public static AutomationRunRecord Finish(AutomationRunRecord record, AutomationRunResult result)
    {
        var now = DateTimeOffset.Now;
        return record with
        {
            Revision = record.Revision + 1,
            Status = result.Status, Summary = record.Warning.Length == 0 ? result.Summary
                : "警告：" + record.Warning + "\n" + result.Summary,
            EndedAt = result.Status == AutomationRunStatus.AwaitingReview ? null : now,
            Review = result.Review ?? record.Review, ArtifactPath = result.ArtifactPath ?? record.ArtifactPath,
            EncodedOutput = result.EncodedOutput?.Copy() ?? record.EncodedOutput,
            SubgraphResult = result.SubgraphResult ?? record.SubgraphResult,
            Steps = record.Steps.Select(step => CloseStep(step, result.Status, now, result.Summary)).ToArray()
        };
    }

    private AutomationRunRecord NodeFinished(PregelRunEvent value, out bool persist)
    {
        persist = true;
        var node = value.NodeId?.Value;
        if (node == null || !_stepByNode.TryGetValue(node, out var step))
        {
            // A hidden node (validation, bookkeeping) failed: surface it on the next step that would have run.
            if (value.Error is { } hiddenError && _steps.FirstOrDefault(s => s.State is AutomationStepState.Pending
                    or AutomationStepState.Running) is { } next)
                Update(next.Key, s => s with { State = AutomationStepState.Failed, Error = AutomationRuntime.Explain(hiddenError.Message), EndedAt = value.Timestamp });
            return Commit(true);
        }

        if (value.Error is { } error)
        {
            Update(step.Key, s => s with { State = AutomationStepState.Failed, Error = AutomationRuntime.Explain(error.Message), EndedAt = value.Timestamp });
            return Commit(true);
        }

        if (value.Message == nameof(PregelInvocationOutcome.Cancelled))
        {
            Update(step.Key, s => s with { State = AutomationStepState.Cancelled, EndedAt = value.Timestamp });
            return Commit(true);
        }

        if (value.Message == nameof(PregelInvocationOutcome.Interrupted))
        {
            Update(step.Key, s => s with { State = AutomationStepState.AwaitingReview, EndedAt = null, Progress = null });
            return Commit(true);
        }

        var skipped = value.Message == nameof(PregelInvocationOutcome.Skipped);
        if (skipped)
        {
            var skippedSet = _skippedNodes.TryGetValue(step.Key, out var existing)
                ? existing : _skippedNodes[step.Key] = [];
            skippedSet.Add(node);
            Update(step.Key, s => s with { Detail = SkipReason(value) });
        }

        // Both success and a conditional skip satisfy dependencies.
        if (!skipped && value.Message != nameof(PregelInvocationOutcome.Succeeded)) return Commit(true);

        var finished = _finishedNodes.TryGetValue(step.Key, out var set) ? set : _finishedNodes[step.Key] = [];
        finished.Add(node);
        if (step.NodeIds.All(finished.Contains))
        {
            var allSkipped = _skippedNodes.TryGetValue(step.Key, out var skippedSet) && step.NodeIds.All(skippedSet.Contains);
            Update(step.Key, s => s.State is AutomationStepState.Failed or AutomationStepState.Cancelled ? s
                : s with
                {
                    State = allSkipped ? AutomationStepState.Skipped : AutomationStepState.Succeeded,
                    EndedAt = value.Timestamp, Progress = allSkipped ? null : s.HasProgress ? 1 : s.Progress
                });
        }
        return Commit(true);
    }

    private static string SkipReason(PregelRunEvent value) =>
        value.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("Reason", out var property)
            ? property.GetString() ?? "运行条件不满足" : "运行条件不满足";

    // A retry keeps the previous attempt's error; a fresh first attempt clears it.
    private static AutomationStepRecord Started(AutomationStepRecord step, PregelRunEvent value)
    {
        var attempt = Math.Max(1, value.AttemptNumber ?? 1);
        return step with
        {
            State = AutomationStepState.Running, StartedAt = step.StartedAt ?? value.Timestamp, EndedAt = null,
            Error = null, Progress = null, Detail = "", IsReused = false, Attempt = attempt,
            RetryError = attempt > 1 ? step.RetryError : null
        };
    }

    /// <summary>The failed attempt is not the step's result while a retry is pending.</summary>
    private static AutomationStepRecord Retrying(AutomationStepRecord step, PregelRunEvent value)
    {
        var next = (value.AttemptNumber ?? Math.Max(1, step.Attempt)) + 1;
        var wait = value.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("DelaySeconds", out var seconds)
            && seconds.TryGetDouble(out var number) && double.IsFinite(number) && number >= 1
            ? $"{Math.Ceiling(number):0} 秒后" : "即将";
        return step with
        {
            State = AutomationStepState.Running, EndedAt = null, Error = null, Progress = null,
            RetryError = step.Error ?? step.RetryError, Detail = $"{wait}第 {next} 次尝试"
        };
    }

    private static bool IsReused(PregelRunEvent value) => value.Data is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("Reused", out var reused) && reused.ValueKind == JsonValueKind.True;

    private PregelRunEvent NormalizeReuse(PregelRunEvent value, IReadOnlyList<AutomationStepRecord>? sourceScope = null)
    {
        sourceScope ??= _reuseSource?.Steps ?? [];
        var source = value.NodeId is { } id ? Find(sourceScope, id.Value) : null;
        if (value.Kind == "subgraph.event" && value.Data is { } data && data.Deserialize<PregelRunEvent>() is { } child)
            return value with { Data = JsonSerializer.SerializeToElement(NormalizeReuse(child, source?.Children ?? [])) };
        if (value.Kind != "node.reused") return value;
        if (source is { Children.Count: > 0 })
        {
            for (var i = 0; i < _steps.Count; i++) _steps[i] = Restore(_steps[i]);
            AutomationStepRecord Restore(AutomationStepRecord current) => current.Key == source.Key
                ? current with { Children = source.Children.Select(ReusedSnapshot).ToArray() }
                : current with { Children = current.Children.Select(Restore).ToArray() };
        }
        var message = value.Message == "Checkpoint" ? source?.State switch
        {
            AutomationStepState.Failed => nameof(PregelInvocationOutcome.Failed),
            AutomationStepState.Skipped => nameof(PregelInvocationOutcome.Skipped),
            _ => nameof(PregelInvocationOutcome.Succeeded)
        } : value.Message;
        return value with { Kind = "node.finished", Message = message,
            Data = JsonSerializer.SerializeToElement(new { Reused = true, Reason = source?.Detail }),
            Error = value.Error ?? (message == nameof(PregelInvocationOutcome.Failed) && source?.Error is { } error
                ? new PregelError("reused_failure", error) : null) };

        static AutomationStepRecord? Find(IEnumerable<AutomationStepRecord> steps, string node)
        {
            foreach (var step in steps)
                if (!step.IsStage && step.NodeIds.Contains(node)) return step;
                else if (step.IsStage && Find(step.Children, node) is { } found) return found;
            return null;
        }
        static AutomationStepRecord ReusedSnapshot(AutomationStepRecord step) => step with
        {
            StartedAt = null, EndedAt = null, IsReused = true,
            Children = step.Children.Select(ReusedSnapshot).ToArray()
        };
    }

    private AutomationStep? Step(PregelRunEvent value) =>
        value.NodeId is { } id && _stepByNode.TryGetValue(id.Value, out var step) ? step : null;

    private static AutomationStepRecord CreateStep(AutomationStep step) => new()
    {
        Key = step.Key, Title = step.Title, NodeIds = step.NodeIds, DependsOn = step.DependsOn,
        HasProgress = step.HasProgress, IsStage = step.IsStage, IsRecovery = step.IsRecovery, IsFinally = step.IsFinally,
        RecoveryFrom = step.RecoveryFrom, Children = step.Children.Select(CreateStep).ToArray()
    };

    internal static AutomationStepRecord CloseStep(AutomationStepRecord step, AutomationRunStatus status,
        DateTimeOffset endedAt, string? error) => step with
    {
        State = step.State switch
        {
            AutomationStepState.Pending => status == AutomationRunStatus.AwaitingReview ? AutomationStepState.Pending : AutomationStepState.NotRun,
            AutomationStepState.Queued => status == AutomationRunStatus.AwaitingReview ? AutomationStepState.Queued
                : status == AutomationRunStatus.Cancelled ? AutomationStepState.Cancelled : AutomationStepState.NotRun,
            AutomationStepState.AwaitingReview => status == AutomationRunStatus.AwaitingReview ? AutomationStepState.AwaitingReview
                : status == AutomationRunStatus.Cancelled ? AutomationStepState.Cancelled : step.State,
            AutomationStepState.Running => status == AutomationRunStatus.Cancelled ? AutomationStepState.Cancelled
                : status == AutomationRunStatus.Failed ? AutomationStepState.Failed
                : status == AutomationRunStatus.AwaitingReview ? AutomationStepState.AwaitingReview : AutomationStepState.Succeeded,
            _ => step.State
        },
        EndedAt = status == AutomationRunStatus.AwaitingReview ? step.EndedAt
            : step.State is AutomationStepState.Running or AutomationStepState.Queued or AutomationStepState.AwaitingReview ? endedAt : step.EndedAt,
        Error = step.State == AutomationStepState.Running && status == AutomationRunStatus.Failed ? error : step.Error,
        Children = step.Children.Select(child => CloseStep(child, status, endedAt, error)).ToArray()
    };

    private static IReadOnlyList<AutomationStepRecord> ApplyChildEvent(IReadOnlyList<AutomationStepRecord> steps, PregelRunEvent value) =>
        value.Kind == "mutation.committed" && value.Data is { } expansionData && PregelMutationSnapshot.FromJson(expansionData) is { } mutationSnapshot
        ? MergeSteps(AutomationStepTopology.FromGraph(mutationSnapshot.Graph), steps)
        : steps.Select(step =>
        {
            if (value.Kind == "run.finished" && value.Message != nameof(PregelRunStatus.Interrupted))
                return CloseStep(step, value.Message == nameof(PregelRunStatus.Cancelled) ? AutomationRunStatus.Cancelled
                    : value.Message == nameof(PregelRunStatus.Failed) ? AutomationRunStatus.Failed : AutomationRunStatus.Succeeded,
                    value.Timestamp, (value.Error is { } failure ? AutomationRuntime.Explain(failure.Message) : null));
            if (value.NodeId is not { } node || !step.NodeIds.Contains(node.Value)) return step;
            // A stage groups nodes in the same graph. Forward their events at the same
            // level; only a real subgraph invocation unwraps subgraph.event below.
            if (step.IsStage)
            {
                var children = ApplyChildEvent(step.Children, value);
                var failed = children.FirstOrDefault(child => child.State == AutomationStepState.Failed);
                var cancelled = children.Any(child => child.State == AutomationStepState.Cancelled);
                var interrupted = children.Any(child => child.State == AutomationStepState.AwaitingReview);
                var completed = children.All(child => child.State is AutomationStepState.Succeeded or AutomationStepState.Skipped);
                var allSkipped = completed && children.All(child => child.State == AutomationStepState.Skipped);
                var startedAt = children.Select(child => child.StartedAt).Min();
                return BubbleWaiting(step with
                {
                    Children = children,
                    State = failed != null ? AutomationStepState.Failed
                        : cancelled ? AutomationStepState.Cancelled
                        : interrupted ? AutomationStepState.AwaitingReview
                        : completed ? allSkipped ? AutomationStepState.Skipped : AutomationStepState.Succeeded
                        : startedAt != null ? AutomationStepState.Running : step.State,
                    StartedAt = step.StartedAt ?? startedAt,
                    EndedAt = completed || failed != null || cancelled ? children.Select(child => child.EndedAt).Max() : null,
                    Error = failed?.Error,
                    Detail = value.Kind == "node.output" ? Trim(value.Message)
                        : allSkipped ? SkipReason(value) : step.Detail,
                    Progress = completed && step.HasProgress && !allSkipped ? 1 : step.Progress
                }, step.Children);
            }
            if (value.Kind == "subgraph.event" && value.Data is { } data && data.Deserialize<PregelRunEvent>() is { } child)
                return ApplySubgraphEvent(step, child);
            if (value.Kind == "node.started")
                return Started(step, value) with { StartedAt = value.Timestamp };
            if (value.Kind == "node.retry") return Retrying(step, value);
            if (value.Kind == "node.finished")
                return step with
                {
                    State = value.Message == nameof(PregelInvocationOutcome.Cancelled) ? AutomationStepState.Cancelled
                        : value.Error != null ? AutomationStepState.Failed
                        : value.Message == nameof(PregelInvocationOutcome.Interrupted) ? AutomationStepState.AwaitingReview
                        : value.Message == nameof(PregelInvocationOutcome.Skipped) ? AutomationStepState.Skipped
                        : value.Message == nameof(PregelInvocationOutcome.Succeeded) ? AutomationStepState.Succeeded : step.State,
                    EndedAt = value.Message == nameof(PregelInvocationOutcome.Interrupted) ? null : value.Timestamp,
                    Error = (value.Error is { } failure ? AutomationRuntime.Explain(failure.Message) : null),
                    IsReused = IsReused(value),
                    Detail = value.Message == nameof(PregelInvocationOutcome.Skipped) ? SkipReason(value) : step.Detail
                };
            if (value.Kind == "node.output") return step with { Detail = Trim(value.Message) };
            return PregelProgress.TryRead(value, out var progress)
                ? step with { HasProgress = true, Progress = progress.Fraction, Detail = progress.Detail } : step;
        }).ToArray();

    // Run-level events have no NodeId. Their containing subgraph invocation owns the queue state.
    private static AutomationStepRecord ApplySubgraphEvent(AutomationStepRecord step, PregelRunEvent value)
    {
        var updated = step with { Children = ApplyChildEvent(step.Children, value) };
        if (value.Kind == "run.waiting")
            return updated with { State = AutomationStepState.Queued, Detail = PregelRunLogFormatter.WaitingSummary(value),
                EndedAt = null, Progress = null };
        if (value.Kind == "run.started")
            return updated with { State = AutomationStepState.Running, Detail = "", EndedAt = null,
                StartedAt = step.StartedAt ?? value.Timestamp };
        return BubbleWaiting(updated, step.Children);
    }

    private static AutomationStepRecord BubbleWaiting(AutomationStepRecord step, IReadOnlyList<AutomationStepRecord> previous)
    {
        if (step.State is not (AutomationStepState.Running or AutomationStepState.Queued)) return step;
        var queued = step.Children.Where(child => child.State == AutomationStepState.Queued).ToArray();
        var running = step.Children.Count(child => child.State == AutomationStepState.Running);
        if (queued.Length > 0)
            return step with { State = running > 0 ? AutomationStepState.Running : AutomationStepState.Queued,
                Detail = running > 0 ? $"{running} 项进行中，{queued.Length} 项排队；{queued[0].Detail}" : queued[0].Detail };
        return step.State == AutomationStepState.Queued || previous.Any(child => child.State == AutomationStepState.Queued)
            ? step with { State = AutomationStepState.Running, Detail = "" } : step;
    }

    private static PregelRunEvent Unwrap(PregelRunEvent value)
    {
        for (var depth = 0; depth < 32 && value.Kind == "subgraph.event" && value.Data is { } data
             && data.Deserialize<PregelRunEvent>() is { } child; depth++) value = child;
        return value;
    }

    private static IEnumerable<AutomationStepRecord> QueuedFrontier(IEnumerable<AutomationStepRecord> steps)
    {
        foreach (var step in steps)
            if (step.State == AutomationStepState.Queued && !step.IsStage) yield return step;
            else foreach (var queued in QueuedFrontier(step.Children)) yield return queued;
    }

    private void UpdateRunActivity()
    {
        if (_isFinalizing || _record.Status is not (AutomationRunStatus.Running or AutomationRunStatus.Queued)) return;
        var queued = QueuedFrontier(_steps).ToArray();
        // Child events mean the root graph has entered execution. Resource waits belong
        // to those child invocations; only the root's own run.waiting can queue the task.
        // A failed stage can still contain active siblings, so its state is not a run status.
        _record = _record with
        {
            Status = AutomationRunStatus.Running,
            Summary = queued.Length > 0 ? $"运行中 · {queued.Length} 项等待资源；{queued[0].Detail}" : "运行中"
        };
    }

    private static IReadOnlyList<AutomationStepRecord> MergeSteps(IReadOnlyList<AutomationStep> plan,
        IReadOnlyList<AutomationStepRecord> previous) => plan.Select(step =>
    {
        var old = previous.FirstOrDefault(x => x.Key == step.Key);
        return old is null ? CreateStep(step) : old with {
            Title = step.Title, NodeIds = step.NodeIds, DependsOn = step.DependsOn,
            IsStage = step.IsStage, IsFinally = step.IsFinally, IsRecovery = step.IsRecovery,
            RecoveryFrom = step.RecoveryFrom, Children = MergeSteps(step.Children, old.Children)
        };
    }).ToArray();

    private void Update(string key, Func<AutomationStepRecord, AutomationStepRecord> change)
    {
        var index = _steps.FindIndex(s => s.Key == key);
        if (index >= 0) _steps[index] = change(_steps[index]);
    }

    private AutomationRunRecord Commit(bool _)
    {
        _record = _record with { Steps = _steps.ToArray(), EvidenceDirectories = _evidence.ToArray(), Revision = _record.Revision + 1 };
        return _record;
    }

    /// <summary>Progress and log lines are live-only and limited to ~4 updates per second.</summary>
    private AutomationRunRecord? Throttled()
    {
        // Always keep the live snapshot current, even when notifications are coalesced.
        var record = Commit(false);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastProgressPublish < TimeSpan.FromMilliseconds(250)) return null;
        _lastProgressPublish = now;
        return record;
    }

    private static string Trim(string message)
    {
        var line = message.Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
