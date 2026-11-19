namespace Heron.Pregel;

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    /// <summary>Passes the current result directly to a typed child graph.</summary>
    public PregelResultGraphDsl<TInput, TResult> Graph<TResult>(string id,
        PregelGraph<TOutput, TResult> graph, Func<TOutput, string>? target = null, string? title = null) =>
        Connect(id, graph, target, title, out _);

    /// <summary>Runs a child graph and preserves the current result.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Graph(string id,
        PregelGraph<TOutput> graph, Func<TOutput, string>? target = null, string? title = null) =>
        Connect(id, graph, target, title, out _);

    internal PregelResultGraphDsl<TInput, TResult> Connect<TResult>(string id,
        PregelGraph<TOutput, TResult> graph, Func<TOutput, string>? target, string? title, out NodeRef<PregelUnit> entry)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var label = title ?? id;
        var children = MutateCore(id, graph.Compiled, graph.InputVariable, graph.Read,
            value => [new("child", value, label) { Target = target?.Invoke(value) ?? "" }],
            PregelMutationMode.Sequential, skipReason: null, out entry, subgraphTitle: label);
        var output = _builder.Output<TResult>("subgraph:" + id + ":result", default!);
        var complete = _builder.Node("subgraph:" + id + ":result", (context, _) =>
        {
            context.Write(output, context.Read(children._output).Single());
            return ValueTask.CompletedTask;
        });
        _builder.DeclareRead(complete, children._output);
        _builder.AddEdge(children._tail!, complete);
        return new PregelResultGraphDsl<TInput, TResult>(_builder, _input, output, complete)
            .WithSkipTarget((PregelMutationRuntime<TOutput, TResult>)_builder.Mutation(id));
    }

    internal PregelResultGraphDsl<TInput, TOutput> Connect(string id, PregelGraph<TOutput> graph,
        Func<TOutput, string>? target, string? title, out NodeRef<PregelUnit> entry)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var label = title ?? id;
        var children = MutateCore<TOutput, PregelUnit>(id, graph.Compiled, graph.InputVariable, _ => PregelUnit.Value,
            value => [new("child", value, label) { Target = target?.Invoke(value) ?? "" }],
            PregelMutationMode.Sequential, skipReason: null, out entry, subgraphTitle: label);
        return new(_builder, _input, _output, children._tail);
    }
}

public sealed partial class PregelLinearGraphDsl<TInput>
{
    /// <summary>Passes the flow input directly to a typed child graph.</summary>
    public PregelResultGraphDsl<TInput, TResult> Graph<TResult>(string id,
        PregelGraph<TInput, TResult> graph, Func<TInput, string>? target = null, string? title = null) =>
        RootCall(id, target is null && title is null, () =>
        {
            var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
                .Connect(id, graph, target, title, out var entry);
            ContinueAfter(entry, result.Tail!);
            return result;
        });

    /// <summary>Runs a child graph and preserves the flow input.</summary>
    public PregelLinearGraphDsl<TInput> Graph(string id,
        PregelGraph<TInput> graph, Func<TInput, string>? target = null, string? title = null) =>
        RootCall(id, target is null && title is null, () =>
        {
            var result = new PregelResultGraphDsl<TInput, TInput>(_builder, _input, _input, _tail)
                .Connect(id, graph, target, title, out var entry);
            return ContinueAfter(entry, result.Tail!);
        });

    private PregelLinearGraphDsl<TInput> ContinueAfter(NodeRef<PregelUnit> entry, NodeRef<PregelUnit> join)
    {
        _first ??= entry;
        _tail = join;
        return this;
    }
}
