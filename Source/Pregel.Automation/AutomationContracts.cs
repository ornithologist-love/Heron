using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>
/// Host service handed to every plugin. A plugin registers the kinds of work it can automate; the host owns
/// scheduling, locking, run history, notifications and the 「自动化」 page.
/// </summary>
public interface IAutomationRegistry
{
    /// <summary>
    /// True when the host runs the unified automation scheduler. Plugins keep their legacy
    /// timers and settings UI only when this is false.
    /// </summary>
    bool IsActive { get; }

    /// <summary>Registers the AI plugin's session loader. Dispose before stopping the plugin.</summary>
    IDisposable RegisterChat(Func<AutomationChatSession, string, CancellationToken, Task> open) =>
        throw new NotSupportedException("此宿主不支持转到 AI Chat");

    IDisposable RegisterTemplate(IAutomationTemplate template);

    /// <summary>Provides already loaded business assemblies to CSX compilation. Existing scripts own their instances until unloaded.</summary>
    IDisposable RegisterScriptReferences(params System.Reflection.Assembly[] references) =>
        throw new NotSupportedException("此宿主不支持脚本程序集注册");

    /// <summary>Saved definitions for one template, including paused tasks.</summary>
    IReadOnlyList<AutomationTask> GetTasks(string templateId);

    /// <summary>Reassigns saved workspace tasks by their workspace parameter while preserving task ids, settings and history.</summary>
    int MigrateTaskTemplates(string sourceTemplateId, IReadOnlyDictionary<string, string> workspaceTemplateIds) => 0;

    /// <summary>
    /// Opens the automation page. <paramref name="taskId"/> selects one task. Otherwise, with a template (and
    /// optionally parameters), selects the first task with that template and matching parameters, or opens a new draft for it when none
    /// exists. A template can have many tasks (profiles), e.g. 「Test 包」「Dev 包」 or several per workspace.
    /// </summary>
    void Open(string? taskId = null, string? templateId = null, IReadOnlyDictionary<string, string>? parameters = null);
}

/// <summary>One kind of automatable work (a nightly build, a daily report…). Implemented by a plugin, called by the host.</summary>
public interface IAutomationTemplate
{
    /// <summary>Stable template id from class TemplateId metadata; explicit properties remain supported.</summary>
    string Id => AutomationTemplateIdentity.Read(this.GetType());

    /// <summary>UI display name from class DisplayName metadata; explicit properties remain supported.</summary>
    string Title => AutomationTemplateMetadata.Title(this.GetType());

    /// <summary>Compatibility templates remain executable but are hidden from new-task choices.</summary>
    bool ShowInCatalog => true;

    /// <summary>UI-only description from class DisplayDescription metadata; explicit properties remain supported.</summary>
    string Description => AutomationTemplateMetadata.Description(this.GetType());

    /// <summary>UI-only icon from class DisplayIcon metadata; explicit properties remain supported.</summary>
    string Icon => AutomationTemplateMetadata.Icon(this.GetType());

    /// <summary>Template parameters. The host renders a uniform form; values are stored as strings.</summary>
    IReadOnlyList<AutomationField> Fields { get; }

    /// <summary>Optional read-only rendering of execution parameters for the current draft. Must not perform IO.</summary>
    string? RenderParameters(AutomationTask task) => null;

