namespace Heron.Pregel;

/// <summary>Builds a graph whose nodes receive the flow's current value.</summary>
public sealed partial class PregelFlow<TInput, TCurrent>
{
    private readonly PregelGraphBuilder _builder;
    private readonly PregelVariable<TInput> _input;
    private readonly PregelFlowSlot<TCurrent> _current;
    private readonly Dictionary<NodeId, ChannelId>? _earlyReturns;
    private readonly NodeRef<PregelUnit>? _tail;
    private readonly bool _changesType;
    private readonly bool _writesValue;
    private readonly bool _isNode;
    private readonly List<Func<TCurrent, string?>>? _branchConditions;
    private readonly IPregelResultSkip<TCurrent>? _graphCall;

    private PregelFlow(PregelGraphBuilder builder, PregelVariable<TInput> input, PregelFlowSlot<TCurrent> current,
        Dictionary<NodeId, ChannelId>? earlyReturns, NodeRef<PregelUnit>? tail = null,
        bool changesType = false, bool writesValue = false, bool isNode = false,
        List<Func<TCurrent, string?>>? branchConditions = null, IPregelResultSkip<TCurrent>? graphCall = null)
    {
        _builder = builder;
        _input = input;
        _current = current;
        _earlyReturns = earlyReturns;
        _tail = tail;
        _changesType = changesType;
        _writesValue = writesValue;
        _isNode = isNode;
        _branchConditions = branchConditions;
        _graphCall = graphCall;
    }

    internal static PregelFlow<TInput, TInput> Start(string graphId, List<Func<TInput, string?>>? branchConditions = null)
    {
        var builder = new PregelGraphBuilder(graphId);
        var input = builder.Variable<TInput>("input");
        var current = new PregelFlowSlot<TInput>(builder, "flow:input", input);
        return new(builder, input, current, [], branchConditions: branchConditions);
    }

    /// <summary>Runs a named synchronous node and keeps the current value.</summary>
    public PregelFlow<TInput, TCurrent> Then(Action<TCurrent, PregelNodeContext, CancellationToken> node) =>
        AppendEffect(node, (value, context, token) =>
        {
            node(value, context, token);
            return ValueTask.CompletedTask;
        });

    /// <summary>Runs a named asynchronous node and keeps the current value.</summary>
    public PregelFlow<TInput, TCurrent> Then(Func<TCurrent, PregelNodeContext, CancellationToken, Task> node) =>
        AppendEffect(node, (value, context, token) => new(node(value, context, token)));

    /// <summary>Passes the named synchronous node's result to the next step.</summary>
    public PregelFlow<TInput, TNext> Then<TNext>(Func<TCurrent, PregelNodeContext, CancellationToken, TNext> node) =>
        AppendResult<TNext>(node, (value, context, token) => new(node(value, context, token)));

    /// <summary>Passes the named asynchronous node's result to the next step.</summary>
    public PregelFlow<TInput, TNext> Then<TNext>(Func<TCurrent, PregelNodeContext, CancellationToken, Task<TNext>> node) =>
        AppendResult<TNext>(node, (value, context, token) => new(node(value, context, token)));

