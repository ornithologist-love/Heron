namespace Heron.Pregel;

/// <summary>The compiled mutation-rule path from the installed root graph, independent of invocation identity.</summary>
public sealed record PregelSubgraphScope(string RootGraphFingerprint, string[] MutationPath);

/// <summary>A complete child graph invocation. Identity and placement survive parent retries and reconnections.</summary>
public sealed record PregelSubgraphExecutionRequest(
    string ParentRunId,
    string NodeId,
    string InvocationId,
    string ChildRunId,
    string[] MutationPath,
    PregelEncodedValue Input,
    string Target,
    string RootGraphFingerprint,
    string GraphFingerprint)
{
    public PregelEncodedValue? ResumeValue { get; init; }
    /// <summary>The child's current interrupt id, when explicitly approving an interrupt without a value.</summary>
    public string? ResumeInterruptId { get; init; }
    public PregelResourceContext? ParentResourceContext { get; init; }
    public TimeSpan? ResourceQueueTimeout { get; init; }
    public PregelResourceAdmission? ResourceAdmission { get; init; }
}

public sealed record PregelSubgraphInterrupt(string InterruptId, string Reason, PregelEncodedValue? Payload);

/// <summary>Transport result of one child; checkpoints and channels remain owned by its executing machine.</summary>
public sealed record PregelSubgraphExecutionResult(string ChildRunId, PregelRunStatus Status,
    PregelEncodedValue? Output = null, PregelError? Error = null, PregelSubgraphInterrupt? Interrupt = null);

public interface IPregelSubgraphExecutor
{
    /// <summary>
    /// Runs or reconnects to the stable child id and forwards native child events to observer.
    /// Cancellation requests child cancellation; the executor must follow its actual stop/finalization result.
    /// A transport disconnect is not proof that the child failed or stopped.
    /// </summary>
    ValueTask<PregelSubgraphExecutionResult> ExecuteAsync(PregelSubgraphExecutionRequest request,
        IPregelRunObserver observer, CancellationToken cancellationToken, CancellationToken finalizationCancellationToken);
}

/// <summary>A locally installed executable child template, resolved without running a node.</summary>
public sealed class PregelSubgraphDefinition
{
    internal PregelSubgraphDefinition(PregelMutationRuntime runtime) => Runtime = runtime;
    internal PregelMutationRuntime Runtime { get; }
    public CompiledPregelGraph Compiled => Runtime.Template;
    public PregelGraphDefinition Definition => Compiled.Definition;
    public string InputCodecId => Runtime.Definition.InputCodecId;
    public string OutputCodecId => Runtime.Definition.OutputCodecId;
}

public static class PregelSubgraphs
{
    /// <summary>Resolves executable bindings from an installed root; a topology JSON document alone is not executable.</summary>
    public static PregelSubgraphDefinition Resolve(CompiledPregelGraph root, PregelSubgraphExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(request);
        if (root.Definition.Fingerprint != request.RootGraphFingerprint)
            throw new PregelException("subgraph_root_mismatch", "The installed root graph differs from the invoking graph.");
        if (request.MutationPath.Length == 0)
            throw new PregelException("subgraph_path_missing", "A child graph requires its compiled mutation-rule path.");
        var graph = root;
        PregelMutationRuntime? runtime = null;
        foreach (var id in request.MutationPath)
        {
            if (!graph.Mutations.TryGetValue(id, out runtime))
                throw new PregelException("subgraph_path_missing", $"Mutation rule '{id}' is not installed at this graph path.");
            graph = runtime.Template;
        }
        if (graph.Definition.Fingerprint != request.GraphFingerprint)
            throw new PregelException("subgraph_fingerprint_mismatch", "The installed child graph differs from the invoking graph.");
        if (runtime!.Definition.InputCodecId != request.Input.CodecId)
            throw new PregelException("mutation_input_codec", "Mutation input codec changed.");
        return new(runtime);
    }

    internal static bool IsLocal(string target, string machine) => string.IsNullOrWhiteSpace(target)
        || string.Equals(target, machine, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class PregelEngine
{
    /// <summary>
    /// Executes the complete installed child with its stable id, or reads/resumes that id's durable state.
    /// Calling again after completion reads its encoded result without repeating the child body.
    /// </summary>
    public async ValueTask<PregelSubgraphExecutionResult> RunSubgraphAsync(CompiledPregelGraph root,
        PregelSubgraphExecutionRequest request, PregelRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var definition = PregelSubgraphs.Resolve(root, request);
        var selected = options ?? new PregelRunOptions();
        if (!PregelSubgraphs.IsLocal(request.Target, selected.ExecutionMachine))
            throw new PregelException("subgraph_target_mismatch", $"Child '{request.ChildRunId}' targets '{request.Target}', not '{selected.ExecutionMachine}'.");
        selected = selected with
        {
            InterruptBefore = null, InterruptAfter = null,
            SubgraphScope = new(request.RootGraphFingerprint, request.MutationPath.ToArray()),
            InheritedResourceContext = request.ParentResourceContext ?? selected.InheritedResourceContext,
            ResourceQueueTimeout = request.ResourceQueueTimeout ?? selected.ResourceQueueTimeout,
            ResourceAdmission = request.ResourceAdmission ?? selected.ResourceAdmission
        };
        var graph = definition.Compiled;
        var id = new PregelRunId(request.ChildRunId);
        var checkpoint = await _store.LoadLatestAsync(id, CancellationToken.None).ConfigureAwait(false);
        PregelRunResult result;
        if (checkpoint is null)
        {
            if (request.ResumeValue != null || request.ResumeInterruptId != null)
                throw new PregelException("run_not_found", $"Child '{request.ChildRunId}' has no state to resume.");
            result = await StartAsync(graph, id, definition.Runtime.Bind(request.Input), selected, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            EnsureGraphMatches(graph, checkpoint);
            if (checkpoint.Status == PregelRunStatus.Interrupted && checkpoint.Interrupt is { } interrupt)
            {
                if (request.ResumeInterruptId != null && request.ResumeInterruptId != interrupt.InterruptId)
                    throw new PregelException("interrupt_id_mismatch", "The child interrupt has changed.");
                if (request.ResumeValue is { } resume && !cancellationToken.IsCancellationRequested)
                    result = await ResumeWithValueAsync(graph, id, interrupt.InterruptId, resume,
                        new MutationResumeCodec(resume.CodecId), selected, cancellationToken).ConfigureAwait(false);
                else if (request.ResumeInterruptId != null || cancellationToken.IsCancellationRequested)
                    result = await ResumeAsync(graph, id, interrupt.InterruptId, selected, cancellationToken).ConfigureAwait(false);
                else result = new(checkpoint.Status, id, checkpoint.CheckpointId, checkpoint,
                    checkpoint.Error, checkpoint.Interrupt);
            }
            else result = await ResumeAsync(graph, id, selected, cancellationToken).ConfigureAwait(false);
        }
        return SubgraphResult(definition.Runtime, result);
    }

    private static PregelSubgraphExecutionResult SubgraphResult(PregelMutationRuntime runtime, PregelRunResult result) =>
        new(result.RunId.Value, result.Status,
            result.Status == PregelRunStatus.Completed ? runtime.Result(result) : null, result.Error,
            result.Interrupt is { } interrupt ? new(interrupt.InterruptId, interrupt.Reason, interrupt.Payload?.Copy()) : null);
}
