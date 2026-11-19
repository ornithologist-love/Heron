namespace Heron.Pregel;

/// <summary>The compiled body of a collection branch: one child run per selected item.</summary>
internal sealed record PregelBranchTemplate<TItem, TResult>(CompiledPregelGraph Graph, PregelVariable<TItem> Input,
    Func<PregelRunResult, TResult> Read, Func<TResult, string?>? SkipReason);

/// <summary>The single Graph call a collection branch made from its root, with the nodes the call declared.</summary>
internal sealed record PregelRootGraphCall(PregelMutationRuntime Runtime, IReadOnlySet<NodeId> Nodes);

internal interface IPregelResultSkip<TResult>
{
    void SkipIf(Func<TResult, string?> reason);
}

public sealed partial class PregelGraphBuilder
{
    internal string GraphId => _graphId;
    internal string Revision => _revision;
    internal PregelMutationRuntime Mutation(string id) => _mutations[id];
    internal PregelRootGraphCall? RootGraphCall { get; set; }

    /// <summary>True when the graph declares anything beyond plain nodes, edges and mutations.</summary>
    internal bool HasControlFlow => _beforeRoutes.Count > 0 || _afterRoutes.Count > 0 || _routeScopes.Count > 0
        || _stages.Count > 0 || _continueOnError.Count > 0 || Finally != null || Recovery != null || _mutexRules.Length > 0;
}

internal static class PregelBranchTemplates
{
    public static PregelBranchTemplate<TItem, PregelUnit> Build<TItem>(PregelGraphBuilder parent, string id,
        Action<PregelLinearGraphDsl<TItem>> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var (builder, root, input) = Start<TItem>(parent, id);
        branch(root);
        root.CloseTemplate();
        return Flatten<TItem, PregelUnit>(builder) ?? new(builder.Compile(), input, _ => PregelUnit.Value, null);
    }

    public static PregelBranchTemplate<TItem, TResult> Build<TItem, TResult>(PregelGraphBuilder parent, string id,
        Func<PregelLinearGraphDsl<TItem>, PregelResultGraphDsl<TItem, TResult>> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        var (builder, root, input) = Start<TItem>(parent, id);
        var result = branch(root) ?? throw new ArgumentException("A branch must return its result chain.", nameof(branch));
        if (!result.IsTailOf(builder, builder.DefinedNodeIds.Last()))
            throw new ArgumentException("A branch must return the final result chain it declared.", nameof(branch));
        root.CloseTemplate();
        if (Flatten<TItem, TResult>(builder) is { } flat) return flat;
        var output = result.OutputVariable;
        var compiled = builder.Compile();
        return new(compiled, input, run => compiled.Read(run, output), null);
    }

    private static (PregelGraphBuilder, PregelLinearGraphDsl<TItem>, PregelVariable<TItem>) Start<TItem>(
        PregelGraphBuilder parent, string id)
    {
        var builder = new PregelGraphBuilder($"{parent.GraphId}/{PregelId.Validate(id, nameof(id))}", parent.Revision);
        var input = builder.Variable<TItem>("input");
        var entry = builder.Node("branch:entry", (_, _) => ValueTask.CompletedTask);
        builder.SetEntry(entry);
        return (builder, PregelLinearGraphDsl<TItem>.TemplateRoot(builder, entry, input), input);
    }

    // A branch that only calls one compiled graph runs that graph directly, without an extra nesting level.
    // The resulting rule equals the legacy Mutate of the same graph, so fingerprints stay compatible.
    private static PregelBranchTemplate<TItem, TResult>? Flatten<TItem, TResult>(PregelGraphBuilder builder) =>
        builder.RootGraphCall is { Runtime: PregelMutationRuntime<TItem, TResult> call } root
        && !builder.HasControlFlow && builder.DefinedNodeIds.Count == root.Nodes.Count + 1
            ? new(call.Template, call.InputVariable, call.ReadResult, call.ResultSkip)
            : null;
}

public sealed partial class PregelLinearGraphDsl<TInput>
{
    private const string GateKey = "when";
    private BranchGate? _gate;
    private bool _templateRoot;

    /// <summary>Runs <paramref name="branch"/> once per selected item, all at the same barrier; then continues.</summary>
    /// <param name="items">Picks the collection. Keep it to one property and at most one Where; name anything larger.</param>
    /// <param name="key">Stable business identity of one item, used to recognise it after a restart.</param>
    public PregelLinearGraphDsl<TInput> Parallel<TItem>(string id, Func<TInput, IEnumerable<TItem>> items,
        Func<TItem, string> key, Action<PregelLinearGraphDsl<TItem>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, branch, title, target, PregelMutationMode.Parallel);

    /// <summary>Runs <paramref name="branch"/> once per selected item and returns the results in item order.</summary>
    public PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> Parallel<TItem, TResult>(string id,
        Func<TInput, IEnumerable<TItem>> items, Func<TItem, string> key,
        Func<PregelLinearGraphDsl<TItem>, PregelResultGraphDsl<TItem, TResult>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, branch, title, target, PregelMutationMode.Parallel);

