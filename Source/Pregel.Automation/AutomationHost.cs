using Heron.Pregel;

namespace Heron.Pregel.Automation;

public enum AutomationTaskAvailability { Available, TemplateNotLoaded, TemplateLoadFailed, ValidationFailed, InvalidTask, TemplateNeedsReload }

/// <summary>Live view of one task for the automation page.</summary>
public sealed record AutomationTaskView(
    AutomationTask Task,
    IAutomationTemplate? Template,
    DateTime? NextRun,
    AutomationRunRecord? Current,
    AutomationRunRecord? Last,
    string? Problem)
{
    public AutomationTaskAvailability Availability { get; init; } = Template == null
        ? AutomationTaskAvailability.TemplateNotLoaded
        : Problem == null ? AutomationTaskAvailability.Available : AutomationTaskAvailability.ValidationFailed;
    public IReadOnlyList<AutomationRunRecord> CurrentRuns { get; init; } = [];
    public bool HasPendingReview { get; init; }
    /// <summary>An embedding scheduler owns the next occurrence; the local timer skips it.</summary>
    public bool NextRunFromServer { get; init; }
}

/// <summary>
/// Host-owned automation runtime: task definitions, daily triggers, resource locks, run history and notifications.
/// Templates come from plugins through <see cref="IAutomationRegistry"/>. One instance per process; lives in the
/// tray, independent of any page.
/// </summary>
public sealed partial class AutomationHost : IAutomationRegistry, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly AutomationStore _store;
    private readonly string _evidenceRoot;
    private readonly Func<DateTime> _now;
    private readonly Action<string, string, bool>? _notify;
    private readonly Func<Action<bool>, IDisposable>? _acquireBackgroundExecution;
    private readonly Dictionary<string, IAutomationTemplate> _templates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveRun> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busyResources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutomationRunRecord> _lastRuns = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _running = [];
    private AutomationDocument _document;
    private CancellationTokenSource? _schedule;
    private IDisposable? _backgroundExecution;
    private readonly Dictionary<string, DateTime> _scheduledTimes = new(StringComparer.Ordinal);
    private List<(AutomationTask Task, DateTime Due)>? _suspendedSchedule;
    private DateTime _suspendedAt;
    private bool _stopped;
    private readonly bool _schedulingEnabled;
    private readonly HashSet<string>? _scheduledTaskIds;
    private readonly Task _externalMonitor;

    /// <param name="notify">(title, message, success) — desktop notification sink.</param>
    public AutomationHost(AutomationStore store, string evidenceRoot, bool isActive,
        Action<string, string, bool>? notify = null, Func<DateTime>? now = null)
        : this(store, evidenceRoot, isActive, notify, now, null) { }

    public AutomationHost(AutomationStore store, string evidenceRoot, bool isActive,
        Action<string, string, bool>? notify, Func<DateTime>? now,
        Func<Action<bool>, IDisposable>? acquireBackgroundExecution)
        : this(store, evidenceRoot, isActive, notify, now, acquireBackgroundExecution, true) { }

    public AutomationHost(AutomationStore store, string evidenceRoot, bool isActive,
        Action<string, string, bool>? notify, Func<DateTime>? now,
        Func<Action<bool>, IDisposable>? acquireBackgroundExecution, bool schedulingEnabled)
        : this(store, evidenceRoot, isActive, notify, now, acquireBackgroundExecution, schedulingEnabled, null) { }

    public AutomationHost(AutomationStore store, string evidenceRoot, bool isActive,
        Action<string, string, bool>? notify, Func<DateTime>? now,
        Func<Action<bool>, IDisposable>? acquireBackgroundExecution, bool schedulingEnabled,
        IReadOnlyCollection<string>? scheduledTaskIds)
    {
        _store = store;
        _evidenceRoot = evidenceRoot;
        IsActive = isActive;
        IsSchedulingEnabled = isActive && schedulingEnabled;
        _notify = notify;
        _acquireBackgroundExecution = acquireBackgroundExecution;
        _now = now ?? (() => DateTime.Now);
        _schedulingEnabled = schedulingEnabled;
        _scheduledTaskIds = scheduledTaskIds?.ToHashSet(StringComparer.Ordinal);
        _document = isActive ? store.Load() : new AutomationDocument();
        Scripts = new AutomationScriptCatalog(this, store.Root);
        using (store.BeginUpdate())
            foreach (var task in _document.Tasks)
                foreach (var record in store.HistorySummaries(task.Id).OrderBy(r => r.QueuedAt))
                    _lastRuns[task.Id] = RecoverInterrupted(record, task);
        _externalMonitor = isActive ? Task.Run(MonitorExternalRunsAsync) : Task.CompletedTask;
    }

    public bool IsActive { get; }
    public bool IsSchedulingEnabled { get; }
    public AutomationScriptCatalog Scripts { get; }
    public Func<AutomationRunContext, IPregelSubgraphExecutor?>? SubgraphExecutorFactory { get; set; }
    public Func<AutomationRunContext, IPregelResourceCoordinator?>? ResourceCoordinatorFactory { get; set; }
    public Func<string, DateTimeOffset, bool>? ScheduleClaimed { get; set; }
    public DateTime CurrentTime => _now();

    public string StoreRoot => _store.Root;
    public IDisposable BeginStoreUpdate() => _store.BeginUpdate();
    public void RaiseChanged() => Changed?.Invoke();

    /// <summary>The identity shared by every copy of this task's graph, whichever script file holds it.</summary>
    public string TaskDefinitionId(AutomationTask task)
    {
        lock (_gate)
            return DescribeTaskGraph(task.Id)?.GraphId
                ?? (_templates.GetValueOrDefault(task.TemplateId) as AutomationScriptTemplate)?.DefinitionId
                ?? task.TemplateId;
    }

    /// <summary>Switches resource coordination only while no run could still hold the previous one.</summary>
    public bool TryEnableResources(Func<bool> isEnabled, Func<bool> enable)
    {
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            if (isEnabled()) return true;
            RefreshExternalRunsLocked();
            if (_document.Tasks.Any(task => _store.HistorySummaries(task.Id).Any(record => !record.IsFinished)))
                return false;
            return enable();
        }
    }

    /// <summary>Raised on any change (tasks, templates, run progress). May fire on worker threads.</summary>
    public event Action? Changed;

    /// <summary>Raised when a page asks to open automation; the shell navigates.</summary>
    public event Action<string?, string?, IReadOnlyDictionary<string, string>?>? OpenRequested;

    public void Open(string? taskId = null, string? templateId = null, IReadOnlyDictionary<string, string>? parameters = null) =>
        OpenRequested?.Invoke(taskId, templateId, parameters);

    public IReadOnlyList<IAutomationTemplate> Templates
    {
        get { lock (_gate) return _templates.Values.Where(t => t.ShowInCatalog).OrderBy(t => t.Title, StringComparer.CurrentCulture).ToArray(); }
    }

    public IReadOnlyList<AutomationTask> GetTasks(string templateId)
    {
        lock (_gate) return EffectiveTasksLocked().Where(task => task.TemplateId == templateId).ToArray();
    }

    public int MigrateTaskTemplates(string sourceTemplateId, IReadOnlyDictionary<string, string> workspaceTemplateIds)
    {
        EnsureActive();
        var count = 0;
        lock (_gate)
        {
            var tasks = _document.Tasks.Select(task =>
            {
                if (task.TemplateId != sourceTemplateId || IsRunning(task.Id) || HasPendingReview(task.Id)
                    || !workspaceTemplateIds.TryGetValue(task.Parameter("workspace"), out var targetId)
                    || targetId == sourceTemplateId || !_templates.ContainsKey(targetId)) return task;
                count++;
                return task with { TemplateId = targetId };
            }).ToList();
            if (count == 0) return 0;
            var document = _document with { Tasks = tasks };
            _store.Save(document);
            _document = document;
        }
        Reschedule();
        return count;
    }

    public IDisposable RegisterTemplate(IAutomationTemplate template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Id);
        lock (_gate)
        {
            if (_templates.ContainsKey(template.Id))
                throw new InvalidOperationException($"Duplicate automation template '{template.Id}'.");
            if (template is IAutomationGraphRuntimeConsumer graphTemplate)
                graphTemplate.ConfigureGraphRuntime(template.Id, template.Title, _store.Root);
            _templates.Add(template.Id, template);
            if (IsActive)
            {
                ImportLegacy(template);
                NameUnnamedTasks(template);
            }
        }
        Reschedule();
        return new Registration(() =>
        {
            lock (_gate) _templates.Remove(template.Id);
            Reschedule();
        });
    }

    public IReadOnlyList<AutomationTaskView> Tasks
    {
        get
        {
            lock (_gate)
            {
                return EffectiveTasksLocked().Select(task =>
                {
                    _templates.TryGetValue(task.TemplateId, out var template);
                    var history = HistorySummaries(task.Id);
                    var current = history.Where(IsLive).Select(record => FindRun(task.Id, record.RunId))
                        .OfType<AutomationRunRecord>().ToArray();
                    var (availability, problem) = InspectTask(task, template);
                    var next = NextRun(task);
                    return new AutomationTaskView(task, template, next, current.FirstOrDefault(), history.FirstOrDefault(), problem)
                    { Availability = availability, CurrentRuns = current,
                        NextRunFromServer = next is { } due && ClaimsSchedule(task.Id, new DateTimeOffset(due)),
                        HasPendingReview = history.Any(r => r.Status == AutomationRunStatus.AwaitingReview) };
                }).ToArray();
            }
        }
    }

    public IReadOnlyList<string> TaskOrder
    {
        get { lock (_gate) return (_document.TaskOrder ?? []).ToArray(); }
    }

    /// <summary>Persists presentation order without changing task settings or rescheduling runs.</summary>
    public void SaveTaskOrder(IEnumerable<string> taskIds)
    {
        EnsureActive();
        lock (_gate)
        {
            var existing = _document.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            var order = taskIds.Where(existing.Contains).Distinct(StringComparer.Ordinal).ToList();
            if (order.SequenceEqual(_document.TaskOrder ?? [])) return;
            var document = _document with { TaskOrder = order };
            _store.Save(document);
            _document = document;
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<AutomationRunRecord> History(string taskId) => ReadHistory(taskId, summariesOnly: false);

    /// <summary>List metadata without steps, task snapshots or review bodies.</summary>
    public IReadOnlyList<AutomationRunRecord> HistorySummaries(string taskId) => ReadHistory(taskId, summariesOnly: true);

    public AutomationRunRecord? FindRun(string taskId, string runId)
    {
        lock (_gate)
            return _active.TryGetValue(runId, out var active) && active.Task.Id == taskId
                ? active.Record : _store.FindRun(taskId, runId);
    }

    public AutomationRunRecord? FindRemoteRun(string taskId, string remoteRequestId) =>
        History(taskId).FirstOrDefault(record => record.RemoteRequestId == remoteRequestId);

    public bool MarkRemoteReported(string taskId, string runId)
    {
        var reported = _store.MarkRemoteReported(taskId, runId);
        if (reported) Changed?.Invoke();
        return reported;
    }

    private IReadOnlyList<AutomationRunRecord> ReadHistory(string taskId, bool summariesOnly)
    {
        lock (_gate)
        {
            var history = (summariesOnly ? _store.HistorySummaries(taskId) : _store.History(taskId)).ToList();
            foreach (var active in _active.Values.Where(r => r.Task.Id == taskId))
            {
                history.RemoveAll(r => r.RunId == active.Record.RunId);
                var record = active.Record;
                history.Add(summariesOnly ? record with { Steps = [], TaskSnapshot = null, Launch = null, Review = null,
                    EvidenceDirectories = [], Artifacts = [], ReexecutionNodePath = [] } : record);
            }
            return history.OrderByDescending(r => r.QueuedAt).ToArray();
        }
    }

    /// <summary>Next free default name for a new task: 「{Title}」, then 「{Title}-2」「{Title}-3」…</summary>
    public string SuggestName(string templateId)
    {
        lock (_gate) return SuggestNameLocked(templateId);
    }

    /// <summary>
    /// All rules for <paramref name="task"/> as it would be saved: host rules (name, time, target) plus the template's business rules. Errors first.
    /// </summary>
    public IReadOnlyList<AutomationRuleViolation> Check(AutomationTask task)
    {
        lock (_gate) return CheckLocked(task);
    }

    /// <summary>Saves a task; throws <see cref="AutomationValidationException"/> when an error-level rule fails.</summary>
    public void Save(AutomationTask task)
    {
        EnsureActive();
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            RefreshExternalRunsLocked();
            // A legacy form may still be open when its saved task is migrated in the background.
            var saved = _document.Tasks.FirstOrDefault(t => t.Id == task.Id);
            if (saved != null && saved.TemplateId != task.TemplateId
                && _templates.TryGetValue(task.TemplateId, out var previousTemplate) && !previousTemplate.ShowInCatalog)
                task = task with { TemplateId = saved.TemplateId };
            var errors = CheckLocked(task).Where(v => v.Severity == AutomationRuleSeverity.Error).ToArray();
            if (errors.Length > 0) throw new AutomationValidationException(errors);
            SaveTaskConfigurationLocked(task, saved);
        }
        Reschedule();
    }

    /// <summary>Atomic template/name reuse for concurrent CLI callers creating the same task.</summary>
    public AutomationTask GetOrCreateTask(AutomationTask task)
    {
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            RefreshExternalRunsLocked();
            var existing = _document.Tasks.FirstOrDefault(t => t.TemplateId == task.TemplateId
                && t.Name == task.Name);
            if (existing != null) return ResolveTriggersLocked(existing);
            Save(ResolveTriggersLocked(task));
            return ResolveTriggersLocked(_document.Tasks.Single(t => t.Id == task.Id));
        }
    }

    /// <summary>
    /// Registers a stable manual invocation entry after checking this invocation's effective input.
    /// Only template defaults are saved; subsequent calls keep their own parameter overrides.
    /// </summary>
    public AutomationTask RegisterInvocationTask(AutomationTask defaults, IReadOnlyDictionary<string, string> parameters,
        bool validateInvocation = true)
    {
        EnsureActive();
        AutomationTask registered;
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            RefreshExternalRunsLocked();
            if (_document.Tasks.FirstOrDefault(task => task.Id == defaults.Id) is { } existing)
            {
                if (existing.TemplateId != defaults.TemplateId)
                    throw new InvalidOperationException("流程标识与已有任务模板冲突，未修改原任务。");
                if (!validateInvocation && existing.ScriptInputPath != null && defaults.ScriptInputPath != null
                    && !Path.GetFullPath(existing.ScriptInputPath).Equals(Path.GetFullPath(defaults.ScriptInputPath),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("流程标识与已有任务输入路径冲突，未修改原任务。");
                return ResolveTriggersLocked(existing);
            }
            var configured = ResolveTriggersLocked(defaults);
            if (!configured.ManualEnabled || configured.ScheduleEnabled)
                throw new InvalidOperationException("远程流程登记必须仅启用手动触发。");
            if (!_templates.TryGetValue(configured.TemplateId, out var template))
                throw new InvalidOperationException("流程模板尚未加载，无法登记。");
            if (validateInvocation)
            {
                // Match StartRunCore's root invocation normalization, including blank-value fallback.
                var fields = template.Fields.Where(field => !field.IsReadOnly).Select(field => field.Key).ToHashSet();
                var invocation = configured with
                {
                    Parameters = configured.Parameters.Concat(parameters).GroupBy(pair => pair.Key)
                        .Select(group => group.Last()).Where(pair => fields.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                        .ToDictionary(pair => pair.Key, pair => pair.Value)
                };
                var errors = CheckLocked(invocation).Where(rule => rule.Severity == AutomationRuleSeverity.Error).ToArray();
                if (errors.Length > 0) throw new AutomationValidationException(errors);
            }
            SaveTaskConfigurationLocked(configured, null);
            registered = ResolveTriggersLocked(_document.Tasks.Single(task => task.Id == defaults.Id));
        }
        Reschedule();
        return registered;
    }

    public void Delete(string taskId)
    {
        EnsureActive();
        AutomationTask? removed;
        IAutomationTemplate? template = null;
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            _document = _store.Load();
            removed = _document.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (removed == null) return;
            _templates.TryGetValue(removed.TemplateId, out template);
            // Deletion is the escape hatch: persist it before any run or template cleanup.
            var document = _document with
            {
                Tasks = _document.Tasks.Where(task => task.Id != taskId).ToList(),
                TaskOrder = _document.TaskOrder.Where(id => id != taskId).ToList()
            };
            _store.Save(document);
            _document = document;
            _lastRuns.Remove(taskId);
            _observedHistory.Remove(taskId);
            try { Cancel(taskId); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] cancellation failed for deleted task {TaskId}", taskId); }
            try { _store.DeleteHistory(taskId); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] history cleanup failed for deleted task {TaskId}", taskId); }
        }
        Reschedule();
        try { template?.OnTaskDeleted(removed); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] template cleanup failed for {TaskId}", taskId); }
    }

    /// <summary>Queues one run now, honoring the saved task's parallel policy.</summary>
    public bool RunNow(string taskId, string trigger = "manual") => RunNow(taskId, trigger, null);

    public bool RunNow(string taskId, string trigger, IReadOnlyDictionary<string, string>? parameters) =>
        StartRun(taskId, trigger, parameters) != null;

    /// <summary>Returns the exact execution id admitted by this call, even with concurrent callers.</summary>
    public string? StartRun(string taskId, string trigger, IReadOnlyDictionary<string, string>? parameters = null)
        => StartRunCore(taskId, trigger, parameters, null, null);

    public string? StartRun(string taskId, string trigger, IReadOnlyDictionary<string, string>? parameters,
        string? remoteRequestId)
        => StartRunCore(taskId, trigger, parameters, null, null, remoteRequestId);

    public string? StartSubgraphRun(string taskId, string trigger, IReadOnlyDictionary<string, string>? rootParameters,
        string remoteRequestId, PregelSubgraphExecutionRequest subgraph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteRequestId);
        ArgumentNullException.ThrowIfNull(subgraph);
        return StartRunCore(taskId, trigger, rootParameters, null, null, remoteRequestId, CopySubgraph(subgraph));
    }

    private string? StartRunCore(string taskId, string trigger, IReadOnlyDictionary<string, string>? parameters,
        string? sourceRunId, Heron.Pregel.PregelReexecutionRequest? reexecution, string? remoteRequestId = null,
        PregelSubgraphExecutionRequest? subgraph = null, Heron.Pregel.PregelManualSkip? manualSkip = null,
        DateTimeOffset? scheduledFor = null)
    {
        EnsureActive();
        var rejected = false;
        string? runId = null;
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            RefreshExternalRunsLocked();
            if (remoteRequestId != null && _store.FindRemoteRun(taskId, remoteRequestId) is { } existing)
                return existing.RunId;
            if (scheduledFor != null && _store.HistorySummaries(taskId).FirstOrDefault(
                    record => record.Trigger == "schedule" && record.ScheduledFor == scheduledFor) is { } occurrenceRun)
                return occurrenceRun.RunId;
            if (_stopped || (trigger == "schedule" && _suspendedSchedule != null)) return null;
            var task = _document.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null && subgraph == null) task = ResolveTriggersLocked(task);
            AutomationRunRecord? source = null;
            if (sourceRunId != null)
            {
                // A derived run is an explicit user request: every refusal explains itself and records nothing.
                if (ReexecutionProblemLocked(taskId, sourceRunId, reexecution!.Mode, out source, out _) is { } refusal)
                    throw new InvalidOperationException(refusal);
                task = source!.TaskSnapshot;
            }
            if (task == null || !_templates.TryGetValue(task.TemplateId, out var template)) return null;
            if (subgraph == null && source == null && (!task.AllowsTrigger(trigger) || (trigger == "schedule" && task.ValidateSchedules() != null))) return null;
            if (subgraph == null && !task.AllowParallel && (IsRunning(taskId) || HasPendingReview(taskId))) return null;
            if (parameters != null) task = task with { Parameters = subgraph != null
                ? parameters.ToDictionary(pair => pair.Key, pair => pair.Value)
                : task.Parameters.Concat(parameters).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value) };
            if (subgraph != null) template = BindSubgraph(template, subgraph);
            var lease = _store.TryAcquireTask(ExecutionLeaseKey(taskId, remoteRequestId, subgraph),
                subgraph == null && source == null && task.AllowParallel);
            if (lease == null) return null;
            using var leaseGuard = new LeaseGuard(lease);
            string? problem;
            try
            {
                if (subgraph == null) task = AutomationParameters.Resolve(template, task);
                problem = subgraph == null ? Problem(task, template) : template.Validate(task);
            }
            catch (Exception error) { problem = error.Message; }
            if (problem != null)
            {
                var record = RecordRejected(task, trigger, problem, remoteRequestId, subgraph);
                if (remoteRequestId != null) runId = record.RunId;
                rejected = true;
            }
            else
            {
                var record = new AutomationRunRecord
                {
                    RunId = $"{_now():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..28], TaskId = task.Id, Trigger = trigger,
                    QueuedAt = DateTimeOffset.Now, Status = AutomationRunStatus.Queued, Summary = "等待资源空闲",
                    TaskSnapshot = task,
                    Launch = subgraph == null ? AutomationLaunchSnapshot.Capture(template, task, Scripts.SavedScriptRevision(task.TemplateId), AutomationRuntime.PublishedTask?.Invoke(this, task)) : null,
                    OwnerProcessId = Environment.ProcessId,
                    OwnerStartedUtcTicks = ProcessStartedUtcTicks,
                    Revision = 1, RemoteRequestId = remoteRequestId, Subgraph = subgraph,
                    ExecutionVersion = (template as AutomationScriptTemplate)?.ExecutionVersion,
                    ScheduledFor = scheduledFor,
                    ScheduleOccurrenceKey = scheduledFor == null ? null : "local:" + task.Id + ":" + scheduledFor.Value.ToUniversalTime().ToString("O"),
                    SourceRunId = sourceRunId, ReexecutionMode = reexecution?.Mode.ToString(),
                    ReexecutionNodePath = reexecution?.NodePath.Select(x => x.Value).ToArray() ?? []
                };
                var recorder = new AutomationRunRecorder(record, template, task, _evidenceRoot, OnProgress, source);
                var run = new ActiveRun(task, template, recorder, template.ResourceKeys(task).ToArray(),
                    CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token)) { Lease = lease, Reexecution = reexecution, ManualSkip = manualSkip };
                _store.Upsert(recorder.Record);
                runId = record.RunId;
                _active[record.RunId] = run;
                UpdateBackgroundExecutionLocked();
                Track(ExecuteAsync(run, trigger));
                leaseGuard.Lease = null;
            }
        }
        // A rejected attempt is still a history entry (「已跳过」 + reason), so listeners must refresh too.
        Changed?.Invoke();
        return rejected && remoteRequestId == null ? null : runId;
    }

    public void RunAll(string trigger = "tray")
    {
        foreach (var view in Tasks.Where(v => v.Task.AllowsTrigger(trigger)))
            RunNow(view.Task.Id, trigger);
    }

    public void Cancel(string taskId) => Cancel(taskId, null);

    /// <summary>Ensures one remote stop request without escalating a retransmission to a second stop.</summary>
    public void CancelRemote(string taskId, string runId)
    {
        lock (_gate)
        {
            if (_store.FindRun(taskId, runId) is not { } record) return;
            if (IsLive(record)) _store.RequestRemoteCancellation(taskId, runId);
            else if (record is { Subgraph: not null, Status: AutomationRunStatus.AwaitingReview, Review: { } review })
                Review(taskId, runId, new(review.Id, review.ContentHash, AutomationReviewAction.Abandon));
        }
    }

    /// <summary>Stops one execution; omitted run id retains the task-wide stop operation.</summary>
    public void Cancel(string taskId, string? runId)
    {
        lock (_gate)
        {
            foreach (var record in HistorySummaries(taskId).Where(r => IsLive(r) && (runId == null || r.RunId == runId)))
            {
                if (_active.TryGetValue(record.RunId, out var run))
                {
                    if (run.Recorder.IsFinalizing || run.Cancellation.IsCancellationRequested) run.FinalizationCancellation.Cancel();
                    else run.Cancellation.Cancel();
                }
                else _store.RequestCancellation(taskId, record.RunId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _schedule?.Cancel();
            _lifetime.Cancel();
            UpdateBackgroundExecutionLocked();
            running = _running.ToArray();
        }
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] stop did not drain all runs"); }
        await Scripts.DisposeAsync();
        await _externalMonitor.ConfigureAwait(false);
    }

    private async Task ExecuteAsync(ActiveRun run, string trigger)
    {
        var token = run.Cancellation.Token;
        AutomationRunResult result;
        try
        {
            await AcquireResourcesAsync(run, token).ConfigureAwait(false);
            try
            {
                Publish(run, r => r with { Status = AutomationRunStatus.Running, StartedAt = r.StartedAt ?? DateTimeOffset.Now,
                    Summary = run.Decision?.Action == AutomationReviewAction.TransferToChat ? "正在转到 AI Chat" : "运行中" }, true);
                var context = new AutomationRunContext(run.Task, run.Recorder.Record.RunId, trigger, run.Recorder,
                    line => Serilog.Log.Information("[Automation:{Task}] {Line}", run.Task.DisplayName, line))
                { PermissionRequest = (permission, ct) => RequestPermissionAsync(run, permission, ct),
                    StateRoot = _store.Root, FinalizationCancellationToken = run.FinalizationCancellation.Token,
                    ReexecutionSourceRunId = run.Record.SourceRunId, Reexecution = run.Reexecution, ManualSkip = run.ManualSkip,
                    RemoteRequestId = run.Record.RemoteRequestId, Subgraph = run.Record.Subgraph,
                    ReadSnapshot = () => FindRun(run.Task.Id, run.Record.RunId) };
                context.SubgraphExecutor = SubgraphExecutorFactory?.Invoke(context);
                context.ResourceCoordinator = ResourceCoordinatorFactory?.Invoke(context)
                    ?? AutomationRuntime.ResourceCoordinator?.Invoke();
                context.ResourceQueueTimeout = AutomationRuntime.ResourceQueueTimeout?.Invoke()
                    ?? (context.ResourceCoordinator == null ? null : TimeSpan.FromMinutes(30));
                using (AutomationRuntime.RunScope?.Invoke())
                using (PregelResourceScope.Enter(context.ResourceCoordinator, context.ResourceQueueTimeout))
                    result = await Task.Run(() => run.Decision is { } decision && run.PreviousReview is { } review
                        ? RunReviewAsync(run, context, review, decision, token)
                        : run.Template.RunAsync(context, token), CancellationToken.None).ConfigureAwait(false);
                if (result.Status == AutomationRunStatus.AwaitingReview && result.Review is null)
                    throw new InvalidOperationException("待审核结果缺少草稿");
                // Graph finalization already decided its terminal status and original reason.
                if (token.IsCancellationRequested
                    && result.Status is not (AutomationRunStatus.Succeeded or AutomationRunStatus.Failed or AutomationRunStatus.Cancelled)
                    && result.Review is null)
                    result = InterruptedReview(run, "操作已取消，原草稿仍待审核") ?? AutomationRunResult.Cancelled();
            }
            finally
            {
                ReleaseResources(run);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = InterruptedReview(run, "操作已取消，原草稿仍待审核") ?? AutomationRunResult.Cancelled();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[Automation] run {RunId} failed", run.Recorder.Record.RunId);
            result = InterruptedReview(run, "审核操作未完成，原草稿已保留：" + ex.Message) ?? AutomationRunResult.Failed(ex.Message);
        }

        var final = run.Recorder.Finish(result);
        var taskExists = true;
        lock (_gate)
        {
            _active.Remove(final.RunId);
            UpdateBackgroundExecutionLocked();
            try { taskExists = _store.UpsertIfTaskExists(final); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] history write failed"); }
            if (!taskExists) _lastRuns.Remove(run.Task.Id);
            else if (!_lastRuns.TryGetValue(run.Task.Id, out var last) || final.QueuedAt >= last.QueuedAt)
                _lastRuns[run.Task.Id] = final;
        }
        run.Cancellation.Dispose();
        run.FinalizationCancellation.Dispose();
        run.Lease?.Dispose();
        Changed?.Invoke();
        if (taskExists) NotifyResult(run, final);
    }

    private void NotifyResult(ActiveRun run, AutomationRunRecord final)
    {
        if (_notify == null || _stopped) return;
        var title = $"{run.Template.Title} · {run.Task.DisplayName}";
        switch (final.Status)
        {
            case AutomationRunStatus.AwaitingReview:
                _notify($"{title} 待审核", "请在自动化的运行详情中查看草稿并审核", true);
                break;
            case AutomationRunStatus.Failed:
                var step = final.Steps.FirstOrDefault(s => s.State == AutomationStepState.Failed);
                _notify($"{title} 失败", step == null ? final.Summary : $"「{step.Title}」失败：{step.Error ?? final.Summary}", false);
                break;
            case AutomationRunStatus.Succeeded when run.Task.NotifyOnSuccess:
                _notify($"{title} 完成", final.Summary, true);
                break;
        }
    }

    private async Task AcquireResourcesAsync(ActiveRun run, CancellationToken token)
    {
        while (true)
        {
            lock (_gate)
            {
                if (!run.Resources.Any(_busyResources.Contains))
                {
                    foreach (var key in run.Resources) _busyResources.Add(key);
                    return;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    private void ReleaseResources(ActiveRun run)
    {
        lock (_gate)
            foreach (var key in run.Resources)
                _busyResources.Remove(key);
    }

    private void OnProgress(AutomationRunRecord record, bool persist)
    {
        lock (_gate)
        {
            // A timed-out callback can publish late; it must never overwrite the saved review or terminal result.
            if (!_active.TryGetValue(record.RunId, out var active) || record.Revision < active.Record.Revision) return;
            if (persist || record.OwnerProcessId != 0)
            {
                try { if (!_store.UpsertIfTaskExists(record)) return; }
                catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] history write failed"); }
            }
        }
        Changed?.Invoke();
    }

    private void Publish(ActiveRun run, Func<AutomationRunRecord, AutomationRunRecord> change, bool persist)
    {
        var record = run.Recorder.Update(change);
        OnProgress(record, persist);
    }

    private void Reschedule()
    {
        if (!IsActive || !_schedulingEnabled) { Changed?.Invoke(); return; }
        CancellationTokenSource schedule;
        CancellationToken scheduleToken;
        List<AutomationTask> tasks;
        lock (_gate)
        {
            if (_stopped || _suspendedSchedule != null) return;
            _schedule?.Cancel();
            _schedule?.Dispose();
            _schedule = schedule = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            scheduleToken = schedule.Token;
            _scheduledTimes.Clear();
            tasks = EffectiveTasksLocked().Where(t => Schedules(t) && t.TriggerConfigurationError == null
                && CanScheduleTemplate(t)).ToList();
            UpdateBackgroundExecutionLocked();
        }
        foreach (var task in tasks)
        {
            if (task.ValidateSchedules() != null) continue;
            Track(RunScheduleAsync(task, schedule, scheduleToken));
        }
        Changed?.Invoke();
    }

    private async Task RunScheduleAsync(AutomationTask task, CancellationTokenSource schedule, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var occurrence = task.NextScheduleOccurrence(new DateTimeOffset(_now()));
                var due = occurrence.LocalDateTime;
                lock (_gate)
                {
                    if (_schedule != schedule || _stopped) return;
                    _scheduledTimes[task.Id] = due;
                }
                Changed?.Invoke();
                // Short bounded waits also respond to wall clock changes and avoid long-delay limits.
                while (new DateTimeOffset(_now()) < occurrence)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp((occurrence - new DateTimeOffset(_now())).TotalSeconds, 0.1, 30)), token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_schedule != schedule || token.IsCancellationRequested || _stopped) return;
                    // A file edit can arrive between the last refresh and this timer waking up.
                    var current = EffectiveTasksLocked().FirstOrDefault(t => t.Id == task.Id);
                    if (current == null || !Schedules(current) || !current.SameSchedules(task)) return;
                    if (ClaimsSchedule(task.Id, occurrence)) continue;
                }
                // Resolve/download/freeze afresh at the due time; a daemon must not keep a stale compiled template.
                if (Scripts.SavedScriptPath(task.TemplateId) is { } script)
                {
                    try { await Scripts.LoadAsync(script, _lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        // A failed local version must be visible, and must not kill the calendar loop or run an old template.
                        lock (_gate)
                        {
                            using var update = _store.BeginUpdate();
                            if (!_store.HistorySummaries(task.Id).Any(record => record.ScheduledFor == occurrence))
                            {
                                AutomationExecutionVersion? version = null;
                                try { version = AutomationRuntime.ScriptVersion?.Invoke(script); }
                                catch (Exception metadataError) when (metadataError is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                                var failed = new AutomationRunRecord
                                {
                                    RunId = $"{_now():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..28],
                                    TaskId = task.Id, Trigger = "schedule", QueuedAt = DateTimeOffset.Now,
                                    EndedAt = DateTimeOffset.Now, Status = AutomationRunStatus.Failed,
                                    Summary = "本地流程选版或加载失败：" + error.Message,
                                    TaskSnapshot = task, Revision = 1, ExecutionVersion = version,
                                    Launch = new AutomationLaunchSnapshot
                                    {
                                        TaskId = AutomationRuntime.PublishedTask?.Invoke(this, task)?.Id, TaskName = AutomationRuntime.PublishedTask?.Invoke(this, task)?.Name,
                                        LocalNickname = task.DisplayName, MachineName = Environment.MachineName,
                                        ScriptRevision = version?.ReportedRevision ?? "",
                                        WorkflowContentRevision = version?.ActualSha256,
                                        Parameters = task.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value)
                                    },
                                    ScheduledFor = occurrence,
                                    ScheduleOccurrenceKey = "local:" + task.Id + ":" + occurrence.ToUniversalTime().ToString("O")
                                };
                                _store.Upsert(failed);
                                _lastRuns[task.Id] = failed;
                            }
                        }
                        Changed?.Invoke();
                        Serilog.Log.Warning(error, "[Automation] 本次定时未执行，保留本地修改：{Task}", task.Id);
                        continue;
                    }
                }
                lock (_gate)
                {
                    if (_stopped || _lifetime.IsCancellationRequested) return;
                    var current = EffectiveTasksLocked().FirstOrDefault(t => t.Id == task.Id);
                    if (current == null || !Schedules(current) || !current.SameSchedules(task)) return;
                    StartRunCore(task.Id, "schedule", null, null, null, scheduledFor: occurrence);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Serilog.Log.Error(ex, "[Automation] schedule failed: {Task}", task.Id); }
    }

    // The desktop supplies the OS lease; this runtime stays independent of Windows and Avalonia.
    private void UpdateBackgroundExecutionLocked()
    {
        if (_acquireBackgroundExecution == null) return;
        var required = !_stopped && (_active.Count > 0 || IsSchedulingEnabled && EffectiveTasksLocked().Any(t =>
            Schedules(t) && (t.AllowParallel || !HasPendingReview(t.Id)) && CanScheduleTemplate(t) && t.ValidateSchedules() == null));
        if (!required)
        {
            if (_backgroundExecution == null) return;
            _backgroundExecution.Dispose();
            _backgroundExecution = null;
            Serilog.Log.Information("[Automation] background execution released");
        }
        else if (_backgroundExecution == null)
        {
            try
            {
                _backgroundExecution = _acquireBackgroundExecution(OnPowerChanged);
                Serilog.Log.Information("[Automation] background execution acquired for scheduled/running tasks");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Automation] background execution unavailable; sleep may delay scheduled tasks");
            }
        }
    }

    private void OnPowerChanged(bool suspended)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (suspended)
            {
                if (_suspendedSchedule != null) return;
                _suspendedAt = _now();
                _suspendedSchedule = EffectiveTasksLocked()
                    .Where(t => Schedules(t) && (t.AllowParallel || !HasPendingReview(t.Id)) && CanScheduleTemplate(t) && t.ValidateSchedules() == null)
                    .Select(t => (t, NextRun(t)!.Value)).ToList();
                // Stop the old relative delays before Windows suspends the process. A due
                // continuation cannot launch a stale task when threads resume out of order.
                _schedule?.Cancel();
                Serilog.Log.Information("[Automation] system suspending; daily triggers paused at {At}", _suspendedAt);
                return;
            }
            if (_suspendedSchedule == null) return;
            var resumedAt = _now();
            foreach (var (task, due) in _suspendedSchedule)
            {
                var current = EffectiveTasksLocked().FirstOrDefault(t => t.Id == task.Id);
                if (current == null || !Schedules(current) || (!current.AllowParallel && HasPendingReview(task.Id)) || !current.SameSchedules(task) ||
                    !_templates.ContainsKey(current.TemplateId)) continue;
                // Keep one history entry per missed daily occurrence (the store retains 50).
                for (var missed = due; missed <= resumedAt; missed = task.NextScheduleLocalRun(missed))
                {
                    var record = new AutomationRunRecord
                    {
                        RunId = $"{missed:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..28],
                        TaskId = task.Id, Trigger = "schedule", QueuedAt = new DateTimeOffset(missed),
                        EndedAt = new DateTimeOffset(resumedAt), Status = AutomationRunStatus.Skipped,
                        Summary = $"系统睡眠错过计划时间 {missed:yyyy-MM-dd HH:mm}，已跳过；" +
                                  $"睡眠 {_suspendedAt:yyyy-MM-dd HH:mm:ss}，恢复 {resumedAt:yyyy-MM-dd HH:mm:ss}"
                    };
                    try
                    {
                        _store.Upsert(record);
                        _lastRuns[task.Id] = record;
                        Serilog.Log.Information("[Automation:{Task}] {Summary}", task.DisplayName, record.Summary);
                    }
                    catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] missed schedule history write failed"); }
                }
            }
            _suspendedSchedule = null;
        }
        // Leave the native notification callback before rescheduling can release its lease.
        Track(Task.Run(Reschedule));
    }

    private string SuggestNameLocked(string templateId)
    {
        var title = _templates.TryGetValue(templateId, out var template) ? template.Title : "自动化任务";
        var used = _document.Tasks.Where(t => t.TemplateId == templateId && !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => t.Name.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!used.Contains(title)) return title;
        for (var i = 2; ; i++)
            if (!used.Contains($"{title}-{i}")) return $"{title}-{i}";
    }

    private IReadOnlyList<AutomationRuleViolation> CheckLocked(AutomationTask task)
    {
        var result = new List<AutomationRuleViolation>();
        _templates.TryGetValue(task.TemplateId, out var template);
        var siblings = EffectiveTasksLocked().Where(t => t.Id != task.Id && t.TemplateId == task.TemplateId).ToArray();
        if (ResolveTriggersLocked(task).TriggerConfigurationError is { } triggerError)
            result.Add(AutomationRuleViolation.Error(AutomationRules.TriggersInvalid, triggerError));

        if (template == null)
            result.Add(AutomationRuleViolation.Error(AutomationRules.TemplateMissing, Problem(task, null) + "，无法保存"));
        if (string.IsNullOrWhiteSpace(task.Name))
            result.Add(AutomationRuleViolation.Error(AutomationRules.NameRequired, "请填写任务名称"));
        else if (siblings.FirstOrDefault(t => string.Equals(t.DisplayName, task.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)) is { } same)
            result.Add(AutomationRuleViolation.Error(AutomationRules.NameDuplicate, $"任务名称「{same.DisplayName}」已被使用", same.Id));
        if (task.ScheduleEnabled && AutomationSchedules.Validate(task.Schedules) is { } timeError)
            result.Add(AutomationRuleViolation.Error(AutomationRules.TimeInvalid, timeError));
        // Kept configuration is checked even while the calendar trigger is off: turning it on must not surface a conflict.
        if (AutomationSchedules.Conflict(task.Schedules) is { } duplicate)
            result.Add(AutomationRuleViolation.Error(AutomationRules.TriggerDuplicate, duplicate));
        if (template == null) return result;

        string? problem;
        try { problem = AutomationParameters.Validate(template, task); }
        catch (Exception ex) { problem = ex.Message; }
        if (problem != null) result.Add(AutomationRuleViolation.Error(AutomationRules.ParametersInvalid, problem));

        try { result.AddRange(template.CheckRules(task, siblings)); }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Automation] rules of {Template} failed", template.Id);
            result.Add(AutomationRuleViolation.Error(AutomationRules.RuleFailed, "检查任务配置时出错：" + ex.Message));
        }

        return result.OrderByDescending(v => v.Severity).ToArray();
    }

    /// <summary>Legacy/imported tasks had no name; give them the template-based default once.</summary>
    private void NameUnnamedTasks(IAutomationTemplate template)
    {
        var changed = false;
        for (var i = 0; i < _document.Tasks.Count; i++)
        {
            var task = _document.Tasks[i];
            if (task.TemplateId != template.Id || !string.IsNullOrWhiteSpace(task.Name)) continue;
            _document.Tasks[i] = task with { Name = SuggestNameLocked(template.Id) };
            changed = true;
        }
        if (!changed) return;
        try { _store.Save(_document); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] naming legacy tasks failed"); }
    }

    private void ImportLegacy(IAutomationTemplate template)
    {
        if (_document.ImportedTemplates.Contains(template.Id)) return;
        try
        {
            var imported = template.ImportLegacyTasks()
                .Where(task => task.TemplateId == template.Id && _document.Tasks.All(t => t.Id != task.Id))
                .ToArray();
            _document.Tasks.AddRange(imported);
            _document.ImportedTemplates.Add(template.Id);
            _store.Save(_document);
            if (imported.Length > 0)
                Serilog.Log.Information("[Automation] Imported {Count} legacy task(s) from {Template}", imported.Length, template.Id);
        }
        catch (Exception ex)
        {
            // Retry on next start; never lose legacy config.
            Serilog.Log.Warning(ex, "[Automation] legacy import failed for {Template}", template.Id);
        }
    }

    private AutomationRunRecord RecordRejected(AutomationTask task, string trigger, string problem, string? remoteRequestId,
        PregelSubgraphExecutionRequest? subgraph = null)
    {
        var now = DateTimeOffset.Now;
        var record = new AutomationRunRecord
        {
            RunId = $"{_now():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..28], TaskId = task.Id, Trigger = trigger,
            QueuedAt = now, StartedAt = now, EndedAt = now, Status = AutomationRunStatus.Skipped, Summary = problem,
            Revision = 1, RemoteRequestId = remoteRequestId, Subgraph = subgraph,
            TaskSnapshot = task,
            Launch = task.ParametersResolved && _templates.TryGetValue(task.TemplateId, out var template)
                ? AutomationLaunchSnapshot.Capture(template, task, Scripts.SavedScriptRevision(task.TemplateId), AutomationRuntime.PublishedTask?.Invoke(this, task)) : null
        };
        _lastRuns[task.Id] = record;
        _store.Upsert(record);
        return record;
    }

    private bool CanScheduleTemplate(AutomationTask task) => _templates.ContainsKey(task.TemplateId)
        || _scheduledTaskIds?.Contains(task.Id) == true && Scripts.SavedScriptPath(task.TemplateId) != null;

    private bool Schedules(AutomationTask task) => task.ScheduleEnabled && (_scheduledTaskIds == null || _scheduledTaskIds.Contains(task.Id));

    private bool ClaimsSchedule(string taskId, DateTimeOffset occurrence) =>
        _scheduledTaskIds?.Contains(taskId) != true
        && (ScheduleClaimed ?? AutomationRuntime.ScheduleClaimed)?.Invoke(taskId, occurrence) == true;

    private DateTime? NextRun(AutomationTask task) =>
        IsSchedulingEnabled && Schedules(task) && (task.AllowParallel || !HasPendingReview(task.Id)) && CanScheduleTemplate(task) && task.ValidateSchedules() == null
            ? _scheduledTimes.TryGetValue(task.Id, out var due) ? due : task.NextScheduleLocalRun(_now())
            : null;

    private string? Problem(AutomationTask task, IAutomationTemplate? template) => InspectTask(task, template).Problem;

    private (AutomationTaskAvailability Availability, string? Problem) InspectTask(AutomationTask task, IAutomationTemplate? template)
    {
        if (task.TriggerConfigurationError is { } triggerError)
            return (AutomationTaskAvailability.ValidationFailed, triggerError);
        if (template is AutomationScriptTemplate { SourceMissing: true }
            || template == null && Scripts.SourceMissing(task.TemplateId))
            return (AutomationTaskAvailability.InvalidTask, "脚本文件已不存在，请恢复文件或选择新的脚本");
        if (template == null) return Scripts.LoadError(task.TemplateId) is { } error
            ? (AutomationTaskAvailability.TemplateLoadFailed, "模板加载失败：" + error)
            : (AutomationTaskAvailability.TemplateNotLoaded, "模板未加载");
        string? problem;
        try
        {
            if (template is AutomationScriptTemplate { SourceChanged: true })
                return (AutomationTaskAvailability.TemplateNeedsReload, AutomationScriptTemplate.SourceChangedMessage);
            problem = AutomationParameters.Validate(template, task);
        }
        catch (Exception ex) { problem = ex.Message; }
        return (problem == null ? AutomationTaskAvailability.Available : AutomationTaskAvailability.ValidationFailed, problem);
    }

    /// <summary>Reconcile durable graph state before allowing the scheduler to replace an interrupted run.</summary>
    private AutomationRunRecord RecoverInterrupted(AutomationRunRecord record, AutomationTask task)
    {
        if (record.IsFinished) return record;
        if (record.Status == AutomationRunStatus.AwaitingReview) return record;
        if (record.OwnerIsAlive) return record;
        // A live CLI may report a different wall-clock process start time on Linux.
        // Only recover after acquiring the same lease held through run finalization.
        using var recoveryLease = _store.TryAcquireTask(ExecutionLeaseKey(task.Id, record.RemoteRequestId, record.Subgraph));
        if (recoveryLease == null) return record;
        // The monitor reads only metadata. Recovery needs the durable steps and review body.
        if (_store.FindRun(task.Id, record.RunId) is not { } persisted) return record;
        record = persisted;
        if (record.IsFinished || record.Status == AutomationRunStatus.AwaitingReview) return record;
        var snapshot = record.TaskSnapshot ?? task;
        if ((record.Subgraph == null
            ? AutomationGraphScript.RecoverInterrupted(_store.Root, snapshot.TemplateId, record.RunId)
            : AutomationSubgraphScript.RecoverInterrupted(_store.Root, snapshot.TemplateId, record)) is { } result)
        {
            var reconciled = AutomationRunRecorder.Finish(record with { TaskSnapshot = snapshot }, result);
            _store.Upsert(reconciled);
            return reconciled;
        }
        if (record.Review is not null)
        {
            var pending = record with { Status = AutomationRunStatus.AwaitingReview, EndedAt = null,
                Summary = "上次审核操作被中断，草稿已保留，请继续审核", Revision = record.Revision + 1 };
            _store.Upsert(pending);
            return pending;
        }
        var recovered = record with
        {
            Status = AutomationRunStatus.Cancelled,
            Revision = record.Revision + 1,
            Summary = record.OwnerProcessId == 0 ? "客户端退出，运行中断" : "执行进程退出，运行中断",
            EndedAt = record.StartedAt ?? record.QueuedAt,
            Steps = record.Steps.Select(step => AutomationRunRecorder.CloseStep(step, AutomationRunStatus.Cancelled,
                record.StartedAt ?? record.QueuedAt, null)).ToArray()
        };
        _store.Upsert(recovered);
        return recovered;
    }

    private void EnsureActive()
    {
        if (!IsActive) throw new InvalidOperationException("自动化未启用");
    }

    private void Track(Task task)
    {
        lock (_gate) _running.Add(task);
        _ = task.ContinueWith(t =>
        {
            lock (_gate) _running.Remove(t);
            if (t.Exception != null) Serilog.Log.Warning(t.Exception, "[Automation] background task failed");
        }, TaskScheduler.Default);
    }

    private sealed record ActiveRun(AutomationTask Task, IAutomationTemplate Template, AutomationRunRecorder Recorder,
        string[] Resources, CancellationTokenSource Cancellation,
        AutomationReviewDecision? Decision = null, AutomationReview? PreviousReview = null)
    {
        public AutomationRunRecord Record => Recorder.Record;
        public CancellationTokenSource FinalizationCancellation { get; } = new();
        public IDisposable? Lease { get; init; }
        public int CancellationRequests { get; set; }
        public Heron.Pregel.PregelReexecutionRequest? Reexecution { get; init; }
        public Heron.Pregel.PregelManualSkip? ManualSkip { get; init; }
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
