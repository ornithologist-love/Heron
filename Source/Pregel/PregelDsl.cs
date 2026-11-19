using System.Reflection;
using System.Runtime.CompilerServices;

namespace Heron.Pregel;

/// <summary>
/// Entry point for the concise code-first Pregel authoring DSL.
/// </summary>
public static class Pregel
{
    public static NodeId NodeId(Delegate task) => new(PregelTaskIdentity.NodeId(task));

    /// <summary>
    /// Starts defining a graph made from user-owned Task methods.
    /// </summary>
    public static PregelTaskGraphDsl Define(string graphId, string revision = "1") =>
        new(graphId, revision);

    /// <summary>
    /// Starts defining a graph whose entry node receives <typeparamref name="TInput"/>.
    /// </summary>
    public static PregelGraphDsl<TInput> Define<TInput>(string graphId, string revision = "1") =>
        new(graphId, revision);
}

/// <summary>
/// Describes what a node asks the flow to do after its execution. This is a closed hierarchy, not an
/// enum, so a future case can carry data (for example a skip reason) without breaking existing callers.
/// Every case must be handled in <see cref="PregelFlowNode.Apply"/>; unknown cases fail with
/// <c>unsupported_flow</c> instead of silently continuing.
/// </summary>
/// <remarks>Node preconditions use When; a false predicate records Skipped before executing the node body.</remarks>
public abstract record PregelFlow
{
    private PregelFlow() { }

    /// <summary>Follow the node's outgoing edges.</summary>
    public static PregelFlow Continue { get; } = new ContinueFlow();

    /// <summary>Stop the flow here; the run completes once no other invocation is pending.</summary>
    public static PregelFlow End { get; } = new EndFlow();

    /// <summary>One representative per defined case; used to prove every case is handled.</summary>
    internal static IReadOnlyList<PregelFlow> All { get; } = [Continue, End];

    internal sealed record ContinueFlow : PregelFlow;

    internal sealed record EndFlow : PregelFlow;
}

/// <summary>
/// Defines a graph by binding user-owned Task methods directly to nodes.
/// </summary>
public sealed class PregelTaskGraphDsl
{
    private readonly PregelGraphBuilder _builder;

    public PregelTaskChainDsl Stage(string id, string title, Action<PregelTaskChainDsl> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        PregelTaskChainDsl? chain = null;
        PregelLinearGraphDsl<PregelUnit>.StartStage(_builder, _builder.Output("stage-input", PregelUnit.Value),
            id, title, stage =>
            {
                chain = new PregelTaskChainDsl(_builder, stage.Tail.Runtime);
                configure(chain);
            }, null, "阶段运行条件不满足");
        return chain!;
    }

    public PregelTaskGraphDsl MutexRules(Type owner, string? memberName = null)
    {
        _builder.MutexRules(owner, memberName);
        return this;
    }

    internal PregelTaskGraphDsl(string graphId, string revision) =>
        _builder = new PregelGraphBuilder(graphId, revision);

    public PregelTaskChainDsl Start(Action<PregelNodeContext, CancellationToken> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(_builder.TaskId(task), (context, token) =>
        {
            task(context, token);
            return ValueTask.CompletedTask;
        }, retryPolicy);
        _builder.SetEntry(node);
        return new PregelTaskChainDsl(_builder, node.Runtime);
    }

    public PregelTaskChainDsl Start(Action<PregelNodeContext> task, PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(_builder.TaskId(task), (context, _) =>
        {
            task(context);
            return ValueTask.CompletedTask;
        }, retryPolicy);
        _builder.SetEntry(node);
        return new PregelTaskChainDsl(_builder, node.Runtime);
    }

    public PregelTaskChainDsl Start(Func<PregelNodeContext, CancellationToken, Task> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(_builder.TaskId(task),
            (context, token) => new ValueTask(task(context, token)), retryPolicy);
        _builder.SetEntry(node);
        return new PregelTaskChainDsl(_builder, node.Runtime);
    }

    /// <summary>Declares a mutable graph variable; its value is supplied per run. Graphs declare none by default.</summary>
    /// <remarks>TODO: const variables (read-only refs) and graph constants are a later iteration.</remarks>
    public PregelVariable<T> Variable<T>(string name, ICheckpointCodec<T>? codec = null) =>
        _builder.Variable(name, codec);

