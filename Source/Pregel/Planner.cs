namespace Heron.Pregel;

internal sealed class PregelPlanner
{
    public PregelPreparedSuperstep PrepareNext(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        IReadOnlyList<PregelPendingOutcome> pendingOutcomes)
    {
        var nextSuperstep = checkpoint.Superstep + 1;
        var pendingByLogicalId = pendingOutcomes.ToDictionary(
            outcome => outcome.Invocation.LogicalInvocationId,
            outcome => outcome);
        var candidates = new HashSet<NodeId>();
        foreach (var channelId in checkpoint.UpdatedChannels)
        {
            if (graph.TriggerIndex.TryGetValue(channelId, out var nodes))
            {
                candidates.UnionWith(nodes);
            }
        }

        var invocations = new List<PregelInvocation>();
        bool InPhase(NodeId id) => graph.Definition.Finally is not { } final
            || (checkpoint.Finalization == null ? !final.NodeIds.Contains(id)
                : final.NodeIds.Contains(id) && (checkpoint.Finalization.RetryNodes is not { } retries
                    || retries.Count > 0 && retries[0] == id));
        foreach (var nodeId in candidates.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            if (!InPhase(nodeId)) continue;
            var definition = graph.Definition.Nodes[nodeId];
            var eligibleTriggers = definition.Triggers
                .Where(trigger => IsTriggerEligible(graph, checkpoint, nodeId, trigger))
                .OrderBy(trigger => trigger.Value, StringComparer.Ordinal)
                .ToArray();
            if (eligibleTriggers.Length == 0)
            {
                continue;
            }

            var input = definition.PullInput.Copy();
            invocations.Add(CreateInvocation(
                checkpoint,
                nodeId,
                PregelInvocationKind.Pull,
                ["pull", nodeId.Value, .. eligibleTriggers.Select(trigger => trigger.Value)],
                eligibleTriggers,
                input,
                pendingByLogicalId));
        }

        foreach (var packet in checkpoint.PendingSends
                     .OrderBy(packet => packet.SendIndex)
                     .ThenBy(packet => packet.TargetNodeId.Value, StringComparer.Ordinal))
        {
            if (!InPhase(packet.TargetNodeId)) continue;
            if (!graph.Nodes.ContainsKey(packet.TargetNodeId))
            {
                throw new PregelException("unknown_send_target", $"Send target '{packet.TargetNodeId}' does not exist.");
            }

            invocations.Add(CreateInvocation(
                checkpoint,
                packet.TargetNodeId,
                PregelInvocationKind.Push,
                [.. packet.ParentPath, "push", packet.SendIndex.ToString(), packet.TargetNodeId.Value],
                Array.Empty<ChannelId>(),
                packet.Payload.Copy(),
                pendingByLogicalId));
        }

        var toExecute = new List<PregelInvocation>();
        var replayed = new List<PregelInvocation>();
        foreach (var invocation in invocations.OrderBy(invocation => invocation.Path, PregelPathComparer.Instance))
        {
            if (pendingByLogicalId.TryGetValue(invocation.LogicalInvocationId, out var pending)
                && !(checkpoint.Finalization != null && pending.Outcome is PregelInvocationOutcome.Failed)
                && ShouldReplay(graph, pending))
            {
                replayed.Add(pending.Invocation);
            }
            else
            {
                toExecute.Add(invocation);
            }
        }

        return new PregelPreparedSuperstep(nextSuperstep, toExecute, replayed);
    }

    private static bool ShouldReplay(CompiledPregelGraph graph, PregelPendingOutcome pending) => pending.Outcome switch
    {
        PregelInvocationOutcome.Succeeded or PregelInvocationOutcome.Skipped => true,
        PregelInvocationOutcome.Failed failed => !graph.Nodes[pending.Invocation.NodeId]
            .RetryPolicy.ShouldRetry(failed.Error, pending.Invocation.AttemptNumber),
        _ => false,
    };

    private static bool IsTriggerEligible(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        NodeId nodeId,
        ChannelId trigger)
    {
        if (!checkpoint.ChannelValues.TryGetValue(trigger, out var value))
        {
            return false;
        }

        var currentVersion = checkpoint.ChannelVersions.GetValueOrDefault(trigger);
        var seenVersion = checkpoint.VersionsSeen.TryGetValue(nodeId, out var seen)
            ? seen.GetValueOrDefault(trigger)
            : 0;
        if (currentVersion <= seenVersion)
        {
            return false;
        }

        return trigger == graph.StartChannel.Definition.Id || graph.Channels[trigger].IsAvailable(value);
    }

    private static PregelInvocation CreateInvocation(
        PregelCheckpoint checkpoint,
        NodeId nodeId,
        PregelInvocationKind kind,
        IReadOnlyList<string> path,
        IReadOnlyList<ChannelId> triggers,
        PregelEncodedValue input,
        IReadOnlyDictionary<PregelInvocationId, PregelPendingOutcome> pendingByLogicalId)
    {
        var logicalId = new PregelInvocationId(PregelHash.Sha256(
            checkpoint.RunId.Value,
            checkpoint.Superstep.ToString(),
            kind.ToString(),
            nodeId.Value,
            string.Join("/", path)));
        var attemptNumber = pendingByLogicalId.TryGetValue(logicalId, out var existing)
            ? existing.Outcome is PregelInvocationOutcome.Interrupted
            {
                Position: PregelInterruptPosition.Before,
            }
                ? existing.Invocation.AttemptNumber
                : existing.Invocation.AttemptNumber + 1
            : 1;
        var attemptId = new PregelAttemptId(PregelHash.Sha256(logicalId.Value, attemptNumber.ToString()));
        return new PregelInvocation(
            logicalId,
            attemptId,
            attemptNumber,
            nodeId,
            kind,
            path,
            triggers,
            input,
            existing?.RetryNotBefore);
    }
}
