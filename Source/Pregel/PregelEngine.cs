namespace Heron.Pregel;

public sealed class PregelInterruptSelector
{
    private readonly HashSet<NodeId> _nodeIds;

    private PregelInterruptSelector(bool matchesAll, IEnumerable<NodeId> nodeIds)
    {
        MatchesAll = matchesAll;
        _nodeIds = nodeIds.ToHashSet();
        if (!matchesAll && _nodeIds.Count == 0)
        {
            throw new ArgumentException("At least one node ID is required.", nameof(nodeIds));
        }
    }

    public bool MatchesAll { get; }
    public IReadOnlySet<NodeId> NodeIds => _nodeIds;

    public static PregelInterruptSelector All { get; } = new(true, Array.Empty<NodeId>());

    public static PregelInterruptSelector Nodes(params NodeId[] nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        return new PregelInterruptSelector(false, nodeIds);
    }

    internal bool Matches(NodeId nodeId) => MatchesAll || _nodeIds.Contains(nodeId);
}

public sealed record PregelRunOptions(
    int MaxSupersteps = 100,
    int MaxDegreeOfParallelism = 0,
    PregelInterruptSelector? InterruptBefore = null,
    PregelInterruptSelector? InterruptAfter = null,
    IPregelRunObserver? Observer = null,
    CancellationToken FinalizationCancellationToken = default)
{
    /// <summary>The engine used when preparing or running a compiled typed graph.</summary>
    public PregelEngine? Engine { get; init; }
    public int MaxMutationInstances { get; init; } = 10_000;
    public string ExecutionMachine { get; init; } = "";
    public IPregelSubgraphExecutor? SubgraphExecutor { get; init; }
    public PregelSubgraphScope? SubgraphScope { get; init; }
    public IPregelResourceCoordinator? ResourceCoordinator { get; init; }
    public PregelResourceContext? InheritedResourceContext { get; init; }
    public TimeSpan? ResourceQueueTimeout { get; init; }
    public PregelResourceAdmission? ResourceAdmission { get; init; }
    /// <summary>
    /// Nodes and stages this run records as Skipped without invoking them, chosen by the caller (for example a person
    /// running only part of a graph). Not stored in the checkpoint: pass the same value when resuming. Child runs do not inherit it.
    /// </summary>
    public PregelManualSkip? ManualSkip { get; init; }
    /// <summary>Rejects node-authored interrupts with this failure and normal finalization; child lifecycle waits remain interruptible.</summary>
    public PregelError? RejectNodeInterruptsWith { get; init; }
    public string? ResourceLockDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pregel", "resource-locks");
}

/// <summary>Node IDs and stage IDs of the root graph to skip in one run; barrier control nodes cannot be named.</summary>
public sealed record PregelManualSkip(IReadOnlySet<NodeId> Nodes, IReadOnlySet<string> Stages, string Reason = "本次手动跳过")
{
    internal bool Matches(CompiledPregelGraph graph, NodeId node) =>
        Nodes.Contains(node) || Stages.Any(stage => graph.StageGuards.TryGetValue(stage, out var guarded) && guarded.Contains(node));
}

public sealed record PregelRunResult(
    PregelRunStatus Status,
    PregelRunId RunId,
    PregelCheckpointId CheckpointId,
    PregelCheckpoint Checkpoint,
    PregelError? Error = null,
    PregelInterruptState? Interrupt = null);

public sealed partial class PregelEngine
{
    private readonly IPregelCheckpointStore _store;
    private readonly PregelPlanner _planner = new();
    private readonly TimeProvider _timeProvider;

