using System.Diagnostics;

namespace Heron.Pregel.Automation;

public sealed partial class AutomationHost
{
    private static readonly long ProcessStartedUtcTicks = ReadProcessStart();

    private static long ReadProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static bool IsLive(AutomationRunRecord record) =>
        record is { IsFinished: false, Status: not AutomationRunStatus.AwaitingReview, OwnerIsAlive: true };
    private bool IsRunning(string taskId) => HistorySummaries(taskId).Any(IsLive);
    private readonly Dictionary<string, IReadOnlyList<AutomationRunRecord>> _observedHistory = new(StringComparer.Ordinal);

    /// <summary>Client and CLI read the same records; only the owning process executes and writes a run.</summary>
    public void RefreshExternalRuns()
    {
        bool changed;
        bool triggersChanged;
        lock (_gate)
        {
            changed = RefreshExternalRunsLocked();
            triggersChanged = RefreshTriggersLocked();
        }
        if (triggersChanged) Reschedule();
        else if (changed) Changed?.Invoke();
    }

    private bool RefreshExternalRunsLocked()
    {
        var changed = _store.ReloadIfChanged(ref _document);
        if (changed)
        {
            var ids = _document.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _observedHistory.Keys.Where(id => !ids.Contains(id)).ToArray())
                _observedHistory.Remove(id);
        }
        foreach (var task in _document.Tasks)
        {
            var history = _store.HistorySummaries(task.Id);
            _observedHistory.TryGetValue(task.Id, out var previousHistory);
            // Terminal snapshots are immutable and cached by the store. Live owners still need
            // checking even without a file write: a crashed CLI cannot publish its final status.
            if (ReferenceEquals(history, previousHistory) && !history.Any(record =>
                    !record.IsFinished && record.Status != AutomationRunStatus.AwaitingReview && !_active.ContainsKey(record.RunId)))
                continue;
            using var update = _store.BeginUpdate();
            history = _store.HistorySummaries(task.Id);
            var recovered = false;
            foreach (var run in history)
                if (!_active.ContainsKey(run.RunId) && !ReferenceEquals(RecoverInterrupted(run, task), run)) recovered = true;
            if (recovered) history = _store.HistorySummaries(task.Id);
            if (previousHistory == null || previousHistory.Count != history.Count ||
                !previousHistory.Zip(history).All(pair => pair.First.RunId == pair.Second.RunId &&
                    pair.First.Revision == pair.Second.Revision && pair.First.Status == pair.Second.Status))
            {
                changed = true;
            }
            _observedHistory[task.Id] = history;
            AutomationRunRecord? record = null;
            foreach (var run in history)
                if (record == null || run.QueuedAt >= record.QueuedAt) record = run;
            if (record == null) continue;
            if (!_lastRuns.TryGetValue(task.Id, out var previous) || previous.RunId != record.RunId ||
                previous.Revision != record.Revision || previous.Status != record.Status)
            {
                changed = true;
            }
            _lastRuns[task.Id] = record;
        }
        return changed;
    }

    private async Task MonitorExternalRunsAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, _lifetime.Token).ConfigureAwait(false);
                try
                {
                    RefreshExternalRuns();
                    lock (_gate)
                    {
                        foreach (var run in _active.Values.ToArray())
                        {
                            var count = _store.CancellationCount(run.Task.Id, run.Record.RunId);
                            while (run.CancellationRequests < count)
                            {
                                run.CancellationRequests++;
                                Cancel(run.Task.Id, run.Record.RunId);
                            }
                        }
                    }
                    if (_schedulingEnabled) await Scripts.RefreshRegistrationsAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch (Exception error) { Serilog.Log.Warning(error, "[Automation] external run refresh failed"); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
}
