using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heron.ClientFramework.Plugins;

/// <summary>Content validation shared by the product build and CLI external-package assembly.</summary>
public static class PluginEditionAssemblyCache
{
    private sealed record Stamp(string Input, string Output);

    public static PluginEditionLock Assemble(
        PluginEdition edition,
        IEnumerable<PluginPackage> packages,
        string destination,
        IPluginFileFilter? fileFilter = null,
        string hostApi = "1")
    {
        var root = Path.GetFullPath(destination);
        var available = packages.ToArray();
        var locked = PluginEditionLock.Resolve(edition, available, hostApi);
        var selected = PluginVersionStore.Select(locked.Plugins, available).ToArray();
        var stampPath = Path.Combine(root, ".plugin-assembly.fingerprint");
        var input = InputDigest(edition, selected, root, fileFilter, hostApi);
        try
        {
            if (File.Exists(stampPath)
                && JsonSerializer.Deserialize<Stamp>(File.ReadAllText(stampPath)) is { } stamp
                && stamp.Input == input && stamp.Output == OutputDigest(root))
            {
                foreach (var file in new[] { stampPath, Path.Combine(root, "edition.json"), Path.Combine(root, "plugins.lock.json") })
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
                Console.WriteLine($"Client plugin assembly is up-to-date: {root}");
                return locked;
            }
        }
        catch (JsonException) { /* Older or incomplete cache: assemble normally. */ }
        catch (FileNotFoundException) { /* An output was removed: assemble normally. */ }
        catch (DirectoryNotFoundException) { /* An output was removed: assemble normally. */ }

        // The assembler owns staging and rollback. Publish a cache record only after successful commit.
        locked = PluginEditionAssembler.Assemble(edition, selected, root, fileFilter, hostApi);
        var temporary = stampPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Stamp(input, OutputDigest(root))));
            File.Move(temporary, stampPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return locked;
    }

    private static string InputDigest(PluginEdition edition, PluginPackage[] packages, string hostRoot, IPluginFileFilter? fileFilter, string hostApi)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, "plugin-assembly-v3:" + hostApi + ":" + fileFilter?.Fingerprint);
        AppendText(hash, edition.ToJson());
        AppendFile(hash, "assembler", typeof(PluginEditionAssembler).Assembly.Location);
        AppendOptionalFile(hash, "host-api", Path.Combine(hostRoot, "host-api.json"));
        var hostFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.OrderBy(package => package.Manifest.Id, StringComparer.Ordinal))
        {
            AppendText(hash, package.Manifest.Id);
            AppendText(hash, package.Manifest.Version);
            foreach (var file in Files(package.Directory))
            {
                var relative = Path.GetRelativePath(package.Directory, file);
                if (fileFilter?.Include(relative) == false) continue;
                AppendFile(hash, relative.Replace('\\', '/'), file);
                // Sharing decisions depend on host assemblies and culture satellites, including their absence.
                if (Path.GetDirectoryName(file) == package.Directory || relative.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
                    hostFiles.Add(relative);
            }
        }
        foreach (var relative in hostFiles.Order(StringComparer.Ordinal))
            AppendOptionalFile(hash, "host/" + relative.Replace('\\', '/'), Path.Combine(hostRoot, relative));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string OutputDigest(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFile(hash, "edition.json", Path.Combine(root, "edition.json"));
        AppendFile(hash, "plugins.lock.json", Path.Combine(root, "plugins.lock.json"));
        var builtin = Path.Combine(root, "plugins", "builtin");
        AppendText(hash, Directory.Exists(builtin) ? "plugins-present" : "plugins-absent");
        if (Directory.Exists(builtin))
            foreach (var file in Files(builtin))
                AppendFile(hash, Path.GetRelativePath(root, file).Replace('\\', '/'), file);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> Files(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    private static void AppendOptionalFile(IncrementalHash hash, string label, string file)
    {
        AppendText(hash, File.Exists(file) ? "present" : "absent");
        if (File.Exists(file)) AppendFile(hash, label, file);
        else AppendText(hash, label);
    }

    private static void AppendFile(IncrementalHash hash, string label, string file)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Plugin source links are not supported.");
        AppendText(hash, label);
        using var stream = File.OpenRead(file);
        hash.AppendData(BitConverter.GetBytes(stream.Length));
        hash.AppendData(SHA256.HashData(stream));
    }

    private static void AppendText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
