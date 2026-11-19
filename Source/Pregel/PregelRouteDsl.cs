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

    internal void ConfigureBranchRoute(NodeRef<PregelUnit> node, Func<PregelNodeContext, PregelRoute> select,
        IReadOnlyList<string> keys, PregelVariable<PregelBranchSelection> selection)
    {
        ConfigureRoute(node.Runtime, new(PregelRoutePhase.AfterNode, select,
            keys.Select(PregelRoute.Branch).Append(PregelRoute.End).ToArray(),
            commitBranchSelection: (context, decision) => context.Write(selection, decision switch
            {
                PregelRoute.BranchRoute branch => new(branch.Key, null),
                PregelRoute.SkipRoute skipped => new(null, skipped.Reason),
                _ => new PregelBranchSelection(null, null)
            })));
    }

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

public sealed partial class PregelLinearGraphDsl<TInput>
{
    /// <summary>Selects one named branch from the original input; unselected nodes are skipped, then all paths join.</summary>
    public PregelLinearGraphDsl<TInput> Route(string id, Func<TInput, PregelRoute> select,
        params (string Key, Action<PregelLinearGraphDsl<TInput>> Configure)[] branches)
    {
        ArgumentNullException.ThrowIfNull(select);
        return StartRoute(id, context => select(context.Read(_input)), branches, _input);
    }

    /// <summary>Selects a branch from committed graph state.</summary>
    public PregelLinearGraphDsl<TInput> Route<TState>(string id, PregelVariable<TState> state,
        Func<TState, PregelRoute> select,
        params (string Key, Action<PregelLinearGraphDsl<TInput>> Configure)[] branches)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(select);
        return StartRoute(id, context => select(context.Read(state)), branches, state);
    }

    private PregelLinearGraphDsl<TInput> StartRoute<TState>(string id, Func<PregelNodeContext, PregelRoute> select,
        (string Key, Action<PregelLinearGraphDsl<TInput>> Configure)[] branches, PregelVariable<TState> source)
    {
        var decision = _builder.Node($"route:{PregelId.Validate(id, nameof(id))}", (_, _) => ValueTask.CompletedTask);
        _builder.DeclareRead(decision, source);
        Append(decision);
        _builder.StageRoutingNodes?.Add(decision.Id);
        return ConfigureRouteBranches(id, decision, select, branches);
    }

    internal PregelLinearGraphDsl<TInput> ConfigureRouteBranches(string id, NodeRef<PregelUnit> decision,
        Func<PregelNodeContext, PregelRoute> select,
        (string Key, Action<PregelLinearGraphDsl<TInput>> Configure)[] branches)
    {
        id = PregelId.Validate(id, nameof(id));
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length == 0) throw new ArgumentException("Route requires at least one named branch.", nameof(branches));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            _ = PregelId.Validate(branch.Key, nameof(branches));
            ArgumentNullException.ThrowIfNull(branch.Configure);
            if (!keys.Add(branch.Key)) throw new PregelException("duplicate_route_branch", $"Route '{id}' repeats branch '{branch.Key}'.");
        }
        var prefix = $"route:{id}";
        var selection = _builder.Output($"{prefix}:selection", new PregelBranchSelection(null, "路由尚未选择分支"));
        _builder.ConfigureBranchRoute(decision, select, branches.Select(branch => branch.Key).ToArray(), selection);
        var arrivals = branches.Select(branch => new NodeId($"{prefix}:arrive:{branch.Key}")).ToArray();
        var barrier = _builder.NamedBarrierChannel($"{prefix}:complete", arrivals);
        for (var index = 0; index < branches.Length; index++)
        {
            var existing = _builder.DefinedNodeIds.ToHashSet();
            var branch = new PregelLinearGraphDsl<TInput>(_builder, decision, _input).AsBranch($"{prefix}:branch:{branches[index].Key}");
            branches[index].Configure(branch);
            if (branch._first is null) throw new ArgumentException("Route branches must contain a node.", nameof(branches));
            // The leading When scope goes first so the route's own scope, inserted ahead of it, is checked first.
            branch.CloseBranch();
            _builder.AddRouteScope(_builder.DefinedNodeIds.Where(node => !existing.Contains(node)).ToArray(),
                decision.Id, selection, branches[index].Key);
            branch.ThenContext(arrivals[index].Value, (context, _) =>
            {
                context.Write(barrier, PregelUnit.Value);
                return ValueTask.CompletedTask;
            });
            _builder.BarrierControlNodes.Add(branch._tail.Id);
            _builder.StageRoutingNodes?.Add(branch._tail.Id);
        }
        var join = _builder.Node($"{prefix}:join", (_, _) => ValueTask.CompletedTask);
        _builder.TriggerOn(join, barrier);
        _builder.BarrierControlNodes.Add(join.Id);
        _builder.StageRoutingNodes?.Add(join.Id);
        _tail = join;
        return this;
    }
}

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    /// <summary>Routes on the current node's fresh result; branches consume that result after its barrier commits.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Route(string id, Func<TOutput, PregelRoute> select,
        params (string Key, Action<PregelLinearGraphDsl<TOutput>> Configure)[] branches)
    {
        ArgumentNullException.ThrowIfNull(select);
        if (_resultNode is null) throw new InvalidOperationException("Result routing requires a result-producing node.");
        var output = _output;
        var flow = new PregelLinearGraphDsl<TOutput>(_builder, _resultNode, output)
            .ConfigureRouteBranches(id, _resultNode, context => select(context.ReadResult(output)), branches);
        _tail = flow.Tail;
        _resultNode = null;
        return this;
    }
}
