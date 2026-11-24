using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Heron.Pregel;
using Heron.Pregel.Tooling;

namespace Heron.Pregel.Automation;

public enum AutomationStepState { Pending, Running, Succeeded, Failed, Cancelled, NotRun, AwaitingReview, Skipped, Queued }

/// <summary>One user-level step of one run. Mutable only through <see cref="AutomationRunRecorder"/>.</summary>
public sealed record AutomationStepRecord
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<string>? DependsOn { get; init; }
    public IReadOnlyList<string> NodeIds { get; init; } = [];
    public IReadOnlyList<AutomationStepRecord> Children { get; init; } = [];
    public bool IsStage { get; init; }
    public bool IsRecovery { get; init; }
    public bool IsFinally { get; init; }
    public IReadOnlyList<string> RecoveryFrom { get; init; } = [];
    public AutomationStepState State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public double? Progress { get; init; }
    public string Detail { get; init; } = "";
    public string? Error { get; init; }
    public bool HasProgress { get; init; }
    /// <summary>The result was inherited from the source run of a manual continue or rerun; this run did not execute it.</summary>
    public bool IsReused { get; init; }
    /// <summary>1-based attempt of the latest execution; 0 in records written before attempts were recorded.</summary>
    public int Attempt { get; init; }
    /// <summary>Error of the previous failed attempt while a retry waits, runs, or after it succeeded.</summary>
    public string? RetryError { get; init; }
}

/// <summary>Persisted history of one execution. The graph evidence stays under <c>Logs/Pregel</c>.</summary>
public sealed record AutomationRunRecord
{
    public string RunId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string Trigger { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutomationExecutionVersion? ExecutionVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? EnvironmentFingerprint { get; init; }
    public DateTimeOffset? ScheduledFor { get; init; }
    public string? ScheduleOccurrenceKey { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemoteRequestId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PregelSubgraphExecutionRequest? Subgraph { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PregelEncodedValue? EncodedOutput { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PregelSubgraphExecutionResult? SubgraphResult { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RemoteReportComplete { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public AutomationRunStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public string Warning { get; init; } = "";
    public IReadOnlyList<AutomationStepRecord> Steps { get; init; } = [];
    public IReadOnlyList<string> EvidenceDirectories { get; init; } = [];
    public IReadOnlyList<ArtifactRecord> Artifacts { get; init; } = [];
    public AutomationTask? TaskSnapshot { get; init; }
    public AutomationLaunchSnapshot? Launch { get; init; }
    public AutomationReview? Review { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastRemoteActionId { get; init; }
    public string? ArtifactPath { get; init; }
    public int OwnerProcessId { get; init; }
    public long OwnerStartedUtcTicks { get; init; }
    public long Revision { get; init; }
    public string? SourceRunId { get; init; }
    public string? ReexecutionMode { get; init; }
    public IReadOnlyList<string> ReexecutionNodePath { get; init; } = [];

    [JsonIgnore]
    public bool OwnerIsAlive
    {
        get
        {
            if (OwnerProcessId <= 0) return false;
            try
            {
                using var process = Process.GetProcessById(OwnerProcessId);
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == OwnerStartedUtcTicks;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }
    }

    [JsonIgnore]
    public bool IsFinished => Status is AutomationRunStatus.Succeeded or AutomationRunStatus.Skipped
        or AutomationRunStatus.Failed or AutomationRunStatus.Cancelled;
}

public static class AutomationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new AutomationTaskJsonConverter() }
    };

    public static readonly JsonSerializerOptions Line = new(Options) { WriteIndented = false };
    public static readonly JsonSerializerOptions Summary = CreateSummaryOptions();

    private static JsonSerializerOptions CreateSummaryOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(AutomationRunRecord)) return;
            for (var i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Name is "steps" or "task_snapshot" or "launch" or "review"
                    or "evidence_directories" or "artifacts" or "reexecution_node_path" or "encoded_output" or "subgraph_result")
                    info.Properties.RemoveAt(i);
        });
        return new(Line) { TypeInfoResolver = resolver };
    }
}

/// <summary>
/// File layout under <c><see cref="AutomationRuntime.DataRoot" />/automation</c>:
/// <c>tasks.json</c> (definitions, one file) and <c>history/{taskId}.jsonl</c> (latest runs, one line each).
/// All writes are atomic replace; all calls are serialized by the owner.
/// </summary>
public sealed partial class AutomationStore(string root)
{
    public string Root => root;
    public const int HistoryLimit = 50;
    private readonly object _gate = new();
    private AutomationDocument? _baseline;
    private (bool Exists, DateTime Written, long Length)? _documentStamp;
    private readonly Dictionary<string, (DateTime LastWriteTimeUtc, long Length, IReadOnlyList<AutomationRunRecord> Records)>
        _historyCache = new(StringComparer.Ordinal);

