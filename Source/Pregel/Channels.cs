namespace Heron.Pregel;

public enum PregelChannelKind
{
    LastValue,
    AnyValue,
    EphemeralValue,
    Topic,
    Aggregate,
    NamedBarrier,
}

public sealed record PregelChannelDefinition(
    ChannelId Id,
    PregelChannelKind Kind,
    string StateTypeId,
    string UpdateTypeId,
    string StateCodecId,
    string UpdateCodecId,
    string? ReducerId = null,
    IReadOnlyList<NodeId>? RequiredSenders = null);

public sealed class ChannelRef<TState, TUpdate>
{
    internal ChannelRef(IChannelRuntime runtime) => Runtime = runtime;
    internal IChannelRuntime Runtime { get; }
    public ChannelId Id => Runtime.Definition.Id;
}

internal sealed record PregelChannelApplyResult(PregelEncodedValue Value, bool IsAvailable);
internal sealed record PregelChannelConsumeResult(PregelEncodedValue Value, bool Changed, bool IsAvailable);

internal interface IChannelRuntime
{
    PregelChannelDefinition Definition { get; }
    PregelEncodedValue InitialValue { get; }
    bool IsAvailable(PregelEncodedValue value);
    PregelChannelApplyResult Apply(PregelEncodedValue current, IReadOnlyList<PregelWrite> orderedWrites);
    PregelChannelConsumeResult Consume(PregelEncodedValue current);
    object DecodeState(PregelEncodedValue value);
    PregelEncodedValue EncodeUpdateObject(object value);
}

internal abstract class ChannelRuntime<TState, TUpdate> : IChannelRuntime
{
    private readonly ICheckpointCodec<TState> _stateCodec;
    private readonly ICheckpointCodec<TUpdate> _updateCodec;
    private readonly TState _initialValue;

    internal bool HasJsonUpdates => _updateCodec is IJsonCheckpointCodec;

    protected ChannelRuntime(
        ChannelId id,
        PregelChannelKind kind,
        TState initialValue,
        ICheckpointCodec<TState> stateCodec,
        ICheckpointCodec<TUpdate> updateCodec,
        string? reducerId = null,
        IReadOnlyList<NodeId>? requiredSenders = null)
    {
        _initialValue = initialValue;
        _stateCodec = stateCodec;
        _updateCodec = updateCodec;
        Definition = new PregelChannelDefinition(
            id,
            kind,
            PregelTypeIdentity.Qualified(typeof(TState)),
            PregelTypeIdentity.Qualified(typeof(TUpdate)),
            stateCodec.CodecId,
            updateCodec.CodecId,
            reducerId,
            requiredSenders);
        InitialValue = EncodeState(initialValue);
    }

    public PregelChannelDefinition Definition { get; }
    public PregelEncodedValue InitialValue { get; }

    public object DecodeState(PregelEncodedValue value) => DecodeStateValue(value)!;

    public PregelEncodedValue EncodeUpdateObject(object value)
    {
        if (value is not TUpdate update)
        {
            throw new PregelException(
                "channel_update_type_mismatch",
                $"Channel '{Definition.Id}' expects {typeof(TUpdate).Name} updates, got {value.GetType().Name}.");
        }

        return new PregelEncodedValue(_updateCodec.CodecId, _updateCodec.Encode(update));
    }

    public bool IsAvailable(PregelEncodedValue value) => IsAvailableCore(DecodeStateValue(value));

    public virtual PregelChannelApplyResult Apply(PregelEncodedValue current, IReadOnlyList<PregelWrite> orderedWrites)
    {
        var updates = orderedWrites.Select(write => DecodeUpdateValue(write.Value)).ToArray();
        var next = ApplyCore(DecodeStateValue(current), updates);
        return new PregelChannelApplyResult(EncodeState(next), IsAvailableCore(next));
    }

    public PregelChannelConsumeResult Consume(PregelEncodedValue current)
    {
        var state = DecodeStateValue(current);
        var next = ConsumeCore(state, out var changed);
        return new PregelChannelConsumeResult(EncodeState(next), changed, IsAvailableCore(next));
    }

    protected virtual TState ConsumeCore(TState current, out bool changed)
    {
        changed = false;
        return current;
    }

    protected abstract TState ApplyCore(TState current, IReadOnlyList<TUpdate> updates);
    protected virtual bool IsAvailableCore(TState value) => true;
    protected TState InitialState => _initialValue;

