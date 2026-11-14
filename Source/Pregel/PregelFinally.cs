namespace Heron.Pregel;

public sealed partial class PregelLinearGraphDsl<TInput>
{
    /// <summary>Declares the run's detached final stage. Body completion, failure and cancellation enter it.</summary>
    public PregelLinearGraphDsl<TInput> Finally(Action<PregelLinearGraphDsl<TInput>> configure, TimeSpan? timeout = null)
    {
        DefineFinally(_builder, _input, configure, timeout);
        return this;
    }

    internal static void DefineFinally(PregelGraphBuilder builder, PregelVariable<TInput> input,
        Action<PregelLinearGraphDsl<TInput>> configure, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (builder.Finally != null || builder.Recovery != null || builder.StageRoutingNodes != null)
            throw new InvalidOperationException("Finally must be declared once, outside another stage.");
        var existing = builder.DefinedNodeIds.ToHashSet();
        var (entry, enabled) = CreateStageEntry(builder, input, "finally", "Finally", null, "收尾条件不满足");
        var flow = new PregelLinearGraphDsl<TInput>(builder, entry, input);
        flow.ConfigureStage("finally", "Finally", configure, "收尾条件不满足", entry, enabled);
        builder.SetFinally(entry.Id, builder.DefinedNodeIds.Where(id => !existing.Contains(id)).ToArray(),
            timeout ?? TimeSpan.FromMinutes(2));
    }

    // TODO: 补齐 Stage / mutation 级失败策略和逐项结果汇总；当前 ContinueOnError 仅作用于末尾节点，不能补造失败输出。
    /// <summary>Explicitly permits successors to run without this node's writes if it fails.</summary>
    public PregelLinearGraphDsl<TInput> ContinueOnError()
    {
        _builder.ContinueOnError(_tail.Id);
        return this;
    }
}

public sealed partial class PregelResultGraphDsl<TInput, TOutput>
{
    /// <summary>Retains failure diagnostics and runs successors without publishing failed writes or commands.</summary>
    public PregelResultGraphDsl<TInput, TOutput> ContinueOnError()
    {
        if (_tail is null) throw new InvalidOperationException("ContinueOnError requires a preceding node.");
        _builder.ContinueOnError(_tail.Id);
        return this;
    }

    public PregelResultGraphDsl<TInput, TOutput> Finally(
        Action<PregelLinearGraphDsl<TInput>> configure, TimeSpan? timeout = null)
    {
        PregelLinearGraphDsl<TInput>.DefineFinally(_builder, _input, configure, timeout);
        return this;
    }
}

public sealed partial class PregelTaskChainDsl
{
    public PregelTaskChainDsl Finally(Action<PregelTaskChainDsl> configure, TimeSpan? timeout = null)
    {
        PregelLinearGraphDsl<PregelUnit>.DefineFinally(_builder, _builder.Output("finally-input", PregelUnit.Value),
            stage => configure(new PregelTaskChainDsl(_builder, stage.Tail.Runtime)), timeout);
        return this;
    }

    public PregelTaskChainDsl ContinueOnError()
    {
        _builder.ContinueOnError(_tail.Id);
        return this;
    }
}