    // All document/history read-modify-write operations share a process-independent gate.
    public IDisposable BeginUpdate(TimeSpan? timeout = null)
    {
        if (!OperatingSystem.IsWindows())
            return AutomationUpdateLock.AcquireFile(Path.Combine(root, ".store.lock"), timeout ?? TimeSpan.FromSeconds(15),
                "自动化存储忙，请稍后重试");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
        var mutex = new Mutex(false, AutomationRuntime.StoreMutexPrefix + key);
        try
        {
            try { if (!mutex.WaitOne(timeout ?? TimeSpan.FromSeconds(15))) throw new IOException("自动化存储忙，请稍后重试"); }
            catch (AbandonedMutexException) { }
            return new Release(() => { mutex.ReleaseMutex(); mutex.Dispose(); });
        }
        catch { mutex.Dispose(); throw; }
    }

    internal IDisposable? TryAcquireTask(string taskId, bool allowParallel = false)
    {
        var admission = TryAcquireExecution();
        if (admission == null) return null;
        try
        {
            var path = HistoryFile(taskId) + ".lease";
            Directory.CreateDirectory(HistoryDirectory);
            // Hold global admission through registration, queued waits, execution and finalization.
            var task = new FileStream(path, FileMode.OpenOrCreate,
                allowParallel ? FileAccess.Read : FileAccess.ReadWrite, allowParallel ? FileShare.Read : FileShare.None);
            return new TaskExecutionLease(task, admission);
        }
        catch (IOException) { admission.Dispose(); return null; }
        catch { admission.Dispose(); throw; }
    }

    internal int CancellationCount(string taskId, string runId)
    {
        var path = CancellationFile(taskId, runId);
        if (!File.Exists(path)) path = HistoryFile(taskId) + ".cancel"; // legacy stop requests
        if (!File.Exists(path)) return 0;
        var parts = ReadShared(path).Split('\n');
        return parts.Length == 2 && parts[0] == runId && int.TryParse(parts[1], out var count) ? count : 0;
    }

    internal void RequestCancellation(string taskId, string runId)
    {
        using var update = BeginUpdate();
        WriteAtomic(CancellationFile(taskId, runId), runId + "\n" + (CancellationCount(taskId, runId) + 1));
    }

    internal void RequestRemoteCancellation(string taskId, string runId)
    {
        using var update = BeginUpdate();
        if (CancellationCount(taskId, runId) == 0)
            WriteAtomic(CancellationFile(taskId, runId), runId + "\n1");
    }

    public static string DefaultRoot => Path.Combine(AutomationRuntime.DataRoot, "automation");

    private string TasksFile => Path.Combine(root, "tasks.json");
    private string HistoryDirectory => Path.Combine(root, "history");

    private (bool Exists, DateTime Written, long Length) DocumentStamp()
    {
        var file = new FileInfo(TasksFile);
        return file.Exists ? (true, file.LastWriteTimeUtc, file.Length) : (false, default, 0);
    }

    internal bool ReloadIfChanged(ref AutomationDocument document)
    {
        lock (_gate)
        {
            if (_documentStamp == DocumentStamp()) return false;
            var latest = Load();
            var changed = JsonSerializer.Serialize(latest, AutomationJson.Line) !=
                          JsonSerializer.Serialize(document, AutomationJson.Line);
            document = latest;
            return changed;
        }
    }

