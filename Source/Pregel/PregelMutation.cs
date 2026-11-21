using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heron.Pregel;

public enum PregelMutationMode { Sequential, Parallel }

/// <summary>A stable business key and the input of one instance of a compiled template.</summary>
internal sealed record PregelMutationItem<T>(string Key, T Input, string? Title = null)
{
    /// <summary>Empty executes locally; a named target is resolved by the host's subgraph executor.</summary>
    public string Target { get; init; } = "";
}

public sealed record PregelMutationDefinition(string Id, NodeId Entry, NodeId Join,
    PregelMutationMode Mode, string InputCodecId, string OutputCodecId, PregelGraphDefinition Template)
{
    public bool HasResultSkipCondition { get; init; }
    public bool IsSingleSubgraph { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasItemSkipCondition { get; init; }
}

public sealed record PregelMutationInstance(string Key, string Title, NodeId NodeId,
    PregelRunId ChildRunId, PregelEncodedValue Input)
{
    public string Target { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkipReason { get; init; }
}

/// <summary>Durable expansion result. Restoring it never evaluates the upstream selector again.</summary>
public sealed record PregelMutationPlan(string Id, string MutationId, string TemplateFingerprint,
    PregelMutationMode Mode, IReadOnlyList<string> ParentPath, IReadOnlyList<PregelMutationInstance> Instances)
{
    public int FormatVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, PregelEncodedValue> Results { get; init; } = new Dictionary<string, PregelEncodedValue>();
    public bool Completed { get; init; }
    internal PregelMutationPlan Copy() => this with {
        ParentPath = ParentPath.ToArray(), Instances = Instances.Select(x => x with { Input = x.Input.Copy() }).ToArray(),
        Results = Results.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.Ordinal)
    };
}

public sealed record PregelMutationSnapshot(long Revision, PregelGraphDefinition Graph)
{
    private static readonly JsonSerializerOptions Json = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new PregelIdJsonConverter<NodeId>(x => new(x), x => x.Value));
        options.Converters.Add(new PregelIdJsonConverter<ChannelId>(x => new(x), x => x.Value));
        options.Converters.Add(new RetryCodeSetConverter());
        return options;
    }
    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this with { Graph = Export(Graph, []) }, Json);
    public static PregelMutationSnapshot? FromJson(JsonElement value) => value.Deserialize<PregelMutationSnapshot>(Json);

    private static PregelGraphDefinition Export(PregelGraphDefinition graph, HashSet<PregelGraphDefinition> ancestors)
    {
        if (!ancestors.Add(graph)) throw new InvalidOperationException($"Recursive subgraph: {graph.GraphId}");
        try
        {
            return graph with
            {
                Nodes = graph.Nodes.ToDictionary(x => x.Key, x => x.Value.Metadata is { IsMutationInstance: false, Subgraph: { } child } metadata
                    ? x.Value with { Metadata = metadata with { SubgraphDefinition = Export(child(), ancestors), Subgraph = null } }
                    : x.Value),
                Mutations = graph.Mutations.Select(rule => rule with { Template = Export(rule.Template, ancestors) }).ToArray()
            };
        }
        finally { ancestors.Remove(graph); }
    }

    private sealed class RetryCodeSetConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string>? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<string[]>(ref reader, options)?.ToHashSet(StringComparer.Ordinal);
        public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.OrderBy(x => x, StringComparer.Ordinal).ToArray(), options);
    }
}

internal abstract class PregelMutationRuntime(PregelMutationDefinition definition)
{
    public PregelMutationDefinition Definition { get; protected set; } = definition;
    protected bool IsFrozen { get; private set; }
    public void Freeze() => IsFrozen = true;
    public abstract CompiledPregelGraph Template { get; }
    public abstract PregelVariables Bind(PregelEncodedValue input);
    public abstract PregelEncodedValue Result(PregelRunResult result);
    public abstract string? SkipReason(PregelEncodedValue result);
}