    public PregelEngine(IPregelCheckpointStore? store = null, TimeProvider? timeProvider = null)
    {
        _store = store ?? new InMemoryPregelCheckpointStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<PregelRunResult> StartAsync(
        CompiledPregelGraph graph,
        CancellationToken cancellationToken = default) =>
        StartAsync(graph, PregelVariables.Empty, new PregelRunOptions(), cancellationToken);

    public ValueTask<PregelRunResult> StartAsync(
        CompiledPregelGraph graph,
        PregelRunOptions options,
        CancellationToken cancellationToken = default) =>
        StartAsync(graph, PregelVariables.Empty, options, cancellationToken);

    public ValueTask<PregelRunResult> StartAsync(
        CompiledPregelGraph graph,
        PregelVariables variables,
        CancellationToken cancellationToken = default) =>
        StartAsync(graph, variables, new PregelRunOptions(), cancellationToken);

    public ValueTask<PregelRunResult> StartAsync(
        CompiledPregelGraph graph,
        PregelVariables variables,
        PregelRunOptions options,
        CancellationToken cancellationToken = default) =>
        StartAsync(graph, new PregelRunId(Guid.NewGuid().ToString("N")), variables, options, cancellationToken);

    public async ValueTask<PregelRunResult> StartAsync(
        CompiledPregelGraph graph,
        PregelRunId runId,
        PregelVariables? variables = null,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _ = PregelId.Validate(runId.Value, nameof(runId));
        var selectedOptions = options ?? new PregelRunOptions();
        ValidateOptions(graph, selectedOptions);

        var encodedVariables = (variables ?? PregelVariables.Empty).Encode(graph);
        var initial = CreateInitialCheckpoint(graph, runId, encodedVariables);
        var mutexRules = PregelMutexRules.Resolve(graph, initial);
        initial = initial with { MutexKeys = mutexRules.Select(rule => rule.Key).ToArray(),
            MutexLimits = PregelMutexRules.Limits(mutexRules),
            WorkingDirectory = ResolveWorkingDirectory(runId, selectedOptions.Observer) };
        await _store.CreateRunAsync(initial, CancellationToken.None).ConfigureAwait(false);
        return await RunToStopAsync(graph, initial, selectedOptions, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PregelRunResult> ResumeAsync(
        CompiledPregelGraph graph,
        PregelRunId runId,
        CancellationToken cancellationToken = default) =>
        await ResumeAsync(graph, runId, new PregelRunOptions(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<PregelRunResult> ResumeAsync(
        CompiledPregelGraph graph,
        PregelRunId runId,
        PregelRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var checkpoint = await _store.LoadLatestAsync(runId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");
        EnsureGraphMatches(graph, checkpoint);
        graph = PregelMutationGraph.Restore(graph, checkpoint);
        ValidateOptions(graph, options);
        if (checkpoint.Status != PregelRunStatus.Running && checkpoint.Status != PregelRunStatus.FinalizationPending)
        {
            return new PregelRunResult(
                checkpoint.Status,
                checkpoint.RunId,
                checkpoint.CheckpointId,
                checkpoint,
                Error: checkpoint.Error,
                Interrupt: checkpoint.Interrupt);
        }

        return await RunToStopAsync(graph, checkpoint, options, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<PregelCheckpoint?> GetStateAsync(
        PregelRunId runId,
        CancellationToken cancellationToken = default) =>
        _store.LoadLatestAsync(runId, cancellationToken);

    public ValueTask<PregelRunResult> ResumeAsync(
        CompiledPregelGraph graph,
        PregelRunId runId,
        string interruptId,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ResumeWithValueAsync(
            graph,
            runId,
            interruptId,
            PregelUnit.Value,
            CheckpointCodecs.Unit,
            options,
            cancellationToken);

    public async ValueTask<PregelRunResult> ResumeWithValueAsync<T>(
        CompiledPregelGraph graph,
        PregelRunId runId,
        string interruptId,
        T resumeValue,
        ICheckpointCodec<T>? codec = null,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptId);
        ArgumentNullException.ThrowIfNull(resumeValue);
        var selectedOptions = options ?? new PregelRunOptions();
        var interrupted = await _store.LoadLatestAsync(runId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");
        EnsureGraphMatches(graph, interrupted);
        graph = PregelMutationGraph.Restore(graph, interrupted);
        ValidateOptions(graph, selectedOptions);
        if (interrupted.Status != PregelRunStatus.Interrupted || interrupted.Interrupt is not { } interrupt)
        {
            throw new PregelException("run_not_interrupted", $"Run '{runId}' is not waiting at an interrupt.");
        }

        if (!StringComparer.Ordinal.Equals(interrupt.InterruptId, interruptId))
        {
            throw new PregelException("interrupt_id_mismatch", "The supplied interrupt ID is not the run's active interrupt.");
        }

        var selectedCodec = codec ?? CheckpointCodecs.Default<T>();
        var encodedResumeValue = new PregelEncodedValue(selectedCodec.CodecId, selectedCodec.Encode(resumeValue));
        var resumeCheckpointId = new PregelCheckpointId(
            $"resume-{interrupted.Superstep:D6}-{PregelHash.Sha256(interrupted.CheckpointId.Value, interruptId)[..16]}");
        var resumed = interrupted with
        {
            CheckpointId = resumeCheckpointId,
            ParentCheckpointId = interrupted.CheckpointId,
            Status = PregelRunStatus.Running,
            Interrupt = null,
            Resume = new PregelResumeState(interruptId, interrupt.InvocationId, encodedResumeValue),
        };

        if (interrupt.Position != PregelInterruptPosition.After)
        {
            var oldOutcomes = await _store
                .LoadOutcomesAsync(runId, interrupt.BaseCheckpointId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!oldOutcomes.Any(outcome =>
                    outcome.Invocation.LogicalInvocationId == interrupt.InvocationId
                    && outcome.Outcome is PregelInvocationOutcome.Interrupted))
            {
                throw new PregelException(
                    "interrupt_outcome_missing",
                    "The durable invocation outcome for this interrupt is missing.");
            }

            foreach (var outcome in oldOutcomes)
            {
                await _store.PutOutcomeAsync(
                    runId,
                    outcome with { BaseCheckpointId = resumeCheckpointId },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        await _store.CommitBarrierAsync(
            runId,
            new PregelBarrierCommit(interrupted.CheckpointId, resumed, Array.Empty<PregelInvocationId>()),
            CancellationToken.None).ConfigureAwait(false);
        return await RunToStopAsync(graph, resumed, selectedOptions, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PregelRunResult> RunToStopAsync(
        CompiledPregelGraph graph, PregelCheckpoint checkpoint, PregelRunOptions options,
        CancellationToken cancellationToken)
    {
        options = PregelResourceScope.Resolve(options);
        ValidateOptions(graph, options);
        if (string.IsNullOrEmpty(checkpoint.WorkingDirectory))
            checkpoint = checkpoint with { WorkingDirectory = ResolveWorkingDirectory(checkpoint.RunId, options.Observer) };
        Directory.CreateDirectory(checkpoint.WorkingDirectory);
        var observer = options.Observer;
        if (observer?.GetRunDirectory(checkpoint.RunId) == null)
        {
            var files = new FilePregelRunObserver(Path.GetDirectoryName(checkpoint.WorkingDirectory)!);
            observer = observer == null ? files : new CompositePregelRunObserver(files, observer);
        }
        var events = new PregelRunEventStream(observer, checkpoint.WorkingDirectory, graph.Definition);
        // Resuming cleanup has one bounded budget, including admission to its mutex resources.
        if (UnresponsiveRuns.TryGetValue(checkpoint.RunId, out var outstanding) && !outstanding.IsCompleted)
            throw new PregelException("finalization_still_running", "收尾代码尚未响应停止；原资源仍被保留，不能并发恢复。");
        using var finalizationTimeout = checkpoint.Finalization is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, options.FinalizationCancellationToken);
        finalizationTimeout?.CancelAfter(graph.Definition.Finally!.Timeout);
        var parent = PregelResourceScope.Current;
        var coordinator = options.ResourceCoordinator ?? PregelLocalResourceCoordinator.ForDirectory(options.ResourceLockDirectory);
        IPregelResourceLease? lease = null;
        PregelRunResult? result = null;
        try
        {
            try
            {
                (checkpoint, lease) = await AcquireGraphResourcesAsync(checkpoint, options, coordinator,
                    parent?.Lease.Context ?? options.InheritedResourceContext, events,
                    finalizationTimeout?.Token ?? cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && checkpoint.Finalization is null)
            {
                var latest = await _store.LoadLatestAsync(checkpoint.RunId, CancellationToken.None).ConfigureAwait(false) ?? checkpoint;
                var cancelled = await BeginFinalizationAsync(graph, latest, PregelRunStatus.Cancelled,
                    new PregelError("cancelled", "Run was cancelled while waiting for mutex resources."), CancellationToken.None)
                    .ConfigureAwait(false);
                if (cancelled.Checkpoint.Finalization is not null)
                {
                    var cleanup = await ClearResourceAdmissionAsync(cancelled.Checkpoint.RunId, null).ConfigureAwait(false)
                        ?? cancelled.Checkpoint;
                    result = await RunToStopAsync(graph, cleanup, options with { ResourceAdmission = null }, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    events.OnEvent(new(checkpoint.RunId, "run.finished", cancelled.Status.ToString(), Error: cancelled.Error));
                    result = cancelled;
                }
            }
            catch (PregelException ex) when (ex.Code == "mutex_queue_timeout" && checkpoint.Finalization is null)
            {
                // A graph that never acquired its resources has no body side effects to clean up.
                // Retain the precise failure so a successful sibling cannot turn the parent green.
                var latest = await _store.LoadLatestAsync(checkpoint.RunId, CancellationToken.None).ConfigureAwait(false) ?? checkpoint;
                var error = new PregelError(ex.Code, ex.Message);
                if (latest.ExecutionStarted == false)
                    result = await CommitTerminalAsync(latest, PregelRunStatus.Failed, error, CancellationToken.None).ConfigureAwait(false);
                else
                {
                    result = await BeginFinalizationAsync(graph, latest, PregelRunStatus.Failed, error, CancellationToken.None).ConfigureAwait(false);
                    if (result.Checkpoint.Finalization is not null)
                    {
                        var cleanup = await ClearResourceAdmissionAsync(result.RunId, null).ConfigureAwait(false) ?? result.Checkpoint;
                        result = await RunToStopAsync(graph, cleanup, options with { ResourceAdmission = null }, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                events.OnEvent(new(checkpoint.RunId, "run.finished", result.Status.ToString(), Error: result.Error));
            }
            catch (PregelException ex) when (checkpoint.Finalization is null
                && (ex.Code is "mutex_limit_conflict" or "mutex_limit_invalid"))
            {
                var latest = await _store.LoadLatestAsync(checkpoint.RunId, CancellationToken.None).ConfigureAwait(false) ?? checkpoint;
                result = await CommitTerminalAsync(latest, PregelRunStatus.Failed,
                    new PregelError(ex.Code, ex.Message), CancellationToken.None).ConfigureAwait(false);
                events.OnEvent(new(checkpoint.RunId, "run.finished", result.Status.ToString(), Error: result.Error));
            }
            catch (OperationCanceledException) when (checkpoint.Finalization != null)
            {
                var cancelled = cancellationToken.IsCancellationRequested || options.FinalizationCancellationToken.IsCancellationRequested;
                var latest = await _store.LoadLatestAsync(checkpoint.RunId, CancellationToken.None).ConfigureAwait(false) ?? checkpoint;
                result = await SuspendFinallyAsync(latest, new PregelError(cancelled ? "finalization_cancelled" : "finalization_timeout",
                    cancelled ? "收尾等待资源时被取消。" : "收尾等待资源超过时限。" )).ConfigureAwait(false);
            }
            catch (PregelException ex) when (ex.Code == "mutex_queue_timeout" && checkpoint.Finalization != null)
            {
                var latest = await _store.LoadLatestAsync(checkpoint.RunId, CancellationToken.None).ConfigureAwait(false) ?? checkpoint;
                result = await SuspendFinallyAsync(latest,
                    new PregelError("finalization_timeout", "收尾等待 Mutex 资源超过排队时限。")).ConfigureAwait(false);
            }
            if (result is null)
            {
                PregelResourceScope.Current = new(coordinator, lease!, options.ResourceQueueTimeout);
                result = await RunWithMutexAsync(graph, checkpoint, options, events, cancellationToken,
                    finalizationTimeout?.Token ?? CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (UnresponsiveFinallyException ex)
        {
            var retainedLease = lease;
            lease = null;
            UnresponsiveRuns[checkpoint.RunId] = ex.Execution;
            _ = ex.Execution.ContinueWith(async task =>
            {
                _ = task.Exception;
                try
                {
                    if (retainedLease != null)
                    {
                        await retainedLease.DisposeAsync().ConfigureAwait(false);
                        if (checkpoint.ResourceAdmission is { } retainedAdmission)
                            await ClearResourceAdmissionAsync(checkpoint.RunId, retainedAdmission.RequestId).ConfigureAwait(false);
                    }
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("Pregel resource release remains unconfirmed: {0}", error.Message);
                }
                finally { UnresponsiveRuns.TryRemove(checkpoint.RunId, out _); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            var latest = await _store.LoadLatestAsync(checkpoint.RunId, CancellationToken.None).ConfigureAwait(false) ?? checkpoint;
            result = await SuspendFinallyAsync(latest, new PregelError("finalization_unresponsive",
                "收尾代码未响应超时或取消；已停止等待，资源保留到该代码退出后才能恢复。")).ConfigureAwait(false);
        }
        finally
        {
            PregelResourceScope.Current = parent;
            if (lease != null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                var released = checkpoint.ResourceAdmission is { } releasedAdmission
                    ? await ClearResourceAdmissionAsync(checkpoint.RunId, releasedAdmission.RequestId).ConfigureAwait(false) : null;
                if (result != null && released != null)
                    result = new(released.Status, released.RunId, released.CheckpointId, released, released.Error, released.Interrupt);
            }
        }
        return result!;
    }

    private async ValueTask<(PregelCheckpoint Checkpoint, IPregelResourceLease Lease)> AcquireGraphResourcesAsync(
        PregelCheckpoint checkpoint, PregelRunOptions options, IPregelResourceCoordinator coordinator,
        PregelResourceContext? inherited, PregelRunEventStream events, CancellationToken cancellationToken)
    {
        if (checkpoint.ResourceAdmission is null && options.ResourceAdmission is null && options.ResourceQueueTimeout is null
            && coordinator is PregelLocalResourceCoordinator { IsDefaultLocal: true })
        {
            // Ordinary local graphs keep their existing checkpoint lifecycle. Their process/file
            // leases need no durable remote-request identity unless a queue deadline is configured.
            var request = new PregelResourceRequest("resource-" + Guid.NewGuid().ToString("N"), checkpoint.RunId.Value,
                PregelMutexRules.Restore(checkpoint), inherited, null);
            var localLease = await coordinator.AcquireAsync(request,
                resources => events.OnEvent(new(checkpoint.RunId, "run.waiting", "Waiting for mutex resource capacity.",
                    Data: System.Text.Json.JsonSerializer.SerializeToElement(resources))), cancellationToken).ConfigureAwait(false);
            return (checkpoint, localLease);
        }
        for (var attempt = 0; ; attempt++)
        {
            var admission = checkpoint.ResourceAdmission;
            if (admission is { Granted: true } && coordinator is PregelLocalResourceCoordinator local
                && !local.HasActiveAdmission(admission.RequestId))
            {
                // Process-owned local leases disappear when their process exits. The queue
                // deadline of an already granted segment must not time out a later recovery.
                checkpoint = await ClearResourceAdmissionAsync(checkpoint.RunId, admission.RequestId).ConfigureAwait(false) ?? checkpoint;
                admission = null;
                options = options with { ResourceAdmission = null };
            }
            if (admission is null)
            {
                admission = checkpoint.ExecutionStarted == false ? options.ResourceAdmission : null;
                admission ??= new("resource-" + Guid.NewGuid().ToString("N"), options.ResourceQueueTimeout is { } timeout
                    ? _timeProvider.GetUtcNow() + timeout : null);
                checkpoint = await CommitResourceProgressAsync(checkpoint,
                    checkpoint with { ResourceAdmission = admission }, "resource-admission").ConfigureAwait(false);
            }
            var request = new PregelResourceRequest(admission.RequestId, checkpoint.RunId.Value,
                PregelMutexRules.Restore(checkpoint), inherited, admission.QueueDeadlineUtc);
            IPregelResourceLease lease;
            try
            {
                // The coordinator owns timeout withdrawal. A local WaitAsync deadline could
                // otherwise publish failure while a remote request is still able to start.
                lease = await coordinator.AcquireAsync(request,
                    resources => events.OnEvent(new(checkpoint.RunId, "run.waiting", "Waiting for mutex resource capacity.",
                        Data: System.Text.Json.JsonSerializer.SerializeToElement(resources))), cancellationToken).ConfigureAwait(false);
            }
            catch (PregelException ex) when (ex.Code == "mutex_lease_released" && attempt == 0
                && (checkpoint.ExecutionStarted != false || admission.Granted))
            {
                // A confirmed release may precede the checkpoint clearing it during a crash.
                checkpoint = await ClearResourceAdmissionAsync(checkpoint.RunId, admission.RequestId).ConfigureAwait(false) ?? checkpoint;
                options = options with { ResourceAdmission = null };
                continue;
            }
            try
            {
                if (!admission.Granted)
                    checkpoint = await CommitResourceProgressAsync(checkpoint,
                        checkpoint with { ResourceAdmission = admission with { Granted = true } }, "resource-granted").ConfigureAwait(false);
                return (checkpoint, lease);
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async ValueTask<PregelCheckpoint?> ClearResourceAdmissionAsync(PregelRunId runId, string? requestId)
    {
        var latest = await _store.LoadLatestAsync(runId, CancellationToken.None).ConfigureAwait(false);
        if (latest?.ResourceAdmission is not { } admission || requestId != null && admission.RequestId != requestId) return null;
        return await CommitResourceProgressAsync(latest, latest with { ResourceAdmission = null }, "resource-released").ConfigureAwait(false);
    }

    private async ValueTask<PregelCheckpoint> CommitResourceProgressAsync(
        PregelCheckpoint previous, PregelCheckpoint next, string phase)
    {
        next = next with
        {
            CheckpointId = new PregelCheckpointId($"{phase}-{Guid.NewGuid():N}"),
            ParentCheckpointId = previous.CheckpointId
        };
        // Admission metadata must not discard a node outcome durably written before a
        // process crash. The runnable frontier is unchanged by acquiring/releasing a lease.
        var outcomes = await _store.LoadOutcomesAsync(previous.RunId, previous.CheckpointId, CancellationToken.None).ConfigureAwait(false);
        foreach (var outcome in outcomes)
            await _store.PutOutcomeAsync(previous.RunId, outcome with { BaseCheckpointId = next.CheckpointId }, CancellationToken.None).ConfigureAwait(false);
        await _store.CommitBarrierAsync(previous.RunId,
            new PregelBarrierCommit(previous.CheckpointId, next, Array.Empty<PregelInvocationId>()), CancellationToken.None).ConfigureAwait(false);
        return next;
    }

    private async ValueTask<PregelRunResult> RunWithMutexAsync(
        CompiledPregelGraph graph, PregelCheckpoint checkpoint, PregelRunOptions options,
        PregelRunEventStream events, CancellationToken cancellationToken, CancellationToken finalizationToken)
    {
        if (checkpoint.ExecutionStarted == false && checkpoint.Finalization is null && !cancellationToken.IsCancellationRequested)
            checkpoint = await CommitLifecycleProgressAsync(checkpoint,
                checkpoint with { ExecutionStarted = true }, "execution-started").ConfigureAwait(false);
        events.OnEvent(new(checkpoint.RunId, "run.started", graph.Definition.GraphId));
        // Only invocation/channel failures become execution failures. Store and scheduling
        // exceptions propagate, leaving the last durable checkpoint available to Resume.
        var result = checkpoint.Finalization is null
            ? await RunToStopCoreAsync(graph, checkpoint, options with { Observer = events }, cancellationToken).ConfigureAwait(false)
            : new PregelRunResult(checkpoint.Status, checkpoint.RunId, checkpoint.CheckpointId, checkpoint, checkpoint.Error);
        if (result.Checkpoint.Finalization is not null)
        {
            graph = PregelMutationGraph.Restore(graph, result.Checkpoint);
            events.OnEvent(new(checkpoint.RunId, "run.finalizing", "正在收尾"));
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(finalizationToken, options.FinalizationCancellationToken);
            if (finalizationToken == CancellationToken.None) budget.CancelAfter(graph.Definition.Finally!.Timeout);
            result = await RunFinallyAsync(graph, result.Checkpoint, options with { Observer = events }, budget.Token)
                .ConfigureAwait(false);
            if (result.Checkpoint.Finalization != null && result.Error?.Code == "cancelled")
            {
                var cancelled = options.FinalizationCancellationToken.IsCancellationRequested
                    || checkpoint.Finalization != null && cancellationToken.IsCancellationRequested;
                result = await SuspendFinallyAsync(result.Checkpoint, new PregelError(
                    cancelled ? "finalization_cancelled" : "finalization_timeout",
                    cancelled ? "用户已停止收尾，可从停点恢复。" : "收尾超过执行时限，可从停点恢复。" )).ConfigureAwait(false);
            }
        }
        events.OnEvent(new(result.RunId, "run.finished", result.Status.ToString(), Error: result.Error));
        return result;
    }

    private async ValueTask<PregelRunResult> RunToStopCoreAsync(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        PregelRunOptions options,
        CancellationToken cancellationToken)
    {
        var compiled = graph.MutationBase ?? graph;
        PublishMutationGraph(compiled, checkpoint, options.Observer);
        if (checkpoint.ReplayOutcomes.Count > 0)
            foreach (var replay in checkpoint.ReplayOutcomes)
                await _store.PutOutcomeAsync(checkpoint.RunId, replay with { BaseCheckpointId = checkpoint.CheckpointId }, CancellationToken.None)
                    .ConfigureAwait(false);
        if (checkpoint.Reexecution is { } origin && checkpoint.Superstep == origin.Superstep)
            foreach (var inherited in origin.InheritedNodes)
                options.Observer?.OnEvent(new(checkpoint.RunId, "node.reused", inherited.Outcome, inherited.NodeId,
                    Error: inherited.Error));
        while (true)
        {
            graph = PregelMutationGraph.Restore(compiled, checkpoint);
            EnsureGraphMatches(graph, checkpoint);
            var existing = await _store
                .LoadOutcomesAsync(checkpoint.RunId, checkpoint.CheckpointId, CancellationToken.None)
                .ConfigureAwait(false);
            var prepared = _planner.PrepareNext(graph, checkpoint, existing);
            if (prepared.ToExecute.Count == 0 && prepared.Replayed.Count == 0)
            {
                return await BeginFinalizationAsync(graph,
                    checkpoint,
                    PregelRunStatus.Completed,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
                return await BeginFinalizationAsync(graph, checkpoint, PregelRunStatus.Cancelled,
                    new PregelError("cancelled", "Run was cancelled."), CancellationToken.None).ConfigureAwait(false);

            if (checkpoint.Superstep >= options.MaxSupersteps)
            {
                return await BeginFinalizationAsync(graph,
                    checkpoint,
                    PregelRunStatus.Failed,
                    new PregelError("recursion_limit", $"Run exceeded {options.MaxSupersteps} supersteps."),
                    CancellationToken.None).ConfigureAwait(false);
            }

            var beforeInvocation = FindInterruptInvocation(
                prepared.ToExecute,
                checkpoint,
                options.InterruptBefore,
                PregelInterruptPosition.Before);
            if (beforeInvocation is not null)
            {
                return await InterruptBeforeAsync(checkpoint, beforeInvocation, CancellationToken.None).ConfigureAwait(false);
            }

            await ExecutePreparedAsync(graph, checkpoint, prepared, options, cancellationToken).ConfigureAwait(false);

            // Only seeds copied from the source run are reuse. An ordinary crash recovery also replays durable
            // outcomes, but those belong to this run and were already reported when they finished.
            foreach (var seed in checkpoint.ReplayOutcomes)
            {
                if (!prepared.Replayed.Any(x => x.LogicalInvocationId == seed.Invocation.LogicalInvocationId)) continue;
                var outcome = existing.First(x => x.Invocation.LogicalInvocationId == seed.Invocation.LogicalInvocationId).Outcome;
                if (outcome is PregelInvocationOutcome.Succeeded or PregelInvocationOutcome.Skipped)
                    options.Observer?.OnEvent(new(checkpoint.RunId, "node.reused", outcome.GetType().Name, seed.Invocation.NodeId));
            }

            var outcomes = await _store
                .LoadOutcomesAsync(checkpoint.RunId, checkpoint.CheckpointId, CancellationToken.None)
                .ConfigureAwait(false);
            var requiredIds = prepared.ToExecute
                .Concat(prepared.Replayed)
                .Select(invocation => invocation.LogicalInvocationId)
                .ToHashSet();
            var requiredOutcomes = outcomes
                .Where(outcome => requiredIds.Contains(outcome.Invocation.LogicalInvocationId))
                .ToDictionary(outcome => outcome.Invocation.LogicalInvocationId);

            if (requiredOutcomes.Count != requiredIds.Count)
            {
                if (cancellationToken.IsCancellationRequested)
                    return await BeginFinalizationAsync(graph, checkpoint, PregelRunStatus.Cancelled,
                        new PregelError("cancelled", "Run was cancelled before all invocations finished."), CancellationToken.None).ConfigureAwait(false);
                throw new PregelException("missing_outcome", "Not all prepared invocations produced a durable outcome.");
            }

            var interrupted = requiredOutcomes.Values.FirstOrDefault(outcome => outcome.Outcome is PregelInvocationOutcome.Interrupted);
            if (interrupted is not null)
            {
                if (options.RejectNodeInterruptsWith is { } interruptFailure
                    && graph.Nodes[interrupted.Invocation.NodeId] is not PregelMutationInstanceRuntime)
                    return await BeginFinalizationAsync(graph, checkpoint, PregelRunStatus.Failed,
                        interruptFailure, CancellationToken.None).ConfigureAwait(false);
                var outcome = (PregelInvocationOutcome.Interrupted)interrupted.Outcome;
                var interrupt = new PregelInterruptState(
                    PregelHash.Sha256(interrupted.Invocation.LogicalInvocationId.Value, "interrupt"),
                    checkpoint.CheckpointId,
                    interrupted.Invocation.NodeId,
                    interrupted.Invocation.LogicalInvocationId,
                    PregelInterruptPosition.Within,
                    outcome.Reason,
                    outcome.Payload);
                return await CommitInterruptedAsync(checkpoint, interrupt, CancellationToken.None).ConfigureAwait(false);
            }

            if (checkpoint.Finalization == null)
            {
                var errors = checkpoint.ContinuedErrors.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var outcome in requiredOutcomes.Values.OrderBy(outcome => outcome.Invocation.Path, PregelPathComparer.Instance))
                    if (outcome.Outcome is PregelInvocationOutcome.Failed failure
                        && graph.Definition.Nodes[outcome.Invocation.NodeId].ContinueOnError)
                        errors[outcome.Invocation.NodeId] = failure.Error;
                checkpoint = checkpoint with { ContinuedErrors = errors };
            }
            if (checkpoint.Finalization != null && graph.Definition.Finally != null)
            {
                var errors = checkpoint.Finalization.Errors.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var outcome in requiredOutcomes.Values)
                    if (outcome.Outcome is PregelInvocationOutcome.Failed failure) errors[outcome.Invocation.NodeId] = failure.Error;
                    else if (outcome.Outcome is PregelInvocationOutcome.Succeeded or PregelInvocationOutcome.Skipped)
                        errors.Remove(outcome.Invocation.NodeId);
                checkpoint = checkpoint with { Finalization = checkpoint.Finalization with { Errors = errors } };
            }
            var failed = requiredOutcomes.Values.FirstOrDefault(outcome => outcome.Outcome is PregelInvocationOutcome.Failed
                && !graph.Definition.Nodes[outcome.Invocation.NodeId].ContinueOnError);
            if (failed is not null)
            {
                var error = ((PregelInvocationOutcome.Failed)failed.Outcome).Error;
                return await BeginFinalizationAsync(graph, checkpoint, PregelRunStatus.Failed, error, CancellationToken.None).ConfigureAwait(false);
            }

            var cancelled = requiredOutcomes.Values.FirstOrDefault(outcome => outcome.Outcome is PregelInvocationOutcome.Cancelled);
            if (cancelled is not null)
            {
                return await BeginFinalizationAsync(graph,
                    checkpoint,
                    PregelRunStatus.Cancelled,
                    new PregelError("cancelled", ((PregelInvocationOutcome.Cancelled)cancelled.Outcome).Reason),
                    CancellationToken.None).ConfigureAwait(false);
            }

            PregelCheckpoint next;
            try
            {
                next = ApplyMutationBarrier(graph, ApplyBarrier(graph, checkpoint, prepared, requiredOutcomes), requiredOutcomes, options.MaxMutationInstances);
            }
            catch (PregelException exception)
            {
                return await BeginFinalizationAsync(graph,
                    checkpoint,
                    PregelRunStatus.Failed,
                    new PregelError(exception.Code, exception.Message),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await BeginFinalizationAsync(graph,
                    checkpoint,
                    PregelRunStatus.Failed,
                    new PregelError(
                        "channel_update_exception",
                        exception.Message,
                        new Dictionary<string, string>
                        {
                            ["exception_type"] = exception.GetType().FullName ?? exception.GetType().Name,
                        }),
                    CancellationToken.None).ConfigureAwait(false);
            }

            var commit = new PregelBarrierCommit(
                checkpoint.CheckpointId,
                next,
                requiredIds.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray());

            var afterInvocation = FindInterruptInvocation(
                prepared.ToExecute.Concat(prepared.Replayed)
                    .Where(invocation => requiredOutcomes[invocation.LogicalInvocationId].Outcome
                        is PregelInvocationOutcome.Succeeded or PregelInvocationOutcome.Skipped
                        || requiredOutcomes[invocation.LogicalInvocationId].Outcome is PregelInvocationOutcome.Failed
                        && graph.Definition.Nodes[invocation.NodeId].ContinueOnError),
                checkpoint,
                options.InterruptAfter,
                PregelInterruptPosition.After);
            if (afterInvocation is not null)
            {
                var marker = CreateInterruptMarker(afterInvocation, PregelInterruptPosition.After);
                var interrupt = CreateConfiguredInterrupt(
                    checkpoint,
                    afterInvocation,
                    PregelInterruptPosition.After,
                    $"Interrupted after node '{afterInvocation.NodeId}'.");
                var interruptedCheckpoint = next with
                {
                    CheckpointId = new PregelCheckpointId($"{next.CheckpointId.Value}-interrupted-after"),
                    Status = PregelRunStatus.Interrupted,
                    Interrupt = interrupt,
                    InterruptMarkers = AppendMarker(next.InterruptMarkers, marker),
                };
                await _store.CommitBarrierAsync(
                    checkpoint.RunId,
                    commit with { NextCheckpoint = interruptedCheckpoint },
                    CancellationToken.None).ConfigureAwait(false);
                if (interruptedCheckpoint.RunGraphRevision != checkpoint.RunGraphRevision)
                    PublishMutationGraph(compiled, interruptedCheckpoint, options.Observer);
                return new PregelRunResult(
                    PregelRunStatus.Interrupted,
                    interruptedCheckpoint.RunId,
                    interruptedCheckpoint.CheckpointId,
                    interruptedCheckpoint,
                    Interrupt: interrupt);
            }

            await _store.CommitBarrierAsync(checkpoint.RunId, commit, CancellationToken.None).ConfigureAwait(false);
            if (next.RunGraphRevision != checkpoint.RunGraphRevision) PublishMutationGraph(compiled, next, options.Observer);
            checkpoint = next;
        }
    }

    private async ValueTask ExecutePreparedAsync(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        PregelPreparedSuperstep prepared,
        PregelRunOptions options,
        CancellationToken cancellationToken)
    {
        var semaphore = options.MaxDegreeOfParallelism > 0
            ? new SemaphoreSlim(options.MaxDegreeOfParallelism, options.MaxDegreeOfParallelism)
            : null;
        var tasks = prepared.ToExecute.Select(async invocation =>
        {
            if (semaphore is not null)
            {
                try { await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }

            try
            {
                if (checkpoint.Finalization != null && graph.Definition.Finally != null)
                    await Task.Run(async () => await ExecuteWithRetryAsync(graph, checkpoint, invocation, options, cancellationToken)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                else
                    await ExecuteWithRetryAsync(graph, checkpoint, invocation, options, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore?.Release();
            }
        });
        var execution = Task.WhenAll(tasks);
        // Timed-out user code may still be releasing this semaphore after the caller has returned.
        _ = execution.ContinueWith(_ => semaphore?.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (checkpoint.Finalization == null || graph.Definition.Finally == null)
        {
            await execution.ConfigureAwait(false);
            return;
        }
        try { await execution.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await execution.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { throw new UnresponsiveFinallyException(execution); }
        }
    }

    private async ValueTask<PregelRunResult> InterruptBeforeAsync(
        PregelCheckpoint checkpoint,
        PregelInvocation invocation,
        CancellationToken cancellationToken)
    {
        var reason = $"Interrupted before node '{invocation.NodeId}'.";
        await _store.PutOutcomeAsync(
            checkpoint.RunId,
            new PregelPendingOutcome(
                checkpoint.CheckpointId,
                invocation,
                new PregelInvocationOutcome.Interrupted(
                    reason,
                    null,
                    PregelInterruptPosition.Before)),
            CancellationToken.None).ConfigureAwait(false);
        var interrupt = CreateConfiguredInterrupt(
            checkpoint,
            invocation,
            PregelInterruptPosition.Before,
            reason);
        return await CommitInterruptedAsync(
            checkpoint,
            interrupt,
            cancellationToken,
            CreateInterruptMarker(invocation, PregelInterruptPosition.Before)).ConfigureAwait(false);
    }

    private static PregelInvocation? FindInterruptInvocation(
        IEnumerable<PregelInvocation> invocations,
        PregelCheckpoint checkpoint,
        PregelInterruptSelector? selector,
        PregelInterruptPosition position)
    {
        if (selector is null)
        {
            return null;
        }

        return invocations
            .OrderBy(invocation => invocation.Path, PregelPathComparer.Instance)
            .FirstOrDefault(invocation =>
                selector.Matches(invocation.NodeId)
                && !checkpoint.InterruptMarkers.Any(marker =>
                    marker.Position == position
                    && marker.InvocationId == invocation.LogicalInvocationId));
    }

    private static PregelInterruptState CreateConfiguredInterrupt(
        PregelCheckpoint checkpoint,
        PregelInvocation invocation,
        PregelInterruptPosition position,
        string reason) =>
        new(
            PregelHash.Sha256(invocation.LogicalInvocationId.Value, "interrupt", position.ToString()),
            checkpoint.CheckpointId,
            invocation.NodeId,
            invocation.LogicalInvocationId,
            position,
            reason,
            null);

    private static PregelInterruptMarker CreateInterruptMarker(
        PregelInvocation invocation,
        PregelInterruptPosition position) =>
        new(position, invocation.NodeId, invocation.LogicalInvocationId);

    private static IReadOnlyList<PregelInterruptMarker> AppendMarker(
        IReadOnlyList<PregelInterruptMarker> markers,
        PregelInterruptMarker marker) =>
        markers
            .Append(marker)
            .Distinct()
            .OrderBy(item => item.InvocationId.Value, StringComparer.Ordinal)
            .ThenBy(item => item.Position)
            .ToArray();

    private async ValueTask ExecuteWithRetryAsync(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        PregelInvocation initialInvocation,
        PregelRunOptions options,
        CancellationToken cancellationToken)
    {
        var observer = options.Observer;
        var retryPolicy = graph.Definition.Nodes[initialInvocation.NodeId].RetryPolicy;
        var invocation = initialInvocation;
        while (true)
        {
            PregelInvocationOutcome outcome;
            try
            {
                await WaitUntilAsync(invocation.NotBefore, cancellationToken).ConfigureAwait(false);
                outcome = await ExecuteSafelyAsync(graph, checkpoint, invocation, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = new PregelInvocationOutcome.Cancelled("Cancelled while waiting to retry.");
            }
            observer?.OnEvent(new(checkpoint.RunId, "node.finished", outcome.GetType().Name,
                invocation.NodeId, invocation.LogicalInvocationId, invocation.AttemptId, invocation.AttemptNumber,
                Error: (outcome as PregelInvocationOutcome.Failed)?.Error,
                Data: outcome is PregelInvocationOutcome.Skipped skipped
                    ? System.Text.Json.JsonSerializer.SerializeToElement(new { skipped.Reason }) : null));
            var shouldRetry = outcome is PregelInvocationOutcome.Failed failed
                && retryPolicy.ShouldRetry(failed.Error, invocation.AttemptNumber);
            var delay = shouldRetry ? RetryDelay(retryPolicy, invocation.AttemptNumber) : TimeSpan.Zero;
            var retryNotBefore = shouldRetry && delay > TimeSpan.Zero
                ? _timeProvider.GetUtcNow() + delay
                : (DateTimeOffset?)null;
            await _store.PutOutcomeAsync(
                checkpoint.RunId,
                new PregelPendingOutcome(checkpoint.CheckpointId, invocation, outcome, retryNotBefore),
                CancellationToken.None).ConfigureAwait(false);

            if (!shouldRetry)
            {
                return;
            }

            observer?.OnEvent(new(checkpoint.RunId, "node.retry", $"Retry after {delay}.",
                invocation.NodeId, invocation.LogicalInvocationId, invocation.AttemptId, invocation.AttemptNumber,
                Data: System.Text.Json.JsonSerializer.SerializeToElement(new { DelaySeconds = delay.TotalSeconds })));
            var nextAttempt = invocation.AttemptNumber + 1;
            invocation = invocation with
            {
                AttemptNumber = nextAttempt,
                AttemptId = new PregelAttemptId(PregelHash.Sha256(invocation.LogicalInvocationId.Value, nextAttempt.ToString())),
                NotBefore = retryNotBefore,
            };
        }
    }

    private async ValueTask WaitUntilAsync(DateTimeOffset? notBefore, CancellationToken cancellationToken)
    {
        if (notBefore is not { } deadline)
        {
            return;
        }

        var delay = deadline - _timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<PregelInvocationOutcome> ExecuteSafelyAsync(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        PregelInvocation invocation,
        PregelRunOptions options,
        CancellationToken cancellationToken)
    {
        var observer = options.Observer;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new PregelNodeContext(graph, checkpoint, invocation, observer, maxMutationInstances: options.MaxMutationInstances);
            observer?.OnEvent(new(checkpoint.RunId, "node.started", invocation.NodeId.Value,
                invocation.NodeId, invocation.LogicalInvocationId, invocation.AttemptId, invocation.AttemptNumber));
            var node = graph.Nodes[invocation.NodeId];
            string? skipReason = null;
            var beforeRoute = PregelRoute.Continue;
            if (graph.StageConditions.TryGetValue(invocation.NodeId, out var stageCondition)
                && !stageCondition.Predicate(context))
                skipReason = stageCondition.SkipReason;
            if (skipReason is null && options.ManualSkip is { } manual && manual.Matches(graph, invocation.NodeId))
                skipReason = manual.Reason;
            if (skipReason is null && graph.RouteScopes.TryGetValue(invocation.NodeId, out var scopes))
            {
                foreach (var scope in scopes)
                    if ((skipReason = scope.SkipReason(context)) is not null) break;
            }
            if (skipReason is null && graph.BeforeRoutes.TryGetValue(invocation.NodeId, out var route))
            {
                beforeRoute = route.Select(context);
                if (beforeRoute is PregelRoute.SkipRoute skippedRoute) skipReason = skippedRoute.Reason;
            }
            if (skipReason is null)
            {
                if (node is PregelMutationInstanceRuntime instance)
                    await ExecuteMutationInstanceAsync(instance, context, options, cancellationToken).ConfigureAwait(false);
                else await node.InvokeAsync(invocation.Input, context, cancellationToken).ConfigureAwait(false);
            }
            if (checkpoint.Finalization != null) cancellationToken.ThrowIfCancellationRequested();
            skipReason ??= context.MutationSkipReason;
            if (skipReason is null && !context.Commands.Any(command => command is PregelCommand.Interrupt))
            {
                var afterRoute = graph.AfterRoutes.TryGetValue(invocation.NodeId, out var resultRoute)
                    ? resultRoute.Select(context) : PregelRoute.Continue;
                if (beforeRoute is not PregelRoute.ContinueRoute && afterRoute is not PregelRoute.ContinueRoute)
                    throw new PregelException("conflicting_routes", $"Node '{invocation.NodeId}' selected destinations both before and after execution.");
                PregelRouteRuntime.Apply(beforeRoute is PregelRoute.ContinueRoute ? afterRoute : beforeRoute, context);
            }
            var interrupt = context.Commands.OfType<PregelCommand.Interrupt>().FirstOrDefault();
            if (interrupt is not null)
            {
                return new PregelInvocationOutcome.Interrupted(interrupt.Reason, interrupt.Payload);
            }

            var writes = context.Writes.ToList();
            var gotos = context.Commands.OfType<PregelCommand.Goto>().ToArray();
            var sends = context.Commands.OfType<PregelCommand.Send>().ToArray();
            var ends = context.Commands.OfType<PregelCommand.End>().ToArray();
            if (ends.Length > 0 && gotos.Length > 0)
            {
                throw new PregelException(
                    "conflicting_flow_commands",
                    $"Node '{invocation.NodeId}' cannot request End and Goto in the same invocation.");
            }

            if (ends.Length > 0 && sends.Length > 0)
            {
                throw new PregelException(
                    "conflicting_flow_commands",
                    $"Node '{invocation.NodeId}' cannot request End and Send in the same invocation.");
            }

            var outgoing = graph.OutgoingEdges.GetValueOrDefault(invocation.NodeId) ?? Array.Empty<EdgeRuntime>();
            // A later skipped pass through this rule must bypass historical instances from earlier expansions.
            if (skipReason is not null && graph.Mutations.Values.Any(x => x.Definition.Entry == invocation.NodeId))
                outgoing = (graph.MutationBase ?? graph).OutgoingEdges.GetValueOrDefault(invocation.NodeId) ?? [];
            var outgoingTargets = outgoing.Select(edge => edge.Edge.To).ToHashSet();
            var invalidSend = sends.FirstOrDefault(command => !outgoingTargets.Contains(command.TargetNodeId));
            if (invalidSend is not null)
            {
                throw new PregelException(
                    "invalid_send_target",
                    $"Node '{invocation.NodeId}' cannot send to '{invalidSend.TargetNodeId}' because the graph has no such outgoing edge.");
            }

            var sendTargets = sends.Select(command => command.TargetNodeId).ToHashSet();
            var selectedEdges = context.Commands.Any(x => x is PregelCommand.Mutate) || ends.Length > 0
                ? Array.Empty<EdgeRuntime>()
                : gotos.Length == 0
                    ? outgoing.Where(edge => !sendTargets.Contains(edge.Edge.To)).ToArray()
                    : gotos.Select(gotoCommand =>
                        outgoing.FirstOrDefault(edge => edge.Edge.To == gotoCommand.TargetNodeId)
                        ?? throw new PregelException(
                            "invalid_goto",
                            $"Node '{invocation.NodeId}' has no outgoing edge to '{gotoCommand.TargetNodeId}'."))
                        .Where(edge => !sendTargets.Contains(edge.Edge.To))
                        .DistinctBy(edge => edge.ChannelId)
                        .ToArray();
            foreach (var edge in selectedEdges)
            {
                var channel = graph.Channels[edge.ChannelId];
                writes.Add(new PregelWrite(
                    invocation.LogicalInvocationId,
                    invocation.NodeId,
                    edge.ChannelId,
                    writes.Count,
                    channel.EncodeUpdateObject(PregelUnit.Value)));
            }

            if (skipReason is not null) return new PregelInvocationOutcome.Skipped(skipReason, writes)
            {
                Commands = context.Commands.Where(command => command is PregelCommand.MutationResult
                    or PregelCommand.CompleteMutation).ToArray()
            };
            return new PregelInvocationOutcome.Succeeded(
                writes,
                context.Commands.Where(command => command is PregelCommand.Send or PregelCommand.Mutate
                    or PregelCommand.MutationResult or PregelCommand.CompleteMutation).ToArray());
        }
        catch (SubgraphCancelledException exception)
        {
            return new PregelInvocationOutcome.Cancelled(exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PregelInvocationOutcome.Cancelled("Invocation was cancelled.");
        }
        catch (PregelException exception)
        {
            return new PregelInvocationOutcome.Failed(new PregelError(exception.Code, exception.Message));
        }
        catch (Exception exception)
        {
            return new PregelInvocationOutcome.Failed(new PregelError(
                "node_exception",
                exception.Message,
                new Dictionary<string, string> { ["exception_type"] = exception.GetType().FullName ?? exception.GetType().Name }));
        }
    }

    private static PregelCheckpoint ApplyBarrier(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        PregelPreparedSuperstep prepared,
        IReadOnlyDictionary<PregelInvocationId, PregelPendingOutcome> outcomes)
    {
        var values = checkpoint.ChannelValues.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
        var versions = checkpoint.ChannelVersions.ToDictionary(pair => pair.Key, pair => pair.Value);
        var seen = checkpoint.VersionsSeen.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<ChannelId, long>)pair.Value.ToDictionary(item => item.Key, item => item.Value));
        var updated = new HashSet<ChannelId>();
        var allInvocations = prepared.ToExecute.Concat(prepared.Replayed).ToArray();

        var consumedTriggers = new HashSet<ChannelId>();
        foreach (var invocation in allInvocations)
        {
            var nodeSeen = seen.TryGetValue(invocation.NodeId, out var existingSeen)
                ? existingSeen.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            foreach (var trigger in invocation.Triggers)
            {
                nodeSeen[trigger] = checkpoint.ChannelVersions.GetValueOrDefault(trigger);
                if (invocation.Kind == PregelInvocationKind.Pull && consumedTriggers.Add(trigger))
                {
                    var consumed = graph.Channels[trigger].Consume(values[trigger]);
                    if (consumed.Changed)
                    {
                        values[trigger] = consumed.Value;
                        versions[trigger] = versions.GetValueOrDefault(trigger) + 1;
                        if (consumed.IsAvailable)
                        {
                            updated.Add(trigger);
                        }
                    }
                }
            }

            seen[invocation.NodeId] = nodeSeen;
        }

        var writes = outcomes.Values
            .OrderBy(outcome => outcome.Invocation.Path, PregelPathComparer.Instance)
            .SelectMany(outcome => outcome.Outcome switch
            {
                PregelInvocationOutcome.Succeeded succeeded => succeeded.Writes.OrderBy(write => write.WriteIndex),
                PregelInvocationOutcome.Skipped skipped => skipped.Writes.OrderBy(write => write.WriteIndex),
                PregelInvocationOutcome.Failed when graph.Definition.Nodes[outcome.Invocation.NodeId].ContinueOnError
                    => CompletionWrites(graph, outcome.Invocation),
                _ => Enumerable.Empty<PregelWrite>()
            })
            .ToArray();
        foreach (var group in writes.GroupBy(write => write.ChannelId).OrderBy(group => group.Key.Value, StringComparer.Ordinal))
        {
            if (!graph.Channels.TryGetValue(group.Key, out var channel))
            {
                throw new PregelException("unknown_channel", $"Write targets unknown channel '{group.Key}'.");
            }

            var current = values.GetValueOrDefault(group.Key) ?? channel.InitialValue;
            var applied = channel.Apply(current, group.ToArray());
            values[group.Key] = applied.Value;
            versions[group.Key] = versions.GetValueOrDefault(group.Key) + 1;
            if (applied.IsAvailable)
            {
                updated.Add(group.Key);
            }
        }

        var sends = outcomes.Values
            .OrderBy(outcome => outcome.Invocation.Path, PregelPathComparer.Instance)
            .SelectMany(outcome => outcome.Outcome is PregelInvocationOutcome.Succeeded succeeded
                ? succeeded.Commands.OfType<PregelCommand.Send>().Select((command, index) => new PregelSendPacket(
                    command.TargetNodeId,
                    command.Payload.Copy(),
                    outcome.Invocation.Path,
                    index))
                : [])
            .ToArray();
        var finalization = checkpoint.Finalization;
        if (finalization?.RetryNodes is { } retries)
        {
            var completed = allInvocations.Select(invocation => invocation.NodeId).ToHashSet();
            var remaining = retries.Where(id => !completed.Contains(id)).ToArray();
            finalization = finalization with { RetryNodes = remaining };
            // Retry only failed cleanup steps; already completed successors retain their durable results.
            sends = remaining.Length == 0 ? [] : [new(remaining[0], graph.Nodes[remaining[0]].PullInput.Copy(), [], 0)];
            updated.Clear();
        }
        var nextSuperstep = checkpoint.Superstep + 1;
        return new PregelCheckpoint(
            checkpoint.RunId,
            new PregelCheckpointId($"checkpoint-{nextSuperstep:D6}"),
            checkpoint.CheckpointId,
            checkpoint.GraphFingerprint,
            nextSuperstep,
            PregelRunStatus.Running,
            values,
            versions,
            seen,
            updated.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray(),
            sends,
            checkpoint.InterruptMarkers.ToArray())
            {
                MutexKeys = checkpoint.MutexKeys.ToArray(),
                MutexLimits = checkpoint.MutexLimits.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                ResourceAdmission = checkpoint.ResourceAdmission,
                WorkingDirectory = checkpoint.WorkingDirectory,
                ExecutionStarted = checkpoint.ExecutionStarted,
                BodyResult = checkpoint.BodyResult,
                ContinuedErrors = checkpoint.ContinuedErrors,
                Mutations = checkpoint.Mutations,
                RunGraphRevision = checkpoint.RunGraphRevision,
                Error = checkpoint.Error,
                Finalization = finalization,
                Reexecution = checkpoint.Reexecution,
                CommittedOutcomes = outcomes.Values.Select(PregelSnapshotCopy.PendingOutcome).ToArray()
            };
    }

    private static string ResolveWorkingDirectory(PregelRunId runId, IPregelRunObserver? observer) =>
        Path.GetFullPath(observer?.GetRunDirectory(runId) ?? new FilePregelRunObserver(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pregel"))
            .GetRunDirectory(runId));

    private static PregelCheckpoint CreateInitialCheckpoint(
        CompiledPregelGraph graph,
        PregelRunId runId,
        IReadOnlyDictionary<ChannelId, PregelEncodedValue> variables)
    {
        var values = graph.Channels.ToDictionary(pair => pair.Key, pair => pair.Value.InitialValue.Copy());
        var versions = graph.Channels.Keys.ToDictionary(id => id, _ => 0L);
        var startId = graph.StartChannel.Definition.Id;
        var startWrite = new PregelWrite(
            new PregelInvocationId("__start"),
            graph.Definition.EntryNode,
            startId,
            0,
            graph.StartChannel.EncodeUpdateObject(PregelUnit.Value));
        values[startId] = graph.StartChannel.Apply(values[startId], [startWrite]).Value;
        versions[startId] = 1;
        foreach (var (channelId, value) in variables)
        {
            // Variable channels are last-value with one codec, so the encoded value is a valid state.
            // Assign directly: the placeholder initial state (default(T), e.g. null) is never decoded.
            values[channelId] = value.Copy();
            versions[channelId] = 1;
        }

        return new PregelCheckpoint(
            runId,
            new PregelCheckpointId("checkpoint-000000"),
            null,
            graph.Definition.Fingerprint,
            0,
            PregelRunStatus.Running,
            values,
            versions,
            new Dictionary<NodeId, IReadOnlyDictionary<ChannelId, long>>(),
            [startId],
            Array.Empty<PregelSendPacket>(),
            Array.Empty<PregelInterruptMarker>()) { ExecutionStarted = false };
    }

    private async ValueTask<PregelRunResult> BeginFinalizationAsync(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        PregelRunStatus status,
        PregelError? error,
        CancellationToken cancellationToken)
    {
        if (graph.Definition.Finally is { } final)
        {
            if (checkpoint.Finalization != null)
            {
                if (status == PregelRunStatus.Completed && checkpoint.Finalization.Errors.Count == 0)
                    return await CommitTerminalAsync(checkpoint, checkpoint.Finalization.Status,
                        checkpoint.Finalization.Error, CancellationToken.None).ConfigureAwait(false);
                return await SuspendFinallyAsync(checkpoint, error ?? new PregelError("finalization_failed", "部分收尾步骤失败，请重试收尾。"))
                    .ConfigureAwait(false);
            }
            if (status == PregelRunStatus.Cancelled)
            {
                // Stopping a body after a failed commit must not erase its already durable failure.
                var outcomes = await _store.LoadOutcomesAsync(checkpoint.RunId, checkpoint.CheckpointId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (outcomes.FirstOrDefault(outcome => outcome.Outcome is PregelInvocationOutcome.Failed
                    && !graph.Definition.Nodes[outcome.Invocation.NodeId].ContinueOnError)?.Outcome
                    is PregelInvocationOutcome.Failed originalFailure)
                {
                    status = PregelRunStatus.Failed;
                    error = originalFailure.Error;
                }
            }
            var pendingFinally = checkpoint with
            {
                Status = PregelRunStatus.Running, Error = error, BodyResult = new(status, error),
                Finalization = new(status, error), UpdatedChannels = [],
                PendingSends = [new(final.EntryNode, graph.Nodes[final.EntryNode].PullInput.Copy(), [], 0)],
                Interrupt = null, Resume = null
            };
            pendingFinally = await CommitLifecycleProgressAsync(checkpoint, pendingFinally, "finally-pending").ConfigureAwait(false);
            return new(pendingFinally.Status, pendingFinally.RunId, pendingFinally.CheckpointId, pendingFinally, error);
        }
        return await CommitTerminalAsync(checkpoint, status, error, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PregelCheckpoint> CommitLifecycleProgressAsync(
        PregelCheckpoint previous, PregelCheckpoint next, string phase)
    {
        next = next with
        {
            CheckpointId = new PregelCheckpointId($"{phase}-{Guid.NewGuid():N}"),
            ParentCheckpointId = previous.CheckpointId,
        };
        await _store.CommitBarrierAsync(previous.RunId,
            new PregelBarrierCommit(previous.CheckpointId, next, Array.Empty<PregelInvocationId>()),
            CancellationToken.None).ConfigureAwait(false);
        return next;
    }

    private async ValueTask<PregelRunResult> CommitTerminalAsync(
        PregelCheckpoint checkpoint,
        PregelRunStatus status,
        PregelError? error,
        CancellationToken cancellationToken)
    {
        var terminal = checkpoint with
        {
            CheckpointId = new PregelCheckpointId($"{checkpoint.CheckpointId.Value}-{status.ToString().ToLowerInvariant()}"),
            ParentCheckpointId = checkpoint.CheckpointId,
            Status = status,
            Error = error,
            Finalization = null,
        };
        await _store.CommitBarrierAsync(
            checkpoint.RunId,
            new PregelBarrierCommit(checkpoint.CheckpointId, terminal, Array.Empty<PregelInvocationId>()),
            cancellationToken).ConfigureAwait(false);
        return new PregelRunResult(status, terminal.RunId, terminal.CheckpointId, terminal, error);
    }

    private async ValueTask<PregelRunResult> CommitInterruptedAsync(
        PregelCheckpoint checkpoint,
        PregelInterruptState interrupt,
        CancellationToken cancellationToken,
        PregelInterruptMarker? marker = null)
    {
        var interrupted = checkpoint with
        {
            CheckpointId = new PregelCheckpointId($"{checkpoint.CheckpointId.Value}-interrupted"),
            ParentCheckpointId = checkpoint.CheckpointId,
            Status = PregelRunStatus.Interrupted,
            Interrupt = interrupt,
            InterruptMarkers = marker is null
                ? checkpoint.InterruptMarkers.ToArray()
                : AppendMarker(checkpoint.InterruptMarkers, marker),
        };
        await _store.CommitBarrierAsync(
            checkpoint.RunId,
            new PregelBarrierCommit(checkpoint.CheckpointId, interrupted, Array.Empty<PregelInvocationId>()),
            cancellationToken).ConfigureAwait(false);
        return new PregelRunResult(
            PregelRunStatus.Interrupted,
            interrupted.RunId,
            interrupted.CheckpointId,
            interrupted,
            Interrupt: interrupt);
    }

    internal static void EnsureGraphMatches(CompiledPregelGraph graph, PregelCheckpoint checkpoint)
    {
        if (!graph.MatchesFingerprint(checkpoint.GraphFingerprint))
        {
            throw new PregelException("graph_fingerprint_mismatch", "The supplied graph does not match the persisted run.");
        }
    }

    private static TimeSpan RetryDelay(PregelRetryPolicy policy, int completedAttemptNumber)
    {
        var initial = policy.Delay ?? TimeSpan.Zero;
        if (initial <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var milliseconds = initial.TotalMilliseconds * Math.Pow(policy.BackoffMultiplier, completedAttemptNumber - 1);
        var delay = TimeSpan.FromMilliseconds(milliseconds);
        return policy.MaxDelay is { } maxDelay && delay > maxDelay ? maxDelay : delay;
    }

    private static void ValidateOptions(CompiledPregelGraph graph, PregelRunOptions options)
    {
        if (options.ResourceQueueTimeout is { } queueTimeout && queueTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "ResourceQueueTimeout must be positive.");
        if (options.ResourceAdmission is { } admission)
            ArgumentException.ThrowIfNullOrWhiteSpace(admission.RequestId);
        if (options.MaxMutationInstances < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxMutationInstances must be at least one.");
        if (options.MaxSupersteps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSupersteps must be at least one.");
        }

        if (options.MaxDegreeOfParallelism < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDegreeOfParallelism cannot be negative.");
        }

        if (options.ManualSkip is { } manual)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(manual.Reason);
            foreach (var node in manual.Nodes)
                if (!graph.Nodes.ContainsKey(node) || graph.BarrierControls.Contains(node))
                    throw new PregelException("invalid_manual_skip", $"Node '{node}' is not a skippable node of this graph.");
            foreach (var stage in manual.Stages)
                if (!graph.StageGuards.ContainsKey(stage))
                    throw new PregelException("invalid_manual_skip", $"Stage '{stage}' is not defined in this graph.");
        }
        ValidateInterruptSelector(graph, options.InterruptBefore, nameof(options.InterruptBefore));
        ValidateInterruptSelector(graph, options.InterruptAfter, nameof(options.InterruptAfter));
    }

    private static void ValidateInterruptSelector(
        CompiledPregelGraph graph,
        PregelInterruptSelector? selector,
        string optionName)
    {
        if (selector is null || selector.MatchesAll)
        {
            return;
        }

        var unknown = selector.NodeIds
            .Where(nodeId => !graph.Nodes.ContainsKey(nodeId))
            .OrderBy(nodeId => nodeId.Value, StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new PregelException(
                "unknown_interrupt_node",
                $"{optionName} contains nodes that do not belong to this graph: {string.Join(", ", unknown)}.");
        }
    }
}
