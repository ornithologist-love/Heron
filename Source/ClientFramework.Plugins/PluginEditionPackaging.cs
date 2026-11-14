using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Heron.ClientFramework.Plugins;

public static class PluginEditionPackaging
{
    /// <summary>Writes only the pinned dependency closure to a fresh destination. Never deletes an existing payload.</summary>
    public static PluginEditionLock Copy(
        PluginEdition edition,
        IEnumerable<PluginPackage> packages,
        string destination,
        string hostApi = "1",
        string? hostDirectory = null,
        IPluginFileFilter? fileFilter = null)
    {
        edition = edition.ForPlatform(edition.TargetPlatform ?? PluginEdition.CurrentPlatform);
        var available = packages.ToArray();
        var locked = PluginEditionLock.Resolve(edition, available, hostApi);
        var root = Path.GetFullPath(destination);
        if (available.Any(p => root.Equals(p.Directory, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(p.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Edition output cannot be inside a source plugin.");
        var pluginRoot = Path.Combine(root, "plugins", "builtin");
        var hostRoot = hostDirectory == null ? root : Path.GetFullPath(hostDirectory);
        var policyPath = Path.Combine(hostRoot, "host-api.json");
        var policy = File.Exists(policyPath) ? PluginHostPolicy.Read(policyPath) : null;
        if (Directory.Exists(pluginRoot) && Directory.EnumerateFileSystemEntries(pluginRoot).Any())
            throw new InvalidOperationException("Plugin destination must be empty to prevent mixed editions.");
        foreach (var package in PluginVersionStore.Select(locked.Plugins, available))
        {
            var target = Path.Combine(pluginRoot, package.Manifest.Id, package.Manifest.Version);
            foreach (var file in Directory.EnumerateFiles(package.Directory, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Plugin source links are not supported.");
                var hostFile = Path.Combine(hostRoot, Path.GetFileName(file));
                if (Path.GetDirectoryName(file) == package.Directory && policy?.IsShared(file) == true && File.Exists(hostFile)
                    && CanUseHostFile(file, hostFile)) continue;
                var relative = Path.GetRelativePath(package.Directory, file);
                if (fileFilter?.Include(relative) == false) continue;
                if (policy != null && CanUseHostSatellite(file, relative, package.Directory, hostRoot, policy)) continue;
                var output = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(file, output, overwrite: false);
            }
        }
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "edition.json"), edition.ToJson());
        File.WriteAllText(Path.Combine(root, "plugins.lock.json"), JsonSerializer.Serialize(locked,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return locked;
    }

    private static bool CanUseHostSatellite(string file, string relative, string packageRoot, string hostRoot, PluginHostPolicy policy)
    {
        const string suffix = ".resources.dll";
        var name = Path.GetFileName(relative);
        var culture = Path.GetDirectoryName(relative);
        // Only culture/<shared assembly>.resources.dll belongs to a shared parent.
        // Other nested content and private plugin resources retain their own copies.
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(culture)
            || culture.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return false;
        var parentName = name[..^suffix.Length] + ".dll";
        var pluginParent = Path.Combine(packageRoot, parentName);
        var hostParent = Path.Combine(hostRoot, parentName);
        var hostSatellite = Path.Combine(hostRoot, relative);
        if (!policy.IsShared(parentName) || !File.Exists(pluginParent) || !File.Exists(hostParent)
            || !File.Exists(hostSatellite)) return false;
        var satellite = TryReadAssemblyName(file);
        if (satellite == null || !string.Equals(satellite.CultureName, culture, StringComparison.OrdinalIgnoreCase))
            return false;
        // ResourceManager uses the shared parent's load context to locate its satellite.
        // Keep resources that differ even when their assembly identities match.
        return CanUseHostFile(pluginParent, hostParent) && CanUseHostFile(file, hostSatellite)
            && SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(hostSatellite)));
    }

    private static bool CanUseHostFile(string pluginFile, string hostFile)
    {
        var pluginAssembly = TryReadAssemblyName(pluginFile);
        var hostAssembly = TryReadAssemblyName(hostFile);
        if (pluginAssembly != null || hostAssembly != null)
        {
            if (pluginAssembly == null || hostAssembly == null
                || !string.Equals(pluginAssembly.FullName, hostAssembly.FullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Shared assembly mismatch: plugin has {pluginAssembly?.FullName ?? Path.GetFileName(pluginFile)}, "
                    + $"host has {hostAssembly?.FullName ?? Path.GetFileName(hostFile)}.");
            return true;
        }

        return SHA256.HashData(File.ReadAllBytes(pluginFile)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(hostFile)));
    }

    private static AssemblyName? TryReadAssemblyName(string path)
    {
        try { return AssemblyName.GetAssemblyName(path); }
        catch (BadImageFormatException) { return null; }
        catch (FileLoadException) { return null; }
    }
}