    /// <summary>Runtime choices for a parameter, using the current task's parameter values.</summary>
    Task<IReadOnlyList<AutomationChoice>?> ListFieldChoicesAsync(AutomationTask task, string fieldKey, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<AutomationChoice>?>(null);

    /// <summary>Returns a user-facing problem, or null when the task can run.</summary>
    string? Validate(AutomationTask task) => null;

    /// <summary>
    /// Business rules checked when a task is saved: does this task make sense next to the other tasks of the same
    /// template (e.g. "a nightly report for this workspace already exists") and are its options coherent.
    /// <see cref="AutomationRuleSeverity.Error"/> blocks saving; warnings are shown and saving continues.
    /// Host rules (name, time, parameter validation) are checked by the host and need not be repeated.
    /// </summary>
    /// <param name="task">The task as it would be saved.</param>
    /// <param name="siblings">Other saved tasks of this template (never includes <paramref name="task"/>).</param>
    IEnumerable<AutomationRuleViolation> CheckRules(AutomationTask task, IReadOnlyList<AutomationTask> siblings) => [];

    /// <summary>Legacy host queue hints. Graph-instance mutex rules are enforced by the Pregel kernel.</summary>
    IReadOnlyList<string> ResourceKeys(AutomationTask task) => [];

    /// <summary>User-level steps in execution order; shown as 「未执行」 before and during a run.</summary>
    IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task);

    /// <summary>Runs once. The template must attach <see cref="AutomationRunContext.Observer"/> to every graph run.</summary>
    Task<AutomationRunResult> RunAsync(AutomationRunContext context, CancellationToken token);

    /// <summary>Tasks created by the plugin's previous, per-page scheduler. Called once per template.</summary>
    IReadOnlyList<AutomationTask> ImportLegacyTasks() => [];

    /// <summary>Releases plugin-side state kept for a deleted task.</summary>
    void OnTaskDeleted(AutomationTask task) { }
}

public enum AutomationRuleSeverity { Warning, Error }

/// <summary>
/// One failed business rule. <paramref name="RuleId"/> is stable (tests, logs); <paramref name="Message"/> is what the
/// user reads; <paramref name="ConflictTaskId"/> points at the clashing task, when there is one.
/// </summary>
public sealed record AutomationRuleViolation(
    string RuleId,
    AutomationRuleSeverity Severity,
    string Message,
    string? ConflictTaskId = null)
{
    public static AutomationRuleViolation Error(string ruleId, string message, string? conflictTaskId = null) =>
        new(ruleId, AutomationRuleSeverity.Error, message, conflictTaskId);

    public static AutomationRuleViolation Warning(string ruleId, string message, string? conflictTaskId = null) =>
        new(ruleId, AutomationRuleSeverity.Warning, message, conflictTaskId);
}

/// <summary>Thrown by <c>AutomationHost.Save</c> when a task breaks an error-level rule.</summary>
public sealed class AutomationValidationException(IReadOnlyList<AutomationRuleViolation> violations)
    : InvalidOperationException(string.Join("；", violations.Select(v => v.Message)))
{
    public IReadOnlyList<AutomationRuleViolation> Violations { get; } = violations;
}

/// <summary>Stable ids of host-level rules. Template rules use their own ids, prefixed with the template id.</summary>
public static class AutomationRules
{
    public const string TemplateMissing = "host.template-missing";
    public const string NameRequired = "host.name-required";
    public const string NameDuplicate = "host.name-duplicate";
    public const string TimeInvalid = "host.time-invalid";
    public const string TriggersInvalid = "host.triggers-invalid";
    public const string TriggerDuplicate = "host.trigger-duplicate";
    public const string ParametersInvalid = "host.parameters-invalid";
    public const string RuleFailed = "host.rule-failed";
    public const string SameTimeSharedResource = "host.same-time-shared-resource";
}

public sealed record AutomationField(
    string Key,
    string Label,
    DisplayFieldKind Kind,
    string DefaultValue = "",
    string? Hint = null,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>Labeled choices supplied by the input schema. Null means no schema-owned list.</summary>
    public IReadOnlyList<AutomationChoice>? With { get; init; }

    /// <summary>Display the supplied value without allowing task parameter overrides.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>Presentation section containing this parameter. Empty keeps the ungrouped layout.</summary>
    public string? Group { get; init; }

    /// <summary>Pure predicate over the current draft. Omitted means visible; hiding retains the parameter value.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<AutomationTask, bool>? If { get; init; }

    /// <summary>Reload dynamic choices when these parameters change.</summary>
    public IReadOnlyList<string>? ChoiceDependencies { get; init; }

    /// <summary>Shown as a placeholder while empty and materialized when the editor saves.</summary>
    public string? EmptyValue { get; init; }

    /// <summary>
    ///     Machine-local value from 「全局设置」used when the task parameter is empty. Displayed as the placeholder and
    ///     bound when saving leaves the field empty; a task value always wins.
    /// </summary>
    public string? GlobalValue { get; init; }
}

