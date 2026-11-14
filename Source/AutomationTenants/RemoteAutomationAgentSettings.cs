using Heron.Pregel.Automation;
namespace Heron.AutomationTenants;

/// <summary>Host-specific process and connection settings; execution remains in AutomationHost.</summary>
public sealed record RemoteAutomationAgentSettings(string ServerUrl, string HostVersion, string CliExecutablePath)
{
    public bool FollowClientConfiguration { get; init; } = true;
    public bool IsServerHost { get; init; }
    public string ObserveOnlyTaskId { get; init; } = "";
    public IReadOnlyList<string> ObserveOnlyTaskIds { get; init; } = [];
    public IReadOnlyList<string> LocalScheduleTaskIds { get; init; } = [];
    internal bool IsObserveOnly(string taskId) => taskId == ObserveOnlyTaskId
        || ObserveOnlyTaskIds.Contains(taskId, StringComparer.Ordinal);
    public IReadOnlyList<string> CliArguments { get; init; } = [];
    public Action<string>? StatusChanged { get; init; }
}
