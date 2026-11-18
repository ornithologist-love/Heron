using Heron.Pregel.Automation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Heron.Pregel;

namespace Heron.AutomationTenants;

/// <summary>Agent transport only. CLI owns execution, shared history and task/resource locks.</summary>
public sealed partial class RemoteAutomationAgent(AutomationHost automation, RemoteAutomationAgentSettings settings,
    RemoteAutomationEnvironment environment)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, IDisposable> _startupLeases = [];
    private string _server = "", _root = "";
    private Dictionary<string, Delivery> _deliveries = [];
    private ObservationJournal _observations = new();
    private Task? _inspection;
    private Task? _deployment;
    private readonly RemoteAutomationOfficialScripts _officialScripts = new(automation, environment);
    private readonly Dictionary<string, (IAutomationTemplate? Template, string Revision, string Fingerprint, string DefinitionId)> _graphFingerprints = [];
    private string _connectionNotice = "";
    private RemoteAutomationTaskDefinition[]? _taskCatalog;

    public async Task RunAsync(CancellationToken token)
    {
        FileStream? workerLease = null;
        try
        {
            var leaseDirectory = Path.Combine(environment.LocalRoot(), "remote-automation");
            Directory.CreateDirectory(leaseDirectory);
            while (workerLease is null)
            {
                token.ThrowIfCancellationRequested();
                try { workerLease = new FileStream(Path.Combine(leaseDirectory, "worker.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    ConnectionNotice("同一数据实例已有派发 worker，等待其退出。");
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
            }
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ReleaseStartupLeases();
                    var config = environment.AgentConfig();
                    if (!settings.FollowClientConfiguration || config.Enabled)
                    {
                        var server = (settings.FollowClientConfiguration && !string.IsNullOrWhiteSpace(config.ServerUrl) ? config.ServerUrl : settings.ServerUrl).TrimEnd('/');
                        if (_server != server) Load(server);
                        await SynchronizeAsync(token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Serilog.Log.ForContext<RemoteAutomationAgent>().Warning("Remote automation connection: {ErrorType}: {Message}",
                        error.GetType().Name, error.Message);
                    ConnectionNotice("调度连接未就绪：" + error.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            try { if (_inspection != null) await _inspection; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                Serilog.Log.ForContext<RemoteAutomationAgent>().Warning("Remote automation inspection shutdown: {Message}", error.Message);
            }
            await _officialScripts.FinishAsync();
            try { if (_deployment != null) await _deployment; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                Serilog.Log.ForContext<RemoteAutomationAgent>().Warning("Remote automation deployment shutdown: {Message}", error.Message);
            }
            foreach (var lease in _startupLeases.Values) lease.Dispose();
            _startupLeases.Clear();
            _http.Dispose();
            workerLease?.Dispose();
        }
    }

    private void Load(string server)
    {
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server))).ToLowerInvariant();
        var root = Path.Combine(environment.LocalRoot(), "remote-automation", scope);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(AutomationStore.DefaultRoot);
        var path = Path.Combine(root, "deliveries.json");
        var deliveries = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, Delivery>>(File.ReadAllText(path), Json) ?? [] : [];
        _root = root;
        _deliveries = deliveries;
        LoadControls();
        _observations = LoadObservations();
        SaveObservations();
        _server = server;
        _taskCatalog = null;
        automation.SetCloudTaskServer(server);
        RemoteSubgraphExecutor.Configure(automation, server);
    }

    private RemoteAutomationTask[] Catalog(IReadOnlyList<AutomationTaskView> tasks)
    {
        var cliAvailable = File.Exists(settings.CliExecutablePath);
        var saved = tasks.Select(view => (view, path: automation.Scripts.SavedScriptPath(view.Task.TemplateId)))
            .Select(item =>
            {
                var task = item.view.Task;
                var reason = item.path == null ? "原生任务可查看流程和执行进度，当前仅 CSX 任务支持远程调用。"
                    : !cliAvailable ? "当前安装缺少 CLI，请更新构建机包。"
                    : !File.Exists(item.path) && !_officialScripts.IsPublished(task.Id) ? "本机任务脚本不存在。"
                    : !task.ManualEnabled ? "任务未启用手动触发。"
                    : task.TriggerConfigurationError ?? _officialScripts.Conflict(task) ?? "";
                return new RemoteAutomationTask
                {
                    Id = task.Id, Name = task.DisplayName, Parameters = task.Parameters,
                    ScriptRevision = automation.Scripts.SavedScriptRevision(task.TemplateId),
                    GraphFingerprint = GraphFingerprint(item.view),
                    DefinitionId = _graphFingerprints.GetValueOrDefault(task.Id).DefinitionId ?? task.TemplateId,
                    CanRun = reason.Length == 0, UnavailableReason = reason, Triggers = TriggersOf(task)
                };
            });
        var official = _officialScripts.Entries.Where(entry =>
                tasks.All(view => view.Task.Id != RemoteAutomationOfficialScripts.TaskId(entry)))
            .Select(entry => new RemoteAutomationTask
            {
                Id = RemoteAutomationOfficialScripts.TaskId(entry), Name = entry.Title, DefinitionId = entry.Id,
                ScriptRevision = entry.Revision + ":shared:" + entry.SharedRevision,
                CanRun = cliAvailable, UnavailableReason = cliAvailable ? "" : "当前安装缺少 CLI，请更新构建机包。"
            });
        // Until this server confirms its registry, publish neither local inputs
        // nor names. The first empty poll obtains the authoritative catalog.
        return saved.Concat(official).Select(task =>
        {
            var registered = AutomationCloudTasks.Resolve(_taskCatalog ?? [], task.Id, task.DefinitionId);
            return registered == null ? null : task with
            {
                Id = registered.Id, ConfigurationId = task.Id,
                Name = registered.Name, LocalNickname = task.Name,
                CanRun = !settings.IsObserveOnly(task.Id) && task.CanRun,
                UnavailableReason = settings.IsObserveOnly(task.Id) ? "此任务在本机运行，云端仅接收记录。" : task.UnavailableReason
            };
        }).OfType<RemoteAutomationTask>().ToArray();
    }

    private void ReleaseStartupLeases()
    {
        foreach (var id in _startupLeases.Keys.ToArray())
            if (_deliveries.TryGetValue(id, out var delivery)
                && (!Alive(delivery) || automation.FindRemoteRun(delivery.TaskId, id) != null))
            {
                _startupLeases[id].Dispose();
                _startupLeases.Remove(id);
            }
    }

    private async Task SynchronizeAsync(CancellationToken token)
    {
        _officialScripts.RefreshIfDue(token);
        automation.RefreshExternalRuns();
        var tasks = automation.Tasks;
        var mailbox = await PostAsync<RemoteAutomationPoll>("poll", new
        {
            machine_name = Environment.MachineName, host_version = settings.HostVersion,
            capabilities = new[] { RemoteAutomationProtocol.Capability, RemoteAutomationProtocol.ObserveCapability,
                RemoteAutomationProtocol.InspectCapability, RemoteAutomationProtocol.SubgraphCapability,
                RemoteAutomationProtocol.ResourceCapability, RemoteAutomationProtocol.ControlCapability,
                RemoteAutomationProtocol.TriggerCapability, RemoteAutomationProtocol.DiscardCapability,
                RemoteAutomationProtocol.DeploymentCapability, RemoteAutomationProtocol.TenantSourceCapability }.Concat(settings.IsServerHost
                    ? [RemoteAutomationProtocol.ServerCapability] : Array.Empty<string>()).ToArray(), tasks = Catalog(tasks)
        }, token);
        _taskCatalog = mailbox.TaskCatalog;
        if (_taskCatalog != null) automation.UpdateCloudTaskCatalog(_server, _taskCatalog);
        if (_startupLeases.Count == 0 && (_deployment == null || _deployment.IsCompleted)
            && mailbox.Deployments.FirstOrDefault() is { } deployment)
        {
            // A lost completion receipt replays preparation, never graph execution.
            if (_deployment?.Exception is { } failure)
                Serilog.Log.ForContext<RemoteAutomationAgent>().Warning("Remote automation deployment report: {Message}", failure.GetBaseException().Message);
            var server = _server;
            _deployment = Task.Run(() => DeployAsync(deployment, server, token), token);
        }
        CaptureObservations(tasks);
        foreach (var control in mailbox.Controls) await ApplyControlAsync(control, token);
        ApplyServerSchedules(mailbox.ServerSchedules);
        if (mailbox.ResourceCoordinationEnabled && !automation.TryEnableRemoteResources(_server))
        {
            ConnectionNotice("等待已有本地运行结束后接入统一 Pregel 资源协调。");
            await SynchronizeObservationsAsync(token);
            return;
        }
        await RemotePregelResourceCoordinator.FlushReleasesAsync(_server, token);
        await RemoteSubgraphExecutor.FlushFailedStopsAsync(_server, token);
        ConnectionNotice(_taskCatalog == null ? "服务器未提供任务登记目录，本地记录暂不上报。"
            : "已接入图调度：" + Environment.MachineName);
        if ((_inspection == null || _inspection.IsCompleted) && mailbox.Inspections.FirstOrDefault() is { } inspection)
        {
            var server = _server;
            _inspection = Task.Run(() => InspectAsync(inspection, server, token), token);
        }
        foreach (var request in mailbox.Requests)
        {
            if (!_deliveries.TryGetValue(request.Id, out var delivery))
            {
                delivery = new() { TaskId = request.TaskId };
                _deliveries.Add(request.Id, delivery);
                Save();
            }
            var record = automation.FindRemoteRun(request.TaskId, request.Id);
            if (record is { Subgraph: not null, EncodedOutput: null, OwnerIsAlive: false } && !Alive(delivery))
                record = await automation.RecoverSubgraphOutputAsync(record.TaskId, record.RunId, token) ?? record;
            if (record != null)
            {
                if (_startupLeases.Remove(request.Id, out var lease)) lease.Dispose();
                if (request.CancelRequested && request.CancellationFailure == null && !delivery.StopSent && !record.IsFinished
                    && !mailbox.Controls.Any(control => control.RunId == request.Id))
                {
                    // One stop enters Pregel Finally; a second stop would interrupt cleanup.
                    automation.CancelRemote(request.TaskId, record.RunId);
                    delivery.StopSent = true;
                    Save();
                }
                await ReportAsync(request.Id, delivery, ApplyCancellationFailure(record, request.CancellationFailure), token);
                continue;
            }
            if (Alive(delivery))
            {
                await ReportStateAsync(request.Id, "accepted", "正在加载本机 Pregel 任务", false, token);
                continue;
            }
            if (_startupLeases.Remove(request.Id, out var startup)) startup.Dispose();
            if (delivery.ProcessId != 0)
            {
                delivery.Failure = "CLI 在创建执行记录前退出。" + ReadLaunchError(request.Id);
                Save();
            }
            if (delivery.Failure.Length > 0)
            {
                await ReportStateAsync(request.Id, "failed", delivery.Failure, true, token);
                continue;
            }
            if (request.CancelRequested)
            {
                await ReportStateAsync(request.Id, request.CancellationFailure == null ? "cancelled" : "failed",
                    request.CancellationFailure?.Message ?? "已在执行前停止", true, token);
                continue;
            }
            if (request.ScheduledFor != null && settings.LocalScheduleTaskIds.Contains(request.TaskId, StringComparer.Ordinal))
            {
                await ReportStateAsync(request.Id, "skipped", "本任务的唯一定时触发方是本机，云端日历请求不执行；手动下发仍可运行。", true, token);
                continue;
            }
            if (settings.IsObserveOnly(request.TaskId))
            {
                await ReportStateAsync(request.Id, "failed", "此任务在本机运行，云端仅接收记录。", true, token);
                continue;
            }
            if (request.ScheduledFor is { } scheduledFor && automation.LocalScheduleRan(request.TaskId, scheduledFor))
            {
                await ReportStateAsync(request.Id, "skipped", "本机已在同一时间按本地定时执行，不重复执行", true, token);
                continue;
            }
            RemoteAutomationOfficialScripts.Draft? officialDraft = null;
            var preparingOfficial = false;
            try
            {
                var saved = automation.Tasks.FirstOrDefault(view => view.Task.Id == request.TaskId);
                if (request.DeploymentCatalogPath is { Length: > 0 } && request.DeploymentSource is { } source)
                {
                    // Let the in-flight installation finish before admitting another pinned publication.
                    if (_startupLeases.Count != 0 || _deployment is { IsCompleted: false }) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    officialDraft = await _officialScripts.PrepareDeploymentAsync(new()
                    {
                        TaskId = request.TaskId, Source = source, CatalogPath = request.DeploymentCatalogPath
                    }, _server, timeout.Token);
                }
                else if (saved != null && _officialScripts.Conflict(saved.Task) is { } conflict)
                    throw new InvalidOperationException(conflict);
                if (officialDraft == null && NeedsOfficialPreparation(request.TaskId, saved))
                {
                    var preparation = _officialScripts.Prepare(request.TaskId, token, reload: saved != null);
                    if (!preparation.IsCompleted)
                        preparingOfficial = true;
                    else officialDraft = await preparation;
                }
            }
            catch (Exception error) when (error is AutomationScriptInUseException or AutomationSourceBusyException)
            {
                continue;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                delivery.Failure = "准备官方流程失败：" + error.Message;
                Save();
                await ReportStateAsync(request.Id, "failed", delivery.Failure, true, token);
                continue;
            }
            if (preparingOfficial)
            {
                await ReportStateAsync(request.Id, "accepted", "正在准备所选官方流程", false, token);
                continue;
            }
            // Hold admission until the child has its own persisted run and execution lease.
            var admission = new AutomationStore(AutomationStore.DefaultRoot).TryAcquireExecution();
            if (admission == null)
            {
                await ReportStateAsync(request.Id, "accepted", "构建机正在维护或安装更新，等待维护结束", false, token);
                continue;
            }
            try
            {
                if (officialDraft != null)
                {
                    var task = officialDraft.Task with { ScriptInputPath = OfficialInputPath(request.TaskId) };
                    automation.RegisterInvocationTask(task, request.Parameters, validateInvocation: request.Subgraph is null);
                }
                if (request.DeploymentSource is { } pinned)
                {
                    var selected = automation.Tasks.FirstOrDefault(view => view.Task.Id == request.TaskId)
                        ?? throw new InvalidOperationException("已部署流程的本机绑定不存在。");
                    var entry = automation.Scripts.SavedScriptPath(selected.Task.TemplateId)
                        ?? throw new InvalidOperationException("已部署流程的本机入口不存在。");
                    RemoteAutomationOfficialScripts.VerifyDeploymentVersion(entry, pinned);
                }
                token.ThrowIfCancellationRequested();
                Start(request, delivery);
                _startupLeases.Add(request.Id, admission);
            }
            catch (Exception error)
            {
                admission.Dispose();
                if (delivery.ProcessId != 0)
                {
                    // The child may already own the run. Keep polling its durable record instead of
                    // closing the mailbox because saving the launch journal or process bookkeeping failed.
                    await ReportStateAsync(request.Id, "accepted", "执行进程已启动，等待本机记录", false, token);
                    continue;
                }
                delivery.Failure = "启动 CLI 失败：" + error.Message;
                Save();
                await ReportStateAsync(request.Id, "failed", delivery.Failure, true, token);
            }
        }
        await SynchronizeObservationsAsync(token);
    }

    private void CaptureObservations(IReadOnlyList<AutomationTaskView> tasks)
    {
        if (_taskCatalog == null) return;
        var changed = false;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in tasks)
        {
            GraphFingerprint(view);
            var registered = AutomationCloudTasks.Resolve(_taskCatalog ?? [], view.Task.Id,
                _graphFingerprints.GetValueOrDefault(view.Task.Id).DefinitionId ?? view.Task.TemplateId);
            if (registered == null) continue;
            foreach (var record in automation.HistorySummaries(view.Task.Id))
            {
                if (record.RemoteRequestId != null) continue;
                retained.Add(record.RunId);
                if (_observations.Runs.TryGetValue(record.RunId, out var known))
                {
                    if (known.LogsComplete && known.LastReportedRevision != record.Revision)
                    {
                        known.LogsComplete = false;
                        changed = true;
                    }
                    continue;
                }
                // Existing completed history remains local. Include active runs, pending reviews and
                // every new execution after observation was enabled, including CLI-owned records.
                if (record.IsFinished && record.QueuedAt < _observations.StartedAt) continue;
                _observations.Runs.Add(record.RunId, new Observation
                {
                    CloudTaskId = registered.Id,
                    TaskId = view.Task.Id, RunId = record.RunId, TaskName = view.Task.DisplayName,
                    ScriptRevision = record.ExecutionVersion?.ReportedRevision ?? automation.Scripts.SavedScriptRevision(view.Task.TemplateId)
                });
                changed = true;
            }
        }
        foreach (var id in _observations.Runs.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            var pending = _observations.Runs[id];
            // Keep an admitted in-flight upload when its configuration was
            // removed locally, but stop uploading workflows removed remotely.
            if (pending is { RemoteId.Length: > 0, LogsComplete: false }
                && RegisteredObservation(pending, null) != null) continue;
            _observations.Runs.Remove(id);
            changed = true;
        }
        if (changed) SaveObservations();
    }

    private async Task SynchronizeObservationsAsync(CancellationToken token)
    {
        if (_taskCatalog == null) return;
        foreach (var observation in _observations.Runs.Values.Where(item => !item.LogsComplete).ToArray())
        {
            var record = automation.FindRun(observation.TaskId, observation.RunId);
            var registered = RegisteredObservation(observation, record);
            if (registered == null || observation.RemoteId.StartsWith("ignored.", StringComparison.Ordinal))
            {
                if (registered == null) _observations.Runs.Remove(observation.RunId);
                else observation.LogsComplete = true;
                SaveObservations();
                continue;
            }
            if (record == null)
            {
                if (observation.RemoteId.Length > 0)
                    await ReportStateAsync(observation.RemoteId, "unknown", "本机运行记录已清理，无法确认最终结果。", true, token);
                _observations.Runs.Remove(observation.RunId);
                SaveObservations();
                continue;
            }
            if (observation.RemoteId.Length == 0)
            {
                var receipt = await PostAsync<RemoteAutomationObservationReceipt>("observations", new
                {
                    machine_name = Environment.MachineName, task_id = registered.Id,
                    configuration_id = observation.TaskId, task_name = registered.Name,
                    local_nickname = record.Launch?.LocalNickname ?? record.TaskSnapshot?.Name ?? observation.TaskName,
                    script_revision = observation.ScriptRevision, snapshot = record
                }, token);
                observation.RemoteId = receipt.Id;
                observation.CloudTaskId = registered.Id;
                observation.LastReportedRevision = record.Revision;
                SaveObservations();
            }
            if (observation.RemoteId.StartsWith("ignored.", StringComparison.Ordinal))
            {
                observation.LogsComplete = true;
                SaveObservations();
                continue;
            }
            await ReportAsync(observation.RemoteId, observation, record, token, observed: true);
        }
    }

    private RemoteAutomationTaskDefinition? RegisteredObservation(Observation observation, AutomationRunRecord? record)
    {
        if (_taskCatalog == null) return null;
        // A remembered canonical ID is not permission to ignore a server-side exclusion.
        if (_taskCatalog.Any(item => item.ExcludedConfigurations.Contains(observation.TaskId, StringComparer.Ordinal))) return null;
        if (observation.CloudTaskId.Length > 0)
            return _taskCatalog.FirstOrDefault(item => item.Id == observation.CloudTaskId);
        var definition = _graphFingerprints.GetValueOrDefault(observation.TaskId).DefinitionId
            ?? record?.TaskSnapshot?.TemplateId ?? "";
        return AutomationCloudTasks.Resolve(_taskCatalog, observation.TaskId, definition);
    }

    private async Task InspectAsync(RemoteAutomationInspection request, string server, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        object? result = null;
        var error = "";
        var preparingOfficial = false;
        try
        {
            var view = automation.Tasks.FirstOrDefault(item => item.Task.Id == request.TaskId);
            if (view != null && _officialScripts.Conflict(view.Task) is { } conflict)
                throw new InvalidOperationException(conflict);
            AutomationTask task;
            IAutomationTemplate template;
            if (view == null || NeedsOfficialPreparation(request.TaskId, view))
            {
                preparingOfficial = true;
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Preparation has its own lifetime: a cold download/compile can finish after this
                // bounded preview request, while the next inspection reuses the same loaded entry.
                var prepared = await _officialScripts.Prepare(request.TaskId, token, reload: view != null).WaitAsync(timeout.Token);
                // A source/Shared reload restores the template without replacing the saved task's
                // parameters, trigger policy or input binding. Re-read edits made during preparation.
                var restored = automation.Tasks.FirstOrDefault(item => item.Task.Id == request.TaskId);
                if (restored != null && (restored.Task.TemplateId != prepared.Task.TemplateId
                    || NeedsOfficialPreparation(request.TaskId, restored)))
                    throw new InvalidOperationException("官方流程模板或任务已变化，请重新读取参数与流程图。");
                task = restored?.Task ?? prepared.Task;
                template = restored?.Template ?? prepared.Template;
                preparingOfficial = false;
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
            }
            else
            {
                task = view.Task;
                template = view.Template ?? throw new InvalidOperationException(view.Problem ?? "本机尚未加载任务模板。");
            }
            var definitions = template.Fields;
            var parameters = task.Parameters.Concat(request.Parameters)
                .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value);
            parameters = AutomationParameters.Resolve(template, task with { Parameters = parameters }).Parameters
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            var values = definitions.ToDictionary(field => field.Key, field => field.IsReadOnly ? field.DefaultValue
                : parameters.GetValueOrDefault(field.Key, field.DefaultValue));
            var problems = new List<string>();
            var visible = new Dictionary<string, bool>();
            var visibilityErrors = new Dictionary<string, string>();
            AutomationTask BuildDraft() => AutomationParameters.Resolve(template, task with
            {
                Parameters = parameters.Concat(definitions.Where(field => !field.IsReadOnly)
                    .Select(field => new KeyValuePair<string, string>(field.Key, values[field.Key])))
                    .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value)
            });
            void RefreshVisibility()
            {
                var predicateTask = task with { Parameters = values };
                visibilityErrors.Clear();
                foreach (var field in definitions)
                {
                    visible[field.Key] = true;
                    try { visible[field.Key] = field.If?.Invoke(predicateTask) ?? true; }
                    catch (Exception exception) { visibilityErrors[field.Key] = $"「{field.Label}」的显示条件计算失败：{exception.Message}"; }
                }
            }
            RefreshVisibility();
            foreach (var field in definitions)
            {
                if (!field.IsReadOnly && visible[field.Key] && string.IsNullOrWhiteSpace(values[field.Key]) && field.EmptyValue != null)
                    values[field.Key] = field.EmptyValue;
            }
            RefreshVisibility();
            var choicesByKey = definitions.ToDictionary(field => field.Key, field => field.With
                ?? field.Choices?.Select(value => new AutomationChoice(value, value)).ToArray()
                ?? (IReadOnlyList<AutomationChoice>)[]);
            var choiceErrors = new Dictionary<string, string>();
            var loaded = new HashSet<string>();
            while (definitions.FirstOrDefault(field => visible[field.Key] && !field.IsReadOnly
                && field.Kind is DisplayFieldKind.Choice or DisplayFieldKind.MultiChoice
                && field.With == null && field.Choices == null && !loaded.Contains(field.Key)) is { } dynamicField)
            {
                loaded.Add(dynamicField.Key);
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    var choices = await template.ListFieldChoicesAsync(BuildDraft(), dynamicField.Key, timeout.Token)
                        .WaitAsync(timeout.Token) ?? [];
                    choicesByKey[dynamicField.Key] = choices;
                    if (choices.Count == 0)
                        choiceErrors[dynamicField.Key] = "没有可用选项，请检查配置后重新读取";
                    else if (dynamicField.Kind == DisplayFieldKind.Choice)
                    {
                        if (!string.IsNullOrWhiteSpace(values[dynamicField.Key]) && !choices.Any(choice => choice.Value == values[dynamicField.Key]))
                            choiceErrors[dynamicField.Key] = "当前值未列在候选项中";
                    }
                    RefreshVisibility();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { choiceErrors[dynamicField.Key] = exception.Message; }
            }
            var fields = new List<RemoteAutomationField>();
            foreach (var field in definitions)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var choices = choicesByKey[field.Key];
                if (visible[field.Key] && field.Kind == DisplayFieldKind.MultiChoice && !choiceErrors.ContainsKey(field.Key))
                {
                    try
                    {
                        var selected = JsonSerializer.Deserialize<string[]>(values[field.Key]) ?? throw new JsonException();
                        if (selected.Any(value => value == null || !choices.Any(choice => choice.Value == value)))
                            choiceErrors[field.Key] = "保存的选项已不可用，请重新勾选或清空。";
                    }
                    catch (JsonException) { choiceErrors[field.Key] = "参数格式已更新，请重新勾选或清空。"; }
                }
                if (field.IsReadOnly) parameters.Remove(field.Key);
                else parameters[field.Key] = values[field.Key];
                fields.Add(new RemoteAutomationField
                {
                    Key = field.Key, Label = field.Label, Kind = field.Kind switch
                    {
                        DisplayFieldKind.Toggle => "toggle", DisplayFieldKind.Folder => "folder",
                        DisplayFieldKind.Choice => "choice", DisplayFieldKind.Multiline => "multiline",
                        DisplayFieldKind.MultiChoice => "multi_choice", _ => "text"
                    },
                    DefaultValue = field.DefaultValue, Hint = field.Hint,
                    Choices = choices.Select(choice => new RemoteAutomationFieldChoice(choice.Value, choice.Label, choice.ToolTip)).ToArray(),
                    IsReadOnly = field.IsReadOnly, Group = field.Group, Visible = visible[field.Key],
                    EmptyValue = field.EmptyValue, Value = values[field.Key], ChoiceDependencies = field.ChoiceDependencies,
                    ChoicesError = choiceErrors.GetValueOrDefault(field.Key)
                });
            }
            problems.AddRange(visibilityErrors.Values);
            var draft = BuildDraft();
            timeout.Token.ThrowIfCancellationRequested();
            try { if (AutomationParameters.Validate(template, draft) is { Length: > 0 } validation) problems.Add(validation); }
            catch (Exception exception) { problems.Add(exception.Message); }
            IReadOnlyList<AutomationStep> steps = [];
            try { steps = template.PlanSteps(draft); }
            catch (Exception exception) { problems.Add("流程预览失败：" + exception.Message); }
            timeout.Token.ThrowIfCancellationRequested();
            var executionPath = automation.Scripts.SavedScriptPath(task.TemplateId);
            result = new { fields, plan_steps = steps, validation_error = string.Join("；", problems),
                parameters = draft.Parameters, machine_name = Environment.MachineName,
                execution_version = executionPath == null ? null : AutomationLocalSource.Describe(executionPath),
                schedule_owner = settings.LocalScheduleTaskIds.Contains(task.Id, StringComparer.Ordinal) ? "agent" : "none" };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            error = preparingOfficial ? "流程仍在准备，请稍后重新读取参数与流程图。" : "本机任务参数或流程预览计算超时。";
        }
        catch (Exception exception) when (!token.IsCancellationRequested) { error = exception.Message; }
        try
        {
            await PostAsync<JsonElement>($"inspections/{request.Id}", new
            {
                machine_name = Environment.MachineName, result, error
            }, token, server);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Serilog.Log.ForContext<RemoteAutomationAgent>().Warning("Remote automation inspection reply: {Message}", exception.Message);
        }
    }

    private void Start(RemoteAutomationRequest request, Delivery delivery)
    {
        var directory = RequestDirectory(request.Id);
        Directory.CreateDirectory(directory);
        var parameters = Path.Combine(directory, "parameters.json");
        File.WriteAllText(parameters, JsonSerializer.Serialize(request.Parameters, Json));
        var instance = environment.ClientInstance();
        var start = new ProcessStartInfo(settings.CliExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory
        };
        if (request.DeploymentCatalogPath is { Length: > 0 } catalog)
            start.Environment[AutomationRemoteSource.DeploymentCatalogVariable] = catalog;
        if (request.ResourceCoordinationEnabled && request.TenantId is { Length: > 0 } tenant)
            start.Environment[RemotePregelResourceCoordinator.TenantVariable] = tenant;
        foreach (var argument in settings.CliArguments) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "graph", "run", "--task", request.TaskId,
                     "--remote-request", request.Id, "--remote-parameters-file", parameters,
                     "--remote-output-file", Path.Combine(directory, "launch.log"), "--yes", "--client-instance", instance })
            start.ArgumentList.Add(argument);
        if (request.Subgraph is { } subgraph)
        {
            var path = Path.Combine(directory, "subgraph.json");
            File.WriteAllText(path, subgraph.GetRawText());
            start.ArgumentList.Add("--remote-subgraph-file");
            start.ArgumentList.Add(path);
        }
        // The host starts and owns the detached CLI; it applies product-specific process settings.
        using var process = environment.StartProcess(start, _server,
            RemotePregelResourceCoordinator.IsEnabled(_server));
        delivery.ProcessId = process.Id;
        delivery.StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        Save();
    }

    private static AutomationRunRecord ApplyCancellationFailure(AutomationRunRecord record, RemoteAutomationFailure? failure)
    {
        if (failure == null || record.Status is not (AutomationRunStatus.Cancelled or AutomationRunStatus.Failed)) return record;
        return record with
        {
            Status = AutomationRunStatus.Failed, Summary = failure.Message,
            SubgraphResult = record.SubgraphResult == null ? null : record.SubgraphResult with
                { Status = PregelRunStatus.Failed, Error = new(failure.Code, failure.Message) }
        };
    }

    private async Task ReportAsync(string requestId, Delivery delivery, AutomationRunRecord record, CancellationToken token, bool observed = false)
    {
        // Send status before draining a backlog, so a long reconnect cannot hide task completion.
        if (!observed || delivery.LastReportedRevision != record.Revision)
        {
            await PostAsync<JsonElement>($"runs/{requestId}/report", new
            {
                machine_name = Environment.MachineName, local_run_id = record.RunId,
                snapshot = record, output = record.EncodedOutput, logs_complete = false
            }, token);
            delivery.LastReportedRevision = record.Revision;
            PersistDelivery(observed);
        }
        var drained = true;
        // Observed desktop runs share the long-lived host process. Completion is the local
        // run's final snapshot, followed by an unchanged, fully drained tail on the next poll.
        // A child may finish its review/Finally in this long-lived worker rather than its original CLI.
        var persistentOwner = observed || record.Subgraph != null;
        var ended = record.IsFinished && (persistentOwner || !record.OwnerIsAlive && !Alive(delivery));
        var incompleteTail = false;
        var lengths = new Dictionary<string, long>();
        foreach (var directory in record.EvidenceDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(directory, "events.jsonl");
            if (!File.Exists(path)) continue;
            var stream = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))).ToLowerInvariant()[..24];
            var offset = delivery.Offsets.GetValueOrDefault(stream);
            var batch = Utf8EventLog.Read(path, offset);
            lengths[stream] = batch.Length;
            if (batch.Lines.Length > 0)
            {
                var ack = await PostAsync<RemoteAutomationLogReceipt>($"runs/{requestId}/logs", new
                {
                    machine_name = Environment.MachineName, stream, offset, lines = batch.Lines
                }, token);
                delivery.Offsets[stream] = ack.NextOffset;
                PersistDelivery(observed);
                drained &= ack.NextOffset == batch.Length;
            }
            else if (ended && offset < batch.Length)
                incompleteTail = true; // The exited writer cannot finish this event; report the gap explicitly.
            else drained &= offset == batch.Length;
        }
        var stableTail = delivery.TerminalRevision == record.Revision
            && lengths.Count == delivery.TerminalLengths.Count
            && lengths.All(pair => delivery.TerminalLengths.TryGetValue(pair.Key, out var length) && length == pair.Value);
        if (persistentOwner && ended && !stableTail)
        {
            delivery.TerminalRevision = record.Revision;
            delivery.TerminalLengths = lengths;
            PersistDelivery(observed);
        }
        if (ended && drained && (!persistentOwner || stableTail))
        {
            await PostAsync<JsonElement>($"runs/{requestId}/report", new
            {
                machine_name = Environment.MachineName, local_run_id = record.RunId,
                snapshot = record, output = record.EncodedOutput, logs_complete = true,
                detail = incompleteTail ? record.Summary + "（日志末尾有未写完的事件，完整日志已同步）" : ""
            }, token);
            delivery.LogsComplete = true;
            PersistDelivery(observed);
            if (!observed) automation.MarkRemoteReported(record.TaskId, record.RunId);
        }
    }

    private Task<JsonElement> ReportStateAsync(string id, string state, string detail, bool complete, CancellationToken token) =>
        PostAsync<JsonElement>($"runs/{id}/report", new
        {
            machine_name = Environment.MachineName, state, detail, logs_complete = complete
        }, token);

    private async Task<T> PostAsync<T>(string path, object payload, CancellationToken token, string? server = null)
    {
        using var response = await _http.PostAsJsonAsync((server ?? _server) + "/api/agent/automation/" + path, payload, Json, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, token)
            ?? throw new InvalidDataException("远程自动化响应为空");
    }


    private string ReadLaunchError(string id)
    {
        var path = Path.Combine(RequestDirectory(id), "launch.log");
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = Math.Max(0, stream.Length - 3000);
        using var reader = new StreamReader(stream);
        return "\n" + reader.ReadToEnd();
    }

    private string RequestDirectory(string id) => Path.Combine(_root, "requests", id);
    private void ConnectionNotice(string message)
    {
        if (_connectionNotice == message) return;
        _connectionNotice = message;
        settings.StatusChanged?.Invoke(message);
    }
    private string GraphFingerprint(AutomationTaskView view)
    {
        var taskId = view.Task.Id;
        var revision = automation.Scripts.SavedScriptRevision(view.Task.TemplateId);
        if (_graphFingerprints.TryGetValue(taskId, out var cached)
            && ReferenceEquals(cached.Template, view.Template) && cached.Revision == revision) return cached.Fingerprint;
        try
        {
            var definition = automation.DescribeTaskGraph(taskId);
            var fingerprint = definition?.Fingerprint ?? "";
            _graphFingerprints[taskId] = (view.Template, revision, fingerprint, definition?.GraphId ?? automation.TaskDefinitionId(view.Task));
            return fingerprint;
        }
        catch (Exception error)
        {
            Serilog.Log.ForContext<RemoteAutomationAgent>().Debug("Graph catalog unavailable for {TaskId}: {ErrorType}",
                taskId, error.GetType().Name);
            return "";
        }
    }
    private bool NeedsOfficialPreparation(string taskId, AutomationTaskView? view) =>
        (_officialScripts.IsPublished(taskId) || view != null && automation.Scripts.SavedScriptPath(view.Task.TemplateId) != null)
            && (view == null || view.Template == null
            || view.Availability is AutomationTaskAvailability.TemplateNeedsReload or AutomationTaskAvailability.InvalidTask);

    private string OfficialInputPath(string taskId)
    {
        var directory = Path.Combine(_root, "official", taskId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Input.json");
        if (!File.Exists(path))
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, "{\"triggers\":{\"manual\":true,\"schedule\":{\"enabled\":false}}}");
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return path;
    }
    private void Save()
    {
        var path = Path.Combine(_root, "deliveries.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_deliveries, Json));
        File.Move(path + ".tmp", path, true);
    }
    private ObservationJournal LoadObservations()
    {
        var path = Path.Combine(_root, "observations.json");
        if (!File.Exists(path)) return new();

        try
        {
            return JsonSerializer.Deserialize<ObservationJournal>(File.ReadAllText(path), Json) ?? new();
        }
        catch (JsonException exception)
        {
            var archive = $"{path}.corrupt-{Guid.NewGuid():N}";
            File.Move(path, archive);
            Serilog.Log.ForContext<RemoteAutomationAgent>().Warning(exception,
                "Archived corrupt remote automation observation journal to {ArchivePath}", archive);
            return new();
        }
    }

    private void SaveObservations()
    {
        var path = Path.Combine(_root, "observations.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_observations, Json));
        File.Move(path + ".tmp", path, true);
    }
    private void PersistDelivery(bool observed)
    {
        if (observed) SaveObservations();
        else Save();
    }
    private static bool Alive(Delivery delivery)
    {
        if (delivery.ProcessId == 0) return false;
        try
        {
            using var process = Process.GetProcessById(delivery.ProcessId);
            return !process.HasExited && (delivery.StartedUtcTicks == 0
                || process.StartTime.ToUniversalTime().Ticks == delivery.StartedUtcTicks);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private class Delivery
    {
        public string TaskId { get; set; } = "";
        public int ProcessId { get; set; }
        public long StartedUtcTicks { get; set; }
        public bool StopSent { get; set; }
        public string Failure { get; set; } = "";
        public Dictionary<string, long> Offsets { get; set; } = [];
        public long LastReportedRevision { get; set; } = -1;
        public bool LogsComplete { get; set; }
        public long TerminalRevision { get; set; } = -1;
        public Dictionary<string, long> TerminalLengths { get; set; } = [];
    }

    private sealed class Observation : Delivery
    {
        public string CloudTaskId { get; set; } = "";
        public string RunId { get; set; } = "";
        public string TaskName { get; set; } = "";
        public string ScriptRevision { get; set; } = "";
        public string RemoteId { get; set; } = "";
    }

    private sealed class ObservationJournal
    {
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, Observation> Runs { get; set; } = [];
    }
}
