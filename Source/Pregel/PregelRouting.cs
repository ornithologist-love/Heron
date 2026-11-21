using System.Collections.Frozen;

namespace Heron.Pregel;

public enum PregelRoutePhase
{
    BeforeNode,
    AfterNode,
}

/// <summary>A route chooses one of the destinations declared when the graph is compiled.</summary>
internal abstract record PregelRoute
{
    private PregelRoute() { }

    public static PregelRoute Continue { get; } = new ContinueRoute();
    public static PregelRoute End { get; } = new EndRoute();

    public static PregelRoute To(NodeId nodeId)
    {
        _ = PregelId.Validate(nodeId.Value, nameof(nodeId));
        return new TargetRoute(nodeId);
    }

    public static PregelRoute To<TInput>(NodeRef<TInput> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return To(node.Id);
    }

    /// <summary>Skip the current node body, record the reason and follow its ordinary outgoing edges.</summary>
    public static PregelRoute Skip(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SkipRoute(reason);
    }

    internal sealed record ContinueRoute : PregelRoute;
    internal sealed record EndRoute : PregelRoute;
    internal sealed record TargetRoute(NodeId NodeId) : PregelRoute;
    internal sealed record SkipRoute(string Reason) : PregelRoute;

    internal PregelRouteDestination Destination => this switch
    {
        ContinueRoute => new("continue"),
        EndRoute => new("end"),
        TargetRoute target => new("node", target.NodeId),
        SkipRoute skipped => new("skip", SkipReason: skipped.Reason),
        _ => throw new PregelException("unsupported_route", "Unknown route decision.")
    };
}

public sealed record PregelRouteDestination(string Kind, NodeId? NodeId = null, string? SkipReason = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Key = null);
public sealed record PregelRouteDefinition(PregelRoutePhase Phase, IReadOnlyList<PregelRouteDestination> Destinations);

internal sealed class PregelRouteRuntime
{
    private readonly Func<PregelNodeContext, PregelRoute> _select;
    private readonly FrozenSet<PregelRoute> _destinations;
    private readonly string _selectorError;

    internal PregelRouteRuntime(PregelRoutePhase phase, Func<PregelNodeContext, PregelRoute> select,
        IReadOnlyList<PregelRoute> destinations, string selectorError = "invalid_route_selector")
    {
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(destinations);
        if (destinations.Count == 0) throw new ArgumentException("A route must declare its destinations.", nameof(destinations));
        foreach (var destination in destinations)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (phase == PregelRoutePhase.AfterNode && destination is PregelRoute.SkipRoute)
                throw new ArgumentException("Skip is only valid before the node body executes.", nameof(destinations));
        }
        _select = select;
        _selectorError = selectorError;
        _destinations = destinations.ToFrozenSet();
        Definition = new(phase, Array.AsReadOnly(_destinations.Select(route => route.Destination)
            .OrderBy(destination => destination.Kind, StringComparer.Ordinal)
            .ThenBy(destination => destination.Key, StringComparer.Ordinal)
            .ThenBy(destination => destination.NodeId?.Value, StringComparer.Ordinal)
            .ThenBy(destination => destination.SkipReason, StringComparer.Ordinal).ToArray()));
    }

    internal PregelRouteDefinition Definition { get; }

    internal PregelRoute Select(PregelNodeContext context)
    {
        var writes = context.Writes.Count;
        var commands = context.Commands.Count;
        var decision = _select(context);
        if (context.Writes.Count != writes || context.Commands.Count != commands)
            throw new PregelException(_selectorError, "Route selectors must not write state or issue flow commands.");
        if (decision is null || !_destinations.Contains(decision))
            throw new PregelException("undeclared_route", $"Node '{context.NodeId}' selected an undeclared route destination.");
        return decision;
    }

    internal static void Apply(PregelRoute decision, PregelNodeContext context)
    {
        switch (decision)
        {
            case PregelRoute.ContinueRoute:
                break;
            case PregelRoute.EndRoute:
                context.End();
                break;
            case PregelRoute.TargetRoute target:
                context.Emit(new PregelCommand.Goto(target.NodeId));
                break;
            default:
                throw new PregelException("unsupported_route", "Only Continue, End and node targets can be applied after execution.");
        }
    }
}

public sealed partial class PregelGraphBuilder
{
    private readonly Dictionary<NodeId, PregelRouteRuntime> _beforeRoutes = [];
    private readonly Dictionary<NodeId, PregelRouteRuntime> _afterRoutes = [];

    /// <summary>
    /// Binds a pure selector to a closed set of destinations. Node targets must have declared outgoing edges.
    /// BeforeNode may skip the body; other decisions take effect after a successful body. AfterNode cannot skip.
    /// </summary>
    internal void Route<TInput>(NodeRef<TInput> node, PregelRoutePhase phase,
        Func<PregelNodeContext, PregelRoute> select, params PregelRoute[] destinations) =>
        ConfigureRoute(node.Runtime, new(phase, select, destinations));

    private void ConfigureRoute(INodeRuntime node, PregelRouteRuntime route)
    {
        EnsureOwned(node);
        var routes = route.Definition.Phase == PregelRoutePhase.BeforeNode ? _beforeRoutes : _afterRoutes;
        if (!routes.TryAdd(node.Id, route))
            throw new InvalidOperationException($"Node '{node.Id}' already has a {route.Definition.Phase} route.");
    }

    private void ValidateRoutes()
    {
        foreach (var (node, route) in _beforeRoutes.Concat(_afterRoutes))
        foreach (var target in route.Definition.Destinations.Where(destination => destination.NodeId is not null))
        {
            if (!_nodes.ContainsKey(target.NodeId!.Value) || !_edges.Contains(new(node, target.NodeId.Value)))
                throw new PregelException("invalid_route_target", $"Route from '{node}' requires an outgoing edge to '{target.NodeId}'.");
        }
    }
}
