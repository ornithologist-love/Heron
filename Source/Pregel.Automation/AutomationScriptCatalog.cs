using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heron.Pregel.Automation;

public sealed record AutomationScriptEntry(string Path, string RelativePath, string Title, string Status,
    bool IsLoaded, string? TemplateId, string? Error)
{
    public string Description { get; init; } = "";
    public string SourceInfo { get; init; } = "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsRemote { get; init; }
    public bool NeedsDownload { get; init; }
    public bool NeedsReload { get; init; }
    public bool IsLocal => !IsRemote;
    public bool ShowLocalReload => IsLocal && IsLoaded;
    public bool HasUpdate => IsRemote && (NeedsDownload || NeedsReload) && IsLoaded;
    public string DownloadText => NeedsDownload ? "下载 / 更新并加载" : "重新加载";
}

/// <summary>Bundled examples, local work directories and explicitly loaded CSX entries.</summary>
public sealed partial class AutomationScriptCatalog : IAsyncDisposable
{
    private readonly AutomationHost _host;
    private readonly string _stateRoot;
    private readonly string _settingsFile;
    private readonly SemaphoreSlim _operations = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, AutomationScriptSlot> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private Settings _settings;
    private string? _settingsError;
    private bool _restored;
    private bool _disposed;
    private sealed record Settings(List<string> Directories, List<string> Scripts);

    public AutomationScriptCatalog(AutomationHost host, string stateRoot)
    {
        _host = host;
        _stateRoot = stateRoot;
        _settingsFile = System.IO.Path.Combine(stateRoot, "script-sources.json");
        try
        {
            _settings = File.Exists(_settingsFile)
                ? JsonSerializer.Deserialize<Settings>(AutomationStore.ReadShared(_settingsFile)) ?? new([], []) : new([], []);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _settings = new([], []);
            _settingsError = "脚本目录配置读取失败，原文件已保留：" + ex.Message;
        }
    }

    public event Action? Changed;
    private DateTime _observedSettingsTime;

