using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heron.ClientFramework.Plugins;

/// <summary>Composes already-built host and plugin packages without evaluating source projects.</summary>
public static class ClientProductAssembler
{
    private sealed record Receipt(string Input, string Output);
    private const string ReceiptName = ".client-product.json";

    public static PluginEditionLock Assemble(PluginEdition edition, string hostDirectory,
        IEnumerable<PluginPackage> candidates, string destination, bool debug = false,
        IPluginFileFilter? fileFilter = null, string? debugExecutableName = null)
    {
        var host = Path.GetFullPath(hostDirectory);
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (debug && string.IsNullOrWhiteSpace(debugExecutableName))
            throw new ArgumentException("A Debug product requires its host executable name.", nameof(debugExecutableName));
        if (output.Equals(host, StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(host + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Product output must be separate from its host build input.");
        if (!File.Exists(Path.Combine(host, "host-api.json")))
            throw new FileNotFoundException("Built host is missing host-api.json.", host);
        var available = candidates.ToArray();
        var hostApi = PluginHostPolicy.Read(Path.Combine(host, "host-api.json")).HostApi;
        var locked = PluginEditionLock.Resolve(edition, available, hostApi);
        var packages = PluginVersionStore.Select(locked.Plugins, available).ToArray();
        var inputs = HostFiles(host).Select(file => ("host/" + Path.GetRelativePath(host, file), file))
            .Concat(packages.SelectMany(package => Files(package.Directory).Select(file =>
                (package.Manifest.Id + "/" + Path.GetRelativePath(package.Directory, file), file))))
            .Append(("assembler", typeof(ClientProductAssembler).Assembly.Location));
        var input = Digest(inputs, edition.ToJson() + ":debug=" + debug + ":filter=" + fileFilter?.Fingerprint);
        var receiptFile = Path.Combine(output, ReceiptName);
        if (File.Exists(receiptFile))
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptFile));
            if (receipt?.Input == input && receipt.Output == OutputDigest(output))
            {
                Console.WriteLine($"[reuse] product assembly: {output}");
                return locked;
            }
        }

        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var prefix = "." + Path.GetFileName(output) + ".assembly-";
        var staging = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));
        var backup = staging + "-previous";
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in HostFiles(host))
            {
                var target = Path.Combine(staging, Path.GetRelativePath(host, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            PluginEditionPackaging.Copy(edition, packages, staging, hostApi: hostApi,
                hostDirectory: staging, fileFilter: fileFilter);
            if (debug)
                File.WriteAllText(Path.Combine(staging, "debug-origin.json"), JsonSerializer.Serialize(
                    Path.Combine(host, debugExecutableName!)));
            File.WriteAllText(Path.Combine(staging, ReceiptName),
                JsonSerializer.Serialize(new Receipt(input, OutputDigest(staging))));
            if (Directory.Exists(output)) Directory.Move(output, backup);
            try { Directory.Move(staging, output); }
            catch
            {
                if (Directory.Exists(backup)) Directory.Move(backup, output);
                throw;
            }
            committed = true;
            Console.WriteLine($"[build] product assembly: {output}");
            return locked;
        }
        finally
        {
            // Only this operation's verified sibling staging directories are disposable.
            foreach (var directory in new[] { staging, backup })
                if ((directory != backup || committed) && Path.GetDirectoryName(Path.GetFullPath(directory)) == parent
                    && Path.GetFileName(directory).StartsWith(prefix, StringComparison.Ordinal)
                    && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<string> HostFiles(string root) => Files(root).Where(file =>
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return !relative.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)
            && relative is not ("edition.json" or "plugins.lock.json" or ".plugin-assembly.fingerprint"
                or ReceiptName or "debug-origin.json");
    });

    private static IEnumerable<string> Files(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Product input/output links are not supported: " + entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                foreach (var file in Files(entry)) yield return file;
            }
            else yield return entry;
        }
    }

    private static string OutputDigest(string root) => Digest(Files(root)
        .Where(file => Path.GetRelativePath(root, file) != ReceiptName)
        .Select(file => (Path.GetRelativePath(root, file), file)), "output");

    private static string Digest(IEnumerable<(string Label, string File)> files, string context)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(context));
        foreach (var (label, file) in files.OrderBy(pair => pair.Label, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(label.Replace('\\', '/') + "\0"));
            using var stream = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