internal sealed class PregelMutationRuntime<TItem, TResult>(PregelMutationDefinition definition,
    CompiledPregelGraph template, PregelVariable<TItem> inputVariable, Func<PregelRunResult, TResult> readResult,
    Action<PregelNodeContext, IReadOnlyList<TResult>> publish, Func<TResult, string?>? skipReason,
    Func<TItem, string?>? itemSkipReason)
    : PregelMutationRuntime(definition), IPregelResultSkip<TResult>
{
    private readonly ICheckpointCodec<TItem> _input = CheckpointCodecs.Default<TItem>();
    private readonly ICheckpointCodec<TResult> _result = CheckpointCodecs.Json<TResult>(allowNull: true);
    private Func<TResult, string?>? _skipReason = skipReason;
    public override CompiledPregelGraph Template => template;
    internal PregelVariable<TItem> InputVariable => inputVariable;
    internal Func<PregelRunResult, TResult> ReadResult => readResult;
    internal Func<TResult, string?>? ResultSkip => _skipReason;

    public void SkipIf(Func<TResult, string?> reason)
    {
        if (IsFrozen) throw new InvalidOperationException("A result skip condition must be declared before compiling the graph.");
        if (_skipReason is not null) throw new InvalidOperationException("A result skip condition is already declared.");
        _skipReason = reason;
        Definition = Definition with { HasResultSkipCondition = true };
    }
    public override PregelVariables Bind(PregelEncodedValue input)
    {
        if (input.CodecId != _input.CodecId) throw new PregelException("mutation_input_codec", "Mutation input codec changed.");
        return new PregelVariables().Set(inputVariable, _input.Decode(input.Data));
    }
    public override PregelEncodedValue Result(PregelRunResult result) => new(_result.CodecId, _result.Encode(readResult(result)));
    public override string? SkipReason(PregelEncodedValue result)
    {
        var reason = _skipReason?.Invoke(_result.Decode(result.Data));
        if (reason is not null && string.IsNullOrWhiteSpace(reason))
            throw new PregelException("invalid_mutation_skip_reason", "A skipped mutation result requires a nonempty reason.");
        return reason;
    }

    public void Plan(IReadOnlyList<PregelMutationItem<TItem>> items, PregelNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > context.MaxMutationInstances) throw new PregelException("mutation_instance_limit", "Expansion exceeds the run instance limit.");
        if (context.Checkpoint.Mutations.Any(x => x.MutationId == Definition.Id && !x.Completed))
            throw new PregelException("mutation_already_active", $"Mutation '{Definition.Id}' already has an unfinished expansion.");
        var id = PregelHash.Sha256(context.RunId.Value, Definition.Id, context.InvocationId.Value);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var instances = new List<PregelMutationInstance>();
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Key) || item.Key.Any(char.IsControl) || !keys.Add(item.Key))
                throw new PregelException("mutation_item_key", "Mutation keys must be nonempty, unique and contain no control characters.");
            var identity = PregelHash.Sha256(id, item.Key);
            instances.Add(new(item.Key, item.Title ?? item.Key, new NodeId("mutation:" + identity),
                new PregelRunId("mutation-" + identity), new(_input.CodecId, _input.Encode(item.Input)))
                { Target = item.Target?.Trim() ?? "", SkipReason = itemSkipReason?.Invoke(item.Input) });
        }
        context.Emit(new PregelCommand.Mutate(new(id, Definition.Id, Template.Definition.Fingerprint,
            Definition.Mode, context.Invocation.Path.ToArray(), instances.ToArray())));
    }

    public void Collect(PregelNodeContext context)
    {
        var plan = context.Checkpoint.Mutations.LastOrDefault(x => x.MutationId == Definition.Id && !x.Completed)
            ?? throw new PregelException("mutation_plan_missing", "No active expansion to join.");
        var results = new List<TResult>(plan.Results.Count);
        var reasons = new List<string>();
        foreach (var item in plan.Instances)
        {
            if (item.SkipReason is { } itemReason)
            {
                reasons.Add(itemReason);
                continue;
            }
            if (!plan.Results.TryGetValue(item.Key, out var value) || value.CodecId != _result.CodecId)
                throw new PregelException("mutation_join_incomplete", $"Instance '{item.Key}' has not completed.");
            results.Add(_result.Decode(value.Data));
            if (SkipReason(value) is { } resultReason) reasons.Add(resultReason);
        }
        publish(context, results.ToArray());
        if (reasons.Count > 0 && reasons.Count == plan.Instances.Count)
            context.MutationSkipReason = string.Join("\n", reasons.Distinct(StringComparer.Ordinal));
        context.Emit(new PregelCommand.CompleteMutation(plan.Id));
    }
}

