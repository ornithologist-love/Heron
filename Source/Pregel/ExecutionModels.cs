using System.Text.Json.Serialization;

namespace Heron.Pregel;

public enum PregelInvocationKind
{
    Pull,
    Push,
}

public sealed record PregelInvocation(
    PregelInvocationId LogicalInvocationId,
    PregelAttemptId AttemptId,
    int AttemptNumber,
    NodeId NodeId,
    PregelInvocationKind Kind,
    IReadOnlyList<string> Path,
    IReadOnlyList<ChannelId> Triggers,
    PregelEncodedValue Input,
    DateTimeOffset? NotBefore = null);

public sealed record PregelWrite(
    PregelInvocationId LogicalInvocationId,
    NodeId SourceNodeId,
    ChannelId ChannelId,
    int WriteIndex,
    PregelEncodedValue Value);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$command")]
[JsonDerivedType(typeof(PregelCommand.Send), "send")]
[JsonDerivedType(typeof(PregelCommand.Goto), "goto")]
[JsonDerivedType(typeof(PregelCommand.Interrupt), "interrupt")]
[JsonDerivedType(typeof(PregelCommand.End), "end")]
[JsonDerivedType(typeof(PregelCommand.Mutate), "mutate")]
[JsonDerivedType(typeof(PregelCommand.MutationResult), "mutation-result")]
[JsonDerivedType(typeof(PregelCommand.CompleteMutation), "mutation-complete")]
public abstract record PregelCommand
{
    private PregelCommand() { }

    public sealed record Send(NodeId TargetNodeId, PregelEncodedValue Payload) : PregelCommand;
    public sealed record Goto(NodeId TargetNodeId) : PregelCommand;
    public sealed record Interrupt(string Reason, PregelEncodedValue? Payload) : PregelCommand;
    public sealed record End : PregelCommand;
    public sealed record Mutate(PregelMutationPlan Plan) : PregelCommand;
    public sealed record MutationResult(string PlanId, string Key, PregelEncodedValue Result) : PregelCommand;
    public sealed record CompleteMutation(string PlanId) : PregelCommand;
}

/// <summary>
/// During recovery, reads expose committed state and observations remain available;
/// writes and graph commands throw because finalization does not publish them.
/// </summary>
public sealed class PregelNodeContext
{
    private readonly CompiledPregelGraph _graph;
    private readonly PregelCheckpoint _checkpoint;
    private readonly PregelInvocation _invocation;
    private readonly IPregelRunObserver? _observer;
    private readonly bool _isRecovery;
    private readonly List<PregelWrite> _writes = [];
    private readonly Dictionary<ChannelId, object> _variableWrites = [];
    private readonly List<PregelCommand> _commands = [];

    internal PregelNodeContext(
        CompiledPregelGraph graph,
        PregelCheckpoint checkpoint,
        PregelInvocation invocation,
        IPregelRunObserver? observer = null,
        bool isRecovery = false, int maxMutationInstances = 10_000)
    {
        _graph = graph;
        _checkpoint = checkpoint;
        _invocation = invocation;
        _observer = observer;
        _isRecovery = isRecovery;
        MaxMutationInstances = maxMutationInstances;
    }

    public PregelRunId RunId => _checkpoint.RunId;
    /// <summary>The source checkpoint for an explicit derived execution; null for an ordinary run.</summary>
    public PregelReexecutionOrigin? Reexecution => _checkpoint.Reexecution;
    internal PregelCheckpoint Checkpoint => _checkpoint;
    internal PregelInvocation Invocation => _invocation;
    internal int MaxMutationInstances { get; }
    internal string? MutationSkipReason { get; set; }
    internal PregelEncodedValue? EncodedResume => HasResumeValue ? _checkpoint.Resume!.Value.Copy() : null;
    internal void Emit(PregelCommand command) { EnsureCanMutate(nameof(Emit)); _commands.Add(command); }
    public string WorkingDirectory => _checkpoint.WorkingDirectory;
    public NodeId NodeId => _invocation.NodeId;
    /// <summary>The original body result, available to ordinary nodes in the Finally stage.</summary>
    public PregelBodyResult? BodyResult => _checkpoint.BodyResult;
    public IReadOnlyDictionary<NodeId, PregelError> ContinuedErrors => _checkpoint.ContinuedErrors;
    public IReadOnlyDictionary<NodeId, PregelError> FinalizationErrors =>
        _checkpoint.Finalization?.Errors ?? new Dictionary<NodeId, PregelError>();

