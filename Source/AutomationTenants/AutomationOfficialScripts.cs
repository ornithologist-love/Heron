using Heron.Pregel.Automation;
namespace Heron.AutomationTenants;

public sealed record AutomationOfficialScript(string Id, string Title, string Revision,
    string? InstalledRevision, string SharedRevision, string Entry, string Path, bool NeedsDownload)
{
    public string? TaskId { get; init; }
    public AutomationExecutionVersion? ExecutionVersion { get; init; }
    public string ArchiveSha256 { get; init; } = "";
    public string SharedArchiveSha256 { get; init; } = "";
}

public sealed record AutomationScriptDownload(string Id, string Revision, string Path, bool Updated)
{
    public AutomationExecutionVersion? ExecutionVersion { get; init; }
    public bool PreservedLocalChanges => ExecutionVersion?.LocalModified == true;
}

/// <summary>Download official source files without evaluating CSX or registering templates.</summary>
public sealed class AutomationOfficialScripts
{
    private readonly AutomationRemoteSource _source = new();
    public string SourceUrl => AutomationRemoteSource.DefaultUrl;
    public string Root => _source.Root;

    public Task<IReadOnlyList<AutomationOfficialScript>> ListAsync(CancellationToken token = default) => ListAsync(token, null);

    public async Task<IReadOnlyList<AutomationOfficialScript>> ListAsync(CancellationToken token, string? sourceId)
    {
        await RefreshAsync(token, sourceId);
        return _source.Manifest!.Scripts.Select(package =>
        {
            var installed = _source.InstalledStamp(package);
            var path = System.IO.Path.Combine(Root, package.Entry);
            return new AutomationOfficialScript(package.Id, package.Title, package.Revision,
                installed.Length == 0 ? null : installed.Split(':')[0], _source.Manifest.Shared.Revision,
                package.Entry, path, NeedsDownload(package))
                { TaskId = package.TaskId, ExecutionVersion = DescribeInstalled(path),
                    ArchiveSha256 = package.Sha256, SharedArchiveSha256 = _source.Manifest.Shared.Sha256 };
        }).ToArray();
    }

    /// <param name="id">Official package ID; null explicitly selects all packages.</param>
    public async Task<IReadOnlyList<AutomationScriptDownload>> PullAsync(string? id, CancellationToken token = default)
    {
        await RefreshAsync(token, id);
        var packages = id == null ? _source.Manifest!.Scripts
            : _source.Manifest!.Scripts.Where(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (id != null && packages.Length == 0) throw new ArgumentException("官方源中找不到脚本：" + id);
        List<AutomationScriptDownload> results = [];
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            var path = System.IO.Path.Combine(Root, package.Entry);
            var neededUpdate = _source.NeedsUpdate(package);
            await _source.EnsurePathAsync(path, commit => commit(), token);
            var updated = neededUpdate && !_source.NeedsUpdate(package);
            results.Add(new(package.Id, package.Revision, path, updated)
                { ExecutionVersion = AutomationLocalSource.Describe(path) });
        }
        return results;
    }

    private bool NeedsDownload(AutomationRemotePackage package) => _source.NeedsUpdate(package);

    private static AutomationExecutionVersion? DescribeInstalled(string path)
    {
        try { return AutomationLocalSource.Describe(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return null; }
    }

    private async Task RefreshAsync(CancellationToken token, string? sourceId = null)
    {
        await _source.RefreshAsync(token, sourceId);
        // Downloads must use a fresh manifest, never a cached fallback after a network error.
        if (_source.Error != null) throw new IOException(_source.Error);
    }
}
