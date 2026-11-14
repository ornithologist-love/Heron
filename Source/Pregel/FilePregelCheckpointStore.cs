using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heron.Pregel;

internal enum FilePregelStoreFaultPoint
{
    BeforeOutcomeWrite,
    AfterOutcomeWriteBeforeBarrier,
    AfterBarrierCheckpointWriteBeforeManifest,
    AfterManifestWriteBeforeOutcomeCleanup,
}

internal interface IFilePregelStoreFaultInjector
{
    ValueTask OnFaultPointAsync(
        FilePregelStoreFaultPoint faultPoint,
        CancellationToken cancellationToken);
}

public sealed class FilePregelCheckpointStore : IPregelCheckpointStore
{
    private const int CurrentSchemaVersion = 2;

    private sealed record Manifest(
        int SchemaVersion,
        PregelCheckpointId CheckpointId,
        string CheckpointFile,
        PregelCheckpointId? ConsumedFromCheckpointId,
        IReadOnlyList<PregelInvocationId> ConsumedOutcomeIds);

    private readonly string _rootDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<PregelRunId, SemaphoreSlim> _runLocks = new();
    private readonly IFilePregelStoreFaultInjector? _faultInjector;

    public FilePregelCheckpointStore(string rootDirectory)
        : this(rootDirectory, null)
    {
    }

    internal FilePregelCheckpointStore(
        string rootDirectory,
        IFilePregelStoreFaultInjector? faultInjector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _faultInjector = faultInjector;
        Directory.CreateDirectory(_rootDirectory);
        _jsonOptions = CreateJsonOptions();
    }

