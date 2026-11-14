using System.Text.Json;
using System.Text.Json.Serialization;

namespace Heron.ClientFramework.Plugins;

public sealed record PluginModuleDescriptor(string Name, string Assembly, string EntryType, string Load = "startup");
public sealed record PluginDependency(string Id, string Version, bool Optional = false);

public sealed record PluginManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    public string HostApi { get; init; } = "";
    public string UpdateMode { get; init; } = "restart_required";
    public PluginModuleDescriptor[] Modules { get; init; } = [];
    public PluginDependency[] Dependencies { get; init; } = [];
}

/// <summary>Reads metadata without executing plugin code. Dependency solving belongs to activation, not discovery.</summary>
public sealed class PluginPackage
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string Directory { get; }
    public PluginManifest Manifest { get; }

    private PluginPackage(string directory, PluginManifest manifest)
    {
        Directory = directory;
        Manifest = manifest;
    }

    public static PluginPackage Read(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Json)
            ?? throw new InvalidDataException("Plugin manifest is null.");
        PluginDependencyGraph.Validate(manifest);
        if (manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Id)
            || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.HostApi)
            || manifest.Modules is not { Length: > 0 } || manifest.Dependencies is null)
            throw new InvalidDataException("Plugin manifest requires schema 1, identity, version, host_api and modules.");
        if (manifest.Modules.Any(m => m is null || string.IsNullOrWhiteSpace(m.Name)
                || string.IsNullOrWhiteSpace(m.EntryType))
            || manifest.Modules.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Modules.Length)
            throw new InvalidDataException("Plugin modules must have unique names and entry types.");
        var package = new PluginPackage(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest);
        foreach (var module in manifest.Modules)
            if (!File.Exists(package.ResolveFile(module.Assembly)))
                throw new FileNotFoundException($"Plugin module {module.Name} does not exist.");
        return package;
    }

    public string ResolveFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Plugin paths must be relative to the package.");
        var path = Path.GetFullPath(Path.Combine(Directory, relativePath));
        if (!path.StartsWith(Directory + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Plugin path escapes the package.");
        return path;
    }
}
