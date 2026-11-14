using System.Text.Json;

namespace Heron.Pregel;

public sealed partial class PregelEngine
{
    /// <summary>Reads the committed topology, including expansion instances, without executing any nodes.</summary>
    public async ValueTask<PregelGraphDefinition> GetRunGraphAsync(CompiledPregelGraph graph, PregelRunId runId,
        CancellationToken token = default)
    {
        var checkpoint = await _store.LoadLatestAsync(runId, token).ConfigureAwait(false)
            ?? throw new PregelException("run_not_found", runId.Value);
        EnsureGraphMatches(graph, checkpoint);
        return PregelMutationGraph.Restore(graph, checkpoint).Definition;
    }

    private async ValueTask ExecuteMutationInstanceAsync(PregelMutationInstanceRuntime instance,
        PregelNodeContext context, PregelRunOptions options, CancellationToken token)
    {
        var graph = instance.Mutation.Template;
        var id = instance.Instance.ChildRunId;
        var scope = options.SubgraphScope ?? new(context.Checkpoint.GraphFingerprint, []);
        var childScope = scope with { MutationPath = [.. scope.MutationPath, instance.Mutation.Definition.Id] };
        if (!PregelSubgraphs.IsLocal(instance.Instance.Target, options.ExecutionMachine))
        {
            var executor = options.SubgraphExecutor ?? throw new PregelException("subgraph_executor_missing",
                $"Child '{instance.Instance.Key}' targets '{instance.Instance.Target}', but no subgraph executor is configured.");
            var request = new PregelSubgraphExecutionRequest(context.RunId.Value, context.NodeId.Value,
                context.InvocationId.Value, id.Value, childScope.MutationPath, instance.Instance.Input.Copy(),
                instance.Instance.Target, childScope.RootGraphFingerprint, graph.Definition.Fingerprint)
                {
                    ResumeValue = context.EncodedResume,
                    ParentResourceContext = PregelResourceScope.Current?.Lease.Context ?? options.InheritedResourceContext,
                    ResourceQueueTimeout = options.ResourceQueueTimeout
                };
            var remote = await executor.ExecuteAsync(request, new MutationObserver(context), token,
                options.FinalizationCancellationToken).ConfigureAwait(false);
            if (remote.ChildRunId != id.Value)
                throw new PregelException("subgraph_run_mismatch", "The executor returned another child run's result.");
            ApplySubgraphResult(instance, context, remote, token);
            return;
        }
        var files = new FilePregelRunObserver(Path.Combine(context.WorkingDirectory, "instances"));
        var observer = new CompositePregelRunObserver(files, new MutationObserver(context));
        var childOptions = options with
        {
            Observer = observer, InterruptBefore = null, InterruptAfter = null, SubgraphScope = childScope,
            ResourceAdmission = null, ManualSkip = null
        };
        var checkpoint = await _store.LoadLatestAsync(id, CancellationToken.None).ConfigureAwait(false);
        PregelRunResult result;
        if (checkpoint is null)
        {
            if (context.Reexecution is { } origin)
            {
                // New descendants still belong to the derived execution, including when expansion itself reruns.
                var initial = CreateInitialCheckpoint(graph, id, instance.Mutation.Bind(instance.Instance.Input).Encode(graph));
                var rules = PregelMutexRules.Resolve(graph, initial);
                initial = initial with { MutexKeys = rules.Select(x => x.Key).ToArray(), MutexLimits = PregelMutexRules.Limits(rules),
                    WorkingDirectory = ResolveWorkingDirectory(id, observer),
                    Reexecution = new(context.RunId, context.Checkpoint.CheckpointId, PregelReexecutionMode.FromNode,
                        [graph.Definition.EntryNode], [], origin.WorkspaceRunId, 0) };
                await _store.CreateRunAsync(initial, CancellationToken.None).ConfigureAwait(false);
                result = await RunToStopAsync(graph, initial, childOptions, token).ConfigureAwait(false);
            }
            else result = await StartAsync(graph, id, instance.Mutation.Bind(instance.Instance.Input), childOptions, token).ConfigureAwait(false);
        }
        else
        {
            EnsureGraphMatches(graph, checkpoint);
            if (checkpoint.Status == PregelRunStatus.Completed)
            {
                // A completed child may be referenced by a derived parent. Read its result without touching its evidence.
                context.Notify("node.reused", nameof(PregelInvocationOutcome.Succeeded), new { SourceRunId = id.Value });
            }
            if (checkpoint.Status == PregelRunStatus.Interrupted && checkpoint.Interrupt is { } interrupt)
            {
                var resume = context.EncodedResume;
                if (resume is null)
                {
                    // Another parallel instance may own this resume value; keep this child at its durable stop.
                    context.Emit(new PregelCommand.Interrupt(interrupt.Reason, interrupt.Payload?.Copy()));
                    return;
                }
                result = await ResumeWithValueAsync(graph, id, interrupt.InterruptId, resume,
                    new MutationResumeCodec(resume.CodecId), childOptions, token).ConfigureAwait(false);
            }
            else result = await ResumeAsync(graph, id, childOptions, token).ConfigureAwait(false);
        }
        ApplySubgraphResult(instance, context, SubgraphResult(instance.Mutation, result), token);
    }

