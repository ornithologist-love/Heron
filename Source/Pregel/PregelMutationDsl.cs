namespace Heron.Pregel;

public static class PregelMutationDsl
{
    public static PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> Mutate<TInput, TItem, TResult>(
        this PregelResultGraphDsl<TInput, IReadOnlyList<PregelMutationItem<TItem>>> flow,
        string id, PregelGraph<TItem, TResult> template, PregelMutationMode mode = PregelMutationMode.Sequential,
        Func<TResult, string?>? skipReason = null) =>
        flow.Mutate(id, template, items => items, mode, skipReason);
}

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    /// <summary>Compiles an expansion rule; item count and inputs are bound only after the upstream barrier.</summary>
    public PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> Mutate<TItem, TResult>(string id,
        PregelGraph<TItem, TResult> template, Func<TOutput, IReadOnlyList<PregelMutationItem<TItem>>> select,
        PregelMutationMode mode = PregelMutationMode.Sequential, Func<TResult, string?>? skipReason = null) =>
        MutateCore(id, template, Plan(select), mode, skipReason, out _);

    private static Func<TOutput, PregelNodeContext, CancellationToken, IReadOnlyList<PregelMutationItem<TItem>>> Plan<TItem>(
        Func<TOutput, IReadOnlyList<PregelMutationItem<TItem>>> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        return (value, _, _) => select(value);
    }

    internal PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> MutateCore<TItem, TResult>(string id,
        PregelGraph<TItem, TResult> template,
        Func<TOutput, PregelNodeContext, CancellationToken, IReadOnlyList<PregelMutationItem<TItem>>> select,
        PregelMutationMode mode, Func<TResult, string?>? skipReason, out NodeRef<PregelUnit> entry,
        string? subgraphTitle = null, PregelVariable<IReadOnlyList<TResult>>? into = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        return MutateCore(id, template.Compiled, template.InputVariable, template.Read, select, mode,
            skipReason, out entry, subgraphTitle, into);
    }

    internal PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> MutateCore<TItem, TResult>(string id,
        CompiledPregelGraph template, PregelVariable<TItem> input, Func<PregelRunResult, TResult> readResult,
        Func<TOutput, PregelNodeContext, CancellationToken, IReadOnlyList<PregelMutationItem<TItem>>> select,
        PregelMutationMode mode, Func<TResult, string?>? skipReason, out NodeRef<PregelUnit> entry,
        string? subgraphTitle = null, PregelVariable<IReadOnlyList<TResult>>? into = null,
        Func<TItem, string?>? itemSkipReason = null)
    {
        _ = PregelId.Validate(id, nameof(id));
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(select);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var entryId = new NodeId("mutation:" + id + ":expand");
        var joinId = new NodeId("mutation:" + id + ":join");
        var output = into ?? _builder.Output<IReadOnlyList<TResult>>("mutation:" + id + ":result", Array.Empty<TResult>());
        var definition = new PregelMutationDefinition(id, entryId, joinId, mode,
            CheckpointCodecs.Default<TItem>().CodecId, CheckpointCodecs.Json<TResult>(allowNull: true).CodecId, template.Definition)
            { HasResultSkipCondition = skipReason is not null, IsSingleSubgraph = subgraphTitle != null,
                HasItemSkipCondition = itemSkipReason is not null };
        var runtime = new PregelMutationRuntime<TItem, TResult>(definition, template, input, readResult, output, skipReason, itemSkipReason);
        _builder.AddMutation(runtime);
        entry = _builder.Node(entryId.Value, (context, token) => {
            runtime.Plan(select(context.Read(_output), context, token), context); return ValueTask.CompletedTask;
        });
        var join = _builder.Node(joinId.Value, (context, _) => { runtime.Collect(context); return ValueTask.CompletedTask; });
        _builder.SetMetadata(entryId, subgraphTitle == null ? new("待展开 " + id) { MutationId = id }
            : new(subgraphTitle) { MutationId = id, Subgraph = () => template.Definition });
        if (subgraphTitle == null) _builder.SetMetadata(joinId, new("汇合 " + id));
        _builder.DeclareRead(entry, _output);
        _builder.AddEdge(_tail, entry);
        _builder.AddEdge(entry, join);
        return new(_builder, _input, output, join);
    }

    /// <summary>Invokes one complete typed child graph; target selection is persisted with its input before execution.</summary>
    public PregelResultGraphDsl<TInput, TResult> Subgraph<TItem, TResult>(string id,
        PregelGraph<TItem, TResult> template, Func<TOutput, TItem> select,
        string target = "", string? title = null) =>
        Subgraph(id, template, select, _ => target, title);

    /// <summary>Chooses the child input and target from the upstream committed result, without performing IO.</summary>
    public PregelResultGraphDsl<TInput, TResult> Subgraph<TItem, TResult>(string id,
        PregelGraph<TItem, TResult> template, Func<TOutput, TItem> select,
        Func<TOutput, string> target, string? title = null) =>
        SubgraphCore(id, template, select, target, title, out _);

    internal PregelResultGraphDsl<TInput, TResult> SubgraphCore<TItem, TResult>(string id,
        PregelGraph<TItem, TResult> template, Func<TOutput, TItem> select,
        Func<TOutput, string> target, string? title, out NodeRef<PregelUnit> entry)
    {
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(target);
        var label = title ?? id;
        var children = MutateCore(id, template, (value, _, _) => [new PregelMutationItem<TItem>("child", select(value), label)
            { Target = target(value) }], PregelMutationMode.Sequential, skipReason: null, out entry, subgraphTitle: label);
        var output = _builder.Output<TResult>("subgraph:" + id + ":result", default!);
        var complete = _builder.Node("subgraph:" + id + ":result", (context, _) =>
        {
            context.Write(output, context.Read(children._output).Single());
            return ValueTask.CompletedTask;
        });
        _builder.DeclareRead(complete, children._output);
        _builder.AddEdge(children._tail!, complete);
        return new PregelResultGraphDsl<TInput, TResult>(_builder, _input, output, complete)
            .WithSkipTarget((PregelMutationRuntime<TItem, TResult>)_builder.Mutation(id));
    }

    /// <summary>Runs a child without a declared result and preserves this flow's committed output.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Subgraph<TItem>(string id,
        PregelGraph<TItem> template, Func<TOutput, TItem> select, string target = "", string? title = null) =>
        Subgraph(id, template, select, _ => target, title);

    public PregelResultGraphDsl<TInput, TOutput> Subgraph<TItem>(string id,
        PregelGraph<TItem> template, Func<TOutput, TItem> select, Func<TOutput, string> target, string? title = null) =>
        SubgraphCore(id, template, select, target, title, out _);

    internal PregelResultGraphDsl<TInput, TOutput> SubgraphCore<TItem>(string id,
        PregelGraph<TItem> template, Func<TOutput, TItem> select,
        Func<TOutput, string> target, string? title, out NodeRef<PregelUnit> entry)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(target);
        var label = title ?? id;
        var children = MutateCore<TItem, PregelUnit>(id, template.Compiled, template.InputVariable,
            _ => PregelUnit.Value, (value, _, _) => [new PregelMutationItem<TItem>("child", select(value), label)
            { Target = target(value) }], PregelMutationMode.Sequential, skipReason: null, out entry, subgraphTitle: label);
        return new(_builder, _input, _output, children._tail);
    }
}