    /// <summary>Publishes structured observations without changing node success or graph state.</summary>
    public void Notify<T>(string kind, string message, T data) =>
        _observer?.OnEvent(new(RunId, kind, message, NodeId, InvocationId, AttemptId, AttemptNumber,
            Data: System.Text.Json.JsonSerializer.SerializeToElement(data)));

    public void Log(string message) => Output(message, "log");

    /// <summary>Reports current node progress in [0,1]; null or a non-finite value means indeterminate.</summary>
    public void ReportProgress(double? fraction, string detail = "") =>
        Notify(PregelProgress.EventKind, detail, new PregelProgress(PregelProgress.Normalize(fraction), detail));

    public void Output(string text, string source = "output") =>
        _observer?.OnEvent(new(RunId, "node.output", text, NodeId, InvocationId, AttemptId, AttemptNumber, source));

    public PregelInvocationId InvocationId => _invocation.LogicalInvocationId;
    public PregelAttemptId AttemptId => _invocation.AttemptId;
    public int AttemptNumber => _invocation.AttemptNumber;
    public bool HasResumeValue => _checkpoint.Resume?.InvocationId == _invocation.LogicalInvocationId;

    public T ResumeValue<T>(ICheckpointCodec<T>? codec = null)
    {
        if (_checkpoint.Resume is not { } resume || resume.InvocationId != _invocation.LogicalInvocationId)
        {
            throw new PregelException("resume_value_unavailable", $"Invocation '{_invocation.LogicalInvocationId}' has no resume value.");
        }

        var selectedCodec = codec ?? CheckpointCodecs.Default<T>();
        if (!StringComparer.Ordinal.Equals(resume.Value.CodecId, selectedCodec.CodecId))
        {
            throw new PregelException(
                "resume_value_codec_mismatch",
                $"Resume value uses codec '{resume.Value.CodecId}', expected '{selectedCodec.CodecId}'.");
        }

        return selectedCodec.Decode(resume.Value.Data);
    }

    public TState Read<TState, TUpdate>(ChannelRef<TState, TUpdate> channel)
    {
        EnsureChannelOwned(channel.Runtime);
        if (!_checkpoint.ChannelValues.TryGetValue(channel.Id, out var encoded))
        {
            encoded = channel.Runtime.InitialValue;
        }

        return (TState)channel.Runtime.DecodeState(encoded);
    }

    public void Write<TState, TUpdate>(ChannelRef<TState, TUpdate> channel, TUpdate update)
    {
        EnsureCanMutate(nameof(Write));
        ArgumentNullException.ThrowIfNull(update);
        EnsureChannelOwned(channel.Runtime);
        _writes.Add(new PregelWrite(
            _invocation.LogicalInvocationId,
            _invocation.NodeId,
            channel.Id,
            _writes.Count,
            channel.Runtime.EncodeUpdateObject(update)));
    }

    public void Send<TInput>(NodeRef<TInput> target, TInput input)
    {
        EnsureCanMutate(nameof(Send));
        ArgumentNullException.ThrowIfNull(input);
        EnsureNodeOwned(target.Runtime);
        _commands.Add(new PregelCommand.Send(target.Id, target.Runtime.EncodeInput(input)));
    }

    public void Goto<TInput>(NodeRef<TInput> target)
    {
        EnsureCanMutate(nameof(Goto));
        EnsureNodeOwned(target.Runtime);
        _commands.Add(new PregelCommand.Goto(target.Id));
    }

    public void End()
    {
        EnsureCanMutate(nameof(End));
        _commands.Add(new PregelCommand.End());
    }

    /// <summary>Reads the committed value of a graph variable.</summary>
    public T Read<T>(PregelVariable<T> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        return Read(variable.Channel);
    }

    // Result routing alone can inspect this invocation's output before its barrier commits.
    // Ordinary node reads continue to expose only the committed checkpoint.
    internal T ReadResult<T>(PregelVariable<T> variable)
    {
        EnsureChannelOwned(variable.Channel.Runtime);
        if (!_variableWrites.TryGetValue(variable.Channel.Id, out var value))
            throw new PregelException("route_result_unavailable", $"Node '{NodeId}' did not produce its routed result.");
        return (T)value;
    }

