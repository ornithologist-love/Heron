using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>Projects graph dependencies onto visible steps, traversing hidden routing nodes.</summary>
public static class AutomationStepTopology
{
    /// <summary>One visible step per declared task. Infrastructure nodes are traversed for dependencies.</summary>
    public static IReadOnlyList<AutomationStep> FromGraph(PregelGraphDefinition graph) => Project(graph, []);

    private static IReadOnlyList<AutomationStep> Project(PregelGraphDefinition graph, HashSet<PregelGraphDefinition> ancestors)
    {
        if (!ancestors.Add(graph)) throw new InvalidOperationException($"Recursive subgraph: {graph.GraphId}");
        try
        {
            var steps = graph.Nodes.Values.Where(node => node.Metadata != null).Select(node =>
                new AutomationStep(node.Id.Value, node.Metadata!.Title, [node.Id.Value])
                {
                    Children = node.Metadata.Subgraph is { } child ? Project(child(), ancestors)
                        : node.Metadata.SubgraphDefinition is { } childDefinition ? Project(childDefinition, ancestors)
                        : node.Metadata.IsMutationInstance && graph.Mutations.FirstOrDefault(x => x.Id == node.Metadata.MutationId) is { } mutation
                            ? Project(mutation.Template, ancestors) : []
                }).ToArray();
            var grouped = graph.Stages.Count == 0 ? steps : GroupStages(steps, graph);
            var projected = FromGraph(grouped, graph);
            projected = projected.Select(step => step.IsFinally ? step with
            { RecoveryFrom = projected.Where(source => !source.IsFinally).Select(source => source.Key).ToArray() } : step).ToArray();
            return graph.Recovery is { } recovery
                ? [.. projected, new AutomationStep(recovery.Id.Value, recovery.Metadata.Title, [recovery.Id.Value])
                {
                    DependsOn = [], IsRecovery = true, RecoveryFrom = projected.Select(step => step.Key).ToArray()
                }]
                : projected;
        }
        finally { ancestors.Remove(graph); }
    }

    private static AutomationStep[] GroupStages(AutomationStep[] steps, PregelGraphDefinition graph)
    {
        var memberIds = graph.Stages.SelectMany(stage => stage.NodeIds).Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var grouped = steps.Where(step => !step.NodeIds.Any(memberIds.Contains)).ToList();
        foreach (var stage in graph.Stages)
        {
            var ids = stage.NodeIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var children = steps.Where(step => step.NodeIds.Any(ids.Contains)).ToArray();
            if (children.Length == 0) continue;
            grouped.Add(new AutomationStep("stage:" + stage.Id, stage.Title, stage.NodeIds.Select(id => id.Value).ToArray())
            {
                IsStage = true, IsFinally = stage.IsFinally, Children = FromGraph(children, graph)
            });
        }
        return grouped.ToArray();
    }

    public static IReadOnlyList<AutomationStep> FromGraph(
        IReadOnlyList<AutomationStep> steps, PregelGraphDefinition graph)
    {
        var owners = steps.SelectMany(step => step.NodeIds.Select(node => (node, step.Key)))
            .ToDictionary(pair => pair.node, pair => pair.Key, StringComparer.Ordinal);
        var parents = graph.Nodes.Keys.ToDictionary(id => id.Value, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var edge in graph.Edges) parents[edge.To.Value].Add(edge.From.Value);
        foreach (var node in graph.Nodes.Values)
        foreach (var trigger in node.Triggers)
            if (graph.Channels[trigger] is { Kind: PregelChannelKind.NamedBarrier, RequiredSenders: { } senders })
                foreach (var sender in senders) parents[node.Id.Value].Add(sender.Value);

        var projected = steps.Select(step =>
        {
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string node)
            {
                if (!visited.Add(node)) return;
                if (owners.TryGetValue(node, out var owner) && owner != step.Key)
                {
                    dependencies.Add(owner);
                    return;
                }
                if (parents.TryGetValue(node, out var previous))
                    foreach (var parent in previous) Visit(parent);
            }
            foreach (var node in step.NodeIds) Visit(node);
            return step with { DependsOn = steps.Where(s => dependencies.Contains(s.Key)).Select(s => s.Key).ToArray() };
        }).ToArray();
        return Order(projected, step => step.Key, step => step.DependsOn);
    }

    /// <summary>Applies the same dependency order to persisted history, including older unordered records.</summary>
    public static IReadOnlyList<AutomationStepRecord> InDependencyOrder(IReadOnlyList<AutomationStepRecord> steps) =>
        Order(steps, step => step.Key, step => step.DependsOn);

    private static IReadOnlyList<T> Order<T>(IReadOnlyList<T> steps, Func<T, string> key,
        Func<T, IReadOnlyList<string>?> dependencies)
    {
        var known = steps.Select(key).ToHashSet(StringComparer.Ordinal);
        var remaining = steps.ToList();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<T>(steps.Count);
        while (remaining.Count > 0)
        {
            var index = remaining.FindIndex(step => (dependencies(step) ?? [])
                .All(parent => !known.Contains(parent) || completed.Contains(parent)));
            // Graphs may contain loops. Preserve the remaining declared order rather than inventing dependencies.
            if (index < 0) { ordered.AddRange(remaining); break; }
            var next = remaining[index];
            remaining.RemoveAt(index);
            completed.Add(key(next));
            ordered.Add(next);
        }
        return ordered;
    }
}
