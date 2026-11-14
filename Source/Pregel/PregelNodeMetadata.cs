using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Heron.Pregel;

/// <summary>Names the graph property invoked by a composition method. Resolved after graph construction.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PregelSubgraphAttribute(string propertyName) : Attribute
{
    public string PropertyName { get; } = propertyName;
}

public sealed record PregelNodeMetadata(string Title)
{
    public string? MutationId { get; init; }
    public bool IsMutationInstance { get; init; }
    public PregelGraphDefinition? SubgraphDefinition { get; init; }
    [JsonIgnore] public Func<PregelGraphDefinition>? Subgraph { get; init; }

    internal static PregelNodeMetadata FromTask(Delegate task)
    {
        var method = task.Method;
        var title = method.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? method.Name;
        var subgraph = method.GetCustomAttribute<PregelSubgraphAttribute>();
        if (subgraph == null) return new(title);
        var property = method.DeclaringType!.GetProperty(subgraph.PropertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing subgraph property: {method.DeclaringType}.{subgraph.PropertyName}");
        return new(title) { Subgraph = () =>
        {
            var graph = property.GetValue(task.Target) ?? throw new InvalidOperationException($"Subgraph {property.Name} is null.");
            var compiled = graph.GetType().GetProperty("Compiled")?.GetValue(graph) as CompiledPregelGraph
                ?? throw new InvalidOperationException($"{property.Name} must return a typed Pregel graph.");
            return compiled.Definition;
        }};
    }
}