    /// <summary>Declares an output with an explicit initial value, read through Graph.Read after a run.</summary>
    public PregelVariable<T> Output<T>(string name, T initialValue, ICheckpointCodec<T>? codec = null) =>
        _builder.Output(name, initialValue, codec);


    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task, PregelVariable<TArgument> variable, Func<TExecution, CancellationToken, ValueTask> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, variable, (execution, _, token) => executor(execution, token), retryPolicy);
    }
    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task, PregelVariable<TArgument> variable, Func<TExecution, CancellationToken, ValueTask<PregelFlow>> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, variable, (execution, _, token) => executor(execution, token), retryPolicy);
    }
    public PregelExecutionChainDsl<TExecution> StartExecution<TExecution>(
        Func<TExecution> task, Func<TExecution, CancellationToken, ValueTask> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, (execution, _, token) => executor(execution, token), retryPolicy);
    }
    public PregelExecutionChainDsl<TExecution> StartExecution<TExecution>(
        Func<TExecution> task, Func<TExecution, CancellationToken, ValueTask<PregelFlow>> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, (execution, _, token) => executor(execution, token), retryPolicy);
    }
    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task, TArgument argument, Func<TExecution, CancellationToken, ValueTask> executor,
        ICheckpointCodec<TArgument>? argumentCodec = null, PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, argument, (execution, _, token) => executor(execution, token), argumentCodec, retryPolicy);
    }
    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task, TArgument argument, Func<TExecution, CancellationToken, ValueTask<PregelFlow>> executor,
        ICheckpointCodec<TArgument>? argumentCodec = null, PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, argument, (execution, _, token) => executor(execution, token), argumentCodec, retryPolicy);
    }

    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task,
        PregelVariable<TArgument> variable,
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, variable, ContinueAfter(executor), retryPolicy);
    }

    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task,
        PregelVariable<TArgument> variable,
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask<PregelFlow>> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(executor);
        var node = _builder.Node(
            _builder.TaskId(task),
            (context, cancellationToken) =>
                PregelFlowNode.RunAsync(executor, task(context.Read(variable)), context, cancellationToken),
            retryPolicy);
        _builder.DeclareRead(node, variable);
        _builder.SetEntry(node);
        return new PregelExecutionChainDsl<TExecution>(_builder, node.Runtime, executor);
    }

    public PregelExecutionChainDsl<TExecution> StartExecution<TExecution>(
        Func<TExecution> task,
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, ContinueAfter(executor), retryPolicy);
    }

    public PregelExecutionChainDsl<TExecution> StartExecution<TExecution>(
        Func<TExecution> task,
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask<PregelFlow>> executor,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(executor);
        var node = _builder.Node(
            _builder.TaskId(task),
            (context, cancellationToken) => PregelFlowNode.RunAsync(executor, task(), context, cancellationToken),
            retryPolicy);
        _builder.SetEntry(node);
        return new PregelExecutionChainDsl<TExecution>(_builder, node.Runtime, executor);
    }

    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task,
        TArgument argument,
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask> executor,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartExecution(task, argument, ContinueAfter(executor), argumentCodec, retryPolicy);
    }

    public PregelExecutionChainDsl<TExecution> StartExecution<TArgument, TExecution>(
        Func<TArgument, TExecution> task,
        TArgument argument,
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask<PregelFlow>> executor,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(executor);
        var node = _builder.Node(
            _builder.TaskId(task),
            argument,
            (value, context, cancellationToken) => PregelFlowNode.RunAsync(executor, task(value), context, cancellationToken),
            argumentCodec,
            retryPolicy);
        _builder.SetEntry(node);
        return new PregelExecutionChainDsl<TExecution>(_builder, node.Runtime, executor);
    }

    private static Func<TExecution, PregelNodeContext, CancellationToken, ValueTask<PregelFlow>> ContinueAfter<TExecution>(
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask> executor) =>
        async (execution, context, cancellationToken) =>
        {
            await executor(execution, context, cancellationToken).ConfigureAwait(false);
            return PregelFlow.Continue;
        };

    public PregelTaskChainDsl Start(
        Func<CancellationToken, Task> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(
            _builder.TaskId(task),
            (_, cancellationToken) => new ValueTask(task(cancellationToken)),
            retryPolicy);
        _builder.SetEntry(node);
        return new PregelTaskChainDsl(_builder, node.Runtime);
    }

    public PregelTaskChainDsl Start<TArgument>(
        Func<TArgument, CancellationToken, Task> task,
        TArgument argument,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(argument);
        var node = _builder.Node(
            _builder.TaskId(task),
            argument,
            (value, _, cancellationToken) => new ValueTask(task(value, cancellationToken)),
            argumentCodec,
            retryPolicy);
        _builder.SetEntry(node);
        return new PregelTaskChainDsl(_builder, node.Runtime);
    }

    public PregelTaskChainDsl Start<TArgument>(
        Func<TArgument, CancellationToken, Task> task,
        PregelVariable<TArgument> variable,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(variable);
        var node = _builder.Node(
            _builder.TaskId(task),
            (context, cancellationToken) => new ValueTask(task(context.Read(variable), cancellationToken)),
            retryPolicy);
        _builder.DeclareRead(node, variable);
        _builder.SetEntry(node);
        return new PregelTaskChainDsl(_builder, node.Runtime);
    }

    public PregelTaskChainDsl Start(Func<Task> task, PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Start(_ => task(), retryPolicy, task);
    }

    private PregelTaskChainDsl Start(
        Func<CancellationToken, Task> invocation,
        PregelRetryPolicy? retryPolicy,
        Delegate identitySource)
    {
        var node = _builder.Node(
            _builder.TaskId(identitySource),
            (_, _) => new ValueTask(invocation(CancellationToken.None)),
            retryPolicy);
        _builder.SetEntry(node);
        return new PregelTaskChainDsl(_builder, node.Runtime);
    }
}

public sealed class PregelExecutionChainDsl<TExecution>
{
    public PregelExecutionChainDsl<TExecution> ContinueOnError()
    {
        _builder.ContinueOnError(_tail.Id);
        return this;
    }

    /// <summary>Applies a per-invocation precondition to the current node.</summary>
    public PregelExecutionChainDsl<TExecution> When(Func<PregelNodeContext, bool> predicate,
        string skipReason = "运行条件不满足")
    {
        _builder.ConfigureCondition(_tail, predicate, skipReason);
        return this;
    }