    /// <summary>Runs <paramref name="branch"/> once per selected item, each after the previous one finished.</summary>
    public PregelLinearGraphDsl<TInput> ForEach<TItem>(string id, Func<TInput, IEnumerable<TItem>> items,
        Func<TItem, string> key, Action<PregelLinearGraphDsl<TItem>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, branch, title, target, PregelMutationMode.Sequential);

    /// <summary>Runs <paramref name="branch"/> once per selected item in order and returns the results.</summary>
    public PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> ForEach<TItem, TResult>(string id,
        Func<TInput, IEnumerable<TItem>> items, Func<TItem, string> key,
        Func<PregelLinearGraphDsl<TItem>, PregelResultGraphDsl<TItem, TResult>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, branch, title, target, PregelMutationMode.Sequential);

    private PregelLinearGraphDsl<TInput> Expand<TItem>(string id, Func<TInput, IEnumerable<TItem>> items,
        Func<TItem, string> key, Action<PregelLinearGraphDsl<TItem>> branch, Func<TItem, string>? title,
        Func<TItem, string>? target, PregelMutationMode mode)
    {
        var template = PregelBranchTemplates.Build(_builder, id, branch);
        var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
            .Expand(id, items, key, title, target, template, mode, out var entry);
        return ContinueAfter(entry, result.Tail!);
    }

    private PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> Expand<TItem, TResult>(string id,
        Func<TInput, IEnumerable<TItem>> items, Func<TItem, string> key,
        Func<PregelLinearGraphDsl<TItem>, PregelResultGraphDsl<TItem, TResult>> branch,
        Func<TItem, string>? title, Func<TItem, string>? target, PregelMutationMode mode)
    {
        var template = PregelBranchTemplates.Build(_builder, id, branch);
        var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
            .Expand(id, items, key, title, target, template, mode, out var entry);
        _first ??= entry;
        _tail = result.Tail!;
        return result;
    }

    internal static PregelLinearGraphDsl<TInput> TemplateRoot(PregelGraphBuilder builder,
        NodeRef<PregelUnit> entry, PregelVariable<TInput> input) =>
        new PregelLinearGraphDsl<TInput>(builder, entry, input) { _templateRoot = true }.AsBranch("branch");

    internal void CloseTemplate()
    {
        if (_first is null) throw new ArgumentException("A branch must contain a node.");
        CloseBranch();
    }

    /// <summary>Records the root Graph call of a collection branch so a branch made of only that call can be flattened.</summary>
    private T RootCall<T>(string id, Func<T> call)
    {
        if (!_templateRoot || _first is not null || _builder.RootGraphCall is not null) return call();
        var before = _builder.DefinedNodeIds.ToHashSet();
        var result = call();
        _builder.RootGraphCall = new(_builder.Mutation(id),
            _builder.DefinedNodeIds.Where(node => !before.Contains(node)).ToHashSet());
        return result;
    }

    /// <summary>Marks the start of a branch: a When before its first node gates the whole branch.</summary>
    internal PregelLinearGraphDsl<TInput> AsBranch(string id)
    {
        _gate = new(id);
        return this;
    }

    private bool TryGateBranch(Func<TInput, bool> predicate, string skipReason)
    {
        if (_gate is not { } gate) return false;
        if (gate.Node is null ? _first is not null : !_tail.Id.Equals(gate.Node.Id)) return false;
        if (string.IsNullOrWhiteSpace(skipReason)) throw new ArgumentException("A skip reason is required.", nameof(skipReason));
        if (gate.Node is null)
        {
            var conditions = gate.Conditions;
            var input = _input;
            var selection = _builder.Output($"{gate.Id}:when", new PregelBranchSelection(null, "分支尚未判断"));
            var node = _builder.Node($"{gate.Id}:when", (context, _) =>
            {
                var value = context.Read(input);
                var reason = conditions.FirstOrDefault(condition => !condition.Predicate(value)).Reason;
                context.Write(selection, reason is null ? new PregelBranchSelection(GateKey, null) : new(null, reason));
                return ValueTask.CompletedTask;
            });
            _builder.DeclareRead(node, input);
            Append(node);
            _builder.StageRoutingNodes?.Add(node.Id);
            gate.Node = node;
            gate.Selection = selection;
            gate.Before = _builder.DefinedNodeIds.ToHashSet();
        }
        gate.Conditions.Add((predicate, skipReason));
        return true;
    }

    /// <summary>Scopes every node declared after a leading When; call before the branch's barrier arrival is added.</summary>
    internal void CloseBranch()
    {
        if (_gate is not { Node: { } node, Selection: { } selection } gate) return;
        var members = _builder.DefinedNodeIds.Where(id => !gate.Before.Contains(id)).ToArray();
        if (members.Length == 0) throw new ArgumentException("A branch must contain a node after its leading When.");
        _builder.AddRouteScope(members, node.Id, selection, GateKey);
    }

