namespace Heron.Pregel;

public enum PregelRunStatus
{
    Running,
    Completed,
    Failed,
    Interrupted,
    Cancelled,
    FinalizationPending,
}

public sealed record PregelSendPacket(
    NodeId TargetNodeId,
    PregelEncodedValue Payload,
    IReadOnlyList<string> ParentPath,
    int SendIndex);

public enum PregelInterruptPosition
{
    Before,
    Within,
    After,
}

public sealed record PregelInterruptMarker(
    PregelInterruptPosition Position,
    NodeId NodeId,
    PregelInvocationId InvocationId);

public sealed record PregelInterruptState(
    string InterruptId,
    PregelCheckpointId BaseCheckpointId,
    NodeId NodeId,
    PregelInvocationId InvocationId,
    PregelInterruptPosition Position,
    string Reason,
    PregelEncodedValue? Payload);

public sealed record PregelResumeState(
    string InterruptId,
    PregelInvocationId InvocationId,
    PregelEncodedValue Value);

/// <summary>A stopped execution whose Finally stage must finish before its terminal status is published.</summary>
public sealed record PregelRunFinalization(
    PregelRunStatus Status,
    PregelError? Error,
    PregelError? RecoveryError = null)
{
    // Once best-effort cleanup has drained, explicitly retry its failed steps in declaration order.
    public IReadOnlyList<NodeId>? RetryNodes { get; init; }
    public IReadOnlyDictionary<NodeId, PregelError> Errors { get; init; } = new Dictionary<NodeId, PregelError>();
}

public sealed record PregelBodyResult(PregelRunStatus Status, PregelError? Error);

public sealed record PregelCheckpoint(
    PregelRunId RunId,
    PregelCheckpointId CheckpointId,
    PregelCheckpointId? ParentCheckpointId,
    string GraphFingerprint,
    long Superstep,
    PregelRunStatus Status,
    IReadOnlyDictionary<ChannelId, PregelEncodedValue> ChannelValues,
    IReadOnlyDictionary<ChannelId, long> ChannelVersions,
    IReadOnlyDictionary<NodeId, IReadOnlyDictionary<ChannelId, long>> VersionsSeen,
    IReadOnlyList<ChannelId> UpdatedChannels,
    IReadOnlyList<PregelSendPacket> PendingSends,
    IReadOnlyList<PregelInterruptMarker> InterruptMarkers,
    PregelInterruptState? Interrupt = null,
    PregelResumeState? Resume = null)
{
    public IReadOnlyList<string> MutexKeys { get; init; } = [];
    public IReadOnlyDictionary<string, int> MutexLimits { get; init; } = new Dictionary<string, int>();
    /// <summary>One resource-admission attempt; retries retain its identity and absolute queue deadline.</summary>
    public PregelResourceAdmission? ResourceAdmission { get; init; }
    public string WorkingDirectory { get; init; } = "";
    // False proves no execution has begun. Older checkpoints omit this fact and require recovery conservatively.
    public bool? ExecutionStarted { get; init; }
    public PregelError? Error { get; init; }
    public PregelRunFinalization? Finalization { get; init; }
    public PregelBodyResult? BodyResult { get; init; }
    /// <summary>Latest body failure per node that was committed with ContinueOnError; retained after barriers and resume.</summary>
    public IReadOnlyDictionary<NodeId, PregelError> ContinuedErrors { get; init; } = new Dictionary<NodeId, PregelError>();
    public long RunGraphRevision { get; init; }
    public IReadOnlyList<PregelMutationPlan> Mutations { get; init; } = [];
    /// <summary>Outcomes consumed by the barrier producing this checkpoint; retained for explicit reexecution.</summary>
    public IReadOnlyList<PregelPendingOutcome> CommittedOutcomes { get; init; } = [];
    public PregelReexecutionOrigin? Reexecution { get; init; }
    /// <summary>Seeds of the first derived barrier. Durable together with the initial checkpoint.</summary>
    public IReadOnlyList<PregelPendingOutcome> ReplayOutcomes { get; init; } = [];
}

public sealed record PregelBarrierCommit(
    PregelCheckpointId BaseCheckpointId,
    PregelCheckpoint NextCheckpoint,
    IReadOnlyList<PregelInvocationId> ConsumedOutcomeIds);

