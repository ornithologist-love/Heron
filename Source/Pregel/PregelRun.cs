namespace Heron.Pregel;

/// <summary>Owns one run's identity, concurrency, durable continuation and terminal result.</summary>
public class PregelRun
{
    protected CompiledPregelGraph Graph { get; }
    private readonly PregelVariables _variables;
    private readonly PregelEngine _engine;
    private readonly PregelRunOptions _options;
    private readonly bool _mustExist;
    private int _executing;

    internal PregelRun(CompiledPregelGraph graph, PregelVariables variables,
        PregelEngine? engine, PregelRunOptions? options, PregelRunId? runId = null, bool mustExist = false)
    {
        Graph = graph;
        _variables = variables;
        _engine = engine ?? new PregelEngine();
        _options = options ?? new PregelRunOptions();
        _mustExist = mustExist;
        Id = runId ?? new PregelRunId(Guid.NewGuid().ToString("N"));
    }

    public PregelRunId Id { get; }
    public PregelRunResult? Result { get; private set; }

    /// <summary>Attaches to an existing run without executing or acknowledging its interrupt.</summary>
    public static async Task<PregelRun> RestoreAsync(CompiledPregelGraph graph, PregelRunId runId,
        PregelEngine engine, PregelRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(engine);
        var checkpoint = await engine.GetStateAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");
        PregelEngine.EnsureGraphMatches(graph, checkpoint);
        return new PregelRun(graph, PregelVariables.Empty, engine, options, runId, mustExist: true)
        {
            Result = new(checkpoint.Status, checkpoint.RunId, checkpoint.CheckpointId, checkpoint,
                checkpoint.Error, checkpoint.Interrupt),
        };
    }

    /// <summary>Starts or continues this run; an interrupt is acknowledged with Unit.</summary>
    public Task<PregelRunResult> RunAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(_options, null, null, cancellationToken);

    /// <summary>Stop before this named node. A subsequent RunAsync continues from the interrupt.</summary>
    public Task<PregelRunResult> RunUntilAsync(Delegate node, CancellationToken cancellationToken = default) =>
        ExecuteAsync(_options with { InterruptBefore = PregelInterruptSelector.Nodes(Pregel.NodeId(node)) }, null, null, cancellationToken);

    public Task<PregelRunResult> ResumeAsync(string interruptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptId);
        return ExecuteAsync(_options, interruptId, null, cancellationToken);
    }

    public Task<PregelRunResult> ResumeWithValueAsync<T>(string interruptId, T value,
        ICheckpointCodec<T>? codec = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptId);
        ArgumentNullException.ThrowIfNull(value);
        return ExecuteAsync(_options, interruptId,
            (options, token) => _engine.ResumeWithValueAsync(Graph, Id, interruptId, value, codec, options, token),
            cancellationToken);
    }

    /// <summary>Continues with an already encoded value, preserving its original codec and bytes.</summary>
    public Task<PregelRunResult> ResumeWithValueAsync(string interruptId, PregelEncodedValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ResumeWithValueAsync(interruptId, value.Copy(), new EncodedCodec(value.CodecId), cancellationToken);
    }

    private async Task<PregelRunResult> ExecuteAsync(PregelRunOptions options, string? interruptId,
        Func<PregelRunOptions, CancellationToken, ValueTask<PregelRunResult>>? resume, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            throw new InvalidOperationException("This run is already executing.");
        try
        {
            if (Result?.Status is PregelRunStatus.Completed or PregelRunStatus.Failed or PregelRunStatus.Cancelled) return Result;
            // Always consult durable state after an interrupt or a failed store operation;
            // the commit may have succeeded even when its caller did not receive a result.
            var existing = await _engine.GetStateAsync(Id, CancellationToken.None).ConfigureAwait(false);
            if (existing is null)
            {
                if (_mustExist || interruptId != null)
                    throw new PregelException("run_not_found", $"Run '{Id}' does not exist.");
                return Result = await _engine.StartAsync(Graph, Id, _variables, options, token).ConfigureAwait(false);
            }
            PregelEngine.EnsureGraphMatches(Graph, existing);
            if (existing.Status == PregelRunStatus.Interrupted && existing.Interrupt is { } interrupt)
            {
                if (interruptId != null && !StringComparer.Ordinal.Equals(interruptId, interrupt.InterruptId))
                    throw new PregelException("interrupt_id_mismatch", "The supplied interrupt ID is not the run's active interrupt.");
                return Result = resume is null
                    ? await _engine.ResumeAsync(Graph, Id, interrupt.InterruptId, options, token).ConfigureAwait(false)
                    : await resume(options, token).ConfigureAwait(false);
            }
            return Result = await _engine.ResumeAsync(Graph, Id, options, token).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _executing, 0); }
    }

    private sealed class EncodedCodec(string codecId) : ICheckpointCodec<PregelEncodedValue>
    {
        public string CodecId => codecId;
        public byte[] Encode(PregelEncodedValue value) => value.Data;
        public PregelEncodedValue Decode(ReadOnlySpan<byte> data) => new(CodecId, data.ToArray());
    }
}

