using System.Text;
using System.Text.Json;

namespace Heron.Pregel.Automation;

public sealed partial class AutomationStore
{
    private string MaintenanceFile => Path.Combine(Root, "maintenance.lock");

    /// <summary>Every execution holds this shared lease until it can no longer mutate its workspace.</summary>
    public IDisposable? TryAcquireExecution() => TryOpenMaintenanceGate(exclusive: false);

    /// <summary>
    /// Atomically stops new executions for this store. Busy work is reported immediately, never cancelled.
    /// The successful lease must remain open until installation exits or is abandoned; process exit releases it.
    /// </summary>
    public bool TryEnterMaintenance(string reason, out IDisposable? lease, out string[] busyReasons)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lease = null;
        busyReasons = [];
        var gate = TryOpenMaintenanceGate(exclusive: true);
        if (gate == null)
        {
            busyReasons = ReadMaintenanceBusyReasons();
            if (busyReasons.Length == 0)
                busyReasons = ["本机有任务正在提交或执行（包括 CLI/Pregel），或另一项维护尚未结束。"];
            return false;
        }
        try
        {
            // Admission was acquired before this mutex: an in-flight registration either already owns
            // a shared lease, or will reject before creating its record. No mutex is held across awaits.
            IDisposable update;
            try { update = BeginUpdate(TimeSpan.Zero); }
            catch (IOException)
            {
                busyReasons = ["自动化任务或配置正在提交，本次不安装更新。"];
                return false;
            }
            using var updateLease = update;
            busyReasons = ReadMaintenanceBusyReasons();
            if (busyReasons.Length > 0) return false;
            var bytes = Encoding.UTF8.GetBytes(reason.Length > 512 ? reason[..512] : reason);
            gate.SetLength(0);
            gate.Write(bytes);
            gate.Flush();
            lease = gate;
            gate = null;
            return true;
        }
        finally { gate?.Dispose(); }
    }

    private FileStream? TryOpenMaintenanceGate(bool exclusive)
    {
        Directory.CreateDirectory(Root);
        var file = MaintenanceFile;
        if (!File.Exists(file))
        {
            try { using var created = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite); }
            catch (IOException) when (File.Exists(file)) { }
        }
        // The file's existence or old contents never mean maintenance is active. Only the live OS
        // handle excludes new work, including when its owner crashes before clearing the file.
        try { return new FileStream(file, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
            exclusive ? FileShare.None : FileShare.Read); }
        catch (IOException) { return null; }
    }

    internal string[] ReadMaintenanceBusyReasons()
    {
        var reasons = new List<string>();
        if (!Directory.Exists(HistoryDirectory)) return [];
        try
        {
            foreach (var file in Directory.EnumerateFiles(HistoryDirectory, "*.jsonl"))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var latest = new Dictionary<string, AutomationRunRecord>(StringComparer.Ordinal);
                while (reader.ReadLine() is { } line)
                {
                    AutomationRunRecord? run;
                    try { run = JsonSerializer.Deserialize<AutomationRunRecord>(line, AutomationJson.Summary); }
                    catch (JsonException) { continue; } // Match the store's handling of torn crash records.
                    if (run == null || string.IsNullOrEmpty(run.RunId)) continue;
                    // A live host can retain older queued/running revisions of an already finished run.
                    // Equal revisions follow file order so the final valid snapshot wins.
                    if (!latest.TryGetValue(run.RunId, out var previous) || run.Revision >= previous.Revision)
                        latest[run.RunId] = run;
                }
                foreach (var run in latest.Values)
                    if (run is { IsFinished: false, Status: not AutomationRunStatus.AwaitingReview, OwnerIsAlive: true })
                        reasons.Add($"自动化任务 {run.TaskId} / {run.RunId} 正在排队或执行（进程 {run.OwnerProcessId}）。");
            }
        }
        catch (IOException) { reasons.Add("自动化执行记录正在变更，暂时无法确认本机空闲。"); }
        catch (UnauthorizedAccessException) { reasons.Add("无法读取自动化执行记录，暂时不能确认本机空闲。"); }
        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private sealed class TaskExecutionLease(IDisposable task, IDisposable admission) : IDisposable
    {
        private IDisposable? _task = task;
        private IDisposable? _admission = admission;
        public void Dispose()
        {
            try { Interlocked.Exchange(ref _task, null)?.Dispose(); }
            finally { Interlocked.Exchange(ref _admission, null)?.Dispose(); }
        }
    }
}

public sealed partial class AutomationHost
{
    /// <summary>Diagnostic snapshot; installation still acquires the atomic maintenance lease.</summary>
    public string[] GetMaintenanceBusyReasons() => _store.ReadMaintenanceBusyReasons();
    /// <summary>Try to enter machine maintenance without cancelling or waiting for running automation.</summary>
    public bool TryEnterMaintenance(string reason, out IDisposable? lease, out string[] busyReasons) =>
        _store.TryEnterMaintenance(reason, out lease, out busyReasons);
}