    private static void ApplySubgraphResult(PregelMutationInstanceRuntime instance, PregelNodeContext context,
        PregelSubgraphExecutionResult result, CancellationToken token)
    {
        if (result.Status is PregelRunStatus.Interrupted or PregelRunStatus.FinalizationPending)
        {
            context.Emit(new PregelCommand.Interrupt(result.Interrupt?.Reason ?? result.Error?.Message ?? "Child finalization is pending.",
                result.Interrupt?.Payload?.Copy()));
            return;
        }
        if (result.Status == PregelRunStatus.Cancelled) throw new SubgraphCancelledException(result.Error?.Message ?? "Mutation instance cancelled.");
        if (result.Status != PregelRunStatus.Completed)
            throw new PregelException(result.Error?.Code ?? "mutation_instance_failed", result.Error?.Message ?? instance.Instance.Key);
        var output = result.Output;
        if (output == null || output.CodecId != instance.Mutation.Definition.OutputCodecId)
            throw new PregelException("mutation_output_codec", "The child result does not match its compiled output codec.");
        context.MutationSkipReason = instance.Mutation.SkipReason(output);
        context.Emit(new PregelCommand.MutationResult(instance.PlanId, instance.Instance.Key, output));
    }

    private sealed class SubgraphCancelledException(string message) : Exception(message);

    private sealed class MutationResumeCodec(string id) : ICheckpointCodec<PregelEncodedValue>
    {
        public string CodecId => id;
        public byte[] Encode(PregelEncodedValue value) => value.Data;
        public PregelEncodedValue Decode(ReadOnlySpan<byte> bytes) => new(id, bytes.ToArray());
    }
    private sealed class MutationObserver(PregelNodeContext parent) : IPregelRunObserver
    {
        public void OnEvent(PregelRunEvent value) => parent.Notify("subgraph.event", value.Message, value);
    }

