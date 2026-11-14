using Heron.Pregel;
using Heron.Pregel.Tooling;

namespace Heron.Pregel.Automation;

internal interface IAutomationGraphRuntimeConsumer
{
    void ConfigureGraphRuntime(string id, string path, string stateRoot);
}

internal interface IAutomationGraphTemplate
{
    CompiledPregelGraph CompiledGraph { get; }
    IAutomationTemplate BindSubgraph(PregelSubgraphExecutionRequest request);
}

public interface IAutomationReexecutionTemplate
{
    bool SupportsReexecution { get; }
    /// <summary>Validates against the source run's durable graph; creates nothing.</summary>
    Task<Heron.Pregel.PregelReexecutionPreview> PreviewReexecutionAsync(string sourceRunId,
        Heron.Pregel.PregelReexecutionRequest request, CancellationToken token);
}

/// <summary>
/// Shared host plumbing for graph-backed templates: durable execution, reviews, re-execution and subgraphs.
/// Scripts derive from <see cref="AutomationGraphTemplate{TInput}"/> or <see cref="AutomationGraphTemplate{TInput, TOutput}"/>.
/// </summary>
public abstract class AutomationGraphTemplateBase<TInput> : AutomationTemplate<TInput>,
    IAutomationReviewTemplate, IAutomationGraphRuntimeConsumer, IAutomationReexecutionTemplate, IAutomationGraphTemplate where TInput : class, new()
{
    private protected AutomationGraphTemplateBase() { }

    bool IAutomationReexecutionTemplate.SupportsReexecution => true;
    private Func<AutomationGraphScript>? _runtime;

    /// <summary>Adjusts the bound input before the graph receives it; the summary hooks see the bound input.</summary>
    protected virtual TInput PrepareGraphInput(AutomationTask task, TInput input) => input;
    protected virtual string FailureSummary => "流程未完成";

    private protected abstract DynamicPregelGraph AdaptGraph();
    private protected abstract CompiledPregelGraph CompileGraph();
    private protected abstract AutomationRunResult CompleteRun(TInput input, object? output);

    void IAutomationGraphRuntimeConsumer.ConfigureGraphRuntime(string id, string path, string stateRoot) =>
        _runtime = () => new AutomationGraphScript(id, path, stateRoot, AdaptGraph(),
            task => PrepareGraphInput(task, BindInput(task)), (task, output) => CompleteRun(BindInput(task), output),
            FailureSummary);

    private AutomationGraphScript Runtime => _runtime?.Invoke()
        ?? throw new InvalidOperationException("Graph 模板须由自动化宿主注册后执行");

    CompiledPregelGraph IAutomationGraphTemplate.CompiledGraph => CompileGraph();
    IAutomationTemplate IAutomationGraphTemplate.BindSubgraph(PregelSubgraphExecutionRequest request) => Runtime.BindSubgraph(request);

    protected sealed override IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task, TInput input) =>
        AutomationStepTopology.FromGraph(CompileGraph().Definition);

    protected sealed override Task<AutomationRunResult> RunAsync(AutomationRunContext context, TInput input,
        CancellationToken token) => Validate(context.Task, input) is { } problem
        ? Task.FromResult(AutomationRunResult.Failed(problem)) : Runtime.RunAsync(context, token);

    public Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, AutomationReview review,
        AutomationReviewDecision decision, CancellationToken token) => Runtime.ReviewAsync(context, review, decision, token);

    Task<Heron.Pregel.PregelReexecutionPreview> IAutomationReexecutionTemplate.PreviewReexecutionAsync(string sourceRunId,
        Heron.Pregel.PregelReexecutionRequest request, CancellationToken token) =>
        Runtime.PreviewReexecutionAsync(sourceRunId, request, token);
}

/// <summary>A template whose graph is driven by its input only.</summary>
public abstract class AutomationGraphTemplate<TInput> : AutomationGraphTemplateBase<TInput> where TInput : class, new()
{
    protected abstract PregelGraph<TInput> CreateGraph();
    protected virtual string SuccessSummary(TInput input) => "流程执行完成";

    private protected sealed override DynamicPregelGraph AdaptGraph() => DynamicPregelGraph.Adapt(CreateGraph());
    private protected sealed override CompiledPregelGraph CompileGraph() => CreateGraph().Compiled;
    private protected sealed override AutomationRunResult CompleteRun(TInput input, object? output) =>
        AutomationRunResult.Succeeded(SuccessSummary(input));
}

/// <summary>A template whose graph returns an output; <see cref="Complete(TInput, TOutput)"/> sees both.</summary>
public abstract class AutomationGraphTemplate<TInput, TOutput> : AutomationGraphTemplateBase<TInput> where TInput : class, new()
{
    protected abstract PregelGraph<TInput, TOutput> CreateGraph();
    protected virtual AutomationRunResult Complete(TOutput output) => AutomationRunResult.Succeeded("流程执行完成");
    protected virtual AutomationRunResult Complete(TInput input, TOutput output) => Complete(output);

    private protected sealed override DynamicPregelGraph AdaptGraph() => DynamicPregelGraph.Adapt(CreateGraph());
    private protected sealed override CompiledPregelGraph CompileGraph() => CreateGraph().Compiled;
    private protected sealed override AutomationRunResult CompleteRun(TInput input, object? output) =>
        Complete(input, (TOutput)output!);
}
