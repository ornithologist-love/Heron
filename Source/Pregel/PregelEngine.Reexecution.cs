namespace Heron.Pregel;

public enum PregelReexecutionMode { Continue, FromNode }

/// <summary>Mutation instance ids followed by a node id. Empty path means continue the stopped body.</summary>
public sealed record PregelReexecutionRequest(PregelReexecutionMode Mode, IReadOnlyList<NodeId> NodePath);
public sealed record PregelReusedNode(NodeId NodeId, string Outcome, PregelError? Error = null)
{
    /// <summary>
    /// Outcome of a node committed by an older store that did not retain consumed outcomes: the barrier proves the
    /// dependency completed, not whether it succeeded or was skipped. Hosts may project their own step record.
    /// </summary>
    public const string CommittedWithoutOutcome = "Checkpoint";
}

/// <summary>A validated restore point. Building it reads history only; nothing is created or changed.</summary>
/// <param name="InheritedNodes">Nodes committed by earlier barriers; their state comes from the checkpoint.</param>
/// <param name="ReplayedNodes">Peers of the restored barrier whose durable outcomes are reused instead of executed.</param>
public sealed record PregelReexecutionPreview(PregelCheckpointId SourceCheckpointId, long Superstep,
    IReadOnlyList<PregelReusedNode> InheritedNodes, IReadOnlyList<NodeId> ReplayedNodes);
public sealed record PregelReexecutionOrigin(PregelRunId SourceRunId, PregelCheckpointId SourceCheckpointId,
    PregelReexecutionMode Mode, IReadOnlyList<NodeId> NodePath, IReadOnlyList<PregelReusedNode> InheritedNodes,
    PregelRunId WorkspaceRunId, long Superstep);

public sealed partial class PregelEngine
{
    /// <summary>
    /// Validates a reexecution against the source history without creating a run. Every rejection a later
    /// <see cref="CreateReexecutionAsync"/> would raise for the same history is raised here as well.
    /// </summary>
    public async ValueTask<PregelReexecutionPreview> PreviewReexecutionAsync(CompiledPregelGraph graph, PregelRunId sourceRunId,
        PregelReexecutionRequest request, PregelRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var planned = await PlanReexecutionTreeAsync(graph, sourceRunId, PreviewRunId, request,
            (options ?? new PregelRunOptions()) with { Observer = null }, cancellationToken).ConfigureAwait(false);
        var root = planned[^1];
        return new(root.Reexecution!.SourceCheckpointId, root.Reexecution.Superstep, root.Reexecution.InheritedNodes,
            root.ReplayOutcomes.Select(x => x.Invocation.NodeId).Distinct().ToArray());
    }

    /// <summary>
    /// Creates a new durable run without executing nodes or changing the source. The selected node's
    /// barrier and all later barriers run again; successful peers in that barrier may be replayed.
    /// </summary>
    public async ValueTask<PregelCheckpoint> CreateReexecutionAsync(CompiledPregelGraph graph, PregelRunId sourceRunId,
        PregelRunId newRunId, PregelReexecutionRequest request, PregelRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = PregelId.Validate(newRunId.Value, nameof(newRunId));
        if (newRunId == PreviewRunId) throw new PregelException("reexecution_identity", "重新运行必须使用新的运行标识。");
        var planned = await PlanReexecutionTreeAsync(graph, sourceRunId, newRunId, request, options ?? new PregelRunOptions(),
            cancellationToken).ConfigureAwait(false);
        // Validate the entire tree before creating anything. A failed creation may leave inert orphan runs,
        // but never executes nodes and never changes the source or publishes a half-prepared parent.
        foreach (var checkpoint in planned)
            await _store.CreateRunAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return planned[^1];
    }

    private static readonly PregelRunId PreviewRunId = new("reexecution-preview");

