using System.Reflection;
using System.Text.Json.Nodes;
using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>
/// What an embedding product adds to automation. Set once at process start, before any store or host
/// is created. Left alone, automation runs local scripts from a private data directory and nothing else.
/// </summary>
public static class AutomationRuntime
{
    /// <summary>Resolved on every use, because a product may choose its data root after registering this.</summary>
    public static Func<string>? DataRootProvider { get; set; }

    /// <summary>Resolved on every use, for the same reason as <see cref="DataRootProvider" />.</summary>
    public static Func<string>? ScriptsDirectoryProvider { get; set; }

    /// <summary>Shared store-lock identity selected by the embedding host, including during a rolling runtime replacement.</summary>
    public static string StoreMutexPrefix { get; set; } = "Heron.Pregel.Automation.Store.";
    public static string InputMutexPrefix { get; set; } = "Heron.Pregel.Automation.Input.";

    public static string DataRoot => DataRootProvider?.Invoke()
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".heron-pregel");

    /// <summary>The script directory every catalog lists even when nothing else is registered.</summary>
    public static string ScriptsDirectory => ScriptsDirectoryProvider?.Invoke() ?? Path.Combine(DataRoot, "scripts");

    /// <summary>Publisher-owned scripts listed beside local directories; null means local scripts only.</summary>
    public static IAutomationScriptSource? ScriptSource { get; set; }

    /// <summary>Ambient state for one script execution, given the task ID and script directory.</summary>
    public static Func<string, string, IDisposable?>? ScriptScope { get; set; }

    /// <summary>Ambient state for one run, such as tool process environment.</summary>
    public static Func<IDisposable?>? RunScope { get; set; }

    /// <summary>Used when the host has no <see cref="AutomationHost.ResourceCoordinatorFactory" /> result.</summary>
    public static Func<IPregelResourceCoordinator?>? ResourceCoordinator { get; set; }

    public static Func<TimeSpan?>? ResourceQueueTimeout { get; set; }

    /// <summary>Machine-local value of a shared parameter, or null when the key is a normal task parameter.</summary>
    public static Func<string, string?>? GlobalParameter { get; set; }

    /// <summary>The identity a task is published under, recorded with each run; null for unpublished tasks.</summary>
    public static Func<AutomationHost, AutomationTask, AutomationPublishedTask?>? PublishedTask { get; set; }

    /// <summary>Node notification kinds that set the run's warning text.</summary>
    public static HashSet<string> WarningEvents { get; } = ["run.warning"];

    /// <summary>Upgrades a saved task document in place before it is read.</summary>
    public static Action<JsonObject>? TaskMigration { get; set; }

    /// <summary>Header comment names that mark a script's title, description and support role.</summary>
    public static List<string> ScriptHeaderMarkers { get; } = ["heron-pregel-script"];

    /// <summary>Assemblies and namespaces every script compiles against, besides the automation ones.</summary>
    public static List<Assembly> ScriptReferences { get; } = [];

    public static List<string> ScriptImports { get; } = [];

    /// <summary>Maps an installed script's editable configuration without prescribing a product's layout.</summary>
    public static Func<string, string>? ScriptInputPath { get; set; }

    /// <summary>Provides an immutable admitted source snapshot; null keeps local scripts in place.</summary>
    public static Func<string, string, CancellationToken, AutomationScriptSnapshot>? ScriptSnapshot { get; set; }

    public static Func<string, AutomationExecutionVersion?>? ScriptVersion { get; set; }

    /// <summary>Marks an admitted snapshot as in use each time a run starts on it, so retention keeps it.</summary>
    public static Action<string>? ScriptSnapshotUsed { get; set; }

    /// <summary>Writable per-script cache for a script directory; null keeps the default beside a local script.</summary>
    public static Func<string, string?>? ScriptCacheDirectory { get; set; }

    /// <summary>Appends host guidance to a step error, such as where a read-only package expects its writes.</summary>
    public static Func<string, string?>? ErrorHint { get; set; }

    public static Func<string, string, CancellationToken, string?>? ScriptContentRevision { get; set; }

    /// <summary>True when an embedding scheduler owns this occurrence and the local timer must skip it.</summary>
    public static Func<string, DateTimeOffset, bool>? ScheduleClaimed { get; set; }

    internal static AutomationScriptSnapshot FreezeScript(string path, string stateRoot, CancellationToken token) =>
        ScriptSnapshot?.Invoke(path, stateRoot, token) ?? new(path, null);

    internal static IDisposable? EnterScriptScope(string taskId, string directory) => ScriptScope?.Invoke(taskId, directory);

    internal static string Explain(string message) =>
        ErrorHint?.Invoke(message) is { Length: > 0 } hint && !message.Contains(hint, StringComparison.Ordinal)
            ? message + Environment.NewLine + hint : message;
}

/// <summary>Writable locations for scripts. Package and snapshot directories are read-only.</summary>
public static class AutomationScriptStorage
{
    /// <summary>
    ///     Persistent cache for one script, such as compiled tools. Per-run output belongs in the node context's
    ///     working directory instead. A local script without a host mapping uses its own <c>.cache</c> directory,
    ///     which is excluded from content hashing.
    /// </summary>
    public static string CacheDirectory(string scriptDirectory)
    {
        var directory = Path.GetFullPath(scriptDirectory);
        var cache = AutomationRuntime.ScriptCacheDirectory?.Invoke(directory) ?? Path.Combine(directory, ".cache");
        Directory.CreateDirectory(cache);
        return cache;
    }
}

public sealed record AutomationPublishedTask(string Id, string Name);

/// <summary>A source of installable scripts that the catalog shows as one extra directory.</summary>
public interface IAutomationScriptSource
{
    string DirectoryLabel { get; }
    string Url { get; }
    string Root { get; }
    string? Error { get; }

    Task RefreshAsync(CancellationToken token);

    /// <summary>The stamp of the installed scripts containing this path; empty when it is not one of them.</summary>
    string InstalledStamp(string path);

    /// <summary>Held while a script is read, so installation cannot replace it; null for paths outside the source.</summary>
    IDisposable? AcquireReadLease(string path);

    /// <summary>Entries of this source, merging what the catalog found under <see cref="Root" />.</summary>
    IReadOnlyList<AutomationScriptEntry> ListEntries(IReadOnlyList<AutomationScriptEntry> local, Func<string, string?> loadedStamp);

    /// <summary>
    /// Installs the suite containing this path unless already current. The callback receives whether shared
    /// files change, and must run the commit action once the affected scripts can be replaced.
    /// </summary>
    Task InstallAsync(string path, Action<bool, Action> commit, CancellationToken token);
}