    private sealed class BranchGate(string id)
    {
        public string Id { get; } = id;
        public List<(Func<TInput, bool> Predicate, string Reason)> Conditions { get; } = [];
        public NodeRef<PregelUnit>? Node { get; set; }
        public PregelVariable<PregelBranchSelection>? Selection { get; set; }
        public HashSet<NodeId> Before { get; set; } = [];
    }
}

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    private IPregelResultSkip<TOutput>? _skipTarget;
    private NodeId? _skipTail;
    internal PregelVariable<TOutput> OutputVariable => _output;

    /// <summary>Runs <paramref name="branch"/> once per item of the current result, all at the same barrier, and keeps the result.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Parallel<TItem>(string id, Func<TOutput, IEnumerable<TItem>> items,
        Func<TItem, string> key, Action<PregelLinearGraphDsl<TItem>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, branch, title, target, PregelMutationMode.Parallel);

    /// <summary>Runs <paramref name="branch"/> once per item of the current result and returns the results in item order.</summary>
    public PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> Parallel<TItem, TResult>(string id,
        Func<TOutput, IEnumerable<TItem>> items, Func<TItem, string> key,
        Func<PregelLinearGraphDsl<TItem>, PregelResultGraphDsl<TItem, TResult>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, PregelBranchTemplates.Build(_builder, id, branch), title, target, PregelMutationMode.Parallel);

    /// <summary>Runs <paramref name="branch"/> once per item of the current result in order, and keeps the result.</summary>
    public PregelResultGraphDsl<TInput, TOutput> ForEach<TItem>(string id, Func<TOutput, IEnumerable<TItem>> items,
        Func<TItem, string> key, Action<PregelLinearGraphDsl<TItem>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, branch, title, target, PregelMutationMode.Sequential);

    /// <summary>Runs <paramref name="branch"/> once per item of the current result in order and returns the results.</summary>
    public PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> ForEach<TItem, TResult>(string id,
        Func<TOutput, IEnumerable<TItem>> items, Func<TItem, string> key,
        Func<PregelLinearGraphDsl<TItem>, PregelResultGraphDsl<TItem, TResult>> branch,
        Func<TItem, string>? title = null, Func<TItem, string>? target = null) =>
        Expand(id, items, key, PregelBranchTemplates.Build(_builder, id, branch), title, target, PregelMutationMode.Sequential);

    /// <summary>
    /// Marks the preceding Graph call as Skipped when <paramref name="reason"/> returns a reason for its result;
    /// null or blank means it ran normally.
    /// </summary>
    public PregelResultGraphDsl<TInput, TOutput> SkipIf(Func<TOutput, string?> reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (_skipTarget is null || _tail is null || !_tail.Id.Equals(_skipTail))
            throw new InvalidOperationException("SkipIf must directly follow a Graph call that returns a result.");
        _skipTarget.SkipIf(result => reason(result) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null);
        _skipTarget = null;
        return this;
    }

    internal PregelResultGraphDsl<TInput, TOutput> WithSkipTarget(IPregelResultSkip<TOutput> target)
    {
        _skipTarget = target;
        _skipTail = _tail?.Id;
        return this;
    }

    private PregelResultGraphDsl<TInput, TOutput> Expand<TItem>(string id, Func<TOutput, IEnumerable<TItem>> items,
        Func<TItem, string> key, Action<PregelLinearGraphDsl<TItem>> branch, Func<TItem, string>? title,
        Func<TItem, string>? target, PregelMutationMode mode)
    {
        var children = Expand(id, items, key, title, target, PregelBranchTemplates.Build(_builder, id, branch), mode, out _);
        return new(_builder, _input, _output, children._tail);
    }

    private PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> Expand<TItem, TResult>(string id,
        Func<TOutput, IEnumerable<TItem>> items, Func<TItem, string> key, PregelBranchTemplate<TItem, TResult> template,
        Func<TItem, string>? title, Func<TItem, string>? target, PregelMutationMode mode) =>
        Expand(id, items, key, title, target, template, mode, out _);

    internal PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> Expand<TItem, TResult>(string id,
        Func<TOutput, IEnumerable<TItem>> items, Func<TItem, string> key, Func<TItem, string>? title,
        Func<TItem, string>? target, PregelBranchTemplate<TItem, TResult> template, PregelMutationMode mode,
        out NodeRef<PregelUnit> entry)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);
        return MutateCore(id, template.Graph, template.Input, template.Read,
            (value, _, _) => Select(items(value), key, title, target), mode, template.SkipReason, out entry);
    }

    private static IReadOnlyList<PregelMutationItem<TItem>> Select<TItem>(IEnumerable<TItem>? items,
        Func<TItem, string> key, Func<TItem, string>? title, Func<TItem, string>? target) =>
        (items ?? throw new PregelException("mutation_items_null", "The item selector returned null."))
        .Select(item => new PregelMutationItem<TItem>(key(item), item, title?.Invoke(item))
            { Target = target?.Invoke(item) ?? "" }).ToArray();
}
