using System.Text.Json;
using Heron.ClientFramework.Plugins;

namespace Heron.ClientFramework.Runtime;

/// <summary>User-facing messages of the plugin host. The embedding product supplies its own language and copy.</summary>
public sealed record ClientPluginHostText
{
    public string NotAssembled { get; init; } = "Plugin packages are not assembled.";
    public string InvalidEmptyPlatformEdition { get; init; } = "The empty platform edition contains unfiltered plugins or lacks its platform marker.";
    public string RecoveredPrefix { get; init; } = "Restored the plugins bundled with the client: ";
    public string StartupErrorPending { get; init; } = "Resolve the current plugin startup error first.";
    public string UpdatePending { get; init; } = "An update is already pending; restart or cancel it first.";
    public string EmptyUpdateManifest { get; init; } = "The update manifest is empty.";
    public string EmptyUpdateGroup { get; init; } = "The update manifest contains no plugin packages.";
    public string FeaturesNotInitialized { get; init; } = "Features are not initialized yet.";
    public string PluginsNotInitialized { get; init; } = "Plugins are not initialized yet.";
}

public sealed record ClientPluginHostOptions
{
    /// <summary>Directory holding <c>plugins/builtin</c>, <c>edition.json</c> and <c>host-api.json</c>.</summary>
    public required string BaseDirectory { get; init; }

    /// <summary>Per-user root for installed versions and enablement choices, one subdirectory per edition.</summary>
    public required string PluginsRoot { get; init; }

    /// <summary>Host assembly whose shared contracts plugins bind to.</summary>
    public string? HostAssemblyPath { get; init; }

    /// <summary>
    ///     When set, the host consumes only the bundled packages filtered for this platform: no user-installed
    ///     overrides, no trial bookkeeping, and an empty edition must be marked for this platform and ship no plugins.
    /// </summary>
    public string? BundledOnlyPlatform { get; init; }

    /// <summary>
    ///     Evaluated after the edition is read. When true the plugin set follows the shipped edition across host
    ///     upgrades: plugins outside it are ignored and pinned versions older than the bundled ones are raised.
    /// </summary>
    public Func<bool> FollowShippedEdition { get; init; } = static () => false;

    public ClientPluginHostText Text { get; init; } = new();
    public Action<string> Log { get; init; } = static _ => { };
}

/// <summary>Archive and expected SHA-256 of one plugin package in an update group.</summary>
public sealed record ClientPluginArchive(string Archive, string Sha256);

/// <summary>Packages and resolution chosen at startup, plus the recovery note when an override was rejected.</summary>
public sealed record ClientPluginStartupSelection(PluginPackage[] Packages, PluginResolution Resolution, string? RecoveryMessage);

/// <summary>
///     Product-neutral plugin host: reads the bundled packages and edition, applies the host interface, version
///     locks, rollback and enablement choices, starts plugins in dependency order and stages updates.
///     Product capabilities reach plugins through the services factory; the host does not know their types.
/// </summary>
public sealed class ClientPluginHostCore : IAsyncDisposable
{
    private readonly ClientPluginHostOptions _options;
    private readonly ClientPluginRegistry _registry;
    private readonly Func<PluginManifest, IReadOnlyDictionary<Type, object>> _services;
    private readonly SemaphoreSlim _preferenceGate = new(1);
    private PluginLifecycle? _runtime;
    private PluginEnablementStore? _preferences;
    private PluginResolution? _resolution;
    private PluginVersionStore _store;
    private PluginPackage[] _selected = [];
    private HashSet<string> _disabledByDefault = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    public ClientPluginHostCore(ClientPluginHostOptions options, ClientPluginRegistry registry,
        Func<PluginManifest, IReadOnlyDictionary<Type, object>> services)
    {
        _options = options;
        _registry = registry;
        _services = services;
        _store = new PluginVersionStore(Path.Combine(options.PluginsRoot, "full"));
    }

    /// <summary>The edition read at startup; null until the bundled packages were found.</summary>
    public PluginEdition? Edition { get; private set; }
    public string HostApi { get; private set; } = "";
    public string? Error { get; private set; }
    public string? RecoveryMessage { get; private set; }

    /// <summary>True once plugins were activated and startup finished without an unhandled error.</summary>
    public bool Started { get; private set; }
    public bool IsActivated => _runtime != null;
    public bool HasPendingUpdate => _store.ReadState().Pending != null;

    public IReadOnlyList<PluginStatus> Status => _runtime?.GetStatus().Select(s =>
    {
        var metadata = Edition?.PluginMetadata.GetValueOrDefault(s.Id);
        return s with
        {
            EnabledNextStart = !_disabled.Contains(s.Id),
            DisplayName = metadata?.Title ?? s.DisplayName,
            Description = metadata?.Description ?? "",
            EnabledByDefault = metadata?.EnabledByDefault ?? true,
        };
    }).ToArray() ?? [];

    public async Task InitializeAsync()
    {
        if (_runtime != null) return;
        try { await InitializeCoreAsync(); }
        catch (Exception error) { Error = error.Message; Log("startup failed: " + error); }
    }