    private PregelFlow<TInput, TCurrent> AppendEffect(Delegate identity,
        Func<TCurrent, PregelNodeContext, CancellationToken, ValueTask> run)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var node = Append(_builder.TaskId(identity), (context, token) => run(_current.Read(context), context, token));
        return new(_builder, _input, _current, _earlyReturns, node, isNode: true);
    }

    private PregelFlow<TInput, TNext> AppendResult<TNext>(Delegate identity,
        Func<TCurrent, PregelNodeContext, CancellationToken, ValueTask<TNext>> run)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var id = _builder.TaskId(identity);
        var changesType = typeof(TCurrent) != typeof(TNext);
        var output = NextSlot<TNext>(id);
        var node = Append(id, async (context, token) =>
            output.Write(context, await run(_current.Read(context), context, token).ConfigureAwait(false)));
        return new(_builder, _input, output, _earlyReturns, node, changesType, writesValue: true, isNode: true);
    }

    private PregelFlowSlot<TNext> NextSlot<TNext>(string id) =>
        typeof(TCurrent) == typeof(TNext) ? (PregelFlowSlot<TNext>)(object)_current
            : new(_builder, $"flow:{id}");

    private NodeRef<PregelUnit> Append(string id, Func<PregelNodeContext, CancellationToken, ValueTask> run)
    {
        var node = _builder.Node(id, run);
        _current.DeclareRead(_builder, node);
        Connect(node);
        return node;
    }

    private void Connect(NodeRef<PregelUnit> node)
    {
        if (_tail is null) _builder.SetEntry(node);
        else _builder.AddEdge(_tail, node);
    }

    /// <summary>Tests the preceding node's input. Skipping preserves that value.</summary>
    public PregelFlow<TInput, TCurrent> When(Func<TCurrent, bool> predicate, string reason = "运行条件不满足")
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (_branchConditions is { } conditions)
        {
            conditions.Add(value => predicate(value) ? null : reason);
            return this;
        }
        var node = SkippableNode(nameof(When));
        _builder.ConfigureCondition(node.Runtime, context => predicate(_current.Read(context)), reason);
        return this;
    }

    /// <summary>Sets the preceding node's retry policy.</summary>
    public PregelFlow<TInput, TCurrent> Retry(PregelRetryPolicy policy)
    {
        _builder.ConfigureRetry(LastStep(nameof(Retry)), policy);
        return this;
    }

    /// <summary>Continues after the preceding node fails without changing the committed value.</summary>
    public PregelFlow<TInput, TCurrent> ContinueOnError()
    {
        _builder.ContinueOnError(SkippableNode(nameof(ContinueOnError)).Id);
        return this;
    }

    /// <summary>Completes the graph when the preceding node leaves a matching value.</summary>
    public PregelFlow<TInput, TCurrent> ReturnIf(Func<TCurrent, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var node = LastStep(nameof(ReturnIf));
        if (_earlyReturns is null)
            throw new InvalidOperationException("ReturnIf cannot end a graph from a fixed parallel branch or Finally.");
        _builder.Route(node, PregelRoutePhase.AfterNode,
            context => predicate(_writesValue ? _current.ReadResult(context) : _current.Read(context))
                ? PregelRoute.End : PregelRoute.Continue,
            PregelRoute.End, PregelRoute.Continue);
        _earlyReturns.Add(node.Id, _current.Id);
        return this;
    }

    /// <summary>Reads graph mutex declarations from the owning type or member.</summary>
    public PregelFlow<TInput, TCurrent> MutexRules(Type hostType, string? memberName = null)
    {
        _builder.MutexRules(hostType, memberName);
        return this;
    }

    public PregelGraph<TInput, TCurrent> Compile()
    {
        var tail = _tail ?? throw new InvalidOperationException("Compile requires at least one node.");
        RequireFinalChain(tail);
        if (_earlyReturns is null) throw new InvalidOperationException("Only a graph body can be compiled.");
        foreach (var (node, channel) in _earlyReturns)
        {
            if (channel != _current.Id)
                throw new InvalidOperationException($"ReturnIf at '{node.Value}' cannot supply the graph's final result.");
        }

        var graph = _builder.Compile();
        return new(graph, _input, result => _current.Read(graph, result));
    }

    private void RequireFinalChain(NodeRef<PregelUnit> tail)
    {
        var finalization = _builder.Finally?.NodeIds;
        var last = _builder.DefinedNodeIds.Last(id => finalization is null || !finalization.Contains(id));
        if (last != tail.Id) throw new InvalidOperationException("The final chain must be returned.");
    }

    private NodeRef<PregelUnit> LastStep(string operation) =>
        _isNode ? _tail! : throw new InvalidOperationException($"{operation} requires a preceding Then node.");

    private NodeRef<PregelUnit> SkippableNode(string operation)
    {
        var node = LastStep(operation);
        if (_changesType)
            throw new InvalidOperationException($"{operation} cannot skip a node that changes the flow type.");
        return node;
    }
}

internal sealed record PregelFlowValue<T>(bool IsSet, T? Value);

internal sealed class PregelFlowSlot<T>(
    PregelGraphBuilder builder, string id, PregelVariable<T>? initial = null)
{
    private readonly PregelVariable<PregelFlowValue<T>> _value = builder.Output(id, new PregelFlowValue<T>(false, default));

    public ChannelId Id => _value.Channel.Id;

    public PregelFlowValue<T> Snapshot(PregelNodeContext context)
    {
        var committed = context.Read(_value);
        return !committed.IsSet && initial is not null ? new(true, context.Read(initial)) : committed;
    }

    public T Read(PregelNodeContext context) => Required(Snapshot(context));

    public T Read(CompiledPregelGraph graph, PregelRunResult result)
    {
        var committed = graph.Read(result, _value);
        return !committed.IsSet && initial is not null ? graph.Read(result, initial) : Required(committed);
    }

    public T ReadResult(PregelNodeContext context) => Required(context.ReadResult(_value));

    public void Write(PregelNodeContext context, T value) => context.Write(_value, new(true, value));

    public void DeclareRead(PregelGraphBuilder graph, NodeRef<PregelUnit> node)
    {
        graph.DeclareRead(node, _value);
        if (initial is not null) graph.DeclareRead(node, initial);
    }

    private static T Required(PregelFlowValue<T> value) => value.IsSet ? value.Value!
        : throw new PregelException("flow_value_unavailable", "The flow has no committed value of the requested type.");
}
