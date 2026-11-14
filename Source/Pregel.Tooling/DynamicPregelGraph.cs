using System.Reflection;

namespace Heron.Pregel.Tooling;

/// <summary>Adapts compiled graphs whose CLR input/output types are discovered at runtime.</summary>
public abstract class DynamicPregelGraph
{
    public abstract CompiledPregelGraph Compiled { get; }
    public abstract Type? InputType { get; }
    public abstract Type? OutputType { get; }

    /// <summary>Binds input without executing nodes. The caller owns the engine, options and observer.</summary>
    public abstract PregelRun Prepare(object? input = null, PregelEngine? engine = null,
        PregelRunOptions? options = null);

    public Task<PregelRun> RestoreAsync(PregelRunId runId, PregelEngine engine,
        PregelRunOptions? options = null, CancellationToken cancellationToken = default) =>
        PregelRun.RestoreAsync(Compiled, runId, engine, options, cancellationToken);

    /// <summary>Reads typed output, including from a result restored through the caller's engine.</summary>
    public virtual object? Read(PregelRunResult result) =>
        throw new InvalidOperationException("该 Graph 没有声明输出类型。");

    public static bool IsGraphType(Type type) => type == typeof(PregelGraph)
        || type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(PregelGraph<>)
            || type.GetGenericTypeDefinition() == typeof(PregelGraph<,>));

    public static DynamicPregelGraph Adapt(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is PregelGraph graph) return new WithoutInput(graph);
        var type = value.GetType();
        if (!IsGraphType(type)) throw new ArgumentException("必须提供 Compile() 后的 Pregel Graph。", nameof(value));
        var factory = type.GetGenericTypeDefinition() == typeof(PregelGraph<>)
            ? nameof(FromInput) : nameof(FromResult);
        return (DynamicPregelGraph)typeof(DynamicPregelGraph)
            .GetMethod(factory, BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type.GetGenericArguments()).Invoke(null, [value])!;
    }

    private static DynamicPregelGraph FromInput<T>(PregelGraph<T> graph) => new WithInput<T>(graph);
    private static DynamicPregelGraph FromResult<T, TOutput>(PregelGraph<T, TOutput> graph) => new WithResult<T, TOutput>(graph);

    private static T Bind<T>(object? input)
    {
        if (input is T value) return value;
        if (input is null && default(T) is null) return default!;
        throw new ArgumentException($"Graph 输入须为 {typeof(T).FullName}。", nameof(input));
    }

    private sealed class WithoutInput(PregelGraph graph) : DynamicPregelGraph
    {
        public override CompiledPregelGraph Compiled => graph.Compiled;
        public override Type? InputType => null;
        public override Type? OutputType => null;
        public override PregelRun Prepare(object? input = null, PregelEngine? engine = null,
            PregelRunOptions? options = null) => graph.Prepare(engine, options);
    }

    private sealed class WithInput<T>(PregelGraph<T> graph) : DynamicPregelGraph
    {
        public override CompiledPregelGraph Compiled => graph.Compiled;
        public override Type InputType => typeof(T);
        public override Type? OutputType => null;
        public override PregelRun Prepare(object? input = null, PregelEngine? engine = null,
            PregelRunOptions? options = null) => graph.Prepare(Bind<T>(input), engine: engine, options: options);
    }

    private sealed class WithResult<T, TOutput>(PregelGraph<T, TOutput> graph) : DynamicPregelGraph
    {
        public override CompiledPregelGraph Compiled => graph.Compiled;
        public override Type InputType => typeof(T);
        public override Type OutputType => typeof(TOutput);
        public override PregelRun Prepare(object? input = null, PregelEngine? engine = null,
            PregelRunOptions? options = null) => graph.Prepare(Bind<T>(input), engine: engine, options: options);
        public override object? Read(PregelRunResult result) => graph.Read(result);
    }
}

