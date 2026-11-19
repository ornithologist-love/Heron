namespace Heron.Pregel;

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    /// <summary>Passes the current result directly to a typed child graph.</summary>
    public PregelResultGraphDsl<TInput, TResult> Graph<TResult>(string id,
        PregelGraph<TOutput, TResult> graph) => Subgraph(id, graph, static value => value);

    /// <summary>Runs a child graph and preserves the current result.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Graph(string id,
        PregelGraph<TOutput> graph) => Subgraph(id, graph, static value => value);

    public PregelResultGraphDsl<TInput, TResult> Subgraph<TResult>(string id,
        PregelGraph<TOutput, TResult> graph) => Graph(id, graph);

    public PregelResultGraphDsl<TInput, TOutput> Subgraph(string id,
        PregelGraph<TOutput> graph) => Graph(id, graph);
}

public sealed partial class PregelLinearGraphDsl<TInput>
{
    /// <summary>Passes the flow input directly to a typed child graph.</summary>
    public PregelResultGraphDsl<TInput, TResult> Graph<TResult>(string id,
        PregelGraph<TInput, TResult> graph) => Subgraph(id, graph, static value => value);

    /// <summary>Runs a child graph and preserves the flow input.</summary>
    public PregelLinearGraphDsl<TInput> Graph(string id,
        PregelGraph<TInput> graph) => Subgraph(id, graph, static value => value);

    public PregelResultGraphDsl<TInput, TResult> Subgraph<TResult>(string id,
        PregelGraph<TInput, TResult> graph) => Graph(id, graph);

    public PregelLinearGraphDsl<TInput> Subgraph(string id,
        PregelGraph<TInput> graph) => Graph(id, graph);
}
