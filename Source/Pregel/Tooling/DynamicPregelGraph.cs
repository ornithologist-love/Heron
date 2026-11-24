using System.Reflection;

namespace Heron.Pregel.Tooling;

/// <summary>Adapts compiled graphs whose CLR input/output types are discovered at runtime.</summary>
public abstract class DynamicPregelGraph
{
    public abstract CompiledPregelGraph Compiled { get; }
    public abstract Type? InputType { get; }
    public abstract Type OutputType { get; }

    /// <summary>Binds input without executing nodes. The caller owns the engine, options and observer.</summary>
    public abstract PregelRun Prepare(object? input = null, PregelRunOptions? options = null);

    public Task<PregelRun> RestoreAsync(PregelRunId runId, PregelEngine engine,
        PregelRunOptions? options = null, CancellationToken cancellationToken = default) =>
        PregelRun.RestoreAsync(Compiled, runId, engine, options, cancellationToken);

    /// <summary>Reads typed output, including from a result restored through the caller's engine.</summary>
    public abstract object? Read(PregelRunResult result);

    public static bool IsGraphType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PregelGraph<,>);

    public static DynamicPregelGraph Adapt(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var type = value.GetType();
        if (!IsGraphType(type)) throw new ArgumentException("必须提供 Compile() 后的 Pregel Graph。", nameof(value));
        return (DynamicPregelGraph)typeof(DynamicPregelGraph)
            .GetMethod(nameof(FromResult), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type.GetGenericArguments()).Invoke(null, [value])!;
    }

    private static DynamicPregelGraph FromResult<T, TOutput>(PregelGraph<T, TOutput> graph) => new WithResult<T, TOutput>(graph);

    private static T Bind<T>(object? input)
    {
        if (input is T value) return value;
        if (input is null && (default(T) is null || typeof(T) == typeof(PregelUnit))) return default!;
        throw new ArgumentException($"Graph 输入须为 {typeof(T).FullName}。", nameof(input));
    }

    private sealed class WithResult<T, TOutput>(PregelGraph<T, TOutput> graph) : DynamicPregelGraph
    {
        public override CompiledPregelGraph Compiled => graph.Compiled;
        public override Type? InputType => typeof(T) == typeof(PregelUnit) ? null : typeof(T);
        public override Type OutputType => typeof(TOutput);
        public override PregelRun Prepare(object? input = null, PregelRunOptions? options = null) =>
            graph.Prepare(Bind<T>(input), options);
        public override object? Read(PregelRunResult result) => graph.Read(result);
    }
}

