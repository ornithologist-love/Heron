using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;

namespace Heron.ClientFramework.Plugins;

public sealed record PluginHostPolicy(
    string HostApi,
    string[] SharedAssemblies,
    string[] SharedPrefixes,
    string[]? CompatiblePatchPrefixes = null,
    Dictionary<string, string>? CompatibleAssemblyVersions = null)
{
    public static PluginHostPolicy Read(string path) => JsonSerializer.Deserialize<PluginHostPolicy>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
        ?? throw new InvalidDataException("Host plugin policy is null.");
    public Assembly[] LoadSharedAssemblies(string directory) => LoadSharedAssemblies(directory, null);

    public Assembly[] LoadSharedAssemblies(string directory, string? hostAssemblyPath)
    {
        // Another executable (e.g. the bundled CLI) can place additional DLLs in
        // this directory. Only the host's dependency closure is shared by the host.
        var hostResolver = hostAssemblyPath == null ? null : new AssemblyDependencyResolver(hostAssemblyPath);
        var assemblies = new List<Assembly>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll").Where(IsShared))
        {
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(file); }
            catch (BadImageFormatException) { continue; } // Self-contained runtime also has System.* native DLLs.
            if (hostResolver != null && hostResolver.ResolveAssemblyToPath(name) == null) continue;
            assemblies.Add(Assembly.Load(name));
        }
        return assemblies.ToArray();
    }

    public bool IsShared(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        return SharedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase)
            || SharedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
