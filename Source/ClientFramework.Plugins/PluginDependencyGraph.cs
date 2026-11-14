namespace Heron.ClientFramework.Plugins;

public sealed record PluginResolution(IReadOnlyList<PluginManifest> LoadOrder,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies);

/// <summary>Resolves one pinned version per plugin. Optional dependencies never pull features into an edition.</summary>
public static class PluginDependencyGraph
{
    public static PluginResolution Resolve(IEnumerable<PluginManifest> manifests, IEnumerable<string> roots, string hostApi)
    {
        var catalog = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            Validate(manifest);
            if (!catalog.TryAdd(manifest.Id, manifest))
                throw new InvalidDataException($"Duplicate plugin '{manifest.Id}'; select a single pinned version.");
        }
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Select(string id)
        {
            if (!catalog.TryGetValue(id, out var manifest)) throw new InvalidDataException($"Required plugin '{id}' is missing.");
            if (!selected.Add(id)) return;
            if (manifest.HostApi != hostApi) throw new InvalidDataException($"Plugin '{id}' requires host_api {manifest.HostApi}, host provides {hostApi}.");
            foreach (var dependency in manifest.Dependencies.Where(d => !d.Optional))
            {
                Select(dependency.Id);
                if (!Compatible(dependency)) throw new InvalidDataException($"Plugin '{id}' requires '{dependency.Id}' {dependency.Version}, found {catalog[dependency.Id].Version}.");
            }
        }
        bool Compatible(PluginDependency dependency) => catalog.TryGetValue(dependency.Id, out var target)
            && PluginVersionRange.Parse(dependency.Version).Contains(PluginVersion.Parse(target.Version));
        foreach (var root in roots) Select(root);
        var edges = selected.ToDictionary(id => id, id => (IReadOnlyList<string>)catalog[id].Dependencies
            .Where(d => selected.Contains(d.Id) && Compatible(d)).Select(d => d.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var order = new List<PluginManifest>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException($"Plugin dependency cycle: {string.Join(" -> ", path.Append(id))}.");
            path.Add(id);
            foreach (var dependency in edges[id]) Visit(dependency);
            path.RemoveAt(path.Count - 1);
            visiting.Remove(id);
            visited.Add(id);
            order.Add(catalog[id]);
        }
        foreach (var id in selected.Order(StringComparer.OrdinalIgnoreCase)) Visit(id);
        return new(order.AsReadOnly(), edges);
    }

    public static void Validate(PluginManifest manifest)
    {
        static bool ValidId(string? id) => id is not null && System.Text.RegularExpressions.Regex.IsMatch(id, @"^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$");
        if (manifest.SchemaVersion != 1 || !ValidId(manifest.Id) || string.IsNullOrWhiteSpace(manifest.HostApi))
            throw new InvalidDataException("Invalid plugin schema, id or host_api.");
        PluginVersion.Parse(manifest.Version);
        if (manifest.UpdateMode is not ("restart_required" or "hot_reload")) throw new InvalidDataException($"Invalid update_mode for '{manifest.Id}'.");
        if (manifest.Modules is not { Length: > 0 } || manifest.Modules.Any(m => m is null
                || string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.EntryType)
                || string.IsNullOrWhiteSpace(m.Assembly) || m.Load is not ("startup" or "on_demand"))
            || manifest.Modules.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Modules.Length)
            throw new InvalidDataException($"Invalid modules for '{manifest.Id}'.");
        if (manifest.Dependencies is null || manifest.Dependencies.Any(d => d is null || !ValidId(d.Id))
            || manifest.Dependencies.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Dependencies.Length)
            throw new InvalidDataException($"Invalid dependencies for '{manifest.Id}'.");
        foreach (var dependency in manifest.Dependencies) PluginVersionRange.Parse(dependency.Version);
    }
}
