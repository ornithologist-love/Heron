namespace Heron.Pregel.Automation;

public sealed partial class AutomationScriptCatalog
{
    private readonly IAutomationScriptSource? _source = AutomationRuntime.ScriptSource;
    private readonly Dictionary<string, string> _sourceLoaded = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The source's directory label, or null when only local directories exist.</summary>
    public static string? RemoteDirectory => AutomationRuntime.ScriptSource?.DirectoryLabel;
    public string? RemoteSourceError => _source?.Error;
    public string? RemoteSourceUrl => _source?.Url;

    private bool IsSourceLabel(string directory) => _source != null && directory == _source.DirectoryLabel;
    private bool IsSourcePath(string path) => _source != null && IsUnder(path, _source.Root);

    private async Task<IReadOnlyList<AutomationScriptEntry>> ScanSourceAsync(IAutomationScriptSource source, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token);
        try
        {
            await source.RefreshAsync(linked.Token);
            var local = await ScanAsync(source.Root, linked.Token);
            lock (_gate)
                return source.ListEntries(local, path =>
                    FindLoaded(path).Key is { } loadedPath ? _sourceLoaded.GetValueOrDefault(loadedPath) : null);
        }
        finally { _operations.Release(); }
    }

    public async Task<string> DownloadAndLoadAsync(string path, CancellationToken token = default)
    {
        var source = _source ?? throw new InvalidOperationException("没有配置脚本源");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token);
        try
        {
            EnsureWritable();
            await source.RefreshAsync(linked.Token);
            await source.InstallAsync(path, (sharedChanged, commit) =>
            {
                // Shared replacement protects every loaded remote suite, not just the selected entry.
                string[] ids;
                lock (_gate) ids = _loaded.Where(p => IsUnder(p.Key, source.Root) && (sharedChanged
                    || AutomationScriptLoader.EntryKey(p.Key).Equals(AutomationScriptLoader.EntryKey(path), StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Value.Id).ToArray();
                _host.CommitScriptSourceChange(ids, commit);
            }, linked.Token);
            return await ChangeScriptAsync(path, true, linked.Token);
        }
        catch (Exception ex) when (ex is not AutomationScriptInUseException)
        {
            lock (_gate) _errors[AutomationScriptLoader.EntryKey(path)] = ex.Message;
            throw;
        }
        finally { _operations.Release(); NotifyChanged(); }
    }
}