public sealed record AutomationChoice(string Value, string Label)
{
    public string ToolTip { get; init; } = Label;
}

/// <summary>
/// One row of the step timeline. Several internal graph nodes may form one user-level step (e.g. the four
/// engine setup nodes are 「准备编译环境」).
/// </summary>
public sealed record AutomationStep(string Key, string Title, IReadOnlyList<string> NodeIds, bool HasProgress = false)
{
    public IReadOnlyList<string>? DependsOn { get; init; }
    public IReadOnlyList<AutomationStep> Children { get; init; } = [];
    public bool IsStage { get; init; }
    /// <summary>Failure/cancellation cleanup, independent of normal scheduling dependencies.</summary>
    public bool IsRecovery { get; init; }
    public bool IsFinally { get; init; }
    public IReadOnlyList<string> RecoveryFrom { get; init; } = [];
    public static AutomationStep For(string title, params Delegate[] nodes) =>
        new(global::Heron.Pregel.Pregel.NodeId(nodes[0]).Value, title, nodes.Select(n => global::Heron.Pregel.Pregel.NodeId(n).Value).ToArray());

    public static AutomationStep WithProgress(string title, params Delegate[] nodes) => For(title, nodes) with { HasProgress = true };
}

/// <summary>A saved automation task: template + target + parameters + trigger + policy.</summary>
public sealed record AutomationTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string TemplateId { get; init; } = "";

    /// <summary>
    /// Task name, unique per template. Defaults to the template title with a counter
    /// (「每日构建检查」「每日构建检查-2」…); users may rename. Unnamed legacy tasks are named when their template registers.
    /// </summary>
    public string Name { get; init; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TemplateId : Name.Trim();
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    /// <summary>Run snapshots have resolved defaults; even an empty value must not re-read later global settings.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ParametersResolved { get; init; }
    /// <summary>Optional per-task CSX input file. Legacy scripts use the adjacent Input.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ScriptInputPath { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TriggerSourcePath { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TriggerSourceVersion { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TriggerConfigurationError { get; init; }
    public AutomationTrigger Trigger { get; init; } = new();
    /// <summary>
    /// Calendar triggers after <see cref="Trigger"/>, sharing <see cref="ScheduleEnabled"/>. Null when there is only one,
    /// so single-trigger records keep their legacy shape.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AutomationTrigger>? AdditionalTriggers { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<AutomationTrigger> Schedules => [Trigger, .. AdditionalTriggers ?? []];
    public AutomationTask WithSchedules(IReadOnlyList<AutomationTrigger> schedules) => this with
    {
        Trigger = schedules.Count > 0 ? schedules[0] : new(),
        AdditionalTriggers = schedules.Count > 1 ? schedules.Skip(1).ToArray() : null
    };
    [System.Text.Json.Serialization.JsonIgnore]
    public string ScheduleSummary => AutomationSchedules.Summary(Schedules);
    /// <summary>Legacy persisted name for the calendar trigger only; never a task-wide enable flag.</summary>
    public bool Enabled { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ScheduleEnabled { get => Enabled; init => Enabled = value; }
    /// <summary>Client, CLI and tray are entry points of the same manual trigger. Legacy tasks keep it enabled.</summary>
    public bool ManualEnabled { get; init; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public string TriggerSummary => (ManualEnabled, ScheduleEnabled) switch
    {
        (true, true) => $"手动 · {ScheduleSummary}",
        (true, false) => "手动",
        (false, true) => ScheduleSummary,
        _ => "未启用触发器"
    };
    public bool AllowsTrigger(string source) => source == "schedule" ? ScheduleEnabled : ManualEnabled;
    public bool NotifyOnSuccess { get; init; } = true;
    /// <summary>Allow multiple executions of this task; resource rules still decide admission.</summary>
    public bool AllowParallel { get; init; }

    public string Parameter(string key, string fallback = "") =>
        Parameters.TryGetValue(key, out var value) ? value : fallback;

    public bool Flag(string key, bool fallback = false) =>
        Parameters.TryGetValue(key, out var value) ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) : fallback;
}

/// <summary>Calendar trigger; legacy DailyTime-only records use daily local time. Missed times are not replayed.</summary>
public sealed partial record AutomationTrigger(string DailyTime = "10:00")
{
    public bool TryGetTime(out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = (DailyTime ?? "").Split(':');
        return parts is [{ Length: 2 } h, { Length: 2 } m]
               && int.TryParse(h, out hour) && int.TryParse(m, out minute)
               && hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }
}

public sealed class AutomationRunContext(AutomationTask task, string runId, string trigger, IPregelRunObserver observer,
    Action<string> log)
{
    public AutomationTask Task { get; } = task;
    public string RunId { get; } = runId;
    public string? RemoteRequestId { get; init; }
    public PregelSubgraphExecutionRequest? Subgraph { get; init; }
    public Func<AutomationRunRecord?>? ReadSnapshot { get; init; }
    public IPregelSubgraphExecutor? SubgraphExecutor { get; internal set; }
    public IPregelResourceCoordinator? ResourceCoordinator { get; internal set; }
    public TimeSpan? ResourceQueueTimeout { get; internal set; }

    /// <summary><c>schedule</c>, <c>manual</c> or <c>tray</c>.</summary>
    public string Trigger { get; } = trigger;

    /// <summary>Host observer: step timeline, history and evidence. Attach to every graph run of this execution.</summary>
    public IPregelRunObserver Observer { get; } = observer;

    public Action<string> Log { get; } = log;
    public CancellationToken FinalizationCancellationToken { get; init; }
    public string StateRoot { get; init; } = "";
    internal string? ReexecutionSourceRunId { get; init; }
    public PregelReexecutionRequest? Reexecution { get; init; }
    /// <summary>Steps the person chose not to run this time; graph templates save it with their run state.</summary>
    public PregelManualSkip? ManualSkip { get; init; }
    internal Func<AutomationPermission, CancellationToken, Task<bool>>? PermissionRequest { get; init; }
    public Task<bool> RequestPermissionAsync(AutomationPermission permission, CancellationToken token) =>
        PermissionRequest?.Invoke(permission, token) ?? System.Threading.Tasks.Task.FromResult(false);
}

public enum AutomationRunStatus { Queued, Running, Succeeded, Skipped, Failed, Cancelled, AwaitingReview }

public sealed record AutomationRunResult(AutomationRunStatus Status, string Summary)
{
    public AutomationReview? Review { get; init; }
    public string? ArtifactPath { get; init; }
    public PregelEncodedValue? EncodedOutput { get; init; }
    public PregelSubgraphExecutionResult? SubgraphResult { get; init; }
    public static AutomationRunResult AwaitingReview(AutomationReview review, string summary = "草稿已生成，等待人工审核") =>
        new(AutomationRunStatus.AwaitingReview, summary) { Review = review };
    public static AutomationRunResult Succeeded(string summary) => new(AutomationRunStatus.Succeeded, summary);
    public static AutomationRunResult Skipped(string summary) => new(AutomationRunStatus.Skipped, summary);
    public static AutomationRunResult Failed(string summary) => new(AutomationRunStatus.Failed, summary);
    public static AutomationRunResult Cancelled(string summary = "已取消") => new(AutomationRunStatus.Cancelled, summary);
}