    public async ValueTask CreateRunAsync(
        PregelCheckpoint initialCheckpoint,
        CancellationToken cancellationToken = default)
    {
        var gate = GetRunLock(initialCheckpoint.RunId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(initialCheckpoint.RunId, cancellationToken)
                .ConfigureAwait(false);
            var runDirectory = RunDirectory(initialCheckpoint.RunId);
            var manifestPath = ManifestPath(runDirectory);
            if (File.Exists(manifestPath))
            {
                throw new PregelException("run_exists", $"Run '{initialCheckpoint.RunId}' already exists.");
            }

            Directory.CreateDirectory(CheckpointsDirectory(runDirectory));
            Directory.CreateDirectory(OutcomesDirectory(runDirectory));
            var checkpointFile = await WriteCheckpointAsync(runDirectory, initialCheckpoint, cancellationToken)
                .ConfigureAwait(false);
            await WriteAtomicJsonAsync(
                manifestPath,
                new Manifest(
                    CurrentSchemaVersion,
                    initialCheckpoint.CheckpointId,
                    checkpointFile,
                    null,
                    Array.Empty<PregelInvocationId>()),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<PregelCheckpoint?> LoadLatestAsync(
        PregelRunId runId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetRunLock(runId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(runId, cancellationToken).ConfigureAwait(false);
            return await LoadLatestCoreAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<PregelCheckpoint>> LoadHistoryAsync(PregelRunId runId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetRunLock(runId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(runId, cancellationToken).ConfigureAwait(false);
            var current = await LoadLatestCoreAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? throw new PregelException("run_not_found", runId.Value);
            var history = new List<PregelCheckpoint>();
            var visited = new HashSet<PregelCheckpointId>();
            var directory = CheckpointsDirectory(RunDirectory(runId));
            // Only follow the manifest's committed ancestry. Orphan files after an interrupted CAS are not history.
            while (true)
            {
                if (!visited.Add(current.CheckpointId) || current.RunId != runId)
                    throw new PregelException("corrupt_checkpoint", "检查点历史损坏，无法重新运行。");
                history.Add(current);
                if (current.ParentCheckpointId is not { } parent) break;
                var files = Directory.GetFiles(directory, $"*-{Hash(parent.Value)}.json");
                if (files.Length != 1)
                    throw new PregelException("reexecution_history_missing", "原运行的检查点历史不完整，无法重新运行。");
                current = await ReadJsonAsync<PregelCheckpoint>(files[0], cancellationToken).ConfigureAwait(false);
                if (current.CheckpointId != parent)
                    throw new PregelException("corrupt_checkpoint", "检查点父引用不匹配。");
            }
            history.Reverse();
            return history;
        }
        finally { gate.Release(); }
    }

    public async ValueTask PutOutcomeAsync(
        PregelRunId runId,
        PregelPendingOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var gate = GetRunLock(runId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(runId, cancellationToken).ConfigureAwait(false);
            EnsureRunExists(runId);
            var directory = OutcomeBaseDirectory(RunDirectory(runId), outcome.BaseCheckpointId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{Hash(outcome.Invocation.LogicalInvocationId.Value)}.json");
            if (File.Exists(path))
            {
                var existing = await ReadJsonAsync<PregelPendingOutcome>(path, cancellationToken).ConfigureAwait(false);
                if (existing.Outcome is PregelInvocationOutcome.Succeeded or PregelInvocationOutcome.Skipped)
                {
                    return;
                }
            }

            await InjectFaultAsync(FilePregelStoreFaultPoint.BeforeOutcomeWrite, cancellationToken).ConfigureAwait(false);
            await WriteAtomicJsonAsync(path, outcome, cancellationToken).ConfigureAwait(false);
            await InjectFaultAsync(
                FilePregelStoreFaultPoint.AfterOutcomeWriteBeforeBarrier,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<PregelPendingOutcome>> LoadOutcomesAsync(
        PregelRunId runId,
        PregelCheckpointId baseCheckpointId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetRunLock(runId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(runId, cancellationToken).ConfigureAwait(false);
            EnsureRunExists(runId);
            var directory = OutcomeBaseDirectory(RunDirectory(runId), baseCheckpointId);
            if (!Directory.Exists(directory))
            {
                return Array.Empty<PregelPendingOutcome>();
            }

            var outcomes = new List<PregelPendingOutcome>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await ReadJsonAsync<PregelPendingOutcome>(path, cancellationToken).ConfigureAwait(false));
            }

            return outcomes
                .OrderBy(outcome => outcome.Invocation.LogicalInvocationId.Value, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask CommitBarrierAsync(
        PregelRunId runId,
        PregelBarrierCommit commit,
        CancellationToken cancellationToken = default)
    {
        var gate = GetRunLock(runId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(runId, cancellationToken).ConfigureAwait(false);
            var current = await LoadLatestCoreAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");
            if (current.CheckpointId != commit.BaseCheckpointId)
            {
                throw new PregelException(
                    "checkpoint_conflict",
                    $"Expected checkpoint '{commit.BaseCheckpointId}', latest is '{current.CheckpointId}'.");
            }

            if (commit.NextCheckpoint.RunId != runId || commit.NextCheckpoint.ParentCheckpointId != commit.BaseCheckpointId)
            {
                throw new PregelException("invalid_barrier_commit", "Barrier commit does not continue the current run checkpoint.");
            }

            var runDirectory = RunDirectory(runId);
            var checkpointFile = await WriteCheckpointAsync(runDirectory, commit.NextCheckpoint, cancellationToken)
                .ConfigureAwait(false);
            await InjectFaultAsync(
                FilePregelStoreFaultPoint.AfterBarrierCheckpointWriteBeforeManifest,
                cancellationToken).ConfigureAwait(false);
            await WriteAtomicJsonAsync(
                ManifestPath(runDirectory),
                new Manifest(
                    CurrentSchemaVersion,
                    commit.NextCheckpoint.CheckpointId,
                    checkpointFile,
                    commit.BaseCheckpointId,
                    commit.ConsumedOutcomeIds.ToArray()),
                cancellationToken).ConfigureAwait(false);
            await InjectFaultAsync(
                FilePregelStoreFaultPoint.AfterManifestWriteBeforeOutcomeCleanup,
                cancellationToken).ConfigureAwait(false);
            CleanupConsumedOutcomesCore(runDirectory, commit.BaseCheckpointId, commit.ConsumedOutcomeIds);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask CleanupConsumedOutcomesAsync(
        PregelRunId runId,
        CancellationToken cancellationToken = default)
    {
        var gate = GetRunLock(runId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(runId, cancellationToken).ConfigureAwait(false);
            var runDirectory = RunDirectory(runId);
            var manifestPath = ManifestPath(runDirectory);
            if (!File.Exists(manifestPath))
            {
                throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");
            }

            var manifest = await ReadJsonAsync<Manifest>(manifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest.ConsumedFromCheckpointId is { } baseCheckpointId)
            {
                CleanupConsumedOutcomesCore(runDirectory, baseCheckpointId, manifest.ConsumedOutcomeIds);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<PregelCheckpoint?> LoadLatestCoreAsync(
        PregelRunId runId,
        CancellationToken cancellationToken)
    {
        var runDirectory = RunDirectory(runId);
        var manifestPath = ManifestPath(runDirectory);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = await ReadJsonAsync<Manifest>(manifestPath, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new PregelException(
                "unsupported_store_schema",
                $"Run '{runId}' uses store schema {manifest.SchemaVersion}; this engine supports {CurrentSchemaVersion}.");
        }

        var checkpointPath = Path.Combine(CheckpointsDirectory(runDirectory), manifest.CheckpointFile);
        var checkpoint = await ReadJsonAsync<PregelCheckpoint>(checkpointPath, cancellationToken).ConfigureAwait(false);
        if (checkpoint.RunId != runId || checkpoint.CheckpointId != manifest.CheckpointId)
        {
            throw new PregelException("corrupt_checkpoint", $"Run '{runId}' manifest does not match its checkpoint.");
        }

        return checkpoint;
    }

    private async ValueTask<string> WriteCheckpointAsync(
        string runDirectory,
        PregelCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var fileName = $"{checkpoint.Superstep:D10}-{Hash(checkpoint.CheckpointId.Value)}.json";
        await WriteAtomicJsonAsync(
            Path.Combine(CheckpointsDirectory(runDirectory), fileName),
            checkpoint,
            cancellationToken).ConfigureAwait(false);
        return fileName;
    }

    private async ValueTask WriteAtomicJsonAsync<T>(string targetPath, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private ValueTask InjectFaultAsync(
        FilePregelStoreFaultPoint faultPoint,
        CancellationToken cancellationToken) =>
        _faultInjector?.OnFaultPointAsync(faultPoint, cancellationToken) ?? ValueTask.CompletedTask;

    private static void CleanupConsumedOutcomesCore(
        string runDirectory,
        PregelCheckpointId baseCheckpointId,
        IReadOnlyList<PregelInvocationId> consumedOutcomeIds)
    {
        var directory = OutcomeBaseDirectory(runDirectory, baseCheckpointId);
        foreach (var invocationId in consumedOutcomeIds)
        {
            try
            {
                File.Delete(Path.Combine(directory, $"{Hash(invocationId.Value)}.json"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The manifest is already durable. Stale pending files are safe and can be cleaned later.
            }
        }

        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must never change commit success.
        }
    }

    private async ValueTask<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new PregelException("invalid_store_json", $"Store file '{path}' is empty.");
        }
        catch (PregelException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new PregelException("store_read_failed", $"Could not read Pregel store file '{path}'.", exception);
        }
    }

    private void EnsureRunExists(PregelRunId runId)
    {
        if (!File.Exists(ManifestPath(RunDirectory(runId))))
        {
            throw new PregelException("run_not_found", $"Run '{runId}' does not exist.");
        }
    }

    private async ValueTask<FileStream> AcquireFileLockAsync(
        PregelRunId runId,
        CancellationToken cancellationToken)
    {
        var runDirectory = RunDirectory(runId);
        Directory.CreateDirectory(runDirectory);
        var lockPath = Path.Combine(runDirectory, ".store.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private SemaphoreSlim GetRunLock(PregelRunId runId) => _runLocks.GetOrAdd(runId, static _ => new SemaphoreSlim(1, 1));
    private string RunDirectory(PregelRunId runId) => Path.Combine(_rootDirectory, $"run-{Hash(runId.Value)}");
    private static string ManifestPath(string runDirectory) => Path.Combine(runDirectory, "manifest.json");
    private static string CheckpointsDirectory(string runDirectory) => Path.Combine(runDirectory, "checkpoints");
    private static string OutcomesDirectory(string runDirectory) => Path.Combine(runDirectory, "outcomes");
    private static string OutcomeBaseDirectory(string runDirectory, PregelCheckpointId checkpointId) =>
        Path.Combine(OutcomesDirectory(runDirectory), Hash(checkpointId.Value));
    private static string Hash(string value) => PregelHash.Sha256(value);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new PregelIdJsonConverter<NodeId>(static value => new NodeId(value), static id => id.Value));
        options.Converters.Add(new PregelIdJsonConverter<ChannelId>(static value => new ChannelId(value), static id => id.Value));
        options.Converters.Add(new PregelIdJsonConverter<PregelRunId>(static value => new PregelRunId(value), static id => id.Value));
        options.Converters.Add(new PregelIdJsonConverter<PregelCheckpointId>(static value => new PregelCheckpointId(value), static id => id.Value));
        options.Converters.Add(new PregelIdJsonConverter<PregelInvocationId>(static value => new PregelInvocationId(value), static id => id.Value));
        options.Converters.Add(new PregelIdJsonConverter<PregelAttemptId>(static value => new PregelAttemptId(value), static id => id.Value));
        return options;
    }
}

internal sealed class PregelIdJsonConverter<TId>(Func<string, TId> parse, Func<TId, string> format) : JsonConverter<TId>
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        parse(reader.GetString() ?? throw new JsonException($"Expected {typeof(TId).Name} string."));

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(format(value));

    public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        parse(reader.GetString() ?? throw new JsonException($"Expected {typeof(TId).Name} property name."));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(format(value));
}
