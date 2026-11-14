using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Heron.ClientFramework.Plugins;

/// <summary>A loading primitive, not a lifecycle manager. Callers must stop/dispose instances before requesting unload.</summary>
public sealed class PluginAssemblySession : IDisposable
{
    private PluginLoadContext? _context;
    private readonly PluginPackage _package;

    public PluginAssemblySession(
        PluginPackage package,
        IEnumerable<Assembly> sharedAssemblies,
        IEnumerable<string>? compatiblePatchPrefixes = null,
        IReadOnlyDictionary<string, string>? compatibleAssemblyVersions = null)
    {
        _package = package;
        _context = new PluginLoadContext(package, sharedAssemblies, compatiblePatchPrefixes, compatibleAssemblyVersions);
        UnloadReference = new WeakReference(_context);
    }

    /// <summary>Diagnostic observation only. A live reference after Dispose means collection has not completed.</summary>
    public WeakReference UnloadReference { get; }

    public T Create<T>(string moduleName) where T : class
    {
        var context = _context ?? throw new ObjectDisposedException(nameof(PluginAssemblySession));
        var module = _package.Manifest.Modules.Single(m => m.Name == moduleName);
        var assembly = context.LoadFromAssemblyPath(_package.ResolveFile(module.Assembly));
        var type = assembly.GetType(module.EntryType, throwOnError: true)!;
        if (!typeof(T).IsAssignableFrom(type))
            throw new InvalidCastException($"{module.EntryType} does not implement the shared contract {typeof(T).FullName}.");
        return (T)Activator.CreateInstance(type)!;
    }

    public void Dispose() => Interlocked.Exchange(ref _context, null)?.Unload();

    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, Assembly> _shared;
        private readonly AssemblyDependencyResolver[] _resolvers;
        private readonly string[] _compatiblePatchPrefixes;
        private readonly IReadOnlyDictionary<string, string> _compatibleAssemblyVersions;
        private static readonly HashSet<string> PlatformAssemblies = GetPlatformAssemblies();