/// <summary>Adds typed output reading to the shared run lifecycle.</summary>
public sealed class PregelRun<TOutput> : PregelRun
{
    private readonly PregelVariable<TOutput> _output;

    internal PregelRun(CompiledPregelGraph graph, PregelVariable<TOutput> output, PregelVariables variables,
        PregelEngine? engine, PregelRunOptions? options) : base(graph, variables, engine, options) => _output = output;

    public TOutput Read() => Graph.Read(Result
        ?? throw new InvalidOperationException("The run has not returned a result."), _output);
}

/// <summary>A typed graph whose final node returns its output.</summary>
public sealed class PregelGraph<TInput, TOutput>
{
    private readonly PregelVariable<TInput> _input;
    internal PregelVariable<TInput> InputVariable => _input;
    private readonly PregelVariable<TOutput> _output;

    internal PregelGraph(CompiledPregelGraph compiled, PregelVariable<TInput> input, PregelVariable<TOutput> output)
    {
        Compiled = compiled;
        _input = input;
        _output = output;
    }

    public CompiledPregelGraph Compiled { get; }
    public TOutput Read(PregelRunResult result) => Compiled.Read(result, _output);

    public PregelRun<TOutput> Prepare(TInput input, IPregelRunObserver? observer = null,
        PregelEngine? engine = null, PregelRunOptions? options = null) =>
        new(Compiled, _output, new PregelVariables().Set(_input, input), engine,
            observer is null ? options : (options ?? new PregelRunOptions()) with { Observer = observer });