public interface IPregelCheckpointStore
{
    /// <summary>Committed parent chain, oldest first. Uncommitted checkpoint files must never be included.</summary>
    ValueTask<IReadOnlyList<PregelCheckpoint>> LoadHistoryAsync(PregelRunId runId, CancellationToken cancellationToken = default) =>
        throw new PregelException("reexecution_store_unsupported", "此检查点存储不支持派生运行。");
    ValueTask CreateRunAsync(PregelCheckpoint initialCheckpoint, CancellationToken cancellationToken = default);
    ValueTask<PregelCheckpoint?> LoadLatestAsync(PregelRunId runId, CancellationToken cancellationToken = default);
    ValueTask PutOutcomeAsync(PregelRunId runId, PregelPendingOutcome outcome, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PregelPendingOutcome>> LoadOutcomesAsync(
        PregelRunId runId,
        PregelCheckpointId baseCheckpointId,
        CancellationToken cancellationToken = default);
    ValueTask CommitBarrierAsync(PregelRunId runId, PregelBarrierCommit commit, CancellationToken cancellationToken = default);
}

public sealed class InMemoryPregelCheckpointStore : IPregelCheckpointStore
{
    private sealed class StoredRun(PregelCheckpoint checkpoint)
    {
        public readonly object Gate = new();
        public PregelCheckpoint Latest = checkpoint;
        public Dictionary<PregelCheckpointId, PregelCheckpoint> History { get; } = new() { [checkpoint.CheckpointId] = checkpoint };
        public Dictionary<(PregelCheckpointId, PregelInvocationId), PregelPendingOutcome> Outcomes { get; } = [];
    }

    private readonly Dictionary<PregelRunId, StoredRun> _runs = [];
    private readonly object _runsGate = new();

    public ValueTask<IReadOnlyList<PregelCheckpoint>> LoadHistoryAsync(PregelRunId runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = GetRun(runId);
        lock (run.Gate)
        {
            var history = new List<PregelCheckpoint>();
            var current = run.Latest;
            while (true)
            {
                history.Add(PregelSnapshotCopy.Checkpoint(current));
                if (current.ParentCheckpointId is not { } parent) break;
                current = run.History[parent];
            }
            history.Reverse();
            return ValueTask.FromResult<IReadOnlyList<PregelCheckpoint>>(history);
        }
    }