/// <summary>Projects committed plans onto a per-run graph. The compiled graph is never modified.</summary>
internal static class PregelMutationGraph
{
    public static CompiledPregelGraph Restore(CompiledPregelGraph source, PregelCheckpoint checkpoint)
    {
        source = source.MutationBase ?? source;
        if (checkpoint.Mutations.Count == 0) return source;
        var nodes = source.Nodes.ToDictionary(x => x.Key, x => x.Value);
        var channels = source.Channels.ToDictionary(x => x.Key, x => x.Value);
        var definitions = source.Definition.Nodes.ToDictionary(x => x.Key, x => x.Value);
        var outgoing = source.OutgoingEdges.ToDictionary(x => x.Key, x => x.Value.ToList());
        var edges = source.Definition.Edges.ToList();
        var stages = source.Definition.Stages.ToList();
        var final = source.Definition.Finally;
        foreach (var plan in checkpoint.Mutations)
        {
            if (plan.FormatVersion != 1)
                throw new PregelException("mutation_format_mismatch", $"Unsupported mutation plan format: {plan.FormatVersion}.");
            if (!source.Mutations.TryGetValue(plan.MutationId, out var mutation)
                || mutation.Template.Definition.Fingerprint != plan.TemplateFingerprint || mutation.Definition.Mode != plan.Mode)
                throw new PregelException("mutation_template_mismatch", $"Expansion '{plan.Id}' does not match its compiled template.");
            foreach (var item in plan.Instances)
            {
                var node = new PregelMutationInstanceRuntime(mutation, plan.Id, item);
                if (!nodes.TryAdd(item.NodeId, node)) throw new PregelException("mutation_duplicate_instance", item.NodeId.Value);
                definitions.Add(item.NodeId, new(item.NodeId, PregelTypeIdentity.Qualified(typeof(PregelUnit)), node.InputCodecId,
                    node.PullInput, [], [], PregelRetryPolicy.None) { Metadata = new(item.Title) {
                        Subgraph = () => mutation.Template.Definition, MutationId = plan.MutationId, IsMutationInstance = true } });
                outgoing.Add(item.NodeId, []);
            }
            var entry = mutation.Definition.Entry;
            var join = mutation.Definition.Join;
            edges.RemoveAll(x => x.From == entry && x.To == join);
            // The planner node starts instances with committed PUSH packets, not the placeholder edge.
            outgoing[entry].RemoveAll(x => x.Edge.To == join);
            if (plan.Instances.Count == 0) AddEdge(entry, join);
            else
            {
                var items = plan.Instances;
                if (plan.Mode == PregelMutationMode.Sequential)
                {
                    AddEdge(entry, items[0].NodeId);
                    for (var i = 1; i < items.Count; i++) AddEdge(items[i - 1].NodeId, items[i].NodeId);
                    AddEdge(items[^1].NodeId, join);
                }
                else foreach (var item in items) { AddEdge(entry, item.NodeId); AddEdge(item.NodeId, join); }
            }
            definitions[entry] = definitions[entry] with { Metadata = mutation.Definition.IsSingleSubgraph
                ? null : new("展开 " + plan.MutationId) { MutationId = plan.MutationId } };
            var stageIndex = stages.FindIndex(x => x.NodeIds.Contains(entry));
            if (stageIndex >= 0) stages[stageIndex] = stages[stageIndex] with {
                NodeIds = stages[stageIndex].NodeIds.Concat(plan.Instances.Select(x => x.NodeId)).ToArray() };
            if (final?.NodeIds.Contains(entry) == true) final = final with {
                NodeIds = final.NodeIds.Concat(plan.Instances.Select(x => x.NodeId)).ToArray() };
        }
        var triggerIndex = definitions.Values.SelectMany(x => x.Triggers.Select(c => (c, x.Id))).GroupBy(x => x.c)
            .ToFrozenDictionary(x => x.Key, x => (IReadOnlyList<NodeId>)x.Select(v => v.Id).Distinct().ToArray());
        return new(source.Definition with { Nodes = definitions.ToFrozenDictionary(),
                Channels = channels.ToFrozenDictionary(x => x.Key, x => x.Value.Definition), Edges = edges.ToArray(),
                Stages = stages.ToArray(), Finally = final, RunGraphRevision = checkpoint.RunGraphRevision },
            nodes.ToFrozenDictionary(), channels.ToFrozenDictionary(), triggerIndex,
            outgoing.ToFrozenDictionary(x => x.Key, x => (IReadOnlyList<EdgeRuntime>)x.Value.ToArray()), source.StartChannel, source.Variables)
            { StageConditions = source.StageConditions, StageGuards = source.StageGuards,
              BarrierControls = source.BarrierControls, Mutations = source.Mutations, MutationBase = source,
                BeforeRoutes = source.BeforeRoutes, AfterRoutes = source.AfterRoutes, RouteScopes = source.RouteScopes };

        void AddEdge(NodeId from, NodeId to)
        {
            var edge = new PregelEdge(from, to);
            if (edges.Contains(edge)) return;
            var id = new ChannelId("__mutation-edge:" + PregelHash.Sha256(from.Value, to.Value));
            var channel = new EphemeralValueChannelRuntime<PregelUnit>(id, default, CheckpointCodecs.Unit, guard: true);
            channels.Add(id, channel); edges.Add(edge); outgoing[from].Add(new(edge, id));
            definitions[to] = definitions[to] with { Triggers = definitions[to].Triggers.Append(id).ToArray() };
        }
    }
}

internal sealed class PregelMutationInstanceRuntime(PregelMutationRuntime mutation, string planId, PregelMutationInstance instance) : INodeRuntime
{
    public PregelMutationRuntime Mutation => mutation;
    public string PlanId => planId;
    public PregelMutationInstance Instance => instance;
    public NodeId Id => instance.NodeId;
    public Type InputType => typeof(PregelUnit);
    public string InputCodecId => CheckpointCodecs.Unit.CodecId;
    public PregelEncodedValue PullInput { get; } = new(CheckpointCodecs.Unit.CodecId, CheckpointCodecs.Unit.Encode(default));
    public PregelRetryPolicy RetryPolicy => PregelRetryPolicy.None;
    public ValueTask InvokeAsync(PregelEncodedValue input, PregelNodeContext context, CancellationToken token) =>
        throw new InvalidOperationException("Mutation instances must be executed by the kernel.");
}