public sealed partial class PregelLinearGraphDsl<TInput>
{
    /// <summary>Runs a child without a declared result, then continues this flow with its original input.</summary>
    public PregelLinearGraphDsl<TInput> Subgraph<TItem>(string id,
        PregelGraph<TItem> template, Func<TInput, TItem> select, string target = "", string? title = null) =>
        Subgraph(id, template, select, _ => target, title);

    public PregelLinearGraphDsl<TInput> Subgraph<TItem>(string id,
        PregelGraph<TItem> template, Func<TInput, TItem> select, Func<TInput, string> target, string? title = null)
    {
        var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
            .SubgraphCore(id, template, select, target, title, out var entry);
        _first ??= entry;
        _tail = result.Tail!;
        return this;
    }

    /// <summary>Invokes a complete child graph using this flow's original input.</summary>
    public PregelResultGraphDsl<TInput, TResult> Subgraph<TItem, TResult>(string id,
        PregelGraph<TItem, TResult> template, Func<TInput, TItem> select, string target = "", string? title = null) =>
        Subgraph(id, template, select, _ => target, title);

    public PregelResultGraphDsl<TInput, TResult> Subgraph<TItem, TResult>(string id,
        PregelGraph<TItem, TResult> template, Func<TInput, TItem> select, Func<TInput, string> target, string? title = null)
    {
        var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
            .SubgraphCore(id, template, select, target, title, out var entry);
        _first ??= entry;
        _tail = result.Tail!;
        return result;
    }

    /// <summary>
    /// Expands one child run per planned item and keeps this chain linear, so the graph keeps its
    /// input-only shape. The planner runs after the upstream barrier and may read committed state.
    /// Child results are written to <paramref name="into"/> when given; a skipped expansion leaves it unchanged.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Mutate<TItem, TResult>(string id, PregelGraph<TItem, TResult> template,
        Func<TInput, PregelNodeContext, CancellationToken, IReadOnlyList<PregelMutationItem<TItem>>> plan,
        PregelVariable<IReadOnlyList<TResult>>? into = null, PregelMutationMode mode = PregelMutationMode.Sequential,
        Func<TResult, string?>? skipReason = null)
    {
        var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
            .MutateCore(id, template, plan, mode, skipReason, out var entry, into: into);
        return ContinueAfter(entry, result.Tail!);
    }

    /// <summary>Expands one child run per item selected from committed state; the chain stays linear.</summary>
    public PregelLinearGraphDsl<TInput> Mutate<TState, TItem, TResult>(string id, PregelGraph<TItem, TResult> template,
        PregelVariable<TState> source, Func<TState, IReadOnlyList<PregelMutationItem<TItem>>> select,
        PregelVariable<IReadOnlyList<TResult>>? into = null, PregelMutationMode mode = PregelMutationMode.Sequential,
        Func<TResult, string?>? skipReason = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(select);
        var result = new PregelResultGraphDsl<TInput, TState>(_builder, _input, source, _tail)
            .MutateCore(id, template, (state, _, _) => select(state), mode, skipReason, out var entry, into: into);
        return ContinueAfter(entry, result.Tail!);
    }

    /// <summary>Expands one run of a child graph without a declared result per planned item.</summary>
    public PregelLinearGraphDsl<TInput> Mutate<TItem>(string id, PregelGraph<TItem> template,
        Func<TInput, PregelNodeContext, CancellationToken, IReadOnlyList<PregelMutationItem<TItem>>> plan,
        PregelMutationMode mode = PregelMutationMode.Sequential)
    {
        ArgumentNullException.ThrowIfNull(template);
        var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
            .MutateCore<TItem, PregelUnit>(id, template.Compiled, template.InputVariable, _ => PregelUnit.Value,
                plan, mode, skipReason: null, out var entry);
        return ContinueAfter(entry, result.Tail!);
    }

    private PregelLinearGraphDsl<TInput> ContinueAfter(NodeRef<PregelUnit> entry, NodeRef<PregelUnit> join)
    {
        _first ??= entry;
        _tail = join;
        return this;
    }
}
