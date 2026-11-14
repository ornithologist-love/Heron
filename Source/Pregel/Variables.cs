namespace Heron.Pregel;

/// <summary>
/// A declared, mutable graph variable. Its value is supplied per run and lives in a reserved
/// last-value channel, so it is checkpointed and recovered with the run. Nodes read it through
/// <see cref="PregelNodeContext.Read{T}(PregelVariable{T})"/> and may replace it with
/// <see cref="PregelNodeContext.Write{T}(PregelVariable{T}, T)"/>; replacements become visible after the barrier.
/// </summary>
/// <remarks>TODO: const variables (read-only refs) and graph constants are a later iteration.</remarks>
public sealed class PregelVariable<T>
{
    internal PregelVariable(string name, ChannelRef<T, T> channel)
    {
        Name = name;
        Channel = channel;
    }

    public string Name { get; }
    internal ChannelRef<T, T> Channel { get; }
}

/// <summary>Values supplied for a run's declared graph variables. Every declared variable must be set.</summary>
public sealed class PregelVariables
{
    private readonly Dictionary<ChannelId, (string Name, Func<IChannelRuntime, PregelEncodedValue> Encode, bool Json)> _values = [];

    public static PregelVariables Empty => new();

    public PregelVariables Set<T>(PregelVariable<T> variable, T value)
    {
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(value);
        if (!_values.TryAdd(variable.Channel.Id, (variable.Name, runtime => runtime.EncodeUpdateObject(value!),
                variable.Channel.Runtime is ChannelRuntime<T, T> { HasJsonUpdates: true })))
        {
            throw new PregelException("duplicate_graph_variable_value", $"Graph variable '{variable.Name}' was set more than once.");
        }

        return this;
    }

    internal IReadOnlyDictionary<ChannelId, PregelEncodedValue> Encode(CompiledPregelGraph graph)
    {
        foreach (var (channelId, entry) in _values)
        {
            if (!graph.Variables.ContainsKey(channelId))
            {
                throw new PregelException("unknown_graph_variable", $"Graph variable '{entry.Name}' is not declared by this graph.");
            }
        }

        var missing = graph.Variables
            .Where(pair => !_values.ContainsKey(pair.Key))
            .Select(pair => pair.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new PregelException("missing_graph_variable", $"Graph variables are required but not set: {string.Join(", ", missing)}.");
        }

        var encoded = _values.ToDictionary(pair => pair.Key, pair => pair.Value.Encode(graph.Channels[pair.Key]));
        return PregelInputTemplates.Bind(encoded, graph.Variables,
            _values.Where(pair => pair.Value.Json).Select(pair => pair.Key));
    }
}
