namespace Heron.Pregel;

/// <summary>Owns one run's identity, concurrency, durable continuation and terminal result.</summary>
public class PregelRun
{
    protected CompiledPregelGraph Graph { get; }
    private readonly PregelVariables _variables;
    private readonly PregelEngine _engine;
    private readonly PregelRunOptions _options;
    private readonly bool _mustExist;
    private int _executing;

    internal PregelRun(CompiledPregelGraph graph, PregelVariables variables,
        PregelEngine? engine, PregelRunOptions? options, PregelRunId? runId = null, bool mustExist = false)
    {
        Graph = graph;
        _variables = variables;
        _engine = engine ?? new PregelEngine();
        _options = options ?? new PregelRunOptions();
        _mustExist = mustExist;
        Id = runId ?? new PregelRunId(Guid.NewGuid().ToString("N"));
    }

    public PregelRunId Id { get; }
    public PregelRunResult? Result { get; private set; }

    /// <summary>Attaches to an existing run without executing or acknowledging its interrupt.</summary>
    public static async Task<PregelRun> RestoreAsync(CompiledPregelGraph graph, PregelRunId runId,
        PregelEngine engine, PregelRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(engine);
        var checkpoint = await engine.GetStateAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");
        PregelEngine.EnsureGraphMatches(graph, checkpoint);
        return new PregelRun(graph, PregelVariables.Empty, engine, options, runId, mustExist: true)
        {
            Result = new(checkpoint.Status, checkpoint.RunId, checkpoint.CheckpointId, checkpoint,
                checkpoint.Error, checkpoint.Interrupt),
        };
    }

    /// <summary>Starts or continues this run; an interrupt is acknowledged with Unit.</summary>
    public Task<PregelRunResult> RunAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(_options, null, null, cancellationToken);

    /// <summary>Stop before this named node. A subsequent RunAsync continues from the interrupt.</summary>
    public Task<PregelRunResult> RunUntilAsync(Delegate node, CancellationToken cancellationToken = default) =>
        ExecuteAsync(_options with { InterruptBefore = PregelInterruptSelector.Nodes(Pregel.NodeId(node)) }, null, null, cancellationToken);

    public Task<PregelRunResult> ResumeAsync(string interruptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptId);
        return ExecuteAsync(_options, interruptId, null, cancellationToken);
    }

    public Task<PregelRunResult> ResumeWithValueAsync<T>(string interruptId, T value,
        ICheckpointCodec<T>? codec = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptId);
        ArgumentNullException.ThrowIfNull(value);
        return ExecuteAsync(_options, interruptId,
            (options, token) => _engine.ResumeWithValueAsync(Graph, Id, interruptId, value, codec, options, token),
            cancellationToken);
    }

    /// <summary>Continues with an already encoded value, preserving its original codec and bytes.</summary>
    public Task<PregelRunResult> ResumeWithValueAsync(string interruptId, PregelEncodedValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ResumeWithValueAsync(interruptId, value.Copy(), new EncodedCodec(value.CodecId), cancellationToken);
    }

    private async Task<PregelRunResult> ExecuteAsync(PregelRunOptions options, string? interruptId,
        Func<PregelRunOptions, CancellationToken, ValueTask<PregelRunResult>>? resume, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            throw new InvalidOperationException("This run is already executing.");
        try
        {
            if (Result?.Status is PregelRunStatus.Completed or PregelRunStatus.Failed or PregelRunStatus.Cancelled) return Result;
            // Always consult durable state after an interrupt or a failed store operation;
            // the commit may have succeeded even when its caller did not receive a result.
            var existing = await _engine.GetStateAsync(Id, CancellationToken.None).ConfigureAwait(false);
            if (existing is null)
            {
                if (_mustExist || interruptId != null)
                    throw new PregelException("run_not_found", $"Run '{Id}' does not exist.");
                return Result = await _engine.StartAsync(Graph, Id, _variables, options, token).ConfigureAwait(false);
            }
            PregelEngine.EnsureGraphMatches(Graph, existing);
            if (existing.Status == PregelRunStatus.Interrupted && existing.Interrupt is { } interrupt)
            {
                if (interruptId != null && !StringComparer.Ordinal.Equals(interruptId, interrupt.InterruptId))
                    throw new PregelException("interrupt_id_mismatch", "The supplied interrupt ID is not the run's active interrupt.");
                return Result = resume is null
                    ? await _engine.ResumeAsync(Graph, Id, interrupt.InterruptId, options, token).ConfigureAwait(false)
                    : await resume(options, token).ConfigureAwait(false);
            }
            return Result = await _engine.ResumeAsync(Graph, Id, options, token).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _executing, 0); }
    }

    private sealed class EncodedCodec(string codecId) : ICheckpointCodec<PregelEncodedValue>
    {
        public string CodecId => codecId;
        public byte[] Encode(PregelEncodedValue value) => value.Data;
        public PregelEncodedValue Decode(ReadOnlySpan<byte> data) => new(CodecId, data.ToArray());
    }
}

/// <summary>Adds typed output reading to the shared run lifecycle.</summary>
public sealed class PregelRun<TOutput> : PregelRun
{
    private readonly Func<PregelRunResult, TOutput> _read;

    internal PregelRun(CompiledPregelGraph graph, Func<PregelRunResult, TOutput> read, PregelVariables variables,
        PregelEngine? engine, PregelRunOptions? options) : base(graph, variables, engine, options) => _read = read;

    public TOutput Read() => _read(Result
        ?? throw new InvalidOperationException("The run has not returned a result."));
}
