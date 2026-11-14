using System.Collections.Concurrent;

namespace Heron.Pregel;

public sealed partial class PregelEngine
{
    private static readonly ConcurrentDictionary<PregelRunId, Task> UnresponsiveRuns = new();
    private sealed class UnresponsiveFinallyException(Task execution) : Exception
    {
        public Task Execution { get; } = execution;
    }

    private async ValueTask<PregelRunResult> RunFinallyAsync(CompiledPregelGraph graph, PregelCheckpoint checkpoint,
        PregelRunOptions options, CancellationToken token)
    {
        if (checkpoint.Status == PregelRunStatus.FinalizationPending)
        {
            var next = checkpoint with { Status = PregelRunStatus.Running,
                Finalization = checkpoint.Finalization! with { RecoveryError = null } };
            var prepared = _planner.PrepareNext(graph, checkpoint, []);
            if (prepared.ToExecute.Count == 0 && next.Finalization.Errors.Count > 0)
            {
                var retries = graph.Definition.Finally!.NodeIds.Where(next.Finalization.Errors.ContainsKey).ToArray();
                next = next with
                {
                    Finalization = next.Finalization with { RetryNodes = retries },
                    UpdatedChannels = [],
                    PendingSends = [new(retries[0], graph.Nodes[retries[0]].PullInput.Copy(), [], 0)]
                };
            }
            checkpoint = await SaveFinallyProgressAsync(checkpoint, next, "finally-resume").ConfigureAwait(false);
        }
        return await RunToStopCoreAsync(graph, checkpoint,
            options with { MaxSupersteps = (int)Math.Min(int.MaxValue, checkpoint.Superstep + options.MaxSupersteps) }, token)
            .ConfigureAwait(false);
    }

    private async ValueTask<PregelRunResult> SuspendFinallyAsync(PregelCheckpoint checkpoint, PregelError error)
    {
        var next = checkpoint with { Status = PregelRunStatus.FinalizationPending,
            Finalization = checkpoint.Finalization! with { RecoveryError = error } };
        next = await SaveFinallyProgressAsync(checkpoint, next, "finally-stopped").ConfigureAwait(false);
        return new(next.Status, next.RunId, next.CheckpointId, next, error);
    }

    private async ValueTask<PregelCheckpoint> SaveFinallyProgressAsync(PregelCheckpoint previous,
        PregelCheckpoint next, string phase)
    {
        next = next with { CheckpointId = new($"{phase}-{Guid.NewGuid():N}"), ParentCheckpointId = previous.CheckpointId };
        // Keep durable successes from an unfinished batch, so an explicit retry does not repeat them.
        var outcomes = await _store.LoadOutcomesAsync(previous.RunId, previous.CheckpointId, CancellationToken.None).ConfigureAwait(false);
        foreach (var outcome in outcomes)
            await _store.PutOutcomeAsync(previous.RunId, outcome with { BaseCheckpointId = next.CheckpointId }, CancellationToken.None).ConfigureAwait(false);
        await _store.CommitBarrierAsync(previous.RunId, new(previous.CheckpointId, next, []), CancellationToken.None).ConfigureAwait(false);
        return next;
    }

    private static IReadOnlyList<PregelWrite> CompletionWrites(CompiledPregelGraph graph, PregelInvocation invocation) =>
        (graph.OutgoingEdges.GetValueOrDefault(invocation.NodeId) ?? []).Select((edge, index) =>
            new PregelWrite(invocation.LogicalInvocationId, invocation.NodeId, edge.ChannelId, index,
                graph.Channels[edge.ChannelId].EncodeUpdateObject(PregelUnit.Value))).ToArray();
}