        public PluginLoadContext(
            PluginPackage package,
            IEnumerable<Assembly> shared,
            IEnumerable<string>? compatiblePatchPrefixes,
            IReadOnlyDictionary<string, string>? compatibleAssemblyVersions) : base(package.Manifest.Id, isCollectible: true)
        {
            _shared = shared.Distinct().ToDictionary(a => a.GetName().Name!, StringComparer.OrdinalIgnoreCase);
            _resolvers = package.Manifest.Modules.Select(m => new AssemblyDependencyResolver(package.ResolveFile(m.Assembly))).ToArray();
            _compatiblePatchPrefixes = compatiblePatchPrefixes?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            _compatibleAssemblyVersions = compatibleAssemblyVersions ?? new Dictionary<string, string>();
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // Framework assemblies unify to the host runtime: a plugin compiled against an
            // older target framework (e.g. net6.0) references System.Runtime 6.0 while the
            // host runs 8.0, and deferring to the default context mirrors plain .NET unification.
            // A plugin may carry a newer out-of-band build of a framework library (e.g. a 9.0 package on an 8.0
            // runtime); only then its private copy wins, so the plugin does not wait for a host upgrade.
            if (PlatformAssemblies.Contains(name.Name!))
            {
                if (!PrefersPrivateFramework(name.Version, HostFrameworkVersion(name.Name!))) return null;
                foreach (var resolver in _resolvers)
                    if (resolver.ResolveAssemblyToPath(name) is { } privateFramework) return LoadFromAssemblyPath(privateFramework);
                return null;
            }
            if (_shared.TryGetValue(name.Name!, out var shared))
            {
                // Contracts remain exact. Selected framework families may opt into forward-only patch
                // unification so a plugin compiled against 11.3.9 can use the host's compatible 11.3.12.
                if (!string.Equals(shared.FullName, name.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    var hostName = shared.GetName();
                    if (!CanUnifyPatch(name, hostName, _compatiblePatchPrefixes)
                        && !CanUnifyDeclaredVersion(name, hostName, _compatibleAssemblyVersions))
                        throw new FileLoadException($"Shared assembly mismatch: requested {name.FullName}, host has {shared.FullName}.");
                }
                return shared;
            }
            foreach (var resolver in _resolvers)
            {
                var path = resolver.ResolveAssemblyToPath(name);
                if (path != null) return LoadFromAssemblyPath(path);
            }
            // Do not silently pick up an unrelated implementation already loaded by the host.
            throw new FileNotFoundException($"Private plugin dependency is missing: {name.FullName}");
        }

        internal static bool PrefersPrivateFramework(Version? requested, Version? host) =>
            requested != null && host != null && requested > host;

        private static readonly Dictionary<string, Version?> HostFrameworkVersions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Version the default context would load: the host's trusted platform assembly of that name.</summary>
        private static Version? HostFrameworkVersion(string name)
        {
            lock (HostFrameworkVersions)
            {
                if (HostFrameworkVersions.TryGetValue(name, out var cached)) return cached;
                var path = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator)
                    .FirstOrDefault(file => string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase));
                Version? version = null;
                try { if (path != null) version = AssemblyName.GetAssemblyName(path).Version; }
                catch (Exception error) when (error is IOException or BadImageFormatException) { }
                return HostFrameworkVersions[name] = version;
            }
        }

        // Exact, reviewed framework pairs include the assembly name, version, culture and token.
        // This does not permit another minor version or relax plugin contracts.
        internal static bool CanUnifyDeclaredVersion(AssemblyName requested, AssemblyName host,
            IReadOnlyDictionary<string, string> pairs) =>
            requested.Name == host.Name && requested.Version?.Major == host.Version?.Major &&
            requested.Version != null && host.Version >= requested.Version &&
            requested.CultureName == host.CultureName &&
            requested.GetPublicKeyToken().AsSpan().SequenceEqual(host.GetPublicKeyToken()) &&
            pairs.TryGetValue(requested.FullName, out var expectedHost) &&
            string.Equals(expectedHost, host.FullName, StringComparison.OrdinalIgnoreCase);

        internal static bool CanUnifyPatch(
            AssemblyName requested,
            AssemblyName host,
            IEnumerable<string> compatiblePatchPrefixes)
        {
            if (requested.Name == null || host.Name == null
                || !string.Equals(requested.Name, host.Name, StringComparison.OrdinalIgnoreCase)
                || !compatiblePatchPrefixes.Any(prefix => requested.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                return false;

            var requestedVersion = requested.Version;
            var hostVersion = host.Version;
            if (requestedVersion == null || hostVersion == null
                || requestedVersion.Major != hostVersion.Major
                || requestedVersion.Minor != hostVersion.Minor
                || hostVersion < requestedVersion)
                return false;

            if (!string.Equals(requested.CultureName ?? string.Empty, host.CultureName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return requested.GetPublicKeyToken().AsSpan().SequenceEqual(host.GetPublicKeyToken());
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            foreach (var resolver in _resolvers)
            {
                var path = resolver.ResolveUnmanagedDllToPath(name);
                if (path != null) return LoadUnmanagedDllFromPath(path);
            }
            return 0; // Normal OS resolution for system libraries.
        }

        private static HashSet<string> GetPlatformAssemblies()
        {
            // In self-contained apps the runtime directory also contains host libraries.
            // Read the runtime pack inventory instead of treating every adjacent DLL as a framework assembly.
            var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { typeof(object).Assembly.GetName().Name! };
            foreach (var file in System.IO.Directory.EnumerateFiles(runtime, "*.deps.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (!document.RootElement.TryGetProperty("targets", out var targets)) continue;
                foreach (var target in targets.EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!library.Name.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal)
                        && !library.Name.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal)) continue;
                    if (!library.Value.TryGetProperty("runtime", out var assets)) continue;
                    foreach (var asset in assets.EnumerateObject())
                        if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            names.Add(Path.GetFileNameWithoutExtension(asset.Name));
                }
            }
            return names;
        }
    }
}