    private readonly PregelGraphBuilder _builder;
    private readonly Func<TExecution, PregelNodeContext, CancellationToken, ValueTask<PregelFlow>> _executor;
    private INodeRuntime _tail;

    internal PregelExecutionChainDsl(
        PregelGraphBuilder builder,
        INodeRuntime tail,
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask<PregelFlow>> executor)
    {
        _builder = builder;
        _tail = tail;
        _executor = executor;
    }

    public PregelExecutionChainDsl<TExecution> ThenExecution(
        Func<TExecution> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(
            _builder.TaskId(task),
            (context, cancellationToken) => PregelFlowNode.RunAsync(_executor, task(), context, cancellationToken),
            retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelExecutionChainDsl<TExecution> ThenExecution<TArgument>(
        Func<TArgument, TExecution> task,
        TArgument argument,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(argument);
        var node = _builder.Node(
            _builder.TaskId(task),
            argument,
            (value, context, cancellationToken) => PregelFlowNode.RunAsync(_executor, task(value), context, cancellationToken),
            argumentCodec,
            retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelExecutionChainDsl<TExecution> ThenExecution<TArgument>(
        Func<TArgument, TExecution> task,
        PregelVariable<TArgument> variable,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(variable);
        var node = _builder.Node(
            _builder.TaskId(task),
            (context, cancellationToken) =>
                PregelFlowNode.RunAsync(_executor, task(context.Read(variable)), context, cancellationToken),
            retryPolicy);
        _builder.DeclareRead(node, variable);
        Append(node.Runtime);
        return this;
    }

    public CompiledPregelGraph Compile() => _builder.Compile();

    private void Append(INodeRuntime node)
    {
        _builder.AddEdgeRuntime(_tail, node);
        _tail = node;
    }
}

internal static class PregelFlowNode
{
    /// <summary>Runs one execution and applies the returned <see cref="PregelFlow"/>.</summary>
    public static async ValueTask RunAsync<TExecution>(
        Func<TExecution, PregelNodeContext, CancellationToken, ValueTask<PregelFlow>> executor,
        TExecution execution,
        PregelNodeContext context,
        CancellationToken cancellationToken)
    {
        var flow = await executor(execution, context, cancellationToken).ConfigureAwait(false);
        Apply(flow, context);
    }

    /// <summary>The single mapping from flow cases to kernel commands. Add new cases here.</summary>
    internal static void Apply(PregelFlow flow, PregelNodeContext context)
    {
        switch (flow)
        {
            case PregelFlow.ContinueFlow:
                break;
            case PregelFlow.EndFlow:
                context.End();
                break;
            default:
                throw new PregelException(
                    "unsupported_flow",
                    $"Flow '{flow?.GetType().Name ?? "null"}' is not handled by the Pregel DSL.");
        }
    }
}

/// <summary>
/// Chains user-owned Task methods while keeping their arguments explicit in the graph definition.
/// </summary>
public sealed partial class PregelTaskChainDsl
{
    public PregelTaskChainDsl Stage(string id, string title, Action<PregelTaskChainDsl> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        PregelLinearGraphDsl<PregelUnit>.StartStage(_builder, _builder.Output($"stage:{id}:input", PregelUnit.Value),
            id, title, stage =>
            {
                _tail = stage.Tail.Runtime;
                configure(this);
            }, null, "阶段运行条件不满足", _tail);
        return this;
    }

    /// <summary>Applies a per-invocation precondition to the current node.</summary>
    public PregelTaskChainDsl When(Func<PregelNodeContext, bool> predicate, string skipReason = "运行条件不满足")
    {
        _builder.ConfigureCondition(_tail, predicate, skipReason);
        return this;
    }

    private readonly PregelGraphBuilder _builder;
    private INodeRuntime _tail;

    internal PregelTaskChainDsl(PregelGraphBuilder builder, INodeRuntime tail)
    {
        _builder = builder;
        _tail = tail;
    }

    public PregelTaskChainDsl Then(Action<PregelNodeContext, CancellationToken> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(_builder.TaskId(task), (context, token) =>
        {
            task(context, token);
            return ValueTask.CompletedTask;
        }, retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelTaskChainDsl Then(Action<PregelNodeContext> task, PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(_builder.TaskId(task), (context, _) =>
        {
            task(context);
            return ValueTask.CompletedTask;
        }, retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelTaskChainDsl Then(Func<PregelNodeContext, CancellationToken, Task> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(_builder.TaskId(task),
            (context, token) => new ValueTask(task(context, token)), retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelTaskChainDsl Then(
        Func<CancellationToken, Task> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(
            _builder.TaskId(task),
            (_, cancellationToken) => new ValueTask(task(cancellationToken)),
            retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelTaskChainDsl Then(Func<Task> task, PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var node = _builder.Node(
            _builder.TaskId(task),
            (_, _) => new ValueTask(task()),
            retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelTaskChainDsl Then<TArgument>(
        Func<TArgument, CancellationToken, Task> task,
        TArgument argument,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(argument);
        var node = _builder.Node(
            _builder.TaskId(task),
            argument,
            (value, _, cancellationToken) => new ValueTask(task(value, cancellationToken)),
            argumentCodec,
            retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelTaskChainDsl Then<TArgument>(
        Func<TArgument, Task> task,
        TArgument argument,
        ICheckpointCodec<TArgument>? argumentCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(argument);
        var node = _builder.Node(
            _builder.TaskId(task),
            argument,
            (value, _, _) => new ValueTask(task(value)),
            argumentCodec,
            retryPolicy);
        Append(node.Runtime);
        return this;
    }

    public PregelGraph Compile() => new(_builder.Compile());

    private void Append(INodeRuntime node)
    {
        _builder.AddEdgeRuntime(_tail, node);
        _tail = node;
    }
}

/// <summary>
/// A compiled no-input Pregel graph authored from Task methods.
/// </summary>
public sealed class PregelGraph
{
    internal PregelGraph(CompiledPregelGraph compiled) => Compiled = compiled;

    public CompiledPregelGraph Compiled { get; }

    public T Read<T>(PregelRunResult result, PregelVariable<T> variable) => Compiled.Read(result, variable);

    public PregelRun Prepare(PregelEngine? engine = null, PregelRunOptions? options = null,
        PregelVariables? variables = null) => new(Compiled, variables ?? PregelVariables.Empty, engine, options);

    public ValueTask<PregelRunResult> RunAsync(PregelEngine engine, PregelVariables variables,
        PregelRunOptions? options = null, CancellationToken cancellationToken = default) =>
        new(Prepare(engine, options, variables).RunAsync(cancellationToken));

    public ValueTask<PregelRunResult> RunAsync(IPregelRunObserver observer,
        CancellationToken cancellationToken = default) =>
        new(Prepare(options: new PregelRunOptions(Observer: observer)).RunAsync(cancellationToken));

    public ValueTask<PregelRunResult> RunAsync(CancellationToken cancellationToken = default) =>
        new(Prepare().RunAsync(cancellationToken));

    public ValueTask<PregelRunResult> RunAsync(
        PregelEngine engine,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new(Prepare(engine, options).RunAsync(cancellationToken));
    }
}

internal static class PregelTaskIdentity
{
    public static string NodeId(Delegate task)
    {
        var method = task.Method;
        if (method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || method.Name.Contains('<', StringComparison.Ordinal))
        {
            throw new PregelException(
                "unstable_task_method",
                "Direct Task DSL requires a named method; lambdas and local functions need an explicit low-level node ID.");
        }

        var declaringType = method.DeclaringType?.FullName
            ?? throw new PregelException("task_method_missing_type", "Task method must have a declaring type.");
        var parameters = string.Join(",", method.GetParameters().Select(ParameterTypeId));
        var signature = PregelTypeIdentity.Normalize($"{declaringType}.{method.Name}({parameters})");
        return $"task:{method.Name}:{PregelHash.Sha256(signature)[..16]}";
    }

    private static string ParameterTypeId(ParameterInfo parameter) =>
        parameter.ParameterType.FullName ?? parameter.ParameterType.Name;
}

/// <summary>
/// Defines the entry of a code-first Pregel graph without exposing kernel wiring details.
/// </summary>
public sealed class PregelGraphDsl<TInput>
{
    private readonly PregelGraphBuilder _builder;

    public PregelGraphDsl<TInput> MutexRules(Type owner, string? memberName = null)
    {
        _builder.MutexRules(owner, memberName);
        return this;
    }

    internal PregelGraphDsl(string graphId, string revision) =>
        _builder = new PregelGraphBuilder(graphId, revision);

    /// <summary>Declares state before configuring the first stage.</summary>
    public PregelVariable<TState> State<TState>(string name, TState initialValue) => _builder.Output(name, initialValue);

    /// <summary>Starts the graph with a stage, without requiring an ungrouped entry task.</summary>
    public PregelLinearGraphDsl<TInput> Stage(string id, string title,
        Action<PregelLinearGraphDsl<TInput>> configure, Func<TInput, bool>? when = null,
        string skipReason = "阶段运行条件不满足") =>
        PregelLinearGraphDsl<TInput>.StartStage(_builder, _builder.Variable<TInput>("input"),
            id, title, configure, when, skipReason);

    public PregelResultGraphDsl<TInput, TOutput> Stage<TOutput>(string id, string title,
        Func<PregelLinearGraphDsl<TInput>, PregelResultGraphDsl<TInput, TOutput>> configure,
        Func<TInput, bool>? when = null, string skipReason = "阶段运行条件不满足")
    {
        ArgumentNullException.ThrowIfNull(configure);
        PregelResultGraphDsl<TInput, TOutput>? result = null;
        PregelLinearGraphDsl<TInput>.StartStage(_builder, _builder.Variable<TInput>("input"), id, title, stage =>
        {
            result = configure(stage) ?? throw new ArgumentException("A result stage must return its result chain.", nameof(configure));
            if (!result.IsTailOf(_builder, _builder.DefinedNodeIds.Last()))
                throw new ArgumentException("A result stage must return the final result chain from this stage.", nameof(configure));
        }, when, skipReason);
        return result!;
    }

    public PregelResultGraphDsl<TInput, TOutput> Start<TOutput>(
        Func<TInput, PregelNodeContext, CancellationToken, Task<TOutput>> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var input = _builder.Variable<TInput>("input");
        var output = _builder.Output<TOutput>("result", default!);
        var entry = _builder.Node(_builder.TaskId(task), async (context, token) =>
        {
            var result = await task(context.Read(input), context, token).ConfigureAwait(false);
            context.Write(output, result);
        }, retryPolicy);
        _builder.DeclareRead(entry, input);
        _builder.SetEntry(entry);
        return new PregelResultGraphDsl<TInput, TOutput>(_builder, input, output, entry);
    }

    public PregelResultGraphDsl<TInput, TOutput> Start<TOutput>(
        Func<TInput, PregelNodeContext, CancellationToken, TOutput> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var input = _builder.Variable<TInput>("input");
        var output = _builder.Output<TOutput>("result", default!);
        var entry = _builder.Node(_builder.TaskId(task), (context, token) =>
        {
            var result = task(context.Read(input), context, token);
            context.Write(output, result);
            return ValueTask.CompletedTask;
        }, retryPolicy);
        _builder.DeclareRead(entry, input);
        _builder.SetEntry(entry);
        return new PregelResultGraphDsl<TInput, TOutput>(_builder, input, output, entry);
    }

    public PregelLinearGraphDsl<TInput> Start(
        Action<TInput, PregelNodeContext, CancellationToken> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Start(_builder.TaskId(task), (input, context, token) =>
        {
            task(input, context, token);
            return ValueTask.CompletedTask;
        }, retryPolicy: retryPolicy);
    }

    public PregelLinearGraphDsl<TInput> Start(
        Func<TInput, PregelNodeContext, CancellationToken, Task> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Start(_builder.TaskId(task),
            (input, context, token) => new ValueTask(task(input, context, token)), retryPolicy: retryPolicy);
    }

    /// <summary>
    /// Adds a synchronous entry node. The value passed to the handler is the value supplied to RunAsync.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Start(
        string nodeId,
        Action<TInput> handler,
        ICheckpointCodec<TInput>? inputCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Start(
            nodeId,
            (input, _, _) =>
            {
                handler(input);
                return ValueTask.CompletedTask;
            },
            inputCodec,
            retryPolicy);
    }

    /// <summary>
    /// Adds an asynchronous entry node.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Start(
        string nodeId,
        Func<TInput, CancellationToken, Task> handler,
        ICheckpointCodec<TInput>? inputCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Start(
            nodeId,
            (input, _, cancellationToken) => new ValueTask(handler(input, cancellationToken)),
            inputCodec,
            retryPolicy);
    }

    /// <summary>
    /// Adds an entry node that also needs access to the Pregel invocation context.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Start(
        string nodeId,
        PregelNodeHandler<TInput> handler,
        ICheckpointCodec<TInput>? inputCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var input = _builder.Variable("input", inputCodec);
        var entry = _builder.Node(
            nodeId,
            (context, cancellationToken) => handler(context.Read(input), context, cancellationToken),
            retryPolicy);
        _builder.DeclareRead(entry, input);
        _builder.SetEntry(entry);
        return new PregelLinearGraphDsl<TInput>(_builder, entry, input);
    }
}

/// <summary>
/// A concise DSL for a linear section of a Pregel graph.
/// </summary>
public sealed partial class PregelLinearGraphDsl<TInput>
{
    /// <summary>Groups tasks and commits a once-per-stage condition. Node When conditions still apply.</summary>
    public PregelLinearGraphDsl<TInput> Stage(string id, string title,
        Action<PregelLinearGraphDsl<TInput>> configure, Func<TInput, bool>? when = null,
        string skipReason = "阶段运行条件不满足")
    {
        if (_builder.StageRoutingNodes != null) throw new InvalidOperationException("Nested stages are not supported.");
        ArgumentNullException.ThrowIfNull(configure);
        var (entry, enabled) = CreateStageEntry(_builder, _input, id, title, when, skipReason);
        Append(entry);
        ConfigureStage(id, title, configure, skipReason, entry, enabled);
        return this;
    }

    internal static PregelLinearGraphDsl<TInput> StartStage(PregelGraphBuilder builder,
        PregelVariable<TInput> input, string id, string title, Action<PregelLinearGraphDsl<TInput>> configure,
        Func<TInput, bool>? when, string skipReason, INodeRuntime? previous = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (builder.StageRoutingNodes != null) throw new InvalidOperationException("Nested stages are not supported.");
        var (entry, enabled) = CreateStageEntry(builder, input, id, title, when, skipReason);
        if (previous is null) builder.SetEntry(entry);
        else builder.AddEdgeRuntime(previous, entry.Runtime);
        var flow = new PregelLinearGraphDsl<TInput>(builder, entry, input);
        flow.ConfigureStage(id, title, configure, skipReason, entry, enabled);
        return flow;
    }

    private static (NodeRef<PregelUnit> Entry, PregelVariable<bool> Enabled) CreateStageEntry(
        PregelGraphBuilder builder, PregelVariable<TInput> input, string id, string title,
        Func<TInput, bool>? when, string skipReason)
    {
        id = PregelId.Validate(id, nameof(id));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A stage title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(skipReason)) throw new ArgumentException("A skip reason is required.", nameof(skipReason));
        var enabled = builder.Output($"stage:{id}:enabled", true);
        var entry = builder.Node($"stage:{id}:entry", (context, _) =>
        {
            context.Write(enabled, when?.Invoke(context.Read(input)) ?? true);
            return ValueTask.CompletedTask;
        });
        builder.DeclareRead(entry, input);
        return (entry, enabled);
    }

    private void ConfigureStage(string id, string title, Action<PregelLinearGraphDsl<TInput>> configure,
        string skipReason, NodeRef<PregelUnit> entry, PregelVariable<bool> enabled)
    {
        var existing = _builder.DefinedNodeIds.ToHashSet();
        var routingNodes = new HashSet<NodeId>();
        _builder.StageRoutingNodes = routingNodes;
        try { configure(this); }
        finally { _builder.StageRoutingNodes = null; }
        var members = _builder.DefinedNodeIds.Where(node => !existing.Contains(node) && !routingNodes.Contains(node)).ToArray();
        if (members.Length == 0) throw new ArgumentException("A stage must contain a node.", nameof(configure));
        _builder.AddStage(new(id, title, entry.Id, Array.AsReadOnly(members), skipReason), enabled,
            _builder.DefinedNodeIds.Where(node => !existing.Contains(node) && !_builder.BarrierControlNodes.Contains(node)));
    }

    /// <summary>Groups tasks while retaining the final node's typed graph output.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Stage<TOutput>(string id, string title,
        Func<PregelLinearGraphDsl<TInput>, PregelResultGraphDsl<TInput, TOutput>> configure,
        Func<TInput, bool>? when = null, string skipReason = "阶段运行条件不满足")
    {
        ArgumentNullException.ThrowIfNull(configure);
        PregelResultGraphDsl<TInput, TOutput>? result = null;
        Stage(id, title, stage =>
        {
            result = configure(stage) ?? throw new ArgumentException("A result stage must return its result chain.", nameof(configure));
            if (!result.IsTailOf(_builder, _builder.DefinedNodeIds.Last()))
                throw new ArgumentException("A result stage must return the final result chain from this stage.", nameof(configure));
        }, when, skipReason);
        return result!;
    }

    /// <summary>
    /// Tests the run input before the current node; false records Skipped and continues.
    /// At the start of a branch it gates every node of that branch instead.
    /// </summary>
    public PregelLinearGraphDsl<TInput> When(Func<TInput, bool> predicate, string skipReason = "运行条件不满足")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (TryGateBranch(predicate, skipReason)) return this;
        _builder.ConfigureCondition(_tail.Runtime, context => predicate(context.Read(_input)), skipReason);
        _builder.DeclareRead(_tail, _input);
        return this;
    }

    private readonly PregelGraphBuilder _builder;
    private readonly PregelVariable<TInput> _input;
    private NodeRef<PregelUnit> _tail;
    private NodeRef<PregelUnit>? _first;
    internal NodeRef<PregelUnit> Tail => _tail;

    /// <summary>Declares per-run state. Parallel branches should use separate slots.</summary>
    public PregelVariable<TState> State<TState>(string name, TState initialValue) => _builder.Output(name, initialValue);

    /// <summary>
    /// Recovers using the specified state's last committed value, including its initial/null value.
    /// The callback has an independent bounded token and cannot write state or issue graph commands.
    /// </summary>
    public PregelLinearGraphDsl<TInput> OnFailure<TState>(PregelVariable<TState> state,
        Func<TState?, PregelNodeContext, CancellationToken, Task> recovery)
    {
        _builder.ConfigureRecovery(state, recovery, recovery);
        return this;
    }

    public PregelLinearGraphDsl<TInput> Then<TArgument, TResult>(
        Func<TArgument, PregelNodeContext, CancellationToken, TResult> task,
        Func<TInput, TArgument> select, PregelVariable<TResult> output) =>
        ThenReading(_builder.TaskId(task), (context, token) =>
        {
            context.Write(output, task(select(context.Read(_input)), context, token));
            return ValueTask.CompletedTask;
        }, null);

    public PregelLinearGraphDsl<TInput> Then<TArgument, TResult>(
        Func<TArgument, PregelNodeContext, CancellationToken, Task<TResult>> task,
        Func<TInput, TArgument> select, PregelVariable<TResult> output) =>
        ThenReading(_builder.TaskId(task), async (context, token) =>
            context.Write(output, await task(select(context.Read(_input)), context, token).ConfigureAwait(false)), null);

    public PregelLinearGraphDsl<TInput> Then<TState>(
        Func<TState, PregelNodeContext, CancellationToken, Task<TState>> task, PregelVariable<TState> state)
    {
        var node = _builder.Node(_builder.TaskId(task), async (context, token) =>
            context.Write(state, await task(context.Read(state), context, token).ConfigureAwait(false)));
        _builder.DeclareRead(node, state);
        Append(node);
        return this;
    }

    public PregelLinearGraphDsl<TInput> Then<TState>(
        Func<TState, PregelNodeContext, CancellationToken, TState> task, PregelVariable<TState> state)
    {
        var node = _builder.Node(_builder.TaskId(task), (context, token) =>
        {
            context.Write(state, task(context.Read(state), context, token));
            return ValueTask.CompletedTask;
        });
        _builder.DeclareRead(node, state);
        Append(node);
        return this;
    }

    /// <summary>Chooses exactly one branch, then joins its tail. Both paths remain in the graph definition.</summary>
    public PregelLinearGraphDsl<TInput> Choose<TState>(string nodeId, PregelVariable<TState> state,
        Func<TState, bool> predicate, Action<PregelLinearGraphDsl<TInput>> whenTrue,
        Action<PregelLinearGraphDsl<TInput>> whenFalse)
    {
        if (_builder.StageRoutingNodes != null) throw new InvalidOperationException("Choose inside a stage is not supported.");
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(whenTrue);
        ArgumentNullException.ThrowIfNull(whenFalse);
        var route = _builder.Node(nodeId, (_, _) => ValueTask.CompletedTask);
        _builder.DeclareRead(route, state);
        Append(route);
        var left = new PregelLinearGraphDsl<TInput>(_builder, route, _input);
        var right = new PregelLinearGraphDsl<TInput>(_builder, route, _input);
        whenTrue(left);
        whenFalse(right);
        var yes = left._first ?? throw new ArgumentException("A choice branch must contain a node.", nameof(whenTrue));
        var no = right._first ?? throw new ArgumentException("A choice branch must contain a node.", nameof(whenFalse));
        _builder.Route(route, PregelRoutePhase.BeforeNode,
            context => PregelRoute.To(predicate(context.Read(state)) ? yes : no),
            PregelRoute.To(yes), PregelRoute.To(no));
        var join = _builder.Node($"{nodeId}:join", (_, _) => ValueTask.CompletedTask);
        _builder.AddEdge(left._tail, join);
        _builder.AddEdge(right._tail, join);
        _tail = join;
        return this;
    }

    internal PregelLinearGraphDsl(
        PregelGraphBuilder builder,
        NodeRef<PregelUnit> entry,
        PregelVariable<TInput> input)
    {
        _builder = builder;
        _tail = entry;
        _input = input;
    }

    /// <summary>
    /// Forks into branches in this graph and continues only after every branch succeeds.
    /// Branches share the original input and retain their own node identities and events.
    /// Execution follows the kernel's superstep barriers, including within multi-node branches.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Parallel(params Action<PregelLinearGraphDsl<TInput>>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length < 2)
            throw new ArgumentException("Parallel requires at least two branches.", nameof(branches));
        foreach (var branch in branches) ArgumentNullException.ThrowIfNull(branch);

        var prefix = $"parallel:{_tail.Id.Value}";
        ThenContext($"{prefix}:fork", (_, _) => ValueTask.CompletedTask);
        var fork = _tail;
        _builder.StageRoutingNodes?.Add(fork.Id);
        _builder.BarrierControlNodes.Add(fork.Id);
        var arrivals = Enumerable.Range(0, branches.Length)
            .Select(index => new NodeId($"{prefix}:arrive:{index}")).ToArray();
        var barrier = _builder.NamedBarrierChannel($"{prefix}:complete", arrivals);
        for (var index = 0; index < branches.Length; index++)
        {
            var branch = new PregelLinearGraphDsl<TInput>(_builder, fork, _input).AsBranch($"{prefix}:branch:{index}");
            branches[index](branch);
            if (branch._tail == fork)
                throw new ArgumentException("Parallel branches must contain at least one node.", nameof(branches));
            branch.CloseBranch();
            branch.ThenContext(arrivals[index].Value, (context, _) =>
            {
                context.Write(barrier, PregelUnit.Value);
                return ValueTask.CompletedTask;
            });
            _builder.StageRoutingNodes?.Add(branch._tail.Id);
            _builder.BarrierControlNodes.Add(branch._tail.Id);
        }

        // An ordinary incoming edge is an OR trigger. Only the named barrier may trigger this join.
        var join = _builder.Node($"{prefix}:join", (_, _) => ValueTask.CompletedTask);
        _builder.TriggerOn(join, barrier);
        _builder.StageRoutingNodes?.Add(join.Id);
        _builder.BarrierControlNodes.Add(join.Id);
        _tail = join;
        return this;
    }

    public PregelLinearGraphDsl<TInput> Then(
        Action<TInput, PregelNodeContext, CancellationToken> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return ThenReading(_builder.TaskId(task), (context, token) =>
        {
            task(context.Read(_input), context, token);
            return ValueTask.CompletedTask;
        }, retryPolicy);
    }

    public PregelLinearGraphDsl<TInput> Then(
        Func<TInput, PregelNodeContext, CancellationToken, Task> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        return ThenReading(_builder.TaskId(task),
            (context, token) => new ValueTask(task(context.Read(_input), context, token)), retryPolicy);
    }

    /// <summary>Pass only the data a reusable node needs, without imposing a shared graph input type.</summary>
    public PregelLinearGraphDsl<TInput> Then<TNodeInput>(
        Func<TNodeInput, PregelNodeContext, CancellationToken, Task> task,
        Func<TInput, TNodeInput> selectInput,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(selectInput);
        return ThenReading(_builder.TaskId(task),
            (context, token) => new ValueTask(task(selectInput(context.Read(_input)), context, token)), retryPolicy);
    }

    public PregelLinearGraphDsl<TInput> Then<TNodeInput>(
        Action<TNodeInput, PregelNodeContext, CancellationToken> task,
        Func<TInput, TNodeInput> selectInput,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(selectInput);
        return ThenReading(_builder.TaskId(task), (context, token) =>
        {
            task(selectInput(context.Read(_input)), context, token);
            return ValueTask.CompletedTask;
        }, retryPolicy);
    }

    /// <summary>Bind the final node's return value as the graph output.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Then<TOutput>(
        Func<TInput, PregelNodeContext, CancellationToken, Task<TOutput>> task)
        => Then(task, retryPolicy: null);

    /// <summary>Bind the final asynchronous result with a retry policy.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Then<TOutput>(
        Func<TInput, PregelNodeContext, CancellationToken, Task<TOutput>> task,
        PregelRetryPolicy? retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(task);
        var output = _builder.Output<TOutput>("result", default!);
        ThenReading(_builder.TaskId(task), async (context, token) =>
        {
            var result = await task(context.Read(_input), context, token).ConfigureAwait(false);
            context.Write(output, result);
        }, retryPolicy);
        return new PregelResultGraphDsl<TInput, TOutput>(_builder, _input, output, _tail);
    }

    /// <summary>Bind the final node's return value as the graph output.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Then<TOutput>(
        Func<TInput, PregelNodeContext, CancellationToken, TOutput> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var output = _builder.Output<TOutput>("result", default!);
        ThenReading(_builder.TaskId(task), (context, token) =>
        {
            var result = task(context.Read(_input), context, token);
            context.Write(output, result);
            return ValueTask.CompletedTask;
        }, retryPolicy);
        return new PregelResultGraphDsl<TInput, TOutput>(_builder, _input, output, _tail);
    }

    /// <summary>
    /// Appends a synchronous node after the current tail.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Then(
        string nodeId,
        Action handler,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ThenContext(
            nodeId,
            (_, _) =>
            {
                handler();
                return ValueTask.CompletedTask;
            },
            retryPolicy);
    }

    /// <summary>
    /// Appends an asynchronous node after the current tail.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Then(
        string nodeId,
        Func<CancellationToken, Task> handler,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ThenContext(
            nodeId,
            (_, cancellationToken) => new ValueTask(handler(cancellationToken)),
            retryPolicy);
    }

    /// <summary>
    /// Appends a synchronous node that receives the original Run input.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Then(
        string nodeId,
        Action<TInput> handler,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ThenReading(
            nodeId,
            (context, _) =>
            {
                handler(context.Read(_input));
                return ValueTask.CompletedTask;
            },
            retryPolicy);
    }

    /// <summary>
    /// Appends an asynchronous node that receives the original Run input.
    /// </summary>
    public PregelLinearGraphDsl<TInput> Then(
        string nodeId,
        Func<TInput, CancellationToken, Task> handler,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ThenReading(
            nodeId,
            (context, cancellationToken) =>
                new ValueTask(handler(context.Read(_input), cancellationToken)),
            retryPolicy);
    }

    /// <summary>
    /// Appends a node that needs direct access to the Pregel invocation context.
    /// </summary>
    public PregelLinearGraphDsl<TInput> ThenContext(
        string nodeId,
        Func<PregelNodeContext, CancellationToken, ValueTask> handler,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Append(_builder.Node(nodeId, handler, retryPolicy));
        return this;
    }

    private PregelLinearGraphDsl<TInput> ThenReading(
        string nodeId,
        Func<PregelNodeContext, CancellationToken, ValueTask> handler,
        PregelRetryPolicy? retryPolicy)
    {
        var node = _builder.Node(nodeId, handler, retryPolicy);
        _builder.DeclareRead(node, _input);
        Append(node);
        return this;
    }

    private void Append(NodeRef<PregelUnit> node)
    {
        _first ??= node;
        _builder.AddEdge(_tail, node);
        _tail = node;
    }

    /// <summary>
    /// Compiles this DSL definition into the same kernel graph used by the low-level builder.
    /// </summary>
    public PregelGraph<TInput> Compile() => new(_builder.Compile(), _input);
}

/// <summary>
/// A compiled, strongly typed Pregel graph with convenience run methods.
/// </summary>
public sealed class PregelGraph<TInput>
{
    private readonly PregelVariable<TInput> _input;
    internal PregelVariable<TInput> InputVariable => _input;

    internal PregelGraph(CompiledPregelGraph compiled, PregelVariable<TInput> input)
    {
        Compiled = compiled;
        _input = input;
    }

    /// <summary>
    /// Gets the kernel graph produced by the DSL.
    /// </summary>
    public CompiledPregelGraph Compiled { get; }

    public PregelRun Prepare(TInput input, IPregelRunObserver? observer = null,
        PregelEngine? engine = null, PregelRunOptions? options = null) =>
        new(Compiled, new PregelVariables().Set(_input, input), engine,
            observer is null ? options : (options ?? new PregelRunOptions()) with { Observer = observer });

    public ValueTask<PregelRunResult> RunAsync(TInput input, IPregelRunObserver observer,
        CancellationToken cancellationToken = default) =>
        RunAsync(new PregelEngine(), input, new PregelRunOptions(Observer: observer), cancellationToken);

    /// <summary>
    /// Runs the graph with a new in-memory engine.
    /// </summary>
    public ValueTask<PregelRunResult> RunAsync(
        TInput input,
        CancellationToken cancellationToken = default) =>
        new(Prepare(input).RunAsync(cancellationToken));

    /// <summary>
    /// Runs the graph with a caller-owned engine and checkpoint store.
    /// </summary>
    public ValueTask<PregelRunResult> RunAsync(
        PregelEngine engine,
        TInput input,
        PregelRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new(Prepare(input, engine: engine, options: options).RunAsync(cancellationToken));
    }
}
