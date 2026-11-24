using System.Text.Json;

namespace Heron.ClientFramework.Plugins;

public sealed record ClientProductAssemblyCliProfile
{
    public string DiagnosticSource { get; init; } = "Heron.Client.Build";
    public string DiagnosticCode { get; init; } = "HRC001";
    public string SourcePluginsRelativePath { get; init; } = Path.Combine("Source", "Client", "Plugins");
    public string HostApiRelativePath { get; init; } = Path.Combine("Source", "Client", "Client", "host-api.json");
    public required string WindowsHostExecutable { get; init; }
    public required string UnixHostExecutable { get; init; }
    public Func<string, string?, IPluginFileFilter?>? FileFilterFactory { get; init; }
    public Func<string, MacDevelopmentBundleOptions>? MacBundleOptionsFactory { get; init; }
}

/// <summary>Generic MSBuild-friendly CLI entrypoint for assembling host + plugin editions.</summary>
public static class ClientProductAssemblyCli
{
    public static int Run(string[] args, ClientProductAssemblyCliProfile profile)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            var options = ParseOptions(args);
            var repository = Path.GetFullPath(Required(options, "--repository"));
            var edition = PluginEdition.Read(Path.GetFullPath(Required(options, "--edition"))).ForPlatform(
                NormalizeTargetPlatform(options.GetValueOrDefault("--target-platform", [PluginEdition.CurrentPlatform]).Single()));
            var configuration = Required(options, "--configuration");
            var output = Path.GetFullPath(Required(options, "--output"));

            if (configuration is not ("Debug" or "Release"))
                throw new ArgumentException("Configuration must be Debug or Release.");

            var external = edition.GetExternalPackageDirectories(options.GetValueOrDefault("--plugin-package", []))
                .Select(path => PluginPackage.Read(Path.Combine(Path.GetFullPath(path), "plugin.json"))).ToArray();
            var externalIds = external.Select(package => package.Manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sourcePluginsRoot = Path.Combine(repository, profile.SourcePluginsRelativePath);
            var sourceDirectories = (options.ContainsKey("--host") && options.ContainsKey("--plugin-package"))
                || edition.Plugins.Length == 0
                || !Directory.Exists(sourcePluginsRoot)
                ? Enumerable.Empty<string>()
                : Directory.EnumerateDirectories(sourcePluginsRoot)
                    .Where(path =>
                    {
                        var manifestPath = Path.Combine(path, "bin", configuration, "net8.0", "plugin.json");
                        if (!File.Exists(manifestPath)) return false;
                        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath),
                            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
                        return !externalIds.Contains(manifest.Id) && edition.SupportsPlugin(manifest.Id, edition.TargetPlatform!);
                    });

            var packages = sourceDirectories
                .Select(path => Path.Combine(path, "bin", configuration, "net8.0", "plugin.json"))
                .Where(File.Exists)
                .Select(PluginPackage.Read)
                .Where(package => !externalIds.Contains(package.Manifest.Id))
                .Concat(external)
                .ToArray();

            var fileFilter = profile.FileFilterFactory?.Invoke(configuration, edition.TargetPlatform)
                ?? (configuration == "Debug" ? PlatformRuntimeFileFilter.ForPlatform(edition.TargetPlatform) : null);

            var locked = options.ContainsKey("--host")
                ? ClientProductAssembler.Assemble(
                    edition,
                    Required(options, "--host"),
                    packages,
                    output,
                    configuration == "Debug",
                    fileFilter,
                    OperatingSystem.IsWindows() ? profile.WindowsHostExecutable : profile.UnixHostExecutable)
                : PluginEditionAssemblyCache.Assemble(
                    edition,
                    packages,
                    output,
                    fileFilter: fileFilter,
                    hostApi: PluginHostPolicy.Read(Path.Combine(repository, profile.HostApiRelativePath)).HostApi);

            if (profile.MacBundleOptionsFactory != null)
                MacDevelopmentBundle.Ensure(output, profile.MacBundleOptionsFactory(repository));

            Console.WriteLine(JsonSerializer.Serialize(locked, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true,
            }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"{profile.DiagnosticSource} : error {profile.DiagnosticCode}: {error.GetType().Name}: {error.Message.ReplaceLineEndings(" ")}");
            return 1;
        }
    }

    public static string NormalizeTargetPlatform(string value) =>
        value.StartsWith("win-", StringComparison.Ordinal) ? "Win64"
        : value.StartsWith("osx-", StringComparison.Ordinal) ? "Mac"
        : value.StartsWith("linux-", StringComparison.Ordinal) ? "Linux"
        : PluginEdition.NormalizePlatform(value);

    public static Dictionary<string, List<string>> ParseOptions(string[] arguments)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || arguments[index] is not
                ("--repository" or "--edition" or "--configuration" or "--output" or "--host" or "--plugin-package" or "--target-platform"))
                throw new ArgumentException("Invalid Client build option.");

            if (!result.TryGetValue(arguments[index], out var values))
                result[arguments[index]] = values = [];
            if (arguments[index] != "--plugin-package" && values.Count != 0)
                throw new ArgumentException($"Duplicate option: {arguments[index]}.");
            values.Add(arguments[index + 1]);
        }

        return result;
    }

    private static string Required(IReadOnlyDictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) && values.Count == 1
            ? values[0]
            : throw new ArgumentException($"Missing {name}.");
}