    private async Task InitializeCoreAsync()
    {
        var root = Path.Combine(_options.BaseDirectory, "plugins", "builtin");
        var builtin = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories).Select(PluginPackage.Read).ToArray()
            : [];
        var editionPath = Path.Combine(_options.BaseDirectory, "edition.json");
        if (builtin.Length == 0 && !File.Exists(editionPath))
            throw new InvalidDataException(_options.Text.NotAssembled);
        var edition = File.Exists(editionPath)
            ? PluginEdition.Read(editionPath)
            : new PluginEdition("full", builtin.Select(p => p.Manifest.Id).ToArray());
        if (_options.BundledOnlyPlatform is { } platform && edition.Plugins.Length == 0)
        {
            if (edition.TargetPlatform != platform || builtin.Length != 0)
                throw new PlatformNotSupportedException(_options.Text.InvalidEmptyPlatformEdition);
            Edition = edition;
            Log($"startup edition={edition.Name} plugins=[] base={_options.BaseDirectory}");
            return; // Do not activate user-installed packages from an earlier edition state.
        }

        Edition = edition;
        _store = new PluginVersionStore(Path.Combine(_options.PluginsRoot, edition.Name));
        var policy = PluginHostPolicy.Read(Path.Combine(_options.BaseDirectory, "host-api.json"));
        HostApi = policy.HostApi;
        var selection = SelectStartup(builtin, edition, _store, policy.HostApi,
            _options.BundledOnlyPlatform, _options.FollowShippedEdition(), _options.Text);
        var packages = selection.Packages;
        var resolution = selection.Resolution;
        RecoveryMessage = selection.RecoveryMessage;
        _resolution = resolution;
        _disabledByDefault = edition.PluginMetadata.Where(pair => !pair.Value.EnabledByDefault)
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _preferences = new PluginEnablementStore(Path.Combine(_options.PluginsRoot, edition.Name, "disabled.json"));
        _disabled = PluginEnablementStore.Blocked(resolution, _preferences.Read(_disabledByDefault));
        _registry.SetHiddenOwners(_disabled);
        Log($"startup edition={edition.Name} disabled=[{string.Join(",", _disabled)}] base={_options.BaseDirectory}");
        _selected = resolution.LoadOrder.Select(m => packages.Single(p => p.Manifest.Id == m.Id)).ToArray();
        var shared = policy.LoadSharedAssemblies(_options.BaseDirectory, _options.HostAssemblyPath);
        _runtime = new PluginLifecycle(resolution, manifest => new ClientPluginInstance(
            packages.Single(p => p.Manifest.Id == manifest.Id), shared, _registry, _services(manifest), Log,
            policy.CompatiblePatchPrefixes, policy.CompatibleAssemblyVersions), _disabled);
        foreach (var manifest in resolution.LoadOrder.Where(m => !_disabled.Contains(m.Id) && m.Modules.Any(module => module.Load == "startup")))
        {
            try { await _runtime.EnableAsync(manifest.Id); Log("started " + manifest.Id); }
            catch (Exception error) { Error = error.Message; Log("start failed " + manifest.Id + ": " + error); }
        }
        if (Error == null && _options.BundledOnlyPlatform == null) _store.MarkHealthy();
        Started = true;
        // A failed trial remains marked booting; the next process recovers the previous healthy group.
    }

    /// <summary>
    ///     Chooses the packages to start: the healthy or trial version group when one exists, otherwise the bundled
    ///     packages. An override that cannot be resolved resets the store to the bundled packages and is reported.
    /// </summary>
    public static ClientPluginStartupSelection SelectStartup(IReadOnlyList<PluginPackage> bundled, PluginEdition edition,
        PluginVersionStore store, string hostApi, string? bundledOnlyPlatform, bool followShippedEdition,
        ClientPluginHostText? text = null)
    {
        text ??= new();
        var builtin = bundled.ToArray();
        var editionPluginIds = followShippedEdition
            ? edition.Resolve(builtin.Select(p => p.Manifest), hostApi).LoadOrder
                .Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        if (editionPluginIds != null)
            builtin = builtin.Where(p => editionPluginIds.Contains(p.Manifest.Id)).ToArray();
        try
        {
            if (bundledOnlyPlatform != null)
                return new(builtin, edition.ForPlatform(bundledOnlyPlatform).Resolve(builtin.Select(p => p.Manifest), hostApi), null);

            var pinned = store.BeginStartup();
            if (editionPluginIds != null)
            {
                var bundledVersions = builtin.ToDictionary(p => p.Manifest.Id, p => p.Manifest.Version,
                    StringComparer.OrdinalIgnoreCase);
                pinned = pinned.Where(p => editionPluginIds.Contains(p.Id)).Select(p =>
                    PluginVersion.Parse(p.Version).CompareTo(PluginVersion.Parse(bundledVersions[p.Id])) < 0
                        ? new PluginLockEntry(p.Id, bundledVersions[p.Id])
                        : p).ToArray();
            }
            var state = store.ReadState();
            var available = store.InstalledPackages().Concat(builtin)
                .Where(p => editionPluginIds == null || editionPluginIds.Contains(p.Manifest.Id))
                .GroupBy(p => (p.Manifest.Id, p.Manifest.Version)).Select(g => g.First()).ToArray();
            var packages = state.HasActive || state.Booting ? PluginVersionStore.Select(pinned, available) : builtin;
            var roots = state.HasActive || state.Booting ? pinned.Select(p => p.Id) : edition.Plugins;
            return new(packages, PluginDependencyGraph.Resolve(packages.Select(p => p.Manifest), roots, hostApi), null);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or JsonException
            or ArgumentException or FileNotFoundException or DirectoryNotFoundException)
        {
            // An incompatible override must not prevent the newly installed host from opening.
            var resolution = edition.Resolve(builtin.Select(p => p.Manifest), hostApi);
            store.ResetToBuiltin();
            return new(builtin, resolution, text.RecoveredPrefix + error.Message);
        }
    }

    public IDisposable BeginHostUpdate() => _store.AcquireUpdateLock();

    public async Task InstallUpdateManifestAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var entries = JsonSerializer.Deserialize<ClientPluginArchive[]>(await File.ReadAllTextAsync(fullPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException(_options.Text.EmptyUpdateManifest);
        await InstallAndStageGroupAsync(entries.Select(e => e with
        {
            Archive = Path.GetFullPath(e.Archive, Path.GetDirectoryName(fullPath)!),
        }).ToArray());
    }

    public async Task InstallAndStageGroupAsync(IReadOnlyList<ClientPluginArchive> archives)
    {
        if (Error != null) throw new InvalidOperationException(_options.Text.StartupErrorPending);
        if (HasPendingUpdate) throw new InvalidOperationException(_options.Text.UpdatePending);
        if (archives.Count == 0) throw new InvalidDataException(_options.Text.EmptyUpdateGroup);
        await _store.InstallAndStageAsync(archives.Select(a => new PluginArchiveSource(a.Archive, a.Sha256)).ToArray(), _selected, HostApi);
    }

    public void CancelPendingUpdate() => _store.RejectPending();
    public void RestoreBundledPlugins() => _store.ResetToBuiltin();

    public async Task SetEnabledNextStartAsync(string id, bool enabled)
    {
        await _preferenceGate.WaitAsync();
        try
        {
            if (_resolution == null || _preferences == null) throw new InvalidOperationException(_options.Text.FeaturesNotInitialized);
            var required = PluginEnablementStore.RequiredClosure(_resolution, id);
            var changed = enabled ? required : PluginEnablementStore.Blocked(_resolution, [id]);
            _disabled = _preferences.SetEnabled(_resolution, _disabledByDefault, changed, enabled);
            Log($"feature preference {id} enabled={enabled}; restart required; disabled=[{string.Join(",", _disabled)}]");
        }
        finally { _preferenceGate.Release(); }
    }

    /// <summary>Hides a plugin's entries; its instance and background work keep running.</summary>
    public async Task DisableAsync(string id)
    {
        await _preferenceGate.WaitAsync();
        try
        {
            if (_runtime == null || _resolution == null || _preferences == null) throw new InvalidOperationException(_options.Text.PluginsNotInitialized);
            _ = PluginEnablementStore.RequiredClosure(_resolution, id); // Validate identity before persisting.
            var changed = PluginEnablementStore.Blocked(_resolution, [id]);
            _disabled = _preferences.SetEnabled(_resolution, _disabledByDefault, changed, false);
            _registry.SetHiddenOwners(_disabled);
            Log($"feature hidden {id}; instances and background work retained; disabled=[{string.Join(",", _disabled)}]");
        }
        catch (Exception error) { Log("disable failed " + id + ": " + error); throw; }
        finally { _preferenceGate.Release(); }
    }

    public async Task EnableAsync(string id)
    {
        await _preferenceGate.WaitAsync();
        try
        {
            if (_runtime == null || _resolution == null || _preferences == null) throw new InvalidOperationException(_options.Text.PluginsNotInitialized);
            var required = PluginEnablementStore.RequiredClosure(_resolution, id);
            var needsRestart = _runtime.GetStatus().Any(s => required.Contains(s.Id) && s.State == PluginState.RestartRequired);
            if (!needsRestart) await _runtime.EnableAsync(id);
            _disabled = _preferences.SetEnabled(_resolution, _disabledByDefault, required, true);
            _registry.SetHiddenOwners(_disabled);
            Log($"enable {id}; restart_required={needsRestart}; saved disabled=[{string.Join(",", _disabled)}]");
        }
        catch (Exception error) { Log("enable failed " + id + ": " + error); throw; }
        finally { _preferenceGate.Release(); }
    }

    /// <summary>Stops plugins in reverse dependency order. Product work that calls into plugins must stop first.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_runtime == null) return;
        Log("shutdown begin");
        try
        {
            await _runtime.DisposeAsync();
            Log("shutdown end");
        }
        catch (Exception error)
        {
            Log("shutdown failed: " + error);
            throw;
        }
    }

    private void Log(string text) => _options.Log(text);
}
