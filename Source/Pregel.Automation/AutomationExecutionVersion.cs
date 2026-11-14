namespace Heron.Pregel.Automation;

/// <summary>The immutable content actually loaded for one execution; publication identity is separate.</summary>
public sealed record AutomationExecutionVersion(
    string SourceId, string Entry, string BaseRevision, string? BaseSha256, string ActualSha256,
    string SharedBaseRevision, string? SharedBaseSha256, string SharedActualSha256,
    string BundleSha256, bool BaselineKnown, bool LocalModified, string Origin)
{
    public string SnapshotRoot { get; init; } = "";
    public string ExecutionEntry { get; init; } = "";
    public string ReportedRevision => ActualSha256 + ":shared:" + SharedActualSha256
        + ":base:" + (BaseSha256 ?? "unknown") + ":local:" + LocalModified.ToString().ToLowerInvariant();
}

public sealed record AutomationScriptSnapshot(string Path, AutomationExecutionVersion? Version);
