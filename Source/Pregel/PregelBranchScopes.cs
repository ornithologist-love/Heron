using System.Collections.Frozen;

namespace Heron.Pregel;

public sealed record PregelRouteScopeDefinition(NodeId DecisionNode, ChannelId SelectionChannel, string BranchKey);

internal sealed record PregelBranchSelection(string? Key, string? SkipReason);

internal sealed record PregelRouteScope(PregelRouteScopeDefinition Definition, PregelVariable<PregelBranchSelection> Selection)
{
    internal string? SkipReason(PregelNodeContext context)
    {
        var selected = context.Read(Selection);
        return selected.Key == Definition.BranchKey ? null
            : selected.SkipReason ?? $"未选择分支：{Definition.BranchKey}（已选择 {selected.Key}）";
    }
}

public sealed partial class PregelGraphBuilder
{
    private readonly Dictionary<NodeId, List<PregelRouteScope>> _routeScopes = [];
    // Barrier arrivals must execute even when the containing branch or stage is skipped.
    internal HashSet<NodeId> BarrierControlNodes { get; } = [];

    internal void AddRouteScope(IEnumerable<NodeId> members, NodeId decision,
        PregelVariable<PregelBranchSelection> selection, string key)
    {
        var scope = new PregelRouteScope(new(decision, selection.Channel.Id, key), selection);
        foreach (var id in members.Where(id => !BarrierControlNodes.Contains(id)))
        {
            if (!_routeScopes.TryGetValue(id, out var scopes)) _routeScopes[id] = scopes = [];
            // The enclosing branch is configured after its children and must short-circuit them.
            scopes.Insert(0, scope);
            if (!_reads.TryGetValue(id, out var reads)) _reads[id] = reads = [];
            reads.Add(selection.Channel.Id);
        }
    }

    private IReadOnlyDictionary<NodeId, IReadOnlyList<PregelRouteScope>> CompileRouteScopes() =>
        _routeScopes.ToFrozenDictionary(pair => pair.Key,
            pair => (IReadOnlyList<PregelRouteScope>)Array.AsReadOnly(pair.Value.ToArray()));
}
