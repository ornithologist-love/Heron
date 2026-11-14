using System.Text.Json;
using System.Text.Json.Nodes;

namespace Heron.ClientFramework.Plugins;

public sealed record PluginEdition(string Name, string[] Plugins)
{
    public string[] ExternalPackages { get; init; } = [];
    // Legacy editions are Windows-only until a plugin is explicitly opted into another platform.
    public string[] PlatformAllowList { get; init; } = ["Win64"];
    public string[] PlatformDenyList { get; init; } = [];
    public Dictionary<string, PluginPlatformRule> PluginPlatforms { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PluginPlatformRule> ExternalPackagePlatforms { get; init; } = new(StringComparer.Ordinal);
    public string? TargetPlatform { get; init; }
    public Dictionary<string, string> PluginPackagePaths { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> PluginBuildInputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExternalPluginIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PluginEditionMetadata> PluginMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static string CurrentPlatform => OperatingSystem.IsWindows() ? "Win64"
        : OperatingSystem.IsMacOS() ? "Mac" : "Linux";

    public static string NormalizePlatform(string platform) => platform.ToLowerInvariant() switch
    {
        "win64" => "Win64", "mac" => "Mac", "linux" => "Linux",
        _ => throw new InvalidDataException($"Unknown target platform '{platform}'; use Win64, Mac or Linux.")
    };

    public bool SupportsPlugin(string id, string platform) => RuleForPlugin(id).Allows(NormalizePlatform(platform), PlatformAllowList, PlatformDenyList);
    private PluginPlatformRule RuleForPlugin(string id) => PluginPlatforms.GetValueOrDefault(id) ?? new();

    public PluginEdition ForPlatform(string platform)
    {
        platform = NormalizePlatform(platform);
        if (TargetPlatform != null && TargetPlatform != platform)
            throw new InvalidDataException($"Edition '{Name}' was resolved for {TargetPlatform}, not {platform}.");
        return this with
        {
            TargetPlatform = platform,
            Plugins = Plugins.Where(id => SupportsPlugin(id, platform)).ToArray(),
            ExternalPackages = ExternalPackages.Where(path =>
                (ExternalPackagePlatforms.GetValueOrDefault(path) ?? new()).Allows(platform, PlatformAllowList, PlatformDenyList)).ToArray()
        };
    }

    public PluginResolution Resolve(IEnumerable<PluginManifest> manifests, string hostApi)
    {
        var target = TargetPlatform ?? CurrentPlatform;
        var edition = ForPlatform(target);
        var catalog = manifests.ToArray();
        // Diagnose filtered required dependencies before graph resolution can silently pull them back in.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Check(string id, string? parent)
        {
            if (!SupportsPlugin(id, target))
                throw new InvalidDataException($"Edition '{Name}' target {target}: plugin '{parent ?? id}' requires plugin '{id}', excluded by platform rules.");
            if (!visited.Add(id)) return;
            foreach (var manifest in catalog.Where(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                foreach (var dependency in manifest.Dependencies.Where(d => !d.Optional)) Check(dependency.Id, id);
        }
        foreach (var id in edition.Plugins) Check(id, null);
        return PluginDependencyGraph.Resolve(catalog.Where(m => SupportsPlugin(m.Id, target)), edition.Plugins, hostApi);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public static PluginEdition Read(string path) => Read(path, new(StringComparer.OrdinalIgnoreCase));

    private static PluginEdition Read(string path, HashSet<string> visited)
    {
        var editionPath = Path.GetFullPath(path);
        if (!visited.Add(editionPath) || visited.Count > 8)
            throw new InvalidDataException("Edition reference cycle or excessive reference depth.");
        var node = JsonNode.Parse(File.ReadAllText(editionPath)) as JsonObject
            ?? throw new InvalidDataException("Edition must be an object.");
        if (node["edition_ref"] is { } reference)
        {
            if (node.Count != 1) throw new InvalidDataException("An Edition reference cannot override its source.");
            return Read(Path.GetFullPath(reference.GetValue<string>(), Path.GetDirectoryName(editionPath)!), visited);
        }
        if (node.ContainsKey("plugin_platforms") || node.ContainsKey("external_package_platforms"))
            throw new InvalidDataException("Platform rules belong in each plugins/external_packages item.");
        var packagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buildInputs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var externalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, PluginEditionMetadata>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PluginPlatformRule> ReadItems(string name, string key)
        {
            var rules = new Dictionary<string, PluginPlatformRule>(StringComparer.OrdinalIgnoreCase);
            if (node[name] is not JsonArray items) throw new InvalidDataException($"{name} must be an array.");
            var values = new JsonArray();
            foreach (var item in items)
            {
                string value;
                if (item is JsonObject entry)
                {
                    if (entry.Any(pair => pair.Key != key && pair.Key is not ("platform_allow_list" or "platform_deny_list" or "is_external_package" or "package_path" or "build_inputs" or "title" or "description" or "enabled_by_default")))
                        throw new InvalidDataException($"Invalid {name} item; expected {key} and platform lists.");
                    value = entry[key]?.GetValue<string>() ?? throw new InvalidDataException($"{name} item requires {key}.");
                    var ruleNode = (JsonObject)entry.DeepClone();
                    ruleNode.Remove(key);
                    var external = entry["is_external_package"]?.GetValue<bool>() ?? false;
                    var packagePath = entry["package_path"]?.GetValue<string>();
                    if (entry.ContainsKey("build_inputs"))
                    {
                        if (key != "id" || !external || entry["build_inputs"] is not JsonObject inputs)
                            throw new InvalidDataException("build_inputs requires an external plugin and an object of file paths.");
                        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var input in inputs)
                        {
                            if (!System.Text.RegularExpressions.Regex.IsMatch(input.Key, "^[a-z][a-z0-9_]*$")
                                || input.Value is not JsonValue inputValue || !inputValue.TryGetValue<string>(out var inputPath)
                                || string.IsNullOrWhiteSpace(inputPath))
                                throw new InvalidDataException($"Invalid build input for plugin '{value}'.");
                            paths.Add(input.Key, Path.GetFullPath(inputPath, Path.GetDirectoryName(editionPath)!));
                        }
                        buildInputs.Add(value, paths);
                    }
                    if (key == "id")
                    {
                        if (entry.ContainsKey("title") || entry.ContainsKey("description") || entry.ContainsKey("enabled_by_default"))
                        {
                            var title = entry["title"]?.GetValue<string>();
                            var description = entry["description"]?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description)
                                || entry["enabled_by_default"] is not JsonValue defaultValue || !defaultValue.TryGetValue<bool>(out var enabled))
                                throw new InvalidDataException($"Plugin '{value}' requires title, description and boolean enabled_by_default.");
                            metadata.Add(value, new(title, description, enabled));
                        }
                        if (!external && packagePath != null) throw new InvalidDataException($"Plugin '{value}' package_path requires is_external_package=true.");
                        if (external)
                        {
                            externalIds.Add(value);
                            if (string.IsNullOrWhiteSpace(packagePath))
                            {
                                if (node["target_platform"] == null) throw new InvalidDataException($"External plugin '{value}' requires package_path.");
                            }
                            else packagePaths.Add(value, packagePath);
                        }
                    }
                    else if (external || packagePath != null) throw new InvalidDataException("Legacy external_packages items only accept path and platform lists.");
                    ruleNode.Remove("is_external_package");
                    ruleNode.Remove("package_path");
                    ruleNode.Remove("build_inputs");
                    ruleNode.Remove("title");
                    ruleNode.Remove("description");
                    ruleNode.Remove("enabled_by_default");
                    var rule = ruleNode.Deserialize<PluginPlatformRule>(Json)!;
                    rule.Validate();
                    if (!rules.TryAdd(value, rule)) throw new InvalidDataException($"Duplicate {name} item '{value}'.");
                }
                else value = item?.GetValue<string>() ?? throw new InvalidDataException($"Invalid {name} item.");
                values.Add(value);
            }
            node[name] = values;
            return rules;
        }
        var pluginRules = ReadItems("plugins", "id");
        if (!node.ContainsKey("external_packages")) node["external_packages"] = new JsonArray();
        var externalRules = ReadItems("external_packages", "path");
        var edition = node.Deserialize<PluginEdition>(Json)! with
        {
            PluginPlatforms = pluginRules,
            ExternalPackagePlatforms = externalRules,
            PluginPackagePaths = packagePaths,
            PluginBuildInputs = buildInputs,
            ExternalPluginIds = externalIds,
            PluginMetadata = metadata
        };
        if (string.IsNullOrWhiteSpace(edition.Name) || edition.Plugins is null
            || !System.Text.RegularExpressions.Regex.IsMatch(edition.Name, @"^[A-Za-z][A-Za-z0-9.-]*$")
            || edition.Plugins.Any(string.IsNullOrWhiteSpace)
            || edition.Plugins.Distinct(StringComparer.OrdinalIgnoreCase).Count() != edition.Plugins.Length)
            throw new InvalidDataException("Edition requires a name and unique plugin IDs.");
        if (edition.ExternalPackages is null || edition.ExternalPackages.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"Edition external_packages must contain non-empty package directories: {editionPath}");
        PluginPlatformRule.Validate(edition.PlatformAllowList, "platform_allow_list");
        PluginPlatformRule.Validate(edition.PlatformDenyList, "platform_deny_list");
        if (edition.PluginPlatforms is null || edition.ExternalPackagePlatforms is null)
            throw new InvalidDataException("Platform rules must be objects.");
        foreach (var rule in edition.PluginPlatforms.Concat(edition.ExternalPackagePlatforms))
        {
            if (string.IsNullOrWhiteSpace(rule.Key) || rule.Value is null) throw new InvalidDataException("Invalid platform rule.");
            rule.Value.Validate();
        }
        if (edition.TargetPlatform != null) NormalizePlatform(edition.TargetPlatform);
        var directory = Path.GetDirectoryName(editionPath)!;
        foreach (var pair in packagePaths)
            externalRules[Path.GetFullPath(pair.Value, directory)] = pluginRules.GetValueOrDefault(pair.Key) ?? new();
        return edition with
        {
            ExternalPackages = edition.ExternalPackages.Concat(packagePaths.Values).Select(package => Path.GetFullPath(package, directory)).Distinct().ToArray(),
            PluginPackagePaths = packagePaths.ToDictionary(pair => pair.Key, pair => Path.GetFullPath(pair.Value, directory), StringComparer.OrdinalIgnoreCase),
            PluginPlatforms = new(edition.PluginPlatforms, StringComparer.OrdinalIgnoreCase),
            ExternalPackagePlatforms = edition.ExternalPackagePlatforms.ToDictionary(
                pair => Path.GetFullPath(pair.Key, directory), pair => pair.Value,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        };
    }

    public string[] GetExternalPackageDirectories(IEnumerable<string>? additionalPackages = null) =>
        ForPlatform(TargetPlatform ?? CurrentPlatform).ExternalPackages.Concat((additionalPackages ?? []).Where(path =>
            (ExternalPackagePlatforms.GetValueOrDefault(Path.GetFullPath(path)) ?? new()).Allows(
                TargetPlatform ?? CurrentPlatform, PlatformAllowList, PlatformDenyList)))
            .Select(package => Path.TrimEndingDirectorySeparator(Path.GetFullPath(package)))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();

    // Package locations are build inputs, not paths required on the installed user's machine.
    public string ToJson() => Serialize(includeBuildInputs: false);
    // Release merging must retain item rules and resolved build inputs.
    public string ToBuildJson() => Serialize(includeBuildInputs: true);

    private string Serialize(bool includeBuildInputs)
    {
        JsonArray Items(IEnumerable<string> values, string key, Dictionary<string, PluginPlatformRule> rules)
        {
            var items = new JsonArray();
            foreach (var value in values)
            {
                var item = JsonSerializer.SerializeToNode(rules.GetValueOrDefault(value) ?? new(), Json)!.AsObject();
                item[key] = value;
                if (key == "id")
                {
                    if (PluginMetadata.TryGetValue(value, out var metadata))
                    {
                        item["title"] = metadata.Title;
                        item["description"] = metadata.Description;
                        item["enabled_by_default"] = metadata.EnabledByDefault;
                    }
                    if (ExternalPluginIds.Contains(value)) item["is_external_package"] = true;
                    if (includeBuildInputs && PluginPackagePaths.TryGetValue(value, out var path)) item["package_path"] = path;
                    if (includeBuildInputs && PluginBuildInputs.TryGetValue(value, out var inputs))
                        item["build_inputs"] = JsonSerializer.SerializeToNode(inputs);
                }
                if (item["platform_allow_list"] is null) item.Remove("platform_allow_list");
                if (item["platform_deny_list"] is null) item.Remove("platform_deny_list");
                items.Add(item);
            }
            return items;
        }
        var node = new JsonObject
        {
            ["name"] = Name,
            ["platform_allow_list"] = JsonSerializer.SerializeToNode(PlatformAllowList),
            ["platform_deny_list"] = JsonSerializer.SerializeToNode(PlatformDenyList),
            ["plugins"] = Items(Plugins, "id", PluginPlatforms)
        };
        if (TargetPlatform != null) node["target_platform"] = TargetPlatform;
        if (includeBuildInputs)
        {
            var unbound = ExternalPackages.Except(PluginPackagePaths.Values).ToArray();
            if (unbound.Length != 0) node["external_packages"] = Items(unbound, "path", ExternalPackagePlatforms);
        }
        return node.ToJsonString(Json);
    }
}

public sealed record PluginEditionMetadata(string Title, string Description, bool EnabledByDefault);

public sealed record PluginLockEntry(string Id, string Version);
public sealed record PluginEditionLock(string Edition, string HostApi, PluginLockEntry[] Plugins)
{
    public static PluginEditionLock Resolve(PluginEdition edition, IEnumerable<PluginPackage> packages, string hostApi)
    {
        var version = PluginHostApiVersion.Parse(hostApi).ToString();
        var resolution = edition.Resolve(packages.Select(p => p.Manifest), version);
        return new(edition.Name, version, resolution.LoadOrder.Select(p => new PluginLockEntry(p.Id, p.Version)).ToArray());
    }
}

public sealed record PluginPlatformRule
{
    // null inherits the edition default; [] explicitly allows no platforms.
    public string[]? PlatformAllowList { get; init; }
    public string[]? PlatformDenyList { get; init; }

    internal bool Allows(string platform, string[] defaultAllow, string[] defaultDeny) =>
        (PlatformAllowList ?? defaultAllow).Contains(platform, StringComparer.OrdinalIgnoreCase)
        && !(PlatformDenyList ?? defaultDeny).Contains(platform, StringComparer.OrdinalIgnoreCase);

    internal void Validate()
    {
        if (PlatformAllowList != null) Validate(PlatformAllowList, "platform_allow_list");
        if (PlatformDenyList != null) Validate(PlatformDenyList, "platform_deny_list");
    }

    internal static void Validate(string[] platforms, string name)
    {
        if (platforms is null || platforms.Any(string.IsNullOrWhiteSpace)
            || platforms.Distinct(StringComparer.OrdinalIgnoreCase).Count() != platforms.Length)
            throw new InvalidDataException($"{name} must contain unique platform names.");
        foreach (var platform in platforms) PluginEdition.NormalizePlatform(platform);
    }
}