    public Task<PregelRunResult> RunAsync(TInput input, CancellationToken cancellationToken = default) =>
        Prepare(input).RunAsync(cancellationToken);
}

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    /// <summary>Groups result transformations while preserving the original run input and output binding.</summary>
    public PregelResultGraphDsl<TInput, TNext> Stage<TNext>(string id, string title,
        Func<PregelResultGraphDsl<TInput, TOutput>, PregelResultGraphDsl<TInput, TNext>> configure,
        Func<TInput, bool>? when = null, string skipReason = "阶段运行条件不满足")
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        PregelResultGraphDsl<TInput, TNext>? result = null;
        var flow = new PregelLinearGraphDsl<TInput>(_builder, _tail, _input);
        flow.Stage(id, title, stage =>
        {
            _tail = stage.Tail;
            _resultNode = null;
            result = configure(this) ?? throw new ArgumentException("A result stage must return its result chain.", nameof(configure));
            if (!result.IsTailOf(_builder, _builder.DefinedNodeIds.Last()))
                throw new ArgumentException("A result stage must return the final result chain from this stage.", nameof(configure));
        }, when, skipReason);
        return result!;
    }

    /// <summary>Runs branches against the previous committed output without overwriting it.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Parallel(params Action<PregelLinearGraphDsl<TOutput>>[] branches)
    {
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var flow = new PregelLinearGraphDsl<TOutput>(_builder, _tail, _output).Parallel(branches);
        _tail = flow.Tail;
        _resultNode = null;
        return this;
    }

    /// <summary>Transforms the committed output into a new result type.</summary>
    public PregelResultGraphDsl<TInput, TNext> Then<TNext>(
        Func<TOutput, PregelNodeContext, CancellationToken, Task<TNext>> task)
        => Then(task, retryPolicy: null);

    /// <summary>Transforms the committed output asynchronously with a retry policy.</summary>
    public PregelResultGraphDsl<TInput, TNext> Then<TNext>(
        Func<TOutput, PregelNodeContext, CancellationToken, Task<TNext>> task,
        PregelRetryPolicy? retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var id = _builder.TaskId(task);
        var output = _builder.Output<TNext>($"result:{id}", default!);
        var node = _builder.Node(id, async (context, token) =>
            context.Write(output, await task(context.Read(_output), context, token).ConfigureAwait(false)), retryPolicy);
        _builder.DeclareRead(node, _output);
        _builder.AddEdge(_tail, node);
        return new PregelResultGraphDsl<TInput, TNext>(_builder, _input, output, node);
    }

    private readonly PregelGraphBuilder _builder;
    private readonly PregelVariable<TInput> _input;
    private readonly PregelVariable<TOutput> _output;

    private NodeRef<PregelUnit>? _tail;
    private NodeRef<PregelUnit>? _resultNode;
    internal NodeRef<PregelUnit>? Tail => _tail;

    internal bool IsTailOf(PregelGraphBuilder builder, NodeId node) =>
        ReferenceEquals(_builder, builder) && _tail is { } tail && tail.Id.Equals(node);

    /// <summary>Skips the current node when false; its output retains the previously committed value.</summary>
    public PregelResultGraphDsl<TInput, TOutput> When(Func<TInput, bool> predicate,
        string skipReason = "运行条件不满足")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_tail is null) throw new InvalidOperationException("This result node cannot be conditioned.");
        _builder.ConfigureCondition(_tail.Runtime, context => predicate(context.Read(_input)), skipReason);
        _builder.DeclareRead(_tail, _input);
        return this;
    }

    internal PregelResultGraphDsl(PregelGraphBuilder builder, PregelVariable<TInput> input, PregelVariable<TOutput> output,
        NodeRef<PregelUnit>? tail = null)
    {
        _builder = builder;
        _input = input;
        _output = output;
        _tail = tail;
        _resultNode = tail;
    }

    /// <summary>Commit the current node's output and finish normally when the predicate matches.</summary>
    public PregelResultGraphDsl<TInput, TOutput> FastReturn(Func<TOutput, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Route(result => predicate(result) ? PregelRoute.End : PregelRoute.Continue,
            PregelRoute.End, PregelRoute.Continue);
    }

    /// <summary>Selects a declared destination using this node's new result, before the barrier commits.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Route(Func<TOutput, PregelRoute> select,
        params PregelRoute[] destinations)
    {
        ArgumentNullException.ThrowIfNull(select);
        if (_resultNode is null) throw new InvalidOperationException("Result routing requires a result-producing node.");
        var output = _output;
        _builder.Route(_resultNode, PregelRoutePhase.AfterNode, context => select(context.ReadResult(output)), destinations);
        return this;
    }

    public PregelResultGraphDsl<TInput, TOutput> Then(
        Func<TInput, PregelNodeContext, CancellationToken, Task<TOutput>> task,
        Func<TInput, TOutput, TInput> selectInput,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(selectInput);
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var node = _builder.Node(_builder.TaskId(task), async (context, token) =>
        {
            var result = await task(selectInput(context.Read(_input), context.Read(_output)), context, token).ConfigureAwait(false);
            context.Write(_output, result);
        }, retryPolicy);
        _builder.DeclareRead(node, _input);
        _builder.DeclareRead(node, _output);
        _builder.AddEdge(_tail, node);
        _tail = node;
        _resultNode = node;
        return this;
    }

    /// <summary>Pass the previous committed result to the next node.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Then(
        Func<TOutput, PregelNodeContext, CancellationToken, Task<TOutput>> task)
    {
        // Match the generic transform overload's arity so same-type calls prefer this
        // non-generic overload and retain the committed state when the node is skipped.
        return Then(task, retryPolicy: null);
    }

    /// <summary>Pass the previous committed result to the next node with a retry policy.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Then(
        Func<TOutput, PregelNodeContext, CancellationToken, Task<TOutput>> task,
        PregelRetryPolicy? retryPolicy)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var node = _builder.Node(_builder.TaskId(task), async (context, token) =>
        {
            var result = await task(context.Read(_output), context, token).ConfigureAwait(false);
            context.Write(_output, result);
        }, retryPolicy);
        _builder.DeclareRead(node, _output);
        _builder.AddEdge(_tail, node);
        _tail = node;
        _resultNode = node;
        return this;
    }

    public PregelResultGraphDsl<TInput, TOutput> Then(
        Func<TOutput, PregelNodeContext, CancellationToken, TOutput> task,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var node = _builder.Node(_builder.TaskId(task), (context, token) =>
        {
            var result = task(context.Read(_output), context, token);
            context.Write(_output, result);
            return ValueTask.CompletedTask;
        }, retryPolicy);
        _builder.DeclareRead(node, _output);
        _builder.AddEdge(_tail, node);
        _tail = node;
        _resultNode = node;
        return this;
    }

    /// <summary>Append a node while retaining the graph's current output.</summary>
    public PregelResultGraphDsl<TInput, TOutput> Then<TNodeInput>(
        Func<TNodeInput, PregelNodeContext, CancellationToken, Task> task,
        Func<TInput, TNodeInput> selectInput,
        PregelRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(selectInput);
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var node = _builder.Node(_builder.TaskId(task),
            (context, token) => new ValueTask(task(selectInput(context.Read(_input)), context, token)), retryPolicy);
        _builder.DeclareRead(node, _input);
        _builder.AddEdge(_tail, node);
        _tail = node;
        _resultNode = null;
        return this;
    }

    /// <summary>Recovers using this chain's last committed output; a null output skips the callback.</summary>
    public PregelResultGraphDsl<TInput, TOutput> OnFailure(
        Func<TOutput, PregelNodeContext, CancellationToken, Task> recovery)
    {
        _builder.ConfigureRecovery(_output, recovery,
            (state, context, token) => state is null ? Task.CompletedTask : recovery(state, context, token));
        return this;
    }

    /// <summary>
    /// Recovers using the specified state's last committed value, including its initial/null value.
    /// The callback has an independent bounded token and cannot write state or issue graph commands.
    /// </summary>
    public PregelResultGraphDsl<TInput, TOutput> OnFailure<TState>(PregelVariable<TState> state,
        Func<TState?, PregelNodeContext, CancellationToken, Task> recovery)
    {
        _builder.ConfigureRecovery(state, recovery, recovery);
        return this;
    }

    public PregelGraph<TInput, TOutput> Compile() => new(_builder.Compile(), _input, _output);
}
