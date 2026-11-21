namespace Heron.Pregel;

/// <summary>The incoming value and the ordered results from a collection expansion.</summary>
public sealed record PregelMapResult<TState, TResult>(TState State, IReadOnlyList<TResult> Items);

internal sealed record PregelBranchTemplate<TItem, TResult>(
    CompiledPregelGraph Graph,
    PregelVariable<TItem> Input,
    Func<PregelRunResult, TResult> Read,
    Func<TResult, string?>? SkipReason)
{
    public Func<TItem, string?>? ItemSkipReason { get; init; }
}

internal sealed record PregelRootGraphCall(PregelMutationRuntime Runtime, IReadOnlySet<NodeId> Nodes);

internal interface IPregelResultSkip<TResult>
{
    void SkipIf(Func<TResult, string?> reason);
}

public sealed partial class PregelGraphBuilder
{
    internal string GraphId => _graphId;
    internal PregelRootGraphCall? RootGraphCall { get; set; }

    internal bool HasControlFlow => _beforeRoutes.Count > 0 || _afterRoutes.Count > 0 || _routeScopes.Count > 0
        || _stages.Count > 0 || _continueOnError.Count > 0 || Finally is not null || _mutexRules.Length > 0;
}

public sealed partial class PregelFlow<TInput, TCurrent>
{
    /// <summary>Runs the named expansion once per item and keeps the incoming value.</summary>
    public PregelFlow<TInput, TCurrent> ForEach<TItem>(string id, Func<TCurrent, IEnumerable<TItem>> items,
        Func<TItem, string> key, Func<PregelFlow<TItem, TItem>, PregelFlow<TItem, TItem>> body,
        PregelMutationMode mode = PregelMutationMode.Sequential)
    {
        var (join, _) = Expand(id, BuildTemplate(id, body), SelectItems(items, key), mode, (_, _) => { });
        return new(_builder, _input, _current, _earlyReturns, join);
    }

    /// <summary>Collects executed item results in selection order and keeps the incoming value alongside them.</summary>
    public PregelFlow<TInput, PregelMapResult<TCurrent, TResult>> Map<TItem, TResult>(string id,
        Func<TCurrent, IEnumerable<TItem>> items, Func<TItem, string> key,
        Func<PregelFlow<TItem, TItem>, PregelFlow<TItem, TResult>> body,
        PregelMutationMode mode = PregelMutationMode.Sequential)
    {
        var output = NextSlot<PregelMapResult<TCurrent, TResult>>($"map:{id}");
        var (join, _) = Expand(id, BuildTemplate(id, body), SelectItems(items, key), mode, (context, results) =>
            output.Write(context, new(_current.Read(context), results)));
        _current.DeclareRead(_builder, join);
        return new(_builder, _input, output, _earlyReturns, join);
    }

    /// <summary>Runs a compiled child graph with the current value and passes on its result.</summary>
    public PregelFlow<TInput, TNext> Graph<TNext>(string id, PregelGraph<TCurrent, TNext> graph,
        Func<TCurrent, string>? target = null, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var directCall = _builder.DefinedNodeIds.Count == 0 && target is null && title is null;
        var label = title ?? id;
        var output = NextSlot<TNext>($"graph:{id}");
        var template = new PregelBranchTemplate<TCurrent, TNext>(graph.Compiled, graph.InputVariable, graph.Read, null);
        var (join, runtime) = Expand(id, template,
            value => [new("child", value, label) { Target = target?.Invoke(value) ?? "" }],
            PregelMutationMode.Sequential, (context, results) => output.Write(context, results.Single()), label);
        if (directCall)
            _builder.RootGraphCall = new(runtime, _builder.DefinedNodeIds.ToHashSet());
        return new(_builder, _input, output, _earlyReturns, join, graphCall: runtime);
    }

