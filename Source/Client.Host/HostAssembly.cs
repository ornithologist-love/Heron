using Heron.ClientFramework.Plugins;

namespace Heron.ClientHost;

/// <summary>
///     Assembles the plugins named on the command line into the launcher directory before the host starts.
///     <c>--plugins</c> points at package folders (a folder holding plugin.json) or at roots searched for them;
///     <c>--edition</c> chooses and orders plugins and adds its external package paths. Without an edition every
///     found package is enabled. A plugin the edition names but no package provides fails the start.
/// </summary>
internal static class HostAssembly
{
    public static void Run(HostOptions options, string hostDirectory)
    {
        var packages = options.PluginDirectories.SelectMany(Find).ToList();
        PluginEdition edition;
        if (options.Edition != null)
        {
            edition = PluginEdition.Read(options.Edition);
            packages.AddRange(edition.GetExternalPackageDirectories().SelectMany(Find));
        }
        else edition = new PluginEdition("host", packages.Select(p => p.Manifest.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var hostApi = PluginHostPolicy.Read(Path.Combine(hostDirectory, "host-api.json")).HostApi;
        var locked = PluginEditionAssembler.Assemble(edition, Distinct(packages), hostDirectory, hostApi: hostApi);
        Console.WriteLine($"[assemble] edition={locked.Edition} plugins=[{string.Join(", ", locked.Plugins.Select(p => p.Id + "@" + p.Version))}]");
    }

    private static IEnumerable<PluginPackage> Find(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Plugin directory not found: " + directory);
        var direct = Path.Combine(directory, "plugin.json");
        return File.Exists(direct)
            ? [PluginPackage.Read(direct)]
            : Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories).Select(PluginPackage.Read);
    }

    private static IEnumerable<PluginPackage> Distinct(IEnumerable<PluginPackage> packages) => packages
        .GroupBy(p => Path.GetFullPath(p.Directory), StringComparer.OrdinalIgnoreCase).Select(g => g.First());
}
