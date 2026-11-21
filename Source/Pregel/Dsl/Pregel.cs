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
    /// Starts defining a strongly typed graph whose steps receive <typeparamref name="TInput"/>.
    /// Use <see cref="PregelUnit"/> when a graph needs no input.
    /// </summary>
    public static PregelFlow<TInput, TInput> Define<TInput>(string graphId) =>
        PregelFlow<TInput, TInput>.Start(graphId);

    public static PregelFlow<PregelUnit, PregelUnit> Define(string graphId) => Define<PregelUnit>(graphId);
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