    public ValueTask CreateRunAsync(PregelCheckpoint initialCheckpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_runsGate)
        {
            if (!_runs.TryAdd(initialCheckpoint.RunId, new StoredRun(PregelSnapshotCopy.Checkpoint(initialCheckpoint))))
            {
                throw new PregelException("run_exists", $"Run '{initialCheckpoint.RunId}' already exists.");
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PregelCheckpoint?> LoadLatestAsync(PregelRunId runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = GetRunOrNull(runId);
        if (run is null)
        {
            return ValueTask.FromResult<PregelCheckpoint?>(null);
        }

        lock (run.Gate)
        {
            return ValueTask.FromResult<PregelCheckpoint?>(PregelSnapshotCopy.Checkpoint(run.Latest));
        }
    }

    public ValueTask PutOutcomeAsync(
        PregelRunId runId,
        PregelPendingOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = GetRun(runId);
        lock (run.Gate)
        {
            var key = (outcome.BaseCheckpointId, outcome.Invocation.LogicalInvocationId);
            if (run.Outcomes.TryGetValue(key, out var existing)
                && existing.Outcome is PregelInvocationOutcome.Succeeded or PregelInvocationOutcome.Skipped)
            {
                return ValueTask.CompletedTask;
            }

            run.Outcomes[key] = PregelSnapshotCopy.PendingOutcome(outcome);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PregelPendingOutcome>> LoadOutcomesAsync(
        PregelRunId runId,
        PregelCheckpointId baseCheckpointId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = GetRun(runId);
        lock (run.Gate)
        {
            IReadOnlyList<PregelPendingOutcome> outcomes = run.Outcomes
                .Where(pair => pair.Key.Item1 == baseCheckpointId)
                .Select(pair => PregelSnapshotCopy.PendingOutcome(pair.Value))
                .OrderBy(outcome => outcome.Invocation.LogicalInvocationId.Value, StringComparer.Ordinal)
                .ToArray();
            return ValueTask.FromResult(outcomes);
        }
    }

    public ValueTask CommitBarrierAsync(
        PregelRunId runId,
        PregelBarrierCommit commit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = GetRun(runId);
        lock (run.Gate)
        {
            if (run.Latest.CheckpointId != commit.BaseCheckpointId)
            {
                throw new PregelException(
                    "checkpoint_conflict",
                    $"Expected checkpoint '{commit.BaseCheckpointId}', latest is '{run.Latest.CheckpointId}'.");
            }

            run.Latest = PregelSnapshotCopy.Checkpoint(commit.NextCheckpoint);
            run.History[run.Latest.CheckpointId] = run.Latest;
            foreach (var invocationId in commit.ConsumedOutcomeIds)
            {
                run.Outcomes.Remove((commit.BaseCheckpointId, invocationId));
            }
        }

        return ValueTask.CompletedTask;
    }

    private StoredRun GetRun(PregelRunId runId) =>
        GetRunOrNull(runId) ?? throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");

    private StoredRun? GetRunOrNull(PregelRunId runId)
    {
        lock (_runsGate)
        {
            return _runs.GetValueOrDefault(runId);
        }
    }
}

internal static class PregelSnapshotCopy
{
    public static PregelCheckpoint Checkpoint(PregelCheckpoint source) => new(
        source.RunId,
        source.CheckpointId,
        source.ParentCheckpointId,
        source.GraphFingerprint,
        source.Superstep,
        source.Status,
        source.ChannelValues.ToDictionary(pair => pair.Key, pair => pair.Value.Copy()),
        source.ChannelVersions.ToDictionary(pair => pair.Key, pair => pair.Value),
        source.VersionsSeen.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<ChannelId, long>)pair.Value.ToDictionary(item => item.Key, item => item.Value)),
        source.UpdatedChannels.ToArray(),
        source.PendingSends.Select(packet => packet with
        {
            Payload = packet.Payload.Copy(),
            ParentPath = packet.ParentPath.ToArray(),
        }).ToArray(),
        source.InterruptMarkers.ToArray(),
        source.Interrupt is null
            ? null
            : source.Interrupt with { Payload = source.Interrupt.Payload?.Copy() },
        source.Resume is null
            ? null
            : source.Resume with { Value = source.Resume.Value.Copy() })
        {
            MutexKeys = source.MutexKeys.ToArray(),
            MutexLimits = source.MutexLimits.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            ResourceAdmission = source.ResourceAdmission,
            WorkingDirectory = source.WorkingDirectory,
            ExecutionStarted = source.ExecutionStarted,
            Error = CopyError(source.Error),
            BodyResult = source.BodyResult is { } body ? body with { Error = CopyError(body.Error) } : null,
            ContinuedErrors = source.ContinuedErrors.ToDictionary(pair => pair.Key, pair => CopyError(pair.Value)!),
            RunGraphRevision = source.RunGraphRevision,
            Mutations = source.Mutations.Select(x => x.Copy()).ToArray(),
            CommittedOutcomes = source.CommittedOutcomes.Select(PendingOutcome).ToArray(),
            ReplayOutcomes = source.ReplayOutcomes.Select(PendingOutcome).ToArray(),
            Reexecution = source.Reexecution is { } origin ? origin with
            { NodePath = origin.NodePath.ToArray(), InheritedNodes = origin.InheritedNodes.Select(x => x with { Error = CopyError(x.Error) }).ToArray() } : null,
            Finalization = source.Finalization is { } finalization
                ? finalization with { Error = CopyError(finalization.Error), RecoveryError = CopyError(finalization.RecoveryError),
                    RetryNodes = finalization.RetryNodes?.ToArray(),
                    Errors = finalization.Errors.ToDictionary(pair => pair.Key, pair => CopyError(pair.Value)!) }
                : null
        };

    private static PregelError? CopyError(PregelError? error) => error is null ? null
        : error with { Details = error.Details?.ToDictionary(pair => pair.Key, pair => pair.Value) };

    public static PregelPendingOutcome PendingOutcome(PregelPendingOutcome source) => source with
    {
        Invocation = source.Invocation with
        {
            Path = source.Invocation.Path.ToArray(),
            Triggers = source.Invocation.Triggers.ToArray(),
            Input = source.Invocation.Input.Copy(),
        },
        Outcome = CopyOutcome(source.Outcome),
    };

    private static PregelInvocationOutcome CopyOutcome(PregelInvocationOutcome source) => source switch
    {
        PregelInvocationOutcome.Succeeded succeeded => new PregelInvocationOutcome.Succeeded(
            succeeded.Writes.Select(write => write with { Value = write.Value.Copy() }).ToArray(),
            succeeded.Commands.Select(CopyCommand).ToArray()),
        PregelInvocationOutcome.Skipped skipped => new PregelInvocationOutcome.Skipped(skipped.Reason,
            skipped.Writes.Select(write => write with { Value = write.Value.Copy() }).ToArray())
            { Commands = skipped.Commands.Select(CopyCommand).ToArray() },
        PregelInvocationOutcome.Failed failed => new PregelInvocationOutcome.Failed(
            failed.Error with
            {
                Details = failed.Error.Details?.ToDictionary(pair => pair.Key, pair => pair.Value),
            }),
        PregelInvocationOutcome.Interrupted interrupted => new PregelInvocationOutcome.Interrupted(
            interrupted.Reason,
            interrupted.Payload?.Copy(),
            interrupted.Position),
        PregelInvocationOutcome.Cancelled cancelled => new PregelInvocationOutcome.Cancelled(cancelled.Reason),
        _ => throw new PregelException("unknown_outcome", $"Unsupported outcome type '{source.GetType().Name}'."),
    };

    private static PregelCommand CopyCommand(PregelCommand source) => source switch
    {
        PregelCommand.Send send => send with { Payload = send.Payload.Copy() },
        PregelCommand.Mutate mutation => new PregelCommand.Mutate(mutation.Plan.Copy()),
        PregelCommand.MutationResult result => result with { Result = result.Result.Copy() },
        PregelCommand.CompleteMutation complete => complete,
        PregelCommand.Goto goTo => goTo,
        PregelCommand.End end => end,
        PregelCommand.Interrupt interrupt => interrupt with { Payload = interrupt.Payload?.Copy() },
        _ => throw new PregelException("unknown_command", $"Unsupported command type '{source.GetType().Name}'."),
    };
}