    /// <summary>Records the immediately preceding Graph call as skipped when its result gives a reason.</summary>
    public PregelFlow<TInput, TCurrent> SkipIf(Func<TCurrent, string?> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_graphCall is null) throw new InvalidOperationException("SkipIf must immediately follow Graph.");
        _graphCall.SkipIf(value => predicate(value) is { Length: > 0 } reason ? reason : null);
        return new(_builder, _input, _current, _earlyReturns, _tail);
    }

    private PregelBranchTemplate<TItem, TResult> BuildTemplate<TItem, TResult>(string id,
        Func<PregelFlow<TItem, TItem>, PregelFlow<TItem, TResult>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        PregelId.Validate(id, nameof(id));
        var conditions = new List<Func<TItem, string?>>();
        var start = PregelFlow<TItem, TItem>.Start($"{_builder.GraphId}/{id}", conditions);
        var end = body(start);
        if (end is null || !ReferenceEquals(start._builder, end._builder))
            throw new ArgumentException("The item body must return its final chain.", nameof(body));
        var tests = conditions.ToArray();
        var compiled = end.Compile();
        var template = start._builder.RootGraphCall is { Runtime: PregelMutationRuntime<TItem, TResult> call } root
            && !start._builder.HasControlFlow && start._builder.DefinedNodeIds.Count == root.Nodes.Count
                ? new PregelBranchTemplate<TItem, TResult>(call.Template, call.InputVariable, call.ReadResult, call.ResultSkip)
                : new PregelBranchTemplate<TItem, TResult>(compiled.Compiled, compiled.InputVariable, compiled.Read, null);
        return tests.Length == 0 ? template : template with
        {
            ItemSkipReason = item => tests.Select(test => test(item)).FirstOrDefault(reason => reason is not null)
        };
    }

    private (NodeRef<PregelUnit> Join, PregelMutationRuntime<TItem, TResult> Runtime) Expand<TItem, TResult>(
        string id, PregelBranchTemplate<TItem, TResult> template,
        Func<TCurrent, IReadOnlyList<PregelMutationItem<TItem>>> select, PregelMutationMode mode,
        Action<PregelNodeContext, IReadOnlyList<TResult>> publish, string? subgraphTitle = null)
    {
        PregelId.Validate(id, nameof(id));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var entryId = new NodeId($"mutation:{id}:expand");
        var joinId = new NodeId($"mutation:{id}:join");
        var definition = new PregelMutationDefinition(id, entryId, joinId, mode,
            CheckpointCodecs.Default<TItem>().CodecId, CheckpointCodecs.Json<TResult>(allowNull: true).CodecId,
            template.Graph.Definition)
        {
            HasResultSkipCondition = template.SkipReason is not null,
            HasItemSkipCondition = template.ItemSkipReason is not null,
            IsSingleSubgraph = subgraphTitle is not null
        };
        var runtime = new PregelMutationRuntime<TItem, TResult>(definition, template.Graph, template.Input,
            template.Read, publish, template.SkipReason, template.ItemSkipReason);
        _builder.AddMutation(runtime);
        var entry = Append(entryId.Value, (context, _) =>
        {
            runtime.Plan(select(_current.Read(context)), context);
            return ValueTask.CompletedTask;
        });
        _builder.SetMetadata(entryId, subgraphTitle is null
            ? new($"待展开 {id}") { MutationId = id }
            : new(subgraphTitle) { MutationId = id, Subgraph = () => template.Graph.Definition });
        var join = _builder.Node(joinId.Value, (context, _) =>
        {
            runtime.Collect(context);
            return ValueTask.CompletedTask;
        });
        _builder.AddEdge(entry, join);
        if (subgraphTitle is null) _builder.SetMetadata(joinId, new($"汇合 {id}"));
        return (join, runtime);
    }

    private static Func<TCurrent, IReadOnlyList<PregelMutationItem<TItem>>> SelectItems<TItem>(
        Func<TCurrent, IEnumerable<TItem>> items, Func<TItem, string> key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);
        return value => (items(value) ?? throw new PregelException("mutation_items_null", "The item selector returned null."))
            .Select(item => new PregelMutationItem<TItem>(key(item), item)).ToArray();
    }
}