    public AutomationDocument Load()
    {
        lock (_gate)
        {
            var stamp = DocumentStamp();
            _documentStamp = null;
            if (!stamp.Exists)
            {
                _historyCache.Clear();
                var empty = new AutomationDocument();
                _baseline = Copy(empty);
                _documentStamp = stamp;
                return empty;
            }
            try
            {
                var document = JsonSerializer.Deserialize<AutomationDocument>(ReadShared(TasksFile), AutomationJson.Options)
                       ?? new AutomationDocument();
                foreach (var taskId in _historyCache.Keys.Except(document.Tasks.Select(task => task.Id)).ToArray())
                    _historyCache.Remove(taskId);
                _baseline = Copy(document);
                // Do not mark a read current if an atomic replacement raced with it.
                if (stamp == DocumentStamp()) _documentStamp = stamp;
                return document;
            }
            catch (JsonException ex)
            {
                // Keep the unreadable file for diagnosis instead of silently overwriting user tasks.
                var backup = TasksFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(TasksFile, backup, overwrite: true);
                Serilog.Log.Warning(ex, "[Automation] tasks.json unreadable, backed up to {Backup}", backup);
                return new AutomationDocument();
            }
        }
    }

    public void Save(AutomationDocument document)
    {
        lock (_gate)
        {
            using var update = BeginUpdate();
            var caller = document;
            if (_baseline is { } baseline && File.Exists(TasksFile))
            {
                var latest = JsonSerializer.Deserialize<AutomationDocument>(ReadShared(TasksFile), AutomationJson.Options)!;
                var removed = baseline.Tasks.Select(t => t.Id).Except(document.Tasks.Select(t => t.Id)).ToHashSet();
                var changed = document.Tasks.Where(t => JsonSerializer.Serialize(t, AutomationJson.Line) !=
                    JsonSerializer.Serialize(baseline.Tasks.FirstOrDefault(b => b.Id == t.Id), AutomationJson.Line)).ToArray();
                latest.Tasks.RemoveAll(t => removed.Contains(t.Id) || changed.Any(c => c.Id == t.Id));
                latest.Tasks.AddRange(changed);
                document = latest with
                {
                    TaskOrder = document.TaskOrder.SequenceEqual(baseline.TaskOrder) ? latest.TaskOrder : document.TaskOrder,
                    ImportedTemplates = latest.ImportedTemplates.Union(document.ImportedTemplates).ToList()
                };
            }
            WriteAtomic(TasksFile, JsonSerializer.Serialize(document, AutomationJson.Options));
            _documentStamp = null;
            _baseline = Copy(document);
            if (!ReferenceEquals(caller, document))
            {
                document = Copy(document);
                caller.Tasks.Clear(); caller.Tasks.AddRange(document.Tasks);
                caller.TaskOrder.Clear(); caller.TaskOrder.AddRange(document.TaskOrder);
                caller.ImportedTemplates.Clear(); caller.ImportedTemplates.AddRange(document.ImportedTemplates);
            }
        }
    }

    private static AutomationDocument Copy(AutomationDocument document) => document with
    { Tasks = document.Tasks.ToList(), TaskOrder = document.TaskOrder.ToList(), ImportedTemplates = document.ImportedTemplates.ToList() };

    public IReadOnlyList<AutomationRunRecord> History(string taskId) => ReadHistory(taskId, summariesOnly: false);

    public AutomationRunRecord? FindRemoteRun(string taskId, string remoteRequestId) =>
        History(taskId).FirstOrDefault(record => record.RemoteRequestId == remoteRequestId);

    /// <summary>A terminal remote run is retained until its final snapshot and logs have been acknowledged.</summary>
    public bool MarkRemoteReported(string taskId, string runId)
    {
        lock (_gate)
        {
            using var update = BeginUpdate();
            var record = FindRun(taskId, runId);
            if (record is not { IsFinished: true, RemoteRequestId: not null }) return false;
            if (!record.RemoteReportComplete)
                Upsert(record with { RemoteReportComplete = true, Revision = record.Revision + 1 });
            return true;
        }
    }

    /// <summary>Cached list metadata; steps, task snapshots and review bodies are loaded only on demand.</summary>
    public IReadOnlyList<AutomationRunRecord> HistorySummaries(string taskId) => ReadHistory(taskId, summariesOnly: true);