    private static PregelCheckpoint ApplyMutationBarrier(CompiledPregelGraph graph, PregelCheckpoint next,
        IReadOnlyDictionary<PregelInvocationId, PregelPendingOutcome> outcomes, int limit)
    {
        if (!outcomes.Values.Any(x => x.Outcome.CommittedCommands
            .Any(c => c is PregelCommand.Mutate or PregelCommand.MutationResult or PregelCommand.CompleteMutation))) return next;
        var plans = next.Mutations.Select(x => x.Copy()).ToList();
        var sends = next.PendingSends.ToList();
        var revision = next.RunGraphRevision;
        foreach (var outcome in outcomes.Values.OrderBy(x => x.Invocation.Path, PregelPathComparer.Instance))
        {
            foreach (var command in outcome.Outcome.CommittedCommands)
            {
                switch (command)
                {
                    case PregelCommand.Mutate expansion:
                        if (plans.Any(x => x.Id == expansion.Plan.Id || x.MutationId == expansion.Plan.MutationId && !x.Completed))
                            throw new PregelException("mutation_conflict", expansion.Plan.MutationId);
                        if (plans.Sum(x => (long)x.Instances.Count) + expansion.Plan.Instances.Count > limit)
                            throw new PregelException("mutation_instance_limit", "Run expansion instance limit exceeded.");
                        if (!graph.Mutations.TryGetValue(expansion.Plan.MutationId, out var runtime)
                            || runtime.Definition.Entry != outcome.Invocation.NodeId)
                            throw new PregelException("mutation_source", "Only the compiled expansion node may submit this mutation.");
                        plans.Add(expansion.Plan.Copy()); revision++;
                        var start = expansion.Plan.Instances.Count == 0 ? new[] { runtime.Definition.Join }
                            : expansion.Plan.Mode == PregelMutationMode.Sequential ? new[] { expansion.Plan.Instances[0].NodeId }
                            : expansion.Plan.Instances.Select(x => x.NodeId).ToArray();
                        for (var i = 0; i < start.Length; i++)
                            sends.Add(new(start[i], new(CheckpointCodecs.Unit.CodecId, CheckpointCodecs.Unit.Encode(default)), expansion.Plan.ParentPath, i));
                        break;
                    case PregelCommand.MutationResult result:
                        var index = plans.FindIndex(x => x.Id == result.PlanId);
                        if (index < 0 || !plans[index].Instances.Any(x => x.Key == result.Key && x.NodeId == outcome.Invocation.NodeId))
                            throw new PregelException("mutation_result_source", "Result does not belong to this instance.");
                        var results = plans[index].Results.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal);
                        if (!results.TryAdd(result.Key, result.Result.Copy()))
                            throw new PregelException("mutation_duplicate_result", result.Key);
                        plans[index] = plans[index] with { Results = results };
                        break;
                    case PregelCommand.CompleteMutation complete:
                        var join = plans.FindIndex(x => x.Id == complete.PlanId);
                        if (join < 0 || graph.Mutations[plans[join].MutationId].Definition.Join != outcome.Invocation.NodeId
                            || plans[join].Results.Count != plans[join].Instances.Count)
                            throw new PregelException("mutation_join_incomplete", complete.PlanId);
                        plans[join] = plans[join] with { Completed = true };
                        break;
                }
            }
        }
        var expanded = next with { Mutations = plans.ToArray(), RunGraphRevision = revision, PendingSends = sends.ToArray() };
        var view = PregelMutationGraph.Restore(graph, expanded);
        var values = expanded.ChannelValues.ToDictionary(x => x.Key, x => x.Value);
        var versions = expanded.ChannelVersions.ToDictionary(x => x.Key, x => x.Value);
        foreach (var channel in view.Channels.Values)
            if (!values.ContainsKey(channel.Definition.Id))
            { values.Add(channel.Definition.Id, channel.InitialValue.Copy()); versions.Add(channel.Definition.Id, 0); }
        return expanded with { ChannelValues = values, ChannelVersions = versions };
    }

    private static void PublishMutationGraph(CompiledPregelGraph graph, PregelCheckpoint checkpoint, IPregelRunObserver? observer)
    {
        if (checkpoint.Mutations.Count == 0) return;
        var view = PregelMutationGraph.Restore(graph, checkpoint).Definition;
        if (observer is PregelRunEventStream stream) stream.UpdateGraph(view);
        observer?.OnEvent(new(checkpoint.RunId, "mutation.committed", $"Execution graph revision {checkpoint.RunGraphRevision}",
            Data: new PregelMutationSnapshot(checkpoint.RunGraphRevision, view).ToJson()));
    }
}