    protected PregelEncodedValue EncodeState(TState value) => new(_stateCodec.CodecId, _stateCodec.Encode(value));

    protected TState DecodeStateValue(PregelEncodedValue value)
    {
        EnsureCodec(value.CodecId, _stateCodec.CodecId);
        return _stateCodec.Decode(value.Data);
    }

    protected TUpdate DecodeUpdateValue(PregelEncodedValue value)
    {
        EnsureCodec(value.CodecId, _updateCodec.CodecId);
        return _updateCodec.Decode(value.Data);
    }

    private static void EnsureCodec(string actual, string expected)
    {
        if (!StringComparer.Ordinal.Equals(actual, expected))
        {
            throw new PregelException("codec_mismatch", $"Expected codec '{expected}', got '{actual}'.");
        }
    }
}

internal sealed class LastValueChannelRuntime<T>(
    ChannelId id,
    T initialValue,
    ICheckpointCodec<T> codec)
    : ChannelRuntime<T, T>(id, PregelChannelKind.LastValue, initialValue, codec, codec)
{
    protected override T ApplyCore(T current, IReadOnlyList<T> updates) => updates.Count switch
    {
        0 => current,
        1 => updates[0],
        _ => throw new PregelException("invalid_concurrent_last_value_update", $"Channel '{Definition.Id}' accepts one write per superstep."),
    };
}

internal sealed class AnyValueChannelRuntime<T>(
    ChannelId id,
    T initialValue,
    ICheckpointCodec<T> codec)
    : ChannelRuntime<T, T>(id, PregelChannelKind.AnyValue, initialValue, codec, codec)
{
    protected override T ApplyCore(T current, IReadOnlyList<T> updates) => updates.Count == 0 ? current : updates[^1];
}

internal sealed class EphemeralValueChannelRuntime<T> : IChannelRuntime
{
    private readonly T _initialValue;
    private readonly ICheckpointCodec<T> _codec;
    private readonly bool _guard;

    public EphemeralValueChannelRuntime(
        ChannelId id,
        T initialValue,
        ICheckpointCodec<T> codec,
        bool guard)
    {
        _initialValue = initialValue;
        _codec = codec;
        _guard = guard;
        Definition = new PregelChannelDefinition(
            id,
            PregelChannelKind.EphemeralValue,
            PregelTypeIdentity.Qualified(typeof(T)),
            PregelTypeIdentity.Qualified(typeof(T)),
            $"pregel:ephemeral:{codec.CodecId}:v1",
            codec.CodecId,
            $"ephemeral:guard={guard}");
        InitialValue = EncodeState(false, initialValue);
    }

    public PregelChannelDefinition Definition { get; }
    public PregelEncodedValue InitialValue { get; }

    public bool IsAvailable(PregelEncodedValue value) => DecodeEnvelope(value).HasValue;

    public PregelChannelApplyResult Apply(PregelEncodedValue current, IReadOnlyList<PregelWrite> orderedWrites)
    {
        if (_guard && orderedWrites.Count > 1)
        {
            throw new PregelException("invalid_concurrent_ephemeral_update", $"Channel '{Definition.Id}' accepts one write per superstep.");
        }

        if (orderedWrites.Count == 0)
        {
            return new PregelChannelApplyResult(current, IsAvailable(current));
        }

        var update = DecodeUpdate(orderedWrites[^1].Value);
        return new PregelChannelApplyResult(EncodeState(true, update), true);
    }

    public PregelChannelConsumeResult Consume(PregelEncodedValue current)
    {
        var state = DecodeEnvelope(current);
        return state.HasValue
            ? new PregelChannelConsumeResult(InitialValue.Copy(), true, false)
            : new PregelChannelConsumeResult(current, false, false);
    }

    public object DecodeState(PregelEncodedValue value) => DecodeEnvelope(value).Value!;

    public PregelEncodedValue EncodeUpdateObject(object value)
    {
        if (value is not T update)
        {
            throw new PregelException(
                "channel_update_type_mismatch",
                $"Channel '{Definition.Id}' expects {typeof(T).Name} updates, got {value.GetType().Name}.");
        }

        return new PregelEncodedValue(_codec.CodecId, _codec.Encode(update));
    }

    private PregelEncodedValue EncodeState(bool hasValue, T value)
    {
        var payload = _codec.Encode(value);
        var data = new byte[payload.Length + 1];
        data[0] = hasValue ? (byte)1 : (byte)0;
        payload.CopyTo(data.AsSpan(1));
        return new PregelEncodedValue(Definition.StateCodecId, data);
    }