    private IReadOnlyList<AutomationRunRecord> ReadHistory(string taskId, bool summariesOnly)
    {
        lock (_gate)
        {
            var file = HistoryFile(taskId);
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                _historyCache.Remove(taskId);
                return [];
            }
            var stamp = (info.LastWriteTimeUtc, info.Length);
            if (summariesOnly && _historyCache.TryGetValue(taskId, out var cached) &&
                cached.LastWriteTimeUtc == stamp.LastWriteTimeUtc && cached.Length == stamp.Length)
                return cached.Records;
            if (summariesOnly) _historyCache.Remove(taskId);

            var records = new List<AutomationRunRecord>();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length == 0) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<AutomationRunRecord>(line,
                                summariesOnly ? AutomationJson.Summary : AutomationJson.Line) is { } record)
                            records.Add(record);
                    }
                    catch (JsonException) { /* A torn line from a crash only loses that run. */ }
                }
            }
            var snapshot = records.AsReadOnly();
            // Atomic replacements from another Client/CLI invalidate the parsed snapshot.
            // Do not cache a read if the file changed while it was being parsed.
            info.Refresh();
            if (summariesOnly && info.Exists && info.LastWriteTimeUtc == stamp.LastWriteTimeUtc && info.Length == stamp.Length)
                _historyCache[taskId] = (stamp.LastWriteTimeUtc, stamp.Length, snapshot);
            return snapshot;
        }
    }

    public AutomationRunRecord? FindRun(string taskId, string runId)
    {
        lock (_gate)
        {
            var path = HistoryFile(taskId);
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<AutomationRunRecord>(line, AutomationJson.Summary)?.RunId == runId)
                        return JsonSerializer.Deserialize<AutomationRunRecord>(line, AutomationJson.Line);
                }
                catch (JsonException) { /* Match History's handling of torn lines. */ }
            }
            return null;
        }
    }

    /// <summary>Inserts or replaces a run (matched by id) and keeps the newest <see cref="HistoryLimit"/>.</summary>
    public void Upsert(AutomationRunRecord record)
    {
        lock (_gate)
        {
            using var update = BeginUpdate();
            var all = History(record.TaskId).Where(r => r.RunId != record.RunId).Append(record).ToArray();
            // Retain remote evidence until the server has received the terminal snapshot and all logs.
            var retained = all.Where(r => !r.IsFinished || r.RemoteRequestId != null && !r.RemoteReportComplete);
            var runs = retained.Concat(all.Where(r => r.IsFinished && (r.RemoteRequestId == null || r.RemoteReportComplete))
                .OrderBy(r => r.QueuedAt).TakeLast(HistoryLimit)).OrderBy(r => r.QueuedAt);
            WriteAtomic(HistoryFile(record.TaskId),
                string.Join('\n', runs.Select(r => JsonSerializer.Serialize(r, AutomationJson.Line))) + "\n");
            _historyCache.Remove(record.TaskId);
        }
    }

    // A Client can delete a task while its CLI owner is still cancelling. Check and write
    // under the same cross-process lock so late progress cannot recreate deleted history.
    internal bool UpsertIfTaskExists(AutomationRunRecord record)
    {
        lock (_gate)
        {
            using var update = BeginUpdate();
            if (!File.Exists(TasksFile) || JsonSerializer.Deserialize<AutomationDocument>(
                    ReadShared(TasksFile), AutomationJson.Options)?.Tasks.Any(task => task.Id == record.TaskId) != true)
                return false;
            Upsert(record);
            return true;
        }
    }

    public void DeleteHistory(string taskId)
    {
        lock (_gate)
        {
            using var update = BeginUpdate();
            var file = HistoryFile(taskId);
            if (File.Exists(file)) File.Delete(file);
            _historyCache.Remove(taskId);
        }
    }

    private string HistoryFile(string taskId)
    {
        // Official task identities use "official.<hash>"; separators remain disallowed.
        if (taskId.Length == 0 || taskId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("任务 ID 无效", nameof(taskId));
        return Path.Combine(HistoryDirectory, taskId + ".jsonl");
    }

    private string CancellationFile(string taskId, string runId)
    {
        if (string.IsNullOrEmpty(runId) || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("运行 ID 无效", nameof(runId));
        return HistoryFile(taskId) + "." + runId + ".cancel";
    }

    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    public static string ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}

public sealed record AutomationDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<AutomationTask> Tasks { get; init; } = [];
    /// <summary>Saved task-list order; new tasks are inserted without reordering existing entries.</summary>
    public List<string> TaskOrder { get; init; } = [];

    /// <summary>Templates whose legacy tasks were already imported; import runs once per template.</summary>
    public List<string> ImportedTemplates { get; init; } = [];
}
