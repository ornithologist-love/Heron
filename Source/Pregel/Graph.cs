using System.Collections.Frozen;
using System.Globalization;

namespace Heron.Pregel;

public sealed record PregelRetryPolicy(
    int MaxAttempts = 1,
    TimeSpan? Delay = null,
    double BackoffMultiplier = 1.0,
    TimeSpan? MaxDelay = null,
    IReadOnlySet<string>? RetryableErrorCodes = null)
{
    public static PregelRetryPolicy None { get; } = new();

    public bool ShouldRetry(PregelError error, int completedAttemptNumber)
    {
        if (completedAttemptNumber >= MaxAttempts)
        {
            return false;
        }

        return RetryableErrorCodes is null || RetryableErrorCodes.Count == 0 || RetryableErrorCodes.Contains(error.Code);
    }
}

public sealed record PregelEdge(NodeId From, NodeId To);

/// <summary>A sequential stage. Its entry commits the run condition before any member executes.</summary>
public sealed record PregelStageDefinition(string Id, string Title, NodeId EntryNode,
    IReadOnlyList<NodeId> NodeIds, string SkipReason)
{
    public bool IsFinally { get; init; }
}

public sealed record PregelFinallyDefinition(NodeId EntryNode, IReadOnlyList<NodeId> NodeIds, TimeSpan Timeout);

public sealed record PregelNodeDefinition(
    NodeId Id,
    string InputTypeId,
    string InputCodecId,
    PregelEncodedValue PullInput,
    IReadOnlyList<ChannelId> Triggers,
    IReadOnlyList<ChannelId> ReadChannels,
    PregelRetryPolicy RetryPolicy)
{
    public PregelNodeMetadata? Metadata { get; init; }
    public string? SkipReason { get; init; }
    public IReadOnlyList<PregelRouteDefinition> Routes { get; init; } = [];
    public IReadOnlyList<PregelRouteScopeDefinition> RouteScopes { get; init; } = [];
    public bool ContinueOnError { get; init; }
}

public sealed record PregelGraphDefinition(
    string GraphId,
    string Revision,
    IReadOnlyDictionary<NodeId, PregelNodeDefinition> Nodes,
    IReadOnlyDictionary<ChannelId, PregelChannelDefinition> Channels,
    IReadOnlyList<PregelEdge> Edges,
    NodeId EntryNode,
    string Fingerprint)
{
    public PregelRecoveryDefinition? Recovery { get; init; }
    public PregelFinallyDefinition? Finally { get; init; }
    public IReadOnlyList<string> MutexRules { get; init; } = [];
    public IReadOnlyDictionary<string, int> MutexLimits { get; init; } = FrozenDictionary<string, int>.Empty;
    public IReadOnlyList<PregelStageDefinition> Stages { get; init; } = [];
    public IReadOnlyList<PregelMutationDefinition> Mutations { get; init; } = [];
    public long RunGraphRevision { get; init; }
}

public sealed class NodeRef<TInput>
{
    internal NodeRef(NodeRuntime<TInput> runtime) => Runtime = runtime;
    internal NodeRuntime<TInput> Runtime { get; }
    public NodeId Id => Runtime.Id;
}

public delegate ValueTask PregelNodeHandler<TInput>(
    TInput input,
    PregelNodeContext context,
    CancellationToken cancellationToken);

internal interface INodeRuntime
{
    NodeId Id { get; }
    Type InputType { get; }
    string InputCodecId { get; }
    PregelEncodedValue PullInput { get; }
    PregelRetryPolicy RetryPolicy { get; }
    ValueTask InvokeAsync(PregelEncodedValue input, PregelNodeContext context, CancellationToken cancellationToken);
}

