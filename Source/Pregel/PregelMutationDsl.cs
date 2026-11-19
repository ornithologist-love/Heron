namespace Heron.Pregel;

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    private PregelResultGraphDsl<TInput, IReadOnlyList<TResult>> MutateCore<TItem, TResult>(string id,
        CompiledPregelGraph template, PregelVariable<TItem> input, Func<PregelRunResult, TResult> readResult,
        Func<TOutput, IReadOnlyList<PregelMutationItem<TItem>>> select,
        PregelMutationMode mode, Func<TResult, string?>? skipReason, out NodeRef<PregelUnit> entry,
        string? subgraphTitle = null,
        Func<TItem, string?>? itemSkipReason = null)
    {
        _ = PregelId.Validate(id, nameof(id));
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(select);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_tail is null) throw new InvalidOperationException("This result node cannot be extended.");
        var entryId = new NodeId("mutation:" + id + ":expand");
        var joinId = new NodeId("mutation:" + id + ":join");
        var output = _builder.Output<IReadOnlyList<TResult>>("mutation:" + id + ":result", Array.Empty<TResult>());
        var definition = new PregelMutationDefinition(id, entryId, joinId, mode,
            CheckpointCodecs.Default<TItem>().CodecId, CheckpointCodecs.Json<TResult>(allowNull: true).CodecId, template.Definition)
            { HasResultSkipCondition = skipReason is not null, IsSingleSubgraph = subgraphTitle != null,
                HasItemSkipCondition = itemSkipReason is not null };
        var runtime = new PregelMutationRuntime<TItem, TResult>(definition, template, input, readResult, output, skipReason, itemSkipReason);
        _builder.AddMutation(runtime);
        entry = _builder.Node(entryId.Value, (context, _) =>
        {
            runtime.Plan(select(context.Read(_output)), context);
            return ValueTask.CompletedTask;
        });
        var join = _builder.Node(joinId.Value, (context, _) => { runtime.Collect(context); return ValueTask.CompletedTask; });
        _builder.SetMetadata(entryId, subgraphTitle == null ? new("待展开 " + id) { MutationId = id }
            : new(subgraphTitle) { MutationId = id, Subgraph = () => template.Definition });
        if (subgraphTitle == null) _builder.SetMetadata(joinId, new("汇合 " + id));
        _builder.DeclareRead(entry, _output);
        _builder.AddEdge(_tail, entry);
        _builder.AddEdge(entry, join);
        return new(_builder, _input, output, join);
    }
}
