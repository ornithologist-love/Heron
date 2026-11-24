using Heron.Pregel.Automation;
using System.Text.Json;

namespace Heron.AutomationTenants;

/// <summary>Go dispatch transport; execution snapshots remain the local Pregel runtime's records.</summary>
public static class RemoteAutomationProtocol
{
    public const string Capability = "pregel.remote.v1";
    public const string ObserveCapability = "pregel.observe.v1";
    public const string InspectCapability = "pregel.inspect.v1";
    public const string SubgraphCapability = "pregel.subgraph.v1";
    public const string ResourceCapability = "pregel.mutex.v1";
    public const string ServerCapability = "pregel.server.v1";
    public const string ControlCapability = "pregel.control.v1";
    public const string TriggerCapability = "pregel.triggers.v1";
    public const string DiscardCapability = "pregel.discard.v1";
    public const string DeploymentCapability = "pregel.deploy.v1";
    public const string TenantSourceCapability = "pregel.tenant-source.v1";
}

public sealed record RemoteAutomationTask
{
    public string Id { get; init; } = "";
    public string ConfigurationId { get; init; } = "";
    public string Name { get; init; } = "";
    public string LocalNickname { get; init; } = "";
    public string DefinitionId { get; init; } = "";
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public string ScriptRevision { get; init; } = "";
    public string GraphFingerprint { get; init; } = "";
    public bool CanRun { get; init; }
    public string UnavailableReason { get; init; } = "";
    /// <summary>Saved task triggers. Null for tasks that only exist as official catalog entries.</summary>
    public RemoteAutomationTriggers? Triggers { get; init; }
}

/// <summary>The trigger section of a task's saved configuration, as the Client editor shows it.</summary>
public sealed record RemoteAutomationTriggers
{
    public bool Manual { get; init; } = true;
    public bool ScheduleEnabled { get; init; }
    public RemoteAutomationSchedule[] Schedules { get; init; } = [];
    /// <summary>Why the triggers cannot be edited remotely (for example an invalid Input.json); empty when editable.</summary>
    public string ReadOnlyReason { get; init; } = "";
}

/// <summary>One calendar trigger. <see cref="WeekDays"/> is a bit mask with Sunday as bit 0; an empty zone is the machine's.</summary>
public sealed record RemoteAutomationSchedule
{
    public string Time { get; init; } = "10:00";
    public string Frequency { get; init; } = "daily";
    public int WeekDays { get; init; }
    public int MonthDay { get; init; } = 1;
    public bool LastDayOfMonth { get; init; }
    public string TimeZone { get; init; } = "";
}

/// <summary>
/// A team schedule the server dispatches to this machine, always in Beijing time. The local scheduler leaves
/// these occurrences of the task to the server.
/// </summary>
public sealed record RemoteAutomationServerSchedule
{
    public string TaskId { get; init; } = "";
    public RemoteAutomationSchedule[] Entries { get; init; } = [];
}

public sealed record RemoteAutomationPoll
{
    public string TenantId { get; init; } = "";
    public string PoolId { get; init; } = "";
    public bool ResourceCoordinationEnabled { get; init; }
    public RemoteAutomationTaskDefinition[]? TaskCatalog { get; init; }
    public RemoteAutomationRequest[] Requests { get; init; } = [];
    public RemoteAutomationInspection[] Inspections { get; init; } = [];
    public RemoteAutomationControl[] Controls { get; init; } = [];
    public RemoteAutomationServerSchedule[] ServerSchedules { get; init; } = [];
    public RemoteAutomationDeployment[] Deployments { get; init; } = [];
}

public sealed record RemoteAutomationDeploymentSource
{
    public string SourceId { get; init; } = "";
    public string Revision { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string SharedRevision { get; init; } = "";
    public string SharedSha256 { get; init; } = "";
}

public sealed record RemoteAutomationDeployment
{
    public string Id { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string? CatalogPath { get; init; }
    public bool RegisterGraph { get; init; }
    public RemoteAutomationDeploymentSource Source { get; init; } = new();
}

/// <summary>Server-owned workflow registration. Local names and executions never create these entries.</summary>
public sealed record RemoteAutomationTaskDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string[] Aliases { get; init; } = [];
    public string[] DefinitionIds { get; init; } = [];
    public string[] ExcludedConfigurations { get; init; } = [];
}

public sealed record RemoteAutomationControl
{
    public string Id { get; init; } = "";
    public string RunId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string LocalRunId { get; init; } = "";
    public string Action { get; init; } = "";
    public string ReviewId { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string Feedback { get; init; } = "";
}

public sealed record RemoteAutomationInspection
{
    public string Id { get; init; } = "";
    public string TaskId { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = [];
}

public sealed record RemoteAutomationFieldChoice(string Value, string Label, string Tooltip);

/// <summary>Machine-evaluated presentation metadata; no delegates or machine global values leave the host.</summary>
public sealed record RemoteAutomationField
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "text";
    public string DefaultValue { get; init; } = "";
    public string? Hint { get; init; }
    public IReadOnlyList<RemoteAutomationFieldChoice> Choices { get; init; } = [];
    public bool IsReadOnly { get; init; }
    public string? Group { get; init; }
    public bool Visible { get; init; } = true;
    public string? EmptyValue { get; init; }
    public string Value { get; init; } = "";
    public IReadOnlyList<string>? ChoiceDependencies { get; init; }
    public string? ChoicesError { get; init; }
}

public sealed record RemoteAutomationObservationReceipt(string Id);

public sealed record RemoteAutomationRequest
{
    public bool PullFromPool { get; init; }
    public string? TenantId { get; init; }
    public bool ResourceCoordinationEnabled { get; init; }
    public string? DeploymentCatalogPath { get; init; }
    public RemoteAutomationDeploymentSource? DeploymentSource { get; init; }
    public string Id { get; init; } = "";
    public string TaskId { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = [];
    public string State { get; init; } = "";
    public bool CancelRequested { get; init; }
    public RemoteAutomationFailure? CancellationFailure { get; init; }
    public JsonElement? Subgraph { get; init; }
    /// <summary>The team schedule occurrence this run was dispatched for.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }
}

public sealed record RemoteAutomationFailure(string Code, string Message);

public sealed record RemoteAutomationLogReceipt(long NextOffset);