    private async ValueTask<List<PregelCheckpoint>> PlanReexecutionTreeAsync(CompiledPregelGraph graph, PregelRunId sourceRunId,
        PregelRunId newRunId, PregelReexecutionRequest request, PregelRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        if (sourceRunId == newRunId) throw new PregelException("reexecution_identity", "重新运行必须使用新的运行标识。");
        if (!Enum.IsDefined(request.Mode) || request.NodePath is null
            || request.Mode == PregelReexecutionMode.Continue && request.NodePath.Count != 0
            || request.Mode == PregelReexecutionMode.FromNode && request.NodePath.Count == 0)
            throw new PregelException("reexecution_request", "请选择继续运行或明确的重新运行节点。");
        ValidateOptions(graph, options);
        var planned = new List<PregelCheckpoint>();
        await PlanReexecutionAsync(graph, sourceRunId, newRunId, request, options, planned, [], cancellationToken)
            .ConfigureAwait(false);
        return planned;
    }

    private async ValueTask PlanReexecutionAsync(CompiledPregelGraph graph, PregelRunId sourceRunId,
        PregelRunId newRunId, PregelReexecutionRequest request, PregelRunOptions options,
        List<PregelCheckpoint> planned, HashSet<PregelRunId> visiting, CancellationToken token, bool allowStoppedChild = false)
    {
        if (!visiting.Add(sourceRunId) || visiting.Count > 64)
            throw new PregelException("reexecution_cycle", "子运行引用存在循环或嵌套过深。");
        try
        {
            var history = await _store.LoadHistoryAsync(sourceRunId, token).ConfigureAwait(false);
            if (history.Count == 0) throw new PregelException("reexecution_history_missing", "原运行没有检查点历史。");
            var source = history[^1];
            EnsureGraphMatches(graph, source);
            await EnsureLocalReexecutionTreeAsync(source, options.ExecutionMachine, [], token).ConfigureAwait(false);
            if ((source.Status is not (PregelRunStatus.Completed or PregelRunStatus.Failed or PregelRunStatus.Cancelled)
                    && !(allowStoppedChild && source.Status == PregelRunStatus.Running))
                || source.Finalization != null)
                throw new PregelException("reexecution_source_active", "请先等待原运行及收尾结束；待审核运行请使用原审核入口。");
            if (request.Mode == PregelReexecutionMode.Continue && source.Status is not (PregelRunStatus.Failed or PregelRunStatus.Cancelled)
                && !(allowStoppedChild && source.Status == PregelRunStatus.Running))
                throw new PregelException("reexecution_not_stopped", "只有失败或取消的运行可以继续。");
            var body = history.Where(x => x.Status == PregelRunStatus.Running && x.Finalization == null && x.BodyResult == null).ToArray();
            if (body.Length == 0) throw new PregelException("reexecution_history_missing", "未找到正文检查点。");
            PregelCheckpoint point;
            NodeId? target = request.NodePath.Count > 0 ? request.NodePath[0] : null;
            if (target is { } selected)
            {
                var candidates = body.Select(x => (Checkpoint: x, Prepared: _planner.PrepareNext(PregelMutationGraph.Restore(graph, x), x, [])))
                    .Where(x => x.Prepared.ToExecute.Any(invocation => invocation.NodeId == selected))
                    .GroupBy(x => x.Checkpoint.Superstep).Select(x => x.Last()).ToArray();
                if (candidates.Length == 0)
                    throw new PregelException("reexecution_node_unavailable", "此节点尚未被调度，或不在本次检查点范围内；请在更早的原运行选择。");
                if (candidates.Length > 1)
                    throw new PregelException("reexecution_node_ambiguous", "此节点执行过多轮，当前入口尚不支持选择其中一轮。");
                point = candidates[0].Checkpoint;
            }
            else point = body[^1];
            // Finally and recovery may have disposed resources referenced by frozen channel state.
            // Only restarting at the graph entry is generally sound without an explicit resource restoration contract.
            if ((graph.Definition.Finally != null || graph.Recovery != null)
                && point.Superstep != 0)
                throw new PregelException("reexecution_resources_released", "此图已执行收尾，无法保证原资源仍有效。请从图入口重新运行。");
            var expanded = PregelMutationGraph.Restore(graph, point);
            var prepared = _planner.PrepareNext(expanded, point, []);
            var invocations = prepared.ToExecute.Concat(prepared.Replayed).ToArray();
            var oldOutcomes = await _store.LoadOutcomesAsync(sourceRunId, point.CheckpointId, token).ConfigureAwait(false);
            var committed = history.SelectMany(x => x.CommittedOutcomes).Where(x => x.BaseCheckpointId == point.CheckpointId);
            var available = point.ReplayOutcomes.Concat(committed).Concat(oldOutcomes)
                .GroupBy(x => x.Invocation.LogicalInvocationId).Select(x => x.Last()).ToArray();
            var replayed = available.Where(x => x.Invocation.NodeId != target
                && x.Outcome is PregelInvocationOutcome.Succeeded or PregelInvocationOutcome.Skipped
                && !x.Outcome.CommittedCommands.Any(c => c is PregelCommand.Mutate or PregelCommand.Interrupt)).ToArray();
            var replayedNodes = replayed.Select(x => x.Invocation.NodeId).ToHashSet();
            var workingDirectory = ResolveWorkingDirectory(newRunId, options.Observer);
            var plans = new List<PregelMutationPlan>();
            foreach (var plan in point.Mutations)
            {
                var instances = new List<PregelMutationInstance>();
                foreach (var instance in plan.Instances)
                {
                    if (plan.Results.ContainsKey(instance.Key) || replayedNodes.Contains(instance.NodeId))
                    { instances.Add(instance); continue; }
                    var childId = new PregelRunId("reexecution-" + PregelHash.Sha256(newRunId.Value, instance.ChildRunId.Value));
                    var childSource = await _store.LoadLatestAsync(instance.ChildRunId, token).ConfigureAwait(false);
                    if (childSource != null)
                    {
                        var selectedChild = target == instance.NodeId;
                        var scheduledPeer = invocations.Any(x => x.NodeId == instance.NodeId) && !selectedChild;
                        // A completed peer is only read, never resumed or written. Future sequential instances are rerun.
                        if (childSource.Status == PregelRunStatus.Completed && (request.Mode == PregelReexecutionMode.Continue || scheduledPeer))
                        { instances.Add(instance); continue; }
                        var childGraph = graph.Mutations[plan.MutationId].Template;
                        var childRequest = selectedChild && request.NodePath.Count > 1
                            ? new PregelReexecutionRequest(PregelReexecutionMode.FromNode, request.NodePath.Skip(1).ToArray())
                            : request.Mode == PregelReexecutionMode.Continue
                                ? new PregelReexecutionRequest(PregelReexecutionMode.Continue, [])
                                : new PregelReexecutionRequest(PregelReexecutionMode.FromNode, [childGraph.Definition.EntryNode]);
                        var childObserver = new FilePregelRunObserver(Path.Combine(workingDirectory, "instances"));
                        await PlanReexecutionAsync(childGraph, instance.ChildRunId, childId, childRequest,
                            options with { Observer = childObserver }, planned, visiting, token, allowStoppedChild: true).ConfigureAwait(false);
                    }
                    instances.Add(instance with { ChildRunId = childId });
                }
                plans.Add(plan with { Instances = instances.ToArray() });
            }
            if (request.NodePath.Count > 1 && !point.Mutations.SelectMany(x => x.Instances)
                    .Any(x => x.NodeId == target && x.SkipReason is null))
                throw new PregelException("reexecution_subgraph_unsupported", "此子图不是内核管理的持久化实例，不能定位其内部检查点。");
            var index = history.ToList().FindIndex(x => x.CheckpointId == point.CheckpointId);
            var inherited = new List<PregelReusedNode>();
            for (var i = 0; i < index; i++)
            {
                var before = history[i];
                var after = history[i + 1];
                if (before.Finalization != null || before.BodyResult != null || after.Superstep != before.Superstep + 1) continue;
                var outcomes = after.CommittedOutcomes.Where(x => x.BaseCheckpointId == before.CheckpointId).ToArray();
                if (outcomes.Length > 0)
                    inherited.AddRange(outcomes.Select(x => new PregelReusedNode(x.Invocation.NodeId, x.Outcome.GetType().Name,
                        (x.Outcome as PregelInvocationOutcome.Failed)?.Error)));
                else
                {
                    // Older stores did not retain consumed outcomes. A committed barrier proves dependency completion,
                    // not whether it was a success or a conditional skip. The host can project its original step record.
                    var previous = _planner.PrepareNext(PregelMutationGraph.Restore(graph, before), before, []);
                    inherited.AddRange(previous.ToExecute.Select(x => new PregelReusedNode(x.NodeId, PregelReusedNode.CommittedWithoutOutcome,
                        after.ContinuedErrors.GetValueOrDefault(x.NodeId))));
                }
            }
            var origin = new PregelReexecutionOrigin(sourceRunId, point.CheckpointId, request.Mode, request.NodePath.ToArray(),
                (point.Reexecution?.InheritedNodes ?? []).Concat(inherited).GroupBy(x => x.NodeId).Select(x => x.Last()).ToArray(),
                source.Reexecution?.WorkspaceRunId ?? sourceRunId, point.Superstep);
            var mutexKey = "Pregel.Reexecution:" + origin.WorkspaceRunId.Value;
            var fork = PregelSnapshotCopy.Checkpoint(point) with
            {
                RunId = newRunId, CheckpointId = new("reexecution-start"), ParentCheckpointId = null,
                Status = PregelRunStatus.Running, Error = null, BodyResult = null, Finalization = null,
                Interrupt = null, Resume = null, InterruptMarkers = [], ExecutionStarted = false,
                ResourceAdmission = null,
                WorkingDirectory = workingDirectory, Mutations = plans, Reexecution = origin, CommittedOutcomes = [], ReplayOutcomes = [],
                MutexKeys = point.MutexKeys.Append(mutexKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                MutexLimits = point.MutexLimits.Where(x => x.Key != mutexKey).Append(new(mutexKey, 1))
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
            };
            var newInvocations = _planner.PrepareNext(PregelMutationGraph.Restore(graph, fork), fork, []).ToExecute;
            var seeds = new List<PregelPendingOutcome>();
            foreach (var previous in replayed)
            {
                var next = newInvocations.SingleOrDefault(x => x.NodeId == previous.Invocation.NodeId
                    && x.Kind == previous.Invocation.Kind && x.Path.SequenceEqual(previous.Invocation.Path));
                if (next == null) continue;
                var outcome = previous.Outcome switch
                {
                    PregelInvocationOutcome.Succeeded success => success with { Writes = Remap(success.Writes, next) },
                    PregelInvocationOutcome.Skipped skip => skip with { Writes = Remap(skip.Writes, next) },
                    _ => previous.Outcome
                };
                seeds.Add(new(fork.CheckpointId, next, outcome));
            }
            planned.Add(fork with { ReplayOutcomes = seeds });
        }
        finally { visiting.Remove(sourceRunId); }
    }

    private async ValueTask EnsureLocalReexecutionTreeAsync(PregelCheckpoint checkpoint, string machine,
        HashSet<PregelRunId> visited, CancellationToken token)
    {
        if (!visited.Add(checkpoint.RunId)) return;
        foreach (var instance in checkpoint.Mutations.SelectMany(plan => plan.Instances))
        {
            if (!PregelSubgraphs.IsLocal(instance.Target, machine))
                throw new PregelException("remote_subgraph_reexecution_unsupported",
                    "此运行包含远端子图；当前不支持从节点重新运行或派生继续，请使用原运行的恢复入口。");
            if (await _store.LoadLatestAsync(instance.ChildRunId, token).ConfigureAwait(false) is { } child)
                await EnsureLocalReexecutionTreeAsync(child, machine, visited, token).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<PregelWrite> Remap(IReadOnlyList<PregelWrite> writes, PregelInvocation invocation) =>
        writes.Select(x => x with { LogicalInvocationId = invocation.LogicalInvocationId, Value = x.Value.Copy() }).ToArray();
}
