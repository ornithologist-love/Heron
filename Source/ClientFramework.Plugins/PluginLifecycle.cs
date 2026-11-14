namespace Heron.ClientFramework.Plugins;

public enum PluginState { Discovered, Starting, Running, Stopping, Disabled, RestartRequired, Failed }

/// <summary>The adapter owns module instances, local DI, registrations and the assembly session.</summary>
public interface IPluginInstance : IAsyncDisposable
{
    bool CanReload => false;
    Task StartAsync(CancellationToken cancellationToken);
    /// <summary>Must await all owned work. On failure retain resources for a later stop attempt.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed record PluginStatus(string Id, PluginState State, string? Error, string Version = "", bool EnabledNextStart = true, string DisplayName = "")
{
    public string Description { get; init; } = "";
    public bool EnabledByDefault { get; init; } = true;
}

/// <summary>Serializes transitions; never releases a provider while an active consumer still owns it.</summary>
public sealed class PluginLifecycle : IAsyncDisposable
{
    private sealed class Entry(PluginManifest manifest)
    {
        public PluginManifest Manifest { get; } = manifest;
        public PluginState State = PluginState.Discovered;
        public IPluginInstance? Instance;
        public string? Error;
    }
    private readonly PluginResolution _resolution;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Func<PluginManifest, IPluginInstance> _factory;
    private readonly SemaphoreSlim _gate = new(1);
    private bool _disposed;
    private readonly HashSet<string> _disabled;

    public PluginLifecycle(PluginResolution resolution, Func<PluginManifest, IPluginInstance> factory, IEnumerable<string>? disabled = null)
    {
        _resolution = resolution;
        _disabled = PluginEnablementStore.Blocked(resolution, disabled ?? []);
        _factory = factory;
        _entries = resolution.LoadOrder.ToDictionary(p => p.Id, p => new Entry(p) { State = _disabled.Contains(p.Id) ? PluginState.Disabled : PluginState.Discovered }, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PluginStatus> GetStatus() => _resolution.LoadOrder.Select(p =>
    {
        var entry = _entries[p.Id];
        return new PluginStatus(p.Id, entry.State, entry.Error, p.Version, DisplayName: p.DisplayName);
    }).ToArray();

    public async Task EnableAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.ContainsKey(id)) throw new KeyNotFoundException(id);
            var started = new List<Entry>();
            async Task Start(string key)
            {
                var entry = _entries[key];
                if (entry.State == PluginState.Running) return;
                if (entry.State == PluginState.RestartRequired || entry.Instance != null)
                    throw new InvalidOperationException($"Plugin '{key}' requires restart or completion of its previous stop.");
                foreach (var dependency in _resolution.Dependencies[key])
                {
                    var optional = entry.Manifest.Dependencies.Single(d => d.Id.Equals(dependency, StringComparison.OrdinalIgnoreCase)).Optional;
                    if (!optional) { await Start(dependency); continue; }
                    if (_disabled.Contains(dependency)) continue;
                    var checkpoint = started.Count;
                    try { await Start(dependency); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        // Optional capability failure must not prevent an independent consumer from starting.
                        foreach (var partial in started.Skip(checkpoint).Reverse())
                        {
                            if (HasConsumer(partial.Manifest.Id)) continue;
                            await Stop(partial, CancellationToken.None);
                            if (partial.State == PluginState.Starting) partial.State = PluginState.Failed;
                            partial.Error ??= error.Message;
                        }
                        started.RemoveRange(checkpoint, started.Count - checkpoint);
                    }
                }
                entry.State = PluginState.Starting;
                entry.Error = null;
                started.Add(entry); // Include partially started instances in rollback.
                entry.Instance = _factory(entry.Manifest);
                await entry.Instance.StartAsync(cancellationToken);
                entry.State = PluginState.Running;
            }
            try { await Start(id); _disabled.ExceptWith(started.Select(e => e.Manifest.Id).Append(id)); }
            catch (Exception error)
            {
                var failures = new List<Exception> { error };
                foreach (var entry in started.AsEnumerable().Reverse())
                {
                    if (HasConsumer(entry.Manifest.Id)) continue;
                    try { await Stop(entry, CancellationToken.None); }
                    catch (Exception cleanup) { failures.Add(cleanup); }
                    if (entry.State == PluginState.Starting) entry.State = PluginState.Failed;
                    entry.Error ??= error.Message;
                }
                if (failures.Count > 1) throw new AggregateException("Plugin activation and rollback failed.", failures);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DisableAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.ContainsKey(id)) throw new KeyNotFoundException(id);
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
            foreach (var manifest in _resolution.LoadOrder)
                if (manifest.Dependencies.Any(d => !d.Optional && affected.Contains(d.Id))) affected.Add(manifest.Id);
            _disabled.UnionWith(affected);
            foreach (var manifest in _resolution.LoadOrder.Reverse().Where(p => affected.Contains(p.Id)))
                await Stop(_entries[manifest.Id], cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private bool HasConsumer(string id) => _entries.Values.Any(e => e.Instance != null
        && e.Manifest.Dependencies.Any(d => !d.Optional && d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

    private static async Task Stop(Entry entry, CancellationToken token)
    {
        if (entry.Instance == null) return;
        entry.State = PluginState.Stopping;
        try
        {
            await entry.Instance.StopAsync(token);
            await entry.Instance.DisposeAsync();
            var canReload = entry.Instance.CanReload;
            entry.Instance = null;
            entry.State = entry.Manifest.UpdateMode == "hot_reload" && canReload ? PluginState.Disabled : PluginState.RestartRequired;
            entry.Error = null;
        }
        catch (Exception error)
        {
            entry.State = PluginState.Failed;
            entry.Error = error.Message;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            var failures = new List<Exception>();
            foreach (var manifest in _resolution.LoadOrder.Reverse())
            {
                if (HasConsumer(manifest.Id)) continue;
                try { await Stop(_entries[manifest.Id], CancellationToken.None); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException("Plugin shutdown failed; dependencies remain owned.", failures);
            _disposed = true;
        }
        finally { _gate.Release(); }
    }
}
