using Heron.ClientFramework.Abstractions;
using System.Reflection;
using Heron.ClientFramework.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Microsoft.Extensions.Logging;

namespace Heron.ClientFramework.Runtime;

/// <summary>One private provider per package. Imported instances are registered as instances, never owned factories.</summary>
public sealed class ClientPluginInstance : IPluginInstance, IClientPluginContext
{
    private readonly PluginPackage _package;
    private readonly Assembly[] _shared;
    private readonly string[] _compatiblePatchPrefixes;
    private readonly IReadOnlyDictionary<string, string>? _compatibleAssemblyVersions;
    private readonly ClientPluginRegistry _registry;
    private readonly IReadOnlyDictionary<Type, object> _imports;
    private readonly Action<string>? _log;
    private readonly List<IClientPluginModule> _modules = [];
    private readonly List<IDisposable> _registrations = [];
    private PluginAssemblySession? _assembly;
    private ServiceProvider? _provider;
    private int _started;
    private bool _accepting;
    public WeakReference? UnloadReference => _assembly?.UnloadReference;
    public IServiceProvider Services => _provider ?? throw new InvalidOperationException("Plugin services are not ready.");

    public ClientPluginInstance(PluginPackage package, IEnumerable<Assembly> shared, ClientPluginRegistry registry,
        IReadOnlyDictionary<Type, object>? imports = null, Action<string>? log = null,
        IEnumerable<string>? compatiblePatchPrefixes = null,
        IReadOnlyDictionary<string, string>? compatibleAssemblyVersions = null)
    {
        _package = package;
        _shared = shared.Append(typeof(IClientPluginModule).Assembly).Append(typeof(IServiceCollection).Assembly).Distinct().ToArray();
        _registry = registry;
        _imports = imports ?? new Dictionary<Type, object>();
        _log = log;
        _compatiblePatchPrefixes = compatiblePatchPrefixes?.ToArray() ?? [];
        _compatibleAssemblyVersions = compatibleAssemblyVersions;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_assembly != null) throw new InvalidOperationException("Instance already started.");
        _assembly = new PluginAssemblySession(_package, _shared, _compatiblePatchPrefixes, _compatibleAssemblyVersions);
        var services = new ServiceCollection();
        // Every plugin gets its own ILogger<T> wired into the same host-wide Serilog pipeline
        // (bounded file segments for the process session, see ClientLogging.Initialize in
        // Main). T identifies the logging category, and the plugin author
        // never sees a file path, a lock, or a rotation policy.
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddSerilog(
            Log.Logger.ForContext("PluginId", _package.Manifest.Id)
                .ForContext("PluginVersion", _package.Manifest.Version.ToString()), dispose: false));
        foreach (var descriptor in _package.Manifest.Modules)
        {
            var module = _assembly.Create<IClientPluginModule>(descriptor.Name);
            _modules.Add(module);
            module.ConfigureServices(services);
        }
        foreach (var (type, instance) in _imports)
        {
            if (services.Any(s => s.ServiceType == type)) throw new InvalidOperationException($"Plugin overrides imported service '{type}'.");
            services.AddSingleton(type, instance);
        }
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _accepting = true;
        foreach (var module in _modules)
        {
            _started++; // Stop is also required for a partially failed Start.
            await module.StartAsync(this, cancellationToken);
        }
    }

    public IDisposable RegisterPage(ClientPluginPage page)
    {
        if (!_accepting) throw new InvalidOperationException("Plugin is stopping.");
        var registration = _registry.RegisterPage(_package.Manifest.Id, page);
        _registrations.Add(registration);
        return registration;
    }
    public IDisposable RegisterCommand(ClientPluginCommand command)
    {
        if (!_accepting) throw new InvalidOperationException("Plugin is stopping.");
        var registration = _registry.RegisterCommand(_package.Manifest.Id, command);
        _registrations.Add(registration);
        return registration;
    }

    public void Export<T>(T service) where T : class
    {
        if (!_accepting) throw new InvalidOperationException("Plugin is stopping.");
        if (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(T).Assembly)?.IsCollectible == true)
            throw new InvalidOperationException("Export contracts must belong to the shared host API.");
        _registrations.Add(_registry.Export(_package.Manifest.Id, _package.Manifest.Version, service));
    }
    public IDisposable RegisterStatus(ClientPluginStatus status)
    {
        if (!_accepting) throw new InvalidOperationException("Plugin is stopping.");
        var registration = _registry.RegisterStatus(_package.Manifest.Id, status);
        _registrations.Add(registration);
        return registration;
    }
    public IPluginService<T> ImportOptional<T>(string pluginId) where T : class
    {
        var dependency = _package.Manifest.Dependencies.SingleOrDefault(d => d.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Undeclared dependency '{pluginId}'.");
        return _registry.ImportOptional<T>(pluginId, dependency.Version);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _accepting = false;
        foreach (var registration in _registrations.AsEnumerable().Reverse()) registration.Dispose();
        _registrations.Clear();
        while (_started > 0)
        {
            var module = _modules[_started - 1];
            _log?.Invoke($"stopping {_package.Manifest.Id} module={module.GetType().FullName}");
            try
            {
                await module.StopAsync(cancellationToken);
                _log?.Invoke($"stopped {_package.Manifest.Id} module={module.GetType().FullName}");
            }
            catch (Exception error)
            {
                _log?.Invoke($"stop failed {_package.Manifest.Id} module={module.GetType().FullName}: {error}");
                throw;
            }
            _started--;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started != 0) throw new InvalidOperationException("Stop modules before disposing their services.");
        if (_provider != null)
        {
            _log?.Invoke($"disposing provider {_package.Manifest.Id}");
            try
            {
                await _provider.DisposeAsync();
                _log?.Invoke($"disposed provider {_package.Manifest.Id}");
            }
            catch (Exception error)
            {
                _log?.Invoke($"provider dispose failed {_package.Manifest.Id}: {error}");
                throw;
            }
        }
        _provider = null;
        _modules.Clear();
        _assembly?.Dispose();
        _assembly = null;
    }
}
