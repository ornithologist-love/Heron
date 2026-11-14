using Heron.Pregel.Automation;
using System.Diagnostics;

namespace Heron.AutomationTenants;

/// <summary>Agent switch and server taken from the host's client configuration on every connection cycle.</summary>
public sealed record RemoteAutomationAgentConfig(bool Enabled, string? ServerUrl);

/// <summary>Published identity of a registered workflow, as reported by the host's workflow source service.</summary>
public sealed record RemoteAutomationPublishedSource(string SourceId, string? Revision);

/// <summary>
///     Everything the remote automation worker needs from its host: local state and script roots, the client
///     instance passed to spawned CLI runs, the agent configuration, the official script source, process ownership
///     and published-source lookup. The worker keeps no product paths, addresses or credentials of its own.
/// </summary>
public sealed class RemoteAutomationEnvironment
{
    private static RemoteAutomationEnvironment? _current;

    /// <summary>Per-instance state root; worker state lives under its <c>remote-automation</c> directory.</summary>
    public required Func<string> LocalRoot { get; init; }

    /// <summary>Root of installed Pregel scripts, including official sources and <c>Shared</c>.</summary>
    public required Func<string> ScriptsRoot { get; init; }

    public required Func<string> SharedRoot { get; init; }

    public required Func<string?> DefaultServerUrl { get; init; }

    public required Func<string?> ResourceServerUrl { get; init; }

    public required Func<string> ResourceDomainDirectory { get; init; }

    public required Func<TimeSpan?> ResourceQueueTimeout { get; init; }

    /// <summary>Value of <c>--client-instance</c> for CLI processes the worker starts.</summary>
    public required Func<string> ClientInstance { get; init; }

    public required Func<RemoteAutomationAgentConfig> AgentConfig { get; init; }

    /// <summary>Official script catalog base address, ending with <c>/api/automation-scripts/</c>.</summary>
    public required string OfficialSourceUrl { get; init; }

    /// <summary>Starts and adopts a CLI process, applying the host's execution environment. The process outlives the host.</summary>
    public required Func<ProcessStartInfo, string, bool, Process> StartProcess { get; init; }

    /// <summary>Looks up the published source of a task on the given server (authority only).</summary>
    public required Func<string, string, CancellationToken, Task<RemoteAutomationPublishedSource>> QueryPublishedSource { get; init; }

    public static RemoteAutomationEnvironment Current =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException("Remote automation environment is not configured.");

    internal static RemoteAutomationEnvironment? CurrentOrNull => Volatile.Read(ref _current);
    internal static void Restore(RemoteAutomationEnvironment? environment) => Volatile.Write(ref _current, environment);

    public static void Configure(RemoteAutomationEnvironment environment) =>
        Volatile.Write(ref _current, environment ?? throw new ArgumentNullException(nameof(environment)));
}