internal sealed class NodeRuntime<TInput>(
    NodeId id,
    TInput pullInput,
    ICheckpointCodec<TInput> codec,
    PregelNodeHandler<TInput> handler,
    PregelRetryPolicy retryPolicy) : INodeRuntime
{
    public NodeId Id { get; } = id;
    public Type InputType => typeof(TInput);
    public string InputCodecId => codec.CodecId;
    public PregelEncodedValue PullInput { get; } = new(codec.CodecId, codec.Encode(pullInput));
    public PregelRetryPolicy RetryPolicy { get; } = retryPolicy;

    internal PregelEncodedValue EncodeInput(TInput value) => new(codec.CodecId, codec.Encode(value));
    internal TInput DecodeInput(ReadOnlySpan<byte> data) => codec.Decode(data);

    public async ValueTask InvokeAsync(
        PregelEncodedValue input,
        PregelNodeContext context,
        CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(input.CodecId, codec.CodecId))
        {
            throw new PregelException(
                "node_input_codec_mismatch",
                $"Node '{Id}' expects codec '{codec.CodecId}', got '{input.CodecId}'.");
        }

        await handler(codec.Decode(input.Data), context, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record EdgeRuntime(PregelEdge Edge, ChannelId ChannelId);

internal sealed record PregelNodeCondition(Func<PregelNodeContext, bool> Predicate, string SkipReason);

public sealed class CompiledPregelGraph
{
    internal CompiledPregelGraph(
        PregelGraphDefinition definition,
        IReadOnlyDictionary<NodeId, INodeRuntime> nodes,
        IReadOnlyDictionary<ChannelId, IChannelRuntime> channels,
        IReadOnlyDictionary<ChannelId, IReadOnlyList<NodeId>> triggerIndex,
        IReadOnlyDictionary<NodeId, IReadOnlyList<EdgeRuntime>> outgoingEdges,
        IChannelRuntime startChannel,
        IReadOnlyDictionary<ChannelId, string> variables)
    {
        Definition = definition;
        Nodes = nodes;
        Channels = channels;
        TriggerIndex = triggerIndex;
        OutgoingEdges = outgoingEdges;
        StartChannel = startChannel;
        Variables = variables;
    }

    internal PregelRecovery? Recovery { get; init; }
    internal CompiledPregelGraph? MutationBase { get; init; }
    internal IReadOnlyDictionary<string, PregelMutationRuntime> Mutations { get; init; } =
        FrozenDictionary<string, PregelMutationRuntime>.Empty;
    public PregelGraphDefinition Definition { get; }
    // The original When DSL and its Continue/Skip routing implementation have identical topology.
    internal string? ConditionCompatibilityFingerprint { get; init; }
    internal bool MatchesFingerprint(string fingerprint) =>
        StringComparer.Ordinal.Equals(Definition.Fingerprint, fingerprint)
        || ConditionCompatibilityFingerprint is { } legacy && StringComparer.Ordinal.Equals(legacy, fingerprint);
    internal IReadOnlyDictionary<NodeId, INodeRuntime> Nodes { get; }
    internal IReadOnlyDictionary<NodeId, PregelRouteRuntime> BeforeRoutes { get; init; } =
        FrozenDictionary<NodeId, PregelRouteRuntime>.Empty;
    internal IReadOnlyDictionary<NodeId, PregelRouteRuntime> AfterRoutes { get; init; } =
        FrozenDictionary<NodeId, PregelRouteRuntime>.Empty;
    internal IReadOnlyDictionary<NodeId, IReadOnlyList<PregelRouteScope>> RouteScopes { get; init; } =
        FrozenDictionary<NodeId, IReadOnlyList<PregelRouteScope>>.Empty;
    internal IReadOnlyDictionary<NodeId, PregelNodeCondition> StageConditions { get; init; } =
        new Dictionary<NodeId, PregelNodeCondition>().ToFrozenDictionary();
    // Nodes a stage condition guards (members plus its routing nodes); a manual stage skip uses the same set.
    internal IReadOnlyDictionary<string, IReadOnlySet<NodeId>> StageGuards { get; init; } =
        FrozenDictionary<string, IReadOnlySet<NodeId>>.Empty;
    // Barrier arrivals and joins must run even when their branch or stage is skipped.
    internal IReadOnlySet<NodeId> BarrierControls { get; init; } = FrozenSet<NodeId>.Empty;
    internal IReadOnlyDictionary<ChannelId, IChannelRuntime> Channels { get; }
    internal IReadOnlyDictionary<ChannelId, IReadOnlyList<NodeId>> TriggerIndex { get; }
    internal IReadOnlyDictionary<NodeId, IReadOnlyList<EdgeRuntime>> OutgoingEdges { get; }
    internal IChannelRuntime StartChannel { get; }

    /// <summary>Reads the last committed value. Failed/cancelled supersteps do not publish partial writes.</summary>
    public T Read<T>(PregelRunResult result, PregelVariable<T> variable)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(variable);
        if (!MatchesFingerprint(result.Checkpoint.GraphFingerprint))
            throw new PregelException("graph_fingerprint_mismatch", "Result belongs to another graph revision.");
        if (!Channels.TryGetValue(variable.Channel.Id, out var channel) || !ReferenceEquals(channel, variable.Channel.Runtime))
            throw new PregelException("foreign_channel", "Variable belongs to another graph.");
        return (T)channel.DecodeState(result.Checkpoint.ChannelValues[variable.Channel.Id]);
    }

    internal IReadOnlyDictionary<ChannelId, string> Variables { get; }
}

public sealed partial class PregelGraphBuilder
{
    internal PregelFinallyDefinition? Finally { get; private set; }
    private readonly HashSet<NodeId> _continueOnError = [];
    internal void ContinueOnError(NodeId node) => _continueOnError.Add(node);
    internal void SetFinally(NodeId entry, IReadOnlyList<NodeId> nodes, TimeSpan timeout)
    {
        if (Finally != null || Recovery != null) throw new InvalidOperationException("Only one Finally or legacy OnFailure can be declared.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        Finally = new(entry, nodes, timeout);
        var index = _stages.FindIndex(stage => stage.EntryNode == entry);
        _stages[index] = _stages[index] with { IsFinally = true };
    }
    internal PregelRecovery? Recovery { get; private set; }

    internal void ConfigureRecovery<TState>(PregelVariable<TState> state, Delegate task,
        Func<TState?, PregelNodeContext, CancellationToken, Task> execute)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(execute);
        EnsureOwned(state.Channel.Runtime);
        if (Recovery is not null || Finally is not null) throw new InvalidOperationException("Recovery or Finally is already declared.");
        Recovery = new(new(new NodeId(PregelTaskIdentity.NodeId(task)), PregelNodeMetadata.FromTask(task)),
            (graph, result, context, token) => execute(graph.Read(result, state), context, token));
    }

    private PregelMutexRule[] _mutexRules = [];

    /// <summary>Projects graph-instance mutex rules from a graph class, property or factory method.</summary>
    public PregelGraphBuilder MutexRules(Type owner, string? memberName = null)
    {
        _mutexRules = PregelMutexRules.Read(owner, memberName);
        return this;
    }

    private readonly Dictionary<NodeId, PregelNodeMetadata> _metadata = [];
    private readonly List<PregelStageDefinition> _stages = [];
    private readonly Dictionary<NodeId, PregelNodeCondition> _stageConditions = [];
    private readonly Dictionary<string, IReadOnlySet<NodeId>> _stageGuards = [];
    internal IReadOnlyCollection<NodeId> DefinedNodeIds => _nodes.Keys;
    internal HashSet<NodeId>? StageRoutingNodes { get; set; }

    internal void AddStage(PregelStageDefinition stage, PregelVariable<bool> enabled, IEnumerable<NodeId>? guardedNodes = null)
    {
        if (_stages.Any(existing => existing.Id == stage.Id))
            throw new InvalidOperationException($"Stage '{stage.Id}' is already defined.");
        var guarded = (guardedNodes ?? stage.NodeIds).ToArray();
        _stageGuards[stage.Id] = guarded.ToFrozenSet();
        foreach (var id in guarded)
        {
            if (_stageConditions.ContainsKey(id))
                throw new InvalidOperationException($"Node '{id}' already belongs to a stage.");
            _stageConditions.Add(id, new(context => context.Read(enabled), stage.SkipReason));
            if (!_reads.TryGetValue(id, out var reads)) _reads[id] = reads = [];
            reads.Add(enabled.Channel.Id);
        }
        _stages.Add(stage);
    }

    internal void Describe(string id, Delegate task) => _metadata[new NodeId(id)] = PregelNodeMetadata.FromTask(task);
    internal void SetMetadata(NodeId id, PregelNodeMetadata metadata) => _metadata[id] = metadata;
    private readonly Dictionary<string, PregelMutationRuntime> _mutations = new(StringComparer.Ordinal);
    internal void AddMutation(PregelMutationRuntime mutation)
    {
        if (!_mutations.TryAdd(mutation.Definition.Id, mutation))
            throw new PregelException("duplicate_mutation", mutation.Definition.Id);
    }

    internal string TaskId(Delegate task)
    {
        var id = PregelTaskIdentity.NodeId(task);
        _metadata[new NodeId(id)] = PregelNodeMetadata.FromTask(task);
        return id;
    }

    private readonly string _graphId;
    private readonly string _revision;
    private readonly Dictionary<NodeId, INodeRuntime> _nodes = [];
    private readonly Dictionary<ChannelId, IChannelRuntime> _channels = [];
    private readonly Dictionary<string, ChannelId> _variables = new(StringComparer.Ordinal);
    private readonly HashSet<PregelEdge> _edges = [];
    private readonly Dictionary<NodeId, HashSet<ChannelId>> _explicitTriggers = [];
    private readonly Dictionary<NodeId, HashSet<ChannelId>> _reads = [];
    private NodeId? _entryNode;

    public PregelGraphBuilder(string graphId, string revision = "1")
    {
        _graphId = PregelId.Validate(graphId, nameof(graphId));
        _revision = PregelId.Validate(revision, nameof(revision));
    }

    public NodeRef<PregelUnit> Node(
        string id,
        Func<PregelNodeContext, CancellationToken, ValueTask> handler,
        PregelRetryPolicy? retryPolicy = null) =>
        Node<PregelUnit>(
            id,
            PregelUnit.Value,
            (_, context, cancellationToken) => handler(context, cancellationToken),
            CheckpointCodecs.Unit,
            retryPolicy);

    public NodeRef<TInput> Node<TInput>(
        string id,
        TInput pullInput,
        PregelNodeHandler<TInput> handler,
        ICheckpointCodec<TInput>? inputCodec = null,
        PregelRetryPolicy? retryPolicy = null)
    {
        if (Finally != null) throw new InvalidOperationException("Finally must be the last declared stage.");
        ArgumentNullException.ThrowIfNull(handler);
        var nodeId = new NodeId(id);
        if (_nodes.ContainsKey(nodeId))
        {
            throw new PregelException("duplicate_node", $"Node '{nodeId}' is already defined.");
        }

        var runtime = new NodeRuntime<TInput>(
            nodeId,
            pullInput,
            inputCodec ?? CheckpointCodecs.Default<TInput>(),
            handler,
            ValidateRetryPolicy(retryPolicy ?? PregelRetryPolicy.None));
        _nodes.Add(nodeId, runtime);
        return new NodeRef<TInput>(runtime);
    }

    public ChannelRef<T, T> LastValueChannel<T>(
        string id,
        T initialValue,
        ICheckpointCodec<T>? codec = null) =>
        AddChannel<T, T>(new LastValueChannelRuntime<T>(new ChannelId(id), initialValue, codec ?? CheckpointCodecs.Default<T>()));

    public ChannelRef<T, T> AnyValueChannel<T>(
        string id,
        T initialValue,
        ICheckpointCodec<T>? codec = null) =>
        AddChannel<T, T>(new AnyValueChannelRuntime<T>(new ChannelId(id), initialValue, codec ?? CheckpointCodecs.Default<T>()));

    public ChannelRef<T, T> EphemeralValueChannel<T>(
        string id,
        T initialValue,
        bool guard = true,
        ICheckpointCodec<T>? codec = null) =>
        AddChannel<T, T>(new EphemeralValueChannelRuntime<T>(new ChannelId(id), initialValue, codec ?? CheckpointCodecs.Default<T>(), guard));

    public ChannelRef<T, T> AggregateChannel<T>(
        string id,
        T initialValue,
        string reducerId,
        Func<T, T, T> reducer,
        ICheckpointCodec<T>? codec = null) =>
        AggregateChannel<T, T>(id, initialValue, reducerId, reducer, codec, codec);

    public ChannelRef<TState, TUpdate> AggregateChannel<TState, TUpdate>(
        string id,
        TState initialValue,
        string reducerId,
        Func<TState, TUpdate, TState> reducer,
        ICheckpointCodec<TState>? stateCodec = null,
        ICheckpointCodec<TUpdate>? updateCodec = null)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        return AddChannel<TState, TUpdate>(new AggregateChannelRuntime<TState, TUpdate>(
            new ChannelId(id),
            initialValue,
            stateCodec ?? CheckpointCodecs.Default<TState>(),
            updateCodec ?? CheckpointCodecs.Default<TUpdate>(),
            PregelId.Validate(reducerId, nameof(reducerId)),
            reducer));
    }

    public ChannelRef<IReadOnlyList<T>, T> TopicChannel<T>(
        string id,
        bool unique = false,
        bool accumulate = false,
        ICheckpointCodec<T>? updateCodec = null,
        ICheckpointCodec<IReadOnlyList<T>>? stateCodec = null) =>
        AddChannel<IReadOnlyList<T>, T>(new TopicChannelRuntime<T>(
            new ChannelId(id),
            stateCodec ?? CheckpointCodecs.Json<IReadOnlyList<T>>($"pregel:topic:{PregelTypeIdentity.Normalize(typeof(T).FullName ?? typeof(T).Name)}:v1"),
            updateCodec ?? CheckpointCodecs.Default<T>(),
            unique,
            accumulate));

    public ChannelRef<NamedBarrierState, PregelUnit> NamedBarrierChannel(string id, params NodeId[] requiredSenders)
    {
        if (requiredSenders.Length == 0)
        {
            throw new PregelException("missing_named_barrier_senders", "Named barrier requires at least one sender.");
        }

        var runtime = new NamedBarrierChannelRuntime(new ChannelId(id), requiredSenders);
        AddChannelRuntime(runtime);
        return new ChannelRef<NamedBarrierState, PregelUnit>(runtime);
    }

    public PregelGraphBuilder AddEdge<TFrom, TTo>(NodeRef<TFrom> from, NodeRef<TTo> to)
    {
        AddEdgeRuntime(from.Runtime, to.Runtime);
        return this;
    }

    internal void AddEdgeRuntime(INodeRuntime from, INodeRuntime to)
    {
        EnsureOwned(from);
        EnsureOwned(to);
        if (!_edges.Add(new PregelEdge(from.Id, to.Id)))
        {
            throw new PregelException("duplicate_edge", $"Edge '{from.Id}' -> '{to.Id}' is already defined.");
        }
    }

    public PregelGraphBuilder TriggerOn<TInput, TState, TUpdate>(
        NodeRef<TInput> node,
        ChannelRef<TState, TUpdate> channel)
    {
        EnsureOwned(node.Runtime);
        EnsureOwned(channel.Runtime);
        if (!_explicitTriggers.TryGetValue(node.Id, out var triggers))
        {
            triggers = [];
            _explicitTriggers.Add(node.Id, triggers);
        }

        triggers.Add(channel.Id);
        return this;
    }

    public PregelGraphBuilder SetEntry<TInput>(NodeRef<TInput> node)
    {
        EnsureOwned(node.Runtime);
        _entryNode = node.Id;
        return this;
    }

    /// <summary>Declares an output with an explicit initial value; no run input is required.</summary>
    public PregelVariable<T> Output<T>(string name, T initialValue, ICheckpointCodec<T>? codec = null)
    {
        var id = new ChannelId($"__var:{PregelId.Validate(name, nameof(name))}");
        var runtime = new LastValueChannelRuntime<T>(id, initialValue,
            codec ?? (initialValue is null ? CheckpointCodecs.Json<T>(allowNull: true) : CheckpointCodecs.Default<T>()));
        if (!_channels.TryAdd(id, runtime))
            throw new PregelException("duplicate_graph_variable", $"Graph variable '{name}' is already defined.");
        return new PregelVariable<T>(name, new ChannelRef<T, T>(runtime));
    }

    /// <summary>
    /// Declares a mutable graph variable. The caller supplies its value when the run starts; nodes may
    /// read it and write a replacement, which becomes visible after the superstep barrier.
    /// The declaration (name, type, codec) is part of the fingerprint; run values are not.
    /// </summary>
    /// <remarks>TODO: const variables (read-only refs) and graph constants are a later iteration.</remarks>
    public PregelVariable<T> Variable<T>(string name, ICheckpointCodec<T>? codec = null)
    {
        var validated = PregelId.Validate(name, nameof(name));
        if (_channels.ContainsKey(new ChannelId($"__var:{validated}")))
        {
            throw new PregelException("duplicate_graph_variable", $"Graph variable '{validated}' is already defined.");
        }

        var channelId = new ChannelId($"__var:{validated}");
        var runtime = new LastValueChannelRuntime<T>(channelId, default!, codec ?? CheckpointCodecs.Default<T>());
        _channels.Add(channelId, runtime);
        _variables.Add(validated, channelId);
        return new PregelVariable<T>(validated, new ChannelRef<T, T>(runtime));
    }

    /// <summary>Runs the node only when the predicate is true; otherwise records Skipped and follows its edges.</summary>
    public void When<TInput>(NodeRef<TInput> node, Func<PregelNodeContext, bool> predicate,
        string skipReason = "运行条件不满足") => ConfigureCondition(node.Runtime, predicate, skipReason);

    private readonly HashSet<NodeId> _legacyConditionNodes = [];
    internal void ConfigureCondition(INodeRuntime node, Func<PregelNodeContext, bool> predicate, string skipReason)
    {
        EnsureOwned(node);
        ArgumentNullException.ThrowIfNull(predicate);
        if (string.IsNullOrWhiteSpace(skipReason)) throw new ArgumentException("A skip reason is required.", nameof(skipReason));
        ConfigureRoute(node, new(PregelRoutePhase.BeforeNode,
            context => predicate(context) ? PregelRoute.Continue : PregelRoute.Skip(skipReason),
            [PregelRoute.Continue, PregelRoute.Skip(skipReason)], "invalid_node_condition"));
        _legacyConditionNodes.Add(node.Id);
    }

    /// <summary>Records that <paramref name="node"/> reads <paramref name="variable"/>; this is part of the fingerprint.</summary>
    internal void DeclareRead<TInput, T>(NodeRef<TInput> node, PregelVariable<T> variable)
    {
        EnsureOwned(node.Runtime);
        EnsureOwned(variable.Channel.Runtime);
        if (!_reads.TryGetValue(node.Id, out var reads))
        {
            reads = [];
            _reads.Add(node.Id, reads);
        }

        reads.Add(variable.Channel.Id);
    }

    public CompiledPregelGraph Compile()
    {
        if (_entryNode is not { } entryNode)
        {
            throw new PregelException("missing_entry_node", "Graph requires an entry node.");
        }

        ValidateRoutes();
        ValidateReachability(entryNode);

        var channels = new Dictionary<ChannelId, IChannelRuntime>(_channels);
        var triggers = _nodes.Keys.ToDictionary(node => node, _ => new HashSet<ChannelId>());
        foreach (var (nodeId, explicitChannels) in _explicitTriggers)
        {
            triggers[nodeId].UnionWith(explicitChannels);
        }

        var outgoing = _nodes.Keys.ToDictionary(node => node, _ => new List<EdgeRuntime>());
        foreach (var edge in _edges.OrderBy(edge => edge.From.Value, StringComparer.Ordinal).ThenBy(edge => edge.To.Value, StringComparer.Ordinal))
        {
            var channelId = EdgeChannelId(edge);
            var runtime = new EphemeralValueChannelRuntime<PregelUnit>(channelId, default, CheckpointCodecs.Unit, guard: true);
            channels.Add(channelId, runtime);
            triggers[edge.To].Add(channelId);
            outgoing[edge.From].Add(new EdgeRuntime(edge, channelId));
        }

        var startChannel = new EphemeralValueChannelRuntime<PregelUnit>(
            new ChannelId("__start"), PregelUnit.Value, CheckpointCodecs.Unit, guard: true);
        channels.Add(startChannel.Definition.Id, startChannel);
        triggers[entryNode].Add(startChannel.Definition.Id);

        var nodeDefinitions = _nodes.Values
            .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
            .ToDictionary(
                node => node.Id,
                node => new PregelNodeDefinition(
                    node.Id,
                    TypeId(node.InputType),
                    node.InputCodecId,
                    node.PullInput.Copy(),
                    triggers[node.Id].OrderBy(id => id.Value, StringComparer.Ordinal).ToArray(),
                    _reads.TryGetValue(node.Id, out var reads)
                        ? reads.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray()
                        : Array.Empty<ChannelId>(),
                    node.RetryPolicy)
                {
                    Metadata = _metadata.GetValueOrDefault(node.Id),
                    SkipReason = _beforeRoutes.GetValueOrDefault(node.Id)?.Definition.Destinations
                        .Select(destination => destination.SkipReason).FirstOrDefault(reason => reason is not null),
                    Routes = Array.AsReadOnly(new[] { _beforeRoutes.GetValueOrDefault(node.Id), _afterRoutes.GetValueOrDefault(node.Id) }
                        .Where(route => route is not null).Select(route => route!.Definition).ToArray()),
                    RouteScopes = _routeScopes.TryGetValue(node.Id, out var scopes)
                        ? Array.AsReadOnly(scopes.Select(scope => scope.Definition).ToArray()) : [],
                    ContinueOnError = _continueOnError.Contains(node.Id)
                });

        var channelDefinitions = channels.Values
            .OrderBy(channel => channel.Definition.Id.Value, StringComparer.Ordinal)
            .ToDictionary(channel => channel.Definition.Id, channel => channel.Definition);
        var edges = _edges.OrderBy(edge => edge.From.Value, StringComparer.Ordinal).ThenBy(edge => edge.To.Value, StringComparer.Ordinal).ToArray();
        var mutexKeys = _mutexRules.Select(rule => rule.Key).ToArray();
        var mutexLimits = PregelMutexRules.Limits(_mutexRules);
        string CompleteFingerprint(string fingerprint)
        {
            if (Recovery is { } recovery) fingerprint = PregelHash.Sha256(fingerprint, recovery.Definition.Id.Value);
            if (Finally != null) fingerprint = PregelHash.Sha256(fingerprint, System.Text.Json.JsonSerializer.Serialize(Finally));
            foreach (var id in _continueOnError.OrderBy(id => id.Value, StringComparer.Ordinal))
                fingerprint = PregelHash.Sha256(fingerprint, "continue-on-error", id.Value);
            if (mutexKeys.Length > 0) fingerprint = PregelHash.Sha256(fingerprint, System.Text.Json.JsonSerializer.Serialize(mutexKeys));
            if (mutexLimits.Count > 0) fingerprint = PregelHash.Sha256(fingerprint,
                System.Text.Json.JsonSerializer.Serialize(_mutexRules.Where(rule => rule.MaxConcurrency != 1).ToArray()));
            foreach (var stage in _stages)
                fingerprint = PregelHash.Sha256(fingerprint, stage.Id, stage.EntryNode.Value,
                    System.Text.Json.JsonSerializer.Serialize(stage.NodeIds), stage.SkipReason);
            foreach (var mutation in _mutations.Values.OrderBy(x => x.Definition.Id, StringComparer.Ordinal))
            {
                fingerprint = PregelHash.Sha256(fingerprint, "mutation:v1", mutation.Definition.Id,
                    mutation.Definition.Mode.ToString(), mutation.Template.Definition.Fingerprint,
                    mutation.Definition.InputCodecId, mutation.Definition.OutputCodecId);
                if (mutation.Definition.HasResultSkipCondition)
                    fingerprint = PregelHash.Sha256(fingerprint, "mutation:result-skip:v1");
                if (mutation.Definition.IsSingleSubgraph)
                    fingerprint = PregelHash.Sha256(fingerprint, "mutation:single-subgraph:v1");
            }
            return fingerprint;
        }
        var fingerprint = CompleteFingerprint(Fingerprint(nodeDefinitions, channelDefinitions, channels, edges, entryNode));
        string? conditionCompatibilityFingerprint = null;
        // Recompute the pre-routing hash only when every route came from the unchanged When API.
        // Explicit routing, scopes and mutation templates retain strict revision matching.
        if (_legacyConditionNodes.Count > 0 && _beforeRoutes.Count == _legacyConditionNodes.Count
            && _afterRoutes.Count == 0 && _routeScopes.Count == 0 && _mutations.Count == 0)
        {
            var legacyNodes = nodeDefinitions.ToDictionary(pair => pair.Key,
                pair => pair.Value with { Routes = [] });
            conditionCompatibilityFingerprint = CompleteFingerprint(
                Fingerprint(legacyNodes, channelDefinitions, channels, edges, entryNode));
        }
        var definition = new PregelGraphDefinition(
            _graphId,
            _revision,
            nodeDefinitions.ToFrozenDictionary(),
            channelDefinitions.ToFrozenDictionary(),
            edges,
            entryNode,
            fingerprint)
        {
            Recovery = Recovery?.Definition, Finally = Finally, MutexRules = Array.AsReadOnly(mutexKeys), MutexLimits = mutexLimits,
            Stages = Array.AsReadOnly(_stages.ToArray()), Mutations = _mutations.Values.Select(x => x.Definition).ToArray()
        };

        var triggerIndex = triggers
            .SelectMany(pair => pair.Value.Select(channel => (channel, pair.Key)))
            .GroupBy(pair => pair.channel)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<NodeId>)group.Select(pair => pair.Key).OrderBy(id => id.Value, StringComparer.Ordinal).ToArray());
        var frozenOutgoing = outgoing.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<EdgeRuntime>)pair.Value.OrderBy(edge => edge.Edge.To.Value, StringComparer.Ordinal).ToArray());

        return new CompiledPregelGraph(
            definition,
            _nodes.ToFrozenDictionary(),
            channels.ToFrozenDictionary(),
            triggerIndex.ToFrozenDictionary(),
            frozenOutgoing.ToFrozenDictionary(),
            startChannel,
            _variables.ToFrozenDictionary(pair => pair.Value, pair => pair.Key))
        {
            Recovery = Recovery, StageConditions = _stageConditions.ToFrozenDictionary(), StageGuards = _stageGuards.ToFrozenDictionary(),
            BarrierControls = BarrierControlNodes.ToFrozenSet(), Mutations = _mutations.ToFrozenDictionary(),
            ConditionCompatibilityFingerprint = conditionCompatibilityFingerprint,
            BeforeRoutes = _beforeRoutes.ToFrozenDictionary(), AfterRoutes = _afterRoutes.ToFrozenDictionary(),
            RouteScopes = CompileRouteScopes()
        };
    }

    private ChannelRef<TState, TUpdate> AddChannel<TState, TUpdate>(IChannelRuntime runtime)
    {
        AddChannelRuntime(runtime);
        return new ChannelRef<TState, TUpdate>(runtime);
    }

    private void AddChannelRuntime(IChannelRuntime runtime)
    {
        if (runtime.Definition.Id.Value.StartsWith("__", StringComparison.Ordinal))
        {
            throw new PregelException(
                "reserved_channel_id",
                $"Channel ID '{runtime.Definition.Id}' uses the reserved '__' prefix.");
        }

        if (_channels.ContainsKey(runtime.Definition.Id))
        {
            throw new PregelException("duplicate_channel", $"Channel '{runtime.Definition.Id}' is already defined.");
        }

        _channels.Add(runtime.Definition.Id, runtime);
    }

    private void EnsureOwned(INodeRuntime node)
    {
        if (!_nodes.TryGetValue(node.Id, out var existing) || !ReferenceEquals(existing, node))
        {
            throw new PregelException("foreign_node", $"Node '{node.Id}' belongs to another graph builder.");
        }
    }

    private void EnsureOwned(IChannelRuntime channel)
    {
        if (!_channels.TryGetValue(channel.Definition.Id, out var existing) || !ReferenceEquals(existing, channel))
        {
            throw new PregelException("foreign_channel", $"Channel '{channel.Definition.Id}' belongs to another graph builder.");
        }
    }

    private void ValidateReachability(NodeId entryNode)
    {
        // Explicit channel triggers are valid entry points too (for example a named-barrier join).
        // Their outgoing edges must be traversed just like the primary entry's edges.
        var reachable = new HashSet<NodeId>(_explicitTriggers.Keys) { entryNode };
        if (Finally != null) reachable.Add(Finally.EntryNode);
        var queue = new Queue<NodeId>();
        foreach (var node in reachable) queue.Enqueue(node);
        while (queue.TryDequeue(out var current))
        {
            foreach (var edge in _edges.Where(edge => edge.From == current))
            {
                if (reachable.Add(edge.To))
                {
                    queue.Enqueue(edge.To);
                }
            }
        }

        var unreachable = _nodes.Keys.Where(node => !reachable.Contains(node) && !_explicitTriggers.ContainsKey(node)).ToArray();
        if (unreachable.Length > 0)
        {
            throw new PregelException("unreachable_node", $"Unreachable nodes: {string.Join(", ", unreachable)}.");
        }
    }

    private string Fingerprint(
        IReadOnlyDictionary<NodeId, PregelNodeDefinition> nodes,
        IReadOnlyDictionary<ChannelId, PregelChannelDefinition> channels,
        IReadOnlyDictionary<ChannelId, IChannelRuntime> channelRuntimes,
        IReadOnlyList<PregelEdge> edges,
        NodeId entryNode)
    {
        var parts = new List<string> { _graphId, _revision, entryNode.Value };
        foreach (var node in nodes.Values.OrderBy(node => node.Id.Value, StringComparer.Ordinal))
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"node:{node.Id.Value}:{node.InputTypeId}:{node.InputCodecId}:{PregelHash.Sha256(Convert.ToBase64String(node.PullInput.Data))}:{node.RetryPolicy.MaxAttempts}:{node.RetryPolicy.Delay?.Ticks}:{node.RetryPolicy.BackoffMultiplier:R}:{node.RetryPolicy.MaxDelay?.Ticks}"));
            if (node.RetryPolicy.RetryableErrorCodes is { } retryableCodes)
            {
                parts.AddRange(retryableCodes.Order(StringComparer.Ordinal).Select(code => $"retry-code:{node.Id.Value}:{code}"));
            }

            parts.AddRange(node.Triggers.Select(trigger => $"trigger:{node.Id.Value}:{trigger.Value}"));
            if (node.SkipReason is { } reason) parts.Add($"condition:{node.Id.Value}:{reason}");
            if (node.Routes.Count > 0) parts.Add($"routes:{node.Id.Value}:{System.Text.Json.JsonSerializer.Serialize(node.Routes)}");
            if (node.RouteScopes.Count > 0) parts.Add($"route-scopes:{node.Id.Value}:{System.Text.Json.JsonSerializer.Serialize(node.RouteScopes)}");
            parts.AddRange(node.ReadChannels.Select(channel => $"read:{node.Id.Value}:{channel.Value}"));
        }

        foreach (var channel in channels.Values.OrderBy(channel => channel.Id.Value, StringComparer.Ordinal))
        {
            var initialHash = PregelHash.Sha256(Convert.ToBase64String(channelRuntimes[channel.Id].InitialValue.Data));
            parts.Add($"channel:{channel.Id.Value}:{channel.Kind}:{channel.StateTypeId}:{channel.UpdateTypeId}:{channel.StateCodecId}:{channel.UpdateCodecId}:{channel.ReducerId}:{initialHash}");
            if (channel.RequiredSenders is { } requiredSenders)
            {
                parts.AddRange(requiredSenders
                    .OrderBy(sender => sender.Value, StringComparer.Ordinal)
                    .Select(sender => $"barrier-sender:{channel.Id.Value}:{sender.Value}"));
            }
        }

        parts.AddRange(edges.Select(edge => $"edge:{edge.From.Value}:{edge.To.Value}"));
        return PregelHash.Sha256(parts.ToArray());
    }

    private static PregelRetryPolicy ValidateRetryPolicy(PregelRetryPolicy policy)
    {
        if (policy.MaxAttempts < 1)
        {
            throw new PregelException("invalid_retry_policy", "MaxAttempts must be at least one.");
        }

        if (policy.BackoffMultiplier < 1)
        {
            throw new PregelException("invalid_retry_policy", "BackoffMultiplier must be at least one.");
        }

        return policy;
    }

    private static string TypeId(Type type) => PregelTypeIdentity.Qualified(type);

    private static ChannelId EdgeChannelId(PregelEdge edge) =>
        new($"__edge:{PregelHash.Sha256(edge.From.Value, edge.To.Value)}");

}