    internal async Task RefreshRegistrationsAsync(CancellationToken token)
    {
        if (!_restored || !File.Exists(_settingsFile)) return;
        var changedAt = File.GetLastWriteTimeUtc(_settingsFile);
        if (changedAt == _observedSettingsTime) return;
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = JsonSerializer.Deserialize<Settings>(AutomationStore.ReadShared(_settingsFile))!;
            lock (_gate) _settings = next;
            foreach (var path in next.Scripts)
            {
                lock (_gate) { if (FindLoaded(path).Value != null) continue; }
                try { await ChangeScriptAsync(path, true, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                { lock (_gate) _errors[AutomationScriptLoader.EntryKey(path)] = error.Message; }
            }
            _observedSettingsTime = changedAt;
        }
        finally { _operations.Release(); NotifyChanged(); }
    }
    public IReadOnlyList<string> Directories
    {
        get
        {
            lock (_gate) return (_source == null ? [] : new[] { _source.DirectoryLabel })
                .Concat(ReduceDirectories(_settings.Directories.Append(AutomationRuntime.ScriptsDirectory))).ToArray();
        }
    }

    public bool HasLoadedScripts(string directory)
    {
        if (IsSourceLabel(directory)) directory = _source!.Root;
        directory = NormalizeDirectory(directory);
        lock (_gate) return _loaded.Keys.Any(path => IsUnder(path, directory));
    }

    public string? LoadedScriptPath(string templateId)
    {
        lock (_gate)
        {
            var path = _loaded.FirstOrDefault(entry => entry.Value.Template.Id == templateId).Key;
            return path == null ? null : AutomationScriptLoader.PreferredPath(path);
        }
    }

    public string? SavedScriptPath(string templateId)
    {
        lock (_gate) return _settings.Scripts.FirstOrDefault(path =>
            "script." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())))[..24].ToLowerInvariant() == templateId);
    }

    /// <summary>The loaded definition's revision, or the saved entry's revision without loading its code.</summary>
    public string SavedScriptRevision(string templateId)
    {
        lock (_gate)
        {
            var savedPath = SavedScriptPath(templateId);
            if (savedPath != null && AutomationRuntime.ScriptVersion?.Invoke(savedPath) is { } actual)
                return actual.ReportedRevision;
            var loaded = _loaded.FirstOrDefault(entry => entry.Value.RegisteredTemplate?.Id == templateId);
            if (loaded.Value != null)
                return _sourceLoaded.TryGetValue(loaded.Key, out var loadedStamp)
                    ? loadedStamp + ":" + loaded.Value.Hash : loaded.Value.Hash;
            var path = SavedScriptPath(templateId);
            if (path == null) return "";
            var stamp = _source?.InstalledStamp(path) ?? "";
            if (stamp.Length > 0) return stamp;
            path = AutomationScriptLoader.PreferredPath(path);
            return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
        }
    }

    public string? LoadError(string templateId)
    {
        lock (_gate)
        {
            var path = SavedScriptPath(templateId);
            return path != null && _errors.TryGetValue(AutomationScriptLoader.EntryKey(path), out var error)
                ? error : null;
        }
    }

    internal bool SourceMissing(string templateId)
    {
        var path = SavedScriptPath(templateId);
        return path != null && AutomationScriptLoader.SourceMissing(AutomationScriptLoader.PreferredPath(path));
    }

    public async Task RestoreAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token);
        try
        {
            if (_restored) return;
            // Drop stale restore records and collapse adjacent graph/template aliases before evaluation.
            // Retain the earlier graph path when present so existing tasks keep their template ID.
            var scripts = _settings.Scripts.Where(path => _source != null && System.IO.Path.GetFullPath(path).StartsWith(
                    System.IO.Path.GetFullPath(_source.Root) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !AutomationScriptLoader.SourceMissing(AutomationScriptLoader.PreferredPath(path)))
                .GroupBy(AutomationScriptLoader.EntryKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(path => path.EndsWith(".Template.csx", StringComparison.OrdinalIgnoreCase)).First())
                .ToList();
            List<string> retained = [];
            foreach (var path in scripts)
            {
                try
                {
                    if (!ReadMetadata(AutomationScriptLoader.PreferredPath(path)).Support) retained.Add(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    retained.Add(path);
                }
            }
            // Normalize restore records before any instance is published. A failed save
            // leaves both the prior settings and live registrations untouched.
            lock (_gate)
            {
                if (!_settings.Scripts.SequenceEqual(retained, StringComparer.OrdinalIgnoreCase))
                {
                    EnsureWritable();
                    var next = _settings with { Scripts = retained };
                    Save(next); _settings = next;
                }
            }
            foreach (var path in retained)
            {
                try
                {
                    await ChangeScriptAsync(path, load: true, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (AutomationScriptInUseException ex)
                {
                    // A busy task rejects this operation; it is not a script load failure.
                    Serilog.Log.Information(ex, "[Automation] script restoration blocked for {Path}", path);
                }
                catch (Exception ex)
                {
                    lock (_gate) _errors[AutomationScriptLoader.EntryKey(path)] = ex.Message;
                }
            }
            _restored = true;
        }
        finally { _operations.Release(); NotifyChanged(); }
    }

    public async Task OpenDirectoryAsync(string directory, CancellationToken token = default)
    {
        directory = NormalizeDirectory(directory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("工作目录不存在");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token);
        try
        {
            lock (_gate)
            {
                EnsureWritable();
                var directories = ReduceDirectories(_settings.Directories.Append(directory));
                if (!_settings.Directories.SequenceEqual(directories, StringComparer.OrdinalIgnoreCase))
                {
                    var next = _settings with { Directories = directories.ToList() };
                    Save(next); _settings = next;
                }
            }
        }
        finally { _operations.Release(); NotifyChanged(); }
    }

    public Task<IReadOnlyList<AutomationScriptEntry>> ScanAsync(string directory, CancellationToken token = default) =>
        IsSourceLabel(directory) ? ScanSourceAsync(_source!, token) : Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        directory = NormalizeDirectory(directory);
        List<string> files = [];
        var stack = new Stack<(string Path, int Depth)>();
        // A missing registered directory can still contain an in-memory registration to unload.
        if (Directory.Exists(directory)) stack.Push((directory, 0));
        while (stack.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFiles(folder.Path, "*.csx"))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var entry = AutomationScriptLoader.PreferredPath(path);
                if (ReadMetadata(entry).Support) continue;
                files.Add(entry);
                if (files.Count > 1000) throw new IOException("目录内 CSX 超过 1000 个，请打开更具体的工作目录");
            }
            if (folder.Depth >= 12) continue;
            foreach (var child in Directory.EnumerateDirectories(folder.Path))
            {
                var name = System.IO.Path.GetFileName(child);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules"
                    || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                stack.Push((child, folder.Depth + 1));
            }
        }
        lock (_gate)
        {
            // Only live registrations need an absent-file row. Failed old restore records are not entries.
            files.AddRange(_loaded.Keys.Where(path => IsUnder(path, directory)));
            return (IReadOnlyList<AutomationScriptEntry>)files.Select(AutomationScriptLoader.PreferredPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p => p.EndsWith(".automation.csx", StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).Select(path =>
                {
                    var loaded = FindLoaded(path).Value;
                    _errors.TryGetValue(AutomationScriptLoader.EntryKey(path), out var error);
                    var exists = File.Exists(path);
                    var changed = exists && loaded != null && Hash(path) != loaded.Hash;
                    var metadata = exists ? ReadMetadata(path) : (Support: false, Title: (string?)null, Description: (string?)null);
                    var file = exists ? new FileInfo(path) : null;
                    return new AutomationScriptEntry(path, System.IO.Path.GetRelativePath(directory, path),
                        loaded?.Template.Title ?? metadata.Title
                            ?? System.IO.Path.GetFileNameWithoutExtension(path),
                        !exists ? "文件不存在" : error != null ? "加载失败" : loaded == null ? "未加载"
                            : changed ? "入口已修改，需重新加载" : "已加载", loaded != null, loaded?.Template.Id, error)
                    {
                        Description = loaded?.Template.Description ?? metadata.Description ?? "",
                        NeedsReload = changed,
                        SourceInfo = file == null ? "源文件已不存在" : $"CSX · {file.Length / 1024d:0.#} KB · 更新 {file.LastWriteTime:yyyy-MM-dd HH:mm}"
                    };
                }).ToArray();
        }
    }, token);

    public async Task<string> LoadAsync(string path, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token);
        try
        {
            EnsureWritable();
            if (!IsSourcePath(path) && !Directories.Where(root => !IsSourceLabel(root))
                .Any(root => IsUnder(path, root))) throw new InvalidOperationException("请先打开脚本所在工作目录");
            return await ChangeScriptAsync(path, load: true, linked.Token);
        }
        catch (Exception ex) when (ex is not AutomationScriptInUseException)
        {
            lock (_gate) _errors[AutomationScriptLoader.EntryKey(path)] = ex.Message;
            throw;
        }
        finally { _operations.Release(); NotifyChanged(); }
    }

    private async Task<string> ChangeScriptAsync(string path, bool load, CancellationToken token)
    {
        if (load && !path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择 CSX 文件");
        if (load && IsSourcePath(path)) await _source!.PrepareForLoadAsync(path, (_, change) =>
        {
            string[] ids;
            lock (_gate) ids = _loaded.Where(item => IsUnder(item.Key, _source.Root)).Select(item => item.Value.Id).ToArray();
            _host.CommitScriptSourceChange(ids, change);
        }, token);
        KeyValuePair<string, AutomationScriptSlot> previous;
        lock (_gate) previous = FindLoaded(path);
        string identityPath;
        lock (_gate) identityPath = previous.Key ?? _settings.Scripts.FirstOrDefault(saved =>
            AutomationScriptLoader.EntryKey(saved).Equals(AutomationScriptLoader.EntryKey(path), StringComparison.OrdinalIgnoreCase))
            ?? System.IO.Path.GetFullPath(path);
        var entryPath = load ? AutomationScriptLoader.PreferredPath(path) : null;
        if (entryPath != null && ReadMetadata(entryPath).Support)
            throw new InvalidOperationException("辅助脚本不能作为任务入口，请选择对应模板。");
        var id = previous.Value?.Id ?? "script." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath.ToUpperInvariant())))[..24].ToLowerInvariant();
        var slot = previous.Value ?? new AutomationScriptSlot(_host, id);
        await slot.ChangeAsync(entryPath, _stateRoot, publish =>
        {
            lock (_gate)
            {
                EnsureWritable();
                var key = AutomationScriptLoader.EntryKey(path);
                var scripts = new List<string>();
                var recorded = false;
                foreach (var saved in _settings.Scripts)
                {
                    if (!AutomationScriptLoader.EntryKey(saved).Equals(key, StringComparison.OrdinalIgnoreCase)) scripts.Add(saved);
                    else if (load && !recorded) { scripts.Add(identityPath); recorded = true; }
                }
                if (load && !recorded) scripts.Add(identityPath);
                var next = _settings with { Scripts = scripts };
                if (!_settings.Scripts.SequenceEqual(scripts, StringComparer.OrdinalIgnoreCase)) Save(next);
                publish();
                _settings = next;
                if (load) _loaded[identityPath] = slot;
                else if (previous.Key != null) _loaded.Remove(previous.Key);
                if (load && IsSourcePath(identityPath)) _sourceLoaded[identityPath] = _source!.InstalledStamp(entryPath!);
                else if (previous.Key != null) _sourceLoaded.Remove(previous.Key);
                _errors.Remove(key);
            }
        }, token);
        return id;
    }

    public async Task UnloadAsync(string path, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token);
        try
        {
            EnsureWritable();
            await ChangeScriptAsync(path, load: false, linked.Token);
        }
        finally { _operations.Release(); NotifyChanged(); }
    }

    public async Task RemoveDirectoryAsync(string directory, CancellationToken token = default)
    {
        if (IsSourceLabel(directory))
            throw new InvalidOperationException("发布方脚本源不能移除；本地工作目录可单独登记。");
        directory = NormalizeDirectory(directory);
        if (string.Equals(directory,
                AutomationRuntime.ScriptsDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此目录为远程脚本源的默认下载目录；可卸载已加载脚本，源文件会保留。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operations.WaitAsync(linked.Token);
        try
        {
            lock (_gate)
            {
                EnsureWritable();
                if (_loaded.Keys.Any(path => IsUnder(path, directory)))
                    throw new InvalidOperationException("请先卸载此目录中已加载的脚本；任务和源文件会保留");
                var next = _settings with
                {
                    Directories = _settings.Directories.Where(p => !p.Equals(directory, StringComparison.OrdinalIgnoreCase)).ToList(),
                    Scripts = _settings.Scripts.Where(path => !IsUnder(path, directory)).ToList()
                };
                Save(next); _settings = next;
                foreach (var path in _errors.Keys.Where(path => IsUnder(path, directory)).ToArray()) _errors.Remove(path);
            }
        }
        finally { _operations.Release(); NotifyChanged(); }
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[Automation] script catalog notification failed"); }
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_settingsError != null) throw new IOException(_settingsError);
    }

    // Caller holds _gate; compare entry families while keeping the saved path-owned identity.
    private KeyValuePair<string, AutomationScriptSlot> FindLoaded(string path)
    {
        var key = AutomationScriptLoader.EntryKey(path);
        return _loaded.FirstOrDefault(entry => AutomationScriptLoader.EntryKey(entry.Key).Equals(key, StringComparison.OrdinalIgnoreCase));
    }
    private void Save(Settings value)
    {
        using var update = new AutomationStore(_stateRoot).BeginUpdate();
        if (File.Exists(_settingsFile))
        {
            var current = JsonSerializer.Deserialize<Settings>(AutomationStore.ReadShared(_settingsFile))!;
            value = new(
                current.Directories.Except(_settings.Directories.Except(value.Directories, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                    .Union(value.Directories.Except(_settings.Directories, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase).ToList(),
                current.Scripts.Except(_settings.Scripts.Except(value.Scripts, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                    .Union(value.Scripts.Except(_settings.Scripts, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase).ToList());
        }
        Directory.CreateDirectory(_stateRoot);
        File.WriteAllText(_settingsFile + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_settingsFile + ".tmp", _settingsFile, true);
    }
    private static string NormalizeDirectory(string directory) => System.IO.Path.TrimEndingDirectorySeparator(
        System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory)));

    private static IReadOnlyList<string> ReduceDirectories(IEnumerable<string> directories)
    {
        var roots = directories.Select(NormalizeDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return roots.Where(root => !roots.Any(parent => IsUnder(root, parent))).ToArray();
    }

    private static bool IsUnder(string file, string directory) => file.StartsWith(
        System.IO.Path.EndsInDirectorySeparator(directory) ? directory : directory + System.IO.Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    // Read declarations only. Discovery must not evaluate a script to determine its role or title.
    private static (bool Support, string? Title, string? Description) ReadMetadata(string path)
    {
        var support = false;
        string? title = null;
        string? description = null;
        foreach (var line in File.ReadLines(path).Take(24))
        {
            var value = line.Trim();
            foreach (var marker in AutomationRuntime.ScriptHeaderMarkers)
            {
                if (value.Equals($"// {marker}: support", StringComparison.OrdinalIgnoreCase)) support = true;
                var titlePrefix = $"// {marker}-title:";
                if (value.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase))
                    title = value[titlePrefix.Length..].Trim() is { Length: > 0 } text ? text : null;
                var descriptionPrefix = $"// {marker}-description:";
                if (value.StartsWith(descriptionPrefix, StringComparison.OrdinalIgnoreCase))
                    description = value[descriptionPrefix.Length..].Trim() is { Length: > 0 } detail ? detail : null;
            }
        }
        return (support, title, description);
    }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _operations.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            AutomationScriptSlot[] loaded;
            lock (_gate) { loaded = _loaded.Values.ToArray(); _loaded.Clear(); }
            foreach (var slot in loaded) await slot.DisposeAsync();
        }
        finally { _operations.Release(); }
    }
}
