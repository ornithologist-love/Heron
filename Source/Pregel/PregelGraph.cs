namespace Heron.Pregel;

/// <summary>A compiled graph with a typed input and final flow value.</summary>
public sealed class PregelGraph<TInput, TOutput>
{
    private readonly PregelVariable<TInput> _input;
    internal PregelVariable<TInput> InputVariable => _input;
    private readonly Func<PregelRunResult, TOutput> _read;

    internal PregelGraph(CompiledPregelGraph compiled, PregelVariable<TInput> input, Func<PregelRunResult, TOutput> read)
    {
        Compiled = compiled;
        _input = input;
        _read = read;
    }

    /// <summary>Gets the kernel graph produced by the DSL.</summary>
    public CompiledPregelGraph Compiled { get; }

    /// <summary>Reads the graph's typed output from a completed run result.</summary>
    public TOutput Read(PregelRunResult result) => _read(result);

    /// <summary>Binds input and optional host dependencies without executing nodes.</summary>
    public PregelRun<TOutput> Prepare(TInput input, PregelRunOptions? options = null) =>
        new(Compiled, _read, new PregelVariables().Set(_input, input), options?.Engine, options);

    /// <summary>Runs the graph with the supplied options, using an in-memory engine by default.</summary>
    public Task<PregelRunResult> RunAsync(TInput input, PregelRunOptions? options = null, CancellationToken token = default) =>
        Prepare(input, options).RunAsync(token);
}