    /// <summary>Replaces a graph variable; the new value is visible to later supersteps after the barrier.</summary>
    public void Write<T>(PregelVariable<T> variable, T value)
    {
        EnsureCanMutate(nameof(Write));
        ArgumentNullException.ThrowIfNull(variable);
        Write(variable.Channel, value);
        _variableWrites[variable.Channel.Id] = value!;
    }

    public void Interrupt(string reason)
    {
        EnsureCanMutate(nameof(Interrupt));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _commands.Add(new PregelCommand.Interrupt(reason, null));
    }

    public void Interrupt<T>(string reason, T payload, ICheckpointCodec<T>? codec = null)
    {
        EnsureCanMutate(nameof(Interrupt));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(payload);
        var selectedCodec = codec ?? CheckpointCodecs.Default<T>();
        _commands.Add(new PregelCommand.Interrupt(
            reason,
            new PregelEncodedValue(selectedCodec.CodecId, selectedCodec.Encode(payload))));
    }

    internal IReadOnlyList<PregelWrite> Writes => _writes;
    internal IReadOnlyList<PregelCommand> Commands => _commands;

    private void EnsureCanMutate(string operation)
    {
        if (_isRecovery)
            throw new PregelException("recovery_operation_not_allowed",
                $"Recovery cannot call {operation}: state writes and graph commands are not supported during finalization.");
    }

    private void EnsureChannelOwned(IChannelRuntime channel)
    {
        if (!_graph.Channels.TryGetValue(channel.Definition.Id, out var existing) || !ReferenceEquals(existing, channel))
        {
            throw new PregelException("foreign_channel", $"Channel '{channel.Definition.Id}' does not belong to this graph.");
        }
    }

    private void EnsureNodeOwned(INodeRuntime node)
    {
        if (!_graph.Nodes.TryGetValue(node.Id, out var existing) || !ReferenceEquals(existing, node))
        {
            throw new PregelException("foreign_node", $"Node '{node.Id}' does not belong to this graph.");
        }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$outcome")]
[JsonDerivedType(typeof(PregelInvocationOutcome.Succeeded), "succeeded")]
[JsonDerivedType(typeof(PregelInvocationOutcome.Failed), "failed")]
[JsonDerivedType(typeof(PregelInvocationOutcome.Interrupted), "interrupted")]
[JsonDerivedType(typeof(PregelInvocationOutcome.Cancelled), "cancelled")]
[JsonDerivedType(typeof(PregelInvocationOutcome.Skipped), "skipped")]
public abstract record PregelInvocationOutcome
{
    private PregelInvocationOutcome() { }

    public sealed record Succeeded(
        IReadOnlyList<PregelWrite> Writes,
        IReadOnlyList<PregelCommand> Commands) : PregelInvocationOutcome;

    public sealed record Failed(PregelError Error) : PregelInvocationOutcome;
    public sealed record Interrupted(
        string Reason,
        PregelEncodedValue? Payload,
        PregelInterruptPosition Position = PregelInterruptPosition.Within) : PregelInvocationOutcome;
    public sealed record Cancelled(string Reason) : PregelInvocationOutcome;
    public sealed record Skipped(string Reason, IReadOnlyList<PregelWrite> Writes) : PregelInvocationOutcome
    {
        // A skipped mutation still publishes its result so sequential siblings and the join can continue.
        public IReadOnlyList<PregelCommand> Commands { get; init; } = [];
    }

    internal IReadOnlyList<PregelCommand> CommittedCommands => this switch
    {
        Succeeded succeeded => succeeded.Commands,
        Skipped skipped => skipped.Commands,
        _ => []
    };
}

public sealed record PregelPendingOutcome(
    PregelCheckpointId BaseCheckpointId,
    PregelInvocation Invocation,
    PregelInvocationOutcome Outcome,
    DateTimeOffset? RetryNotBefore = null);

public sealed record PregelPreparedSuperstep(
    long Superstep,
    IReadOnlyList<PregelInvocation> ToExecute,
    IReadOnlyList<PregelInvocation> Replayed);