    private (bool HasValue, T Value) DecodeEnvelope(PregelEncodedValue value)
    {
        EnsureCodec(value.CodecId, Definition.StateCodecId);
        if (value.Data.Length == 0 || value.Data[0] > 1)
        {
            throw new PregelException("invalid_ephemeral_state", $"Channel '{Definition.Id}' has an invalid checkpoint value.");
        }

        return (value.Data[0] == 1, _codec.Decode(value.Data.AsSpan(1)));
    }

    private T DecodeUpdate(PregelEncodedValue value)
    {
        EnsureCodec(value.CodecId, _codec.CodecId);
        return _codec.Decode(value.Data);
    }

    private static void EnsureCodec(string actual, string expected)
    {
        if (!StringComparer.Ordinal.Equals(actual, expected))
        {
            throw new PregelException("codec_mismatch", $"Expected codec '{expected}', got '{actual}'.");
        }
    }
}

internal sealed class AggregateChannelRuntime<TState, TUpdate>(
    ChannelId id,
    TState initialValue,
    ICheckpointCodec<TState> stateCodec,
    ICheckpointCodec<TUpdate> updateCodec,
    string reducerId,
    Func<TState, TUpdate, TState> reducer)
    : ChannelRuntime<TState, TUpdate>(
        id,
        PregelChannelKind.Aggregate,
        initialValue,
        stateCodec,
        updateCodec,
        reducerId)
{
    protected override TState ApplyCore(TState current, IReadOnlyList<TUpdate> updates)
    {
        var next = current;
        foreach (var update in updates)
        {
            next = reducer(next, update);
        }

        return next;
    }
}

internal sealed class TopicChannelRuntime<T>(
    ChannelId id,
    ICheckpointCodec<IReadOnlyList<T>> stateCodec,
    ICheckpointCodec<T> updateCodec,
    bool unique,
    bool accumulate)
    : ChannelRuntime<IReadOnlyList<T>, T>(
        id,
        PregelChannelKind.Topic,
        Array.Empty<T>(),
        stateCodec,
        updateCodec,
        $"topic:unique={unique}:accumulate={accumulate}")
{
    protected override IReadOnlyList<T> ApplyCore(IReadOnlyList<T> current, IReadOnlyList<T> updates)
    {
        var result = accumulate ? current.ToList() : [];
        foreach (var update in updates)
        {
            if (!unique || !result.Contains(update))
            {
                result.Add(update);
            }
        }

        return result;
    }
}

public sealed record NamedBarrierState(IReadOnlyList<string> Arrived, bool Ready);

internal sealed class NamedBarrierChannelRuntime(
    ChannelId id,
    IReadOnlyList<NodeId> requiredSenders)
    : ChannelRuntime<NamedBarrierState, PregelUnit>(
        id,
        PregelChannelKind.NamedBarrier,
        new NamedBarrierState(Array.Empty<string>(), false),
        CheckpointCodecs.Json<NamedBarrierState>($"pregel:named-barrier-state:v1"),
        CheckpointCodecs.Unit,
        "named-barrier-v1",
        requiredSenders)
{
    private readonly HashSet<string> _required = requiredSenders.Select(sender => sender.Value).ToHashSet(StringComparer.Ordinal);

    public override PregelChannelApplyResult Apply(PregelEncodedValue current, IReadOnlyList<PregelWrite> orderedWrites)
    {
        var arrived = DecodeStateValue(current).Arrived.ToHashSet(StringComparer.Ordinal);
        foreach (var write in orderedWrites)
        {
            _ = DecodeUpdateValue(write.Value);
            if (!_required.Contains(write.SourceNodeId.Value))
            {
                throw new PregelException(
                    "invalid_named_barrier_sender",
                    $"Node '{write.SourceNodeId}' is not a declared sender for channel '{Definition.Id}'.");
            }

            arrived.Add(write.SourceNodeId.Value);
        }

        var next = new NamedBarrierState(
            arrived.Order(StringComparer.Ordinal).ToArray(),
            _required.Count > 0 && _required.IsSubsetOf(arrived));
        return new PregelChannelApplyResult(EncodeState(next), next.Ready);
    }

    protected override NamedBarrierState ApplyCore(NamedBarrierState current, IReadOnlyList<PregelUnit> updates) => current;

    protected override bool IsAvailableCore(NamedBarrierState value) => value.Ready;
}
