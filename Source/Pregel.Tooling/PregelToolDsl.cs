namespace Heron.Pregel.Tooling;

public static class PregelToolDslExtensions
{
    public static PregelToolChainDsl Start(
        this PregelTaskGraphDsl definition,
        Func<ToolExecution> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(task);
        var binding = new ToolRunnerBinding();
        var chain = definition.StartExecution(task, binding.ExecuteAsync, retryPolicy);
        return new PregelToolChainDsl(chain, binding);
    }

    public static PregelToolChainDsl Start<TArgument>(
        this PregelTaskGraphDsl definition,
        Func<TArgument, ToolExecution> task,
        TArgument argument,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(argument);
        var binding = new ToolRunnerBinding();
        var chain = definition.StartExecution(task, argument, binding.ExecuteAsync, argumentCodec, retryPolicy);
        return new PregelToolChainDsl(chain, binding);
    }

    public static PregelToolChainDsl Start<TArgument>(
        this PregelTaskGraphDsl definition,
        Func<TArgument, ToolExecution> task,
        PregelVariable<TArgument> variable,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(variable);
        var binding = new ToolRunnerBinding();
        var chain = definition.StartExecution(task, variable, binding.ExecuteAsync, retryPolicy);
        return new PregelToolChainDsl(chain, binding);
    }
}

public sealed class PregelToolChainDsl
{
    private readonly PregelExecutionChainDsl<ToolExecution> _chain;
    private readonly ToolRunnerBinding _binding;

    internal PregelToolChainDsl(
        PregelExecutionChainDsl<ToolExecution> chain,
        ToolRunnerBinding binding)
    {
        _chain = chain;
        _binding = binding;
    }

    public PregelToolChainDsl Then(
        Func<ToolExecution> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        _chain.ThenExecution(task, retryPolicy);
        return this;
    }

    public PregelToolChainDsl Then<TArgument>(
        Func<TArgument, ToolExecution> task,
        TArgument argument,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(argument);
        _chain.ThenExecution(task, argument, argumentCodec, retryPolicy);
        return this;
    }

    public PregelToolChainDsl Then<TArgument>(
        Func<TArgument, ToolExecution> task,
        PregelVariable<TArgument> variable,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(variable);
        _chain.ThenExecution(task, variable, retryPolicy);
        return this;
    }

    public PregelToolGraph Compile() => new(_chain.Compile(), _binding);
}

public sealed class PregelToolGraph
{
    private readonly ToolRunnerBinding _binding;

    internal PregelToolGraph(CompiledPregelGraph compiled, ToolRunnerBinding binding)
    {
        Compiled = compiled;
        _binding = binding;
    }

    public CompiledPregelGraph Compiled { get; }

    public async ValueTask<PregelRunResult> RunAsync(
        IToolExecutionRunner runner,
        CancellationToken cancellationToken = default) =>
        await RunAsync(new PregelEngine(), runner, PregelVariables.Empty, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PregelRunResult> RunAsync(
        IToolExecutionRunner runner,
        PregelVariables variables,
        CancellationToken cancellationToken = default) =>
        await RunAsync(new PregelEngine(), runner, variables, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PregelRunResult> RunAsync(
        PregelEngine engine,
        IToolExecutionRunner runner,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(engine, runner, PregelVariables.Empty, options, cancellationToken).ConfigureAwait(false);

    public async ValueTask<PregelRunResult> RunAsync(
        PregelEngine engine,
        IToolExecutionRunner runner,
        PregelVariables variables,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(variables);
        using var scope = _binding.Push(runner);
        return await engine
            .StartAsync(Compiled, variables, options ?? new PregelRunOptions(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<PregelRunResult> ResumeAsync(
        PregelEngine engine,
        IToolExecutionRunner runner,
        PregelRunId runId,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(runner);
        using var scope = _binding.Push(runner);
        return await engine
            .ResumeAsync(Compiled, runId, options ?? new PregelRunOptions(), cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class ToolRunnerBinding
{
    private readonly AsyncLocal<IToolExecutionRunner?> _current = new();

    public IDisposable Push(IToolExecutionRunner runner)
    {
        var previous = _current.Value;
        _current.Value = runner;
        return new Scope(() => _current.Value = previous);
    }

    public async ValueTask ExecuteAsync(ToolExecution execution, PregelNodeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var runner = _current.Value
            ?? throw new PregelException("tool_runner_unavailable", "Tool graph requires an execution runner.");
        var result = await runner.ExecuteAsync(execution, context.Output, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new PregelException(
                "tool_execution_failed",
                $"{execution.GetType().Name} exited with code {result.ExitCode}.");
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
