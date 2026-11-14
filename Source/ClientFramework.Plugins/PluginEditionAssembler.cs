namespace Heron.ClientFramework.Plugins;

/// <summary>
/// Atomically replaces the assembled built-in plugin payload in a Client output directory.
/// </summary>
public static class PluginEditionAssembler
{
    public static PluginEditionLock Assemble(
        PluginEdition edition,
        IEnumerable<PluginPackage> packages,
        string destination,
        IPluginFileFilter? fileFilter = null,
        string hostApi = "1")
    {
        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(destinationRoot))?.FullName
            ?? throw new InvalidOperationException("Plugin destination must have a parent directory.");
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(destinationRoot));
        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(parent, $".{leaf}.plugin-staging-{operationId}");
        var backupRoot = Path.Combine(parent, $".{leaf}.plugin-backup-{operationId}");

        try
        {
            Directory.CreateDirectory(stagingRoot);
            var locked = PluginEditionPackaging.Copy(
                edition,
                packages,
                stagingRoot,
                hostDirectory: destinationRoot,
                hostApi: hostApi, fileFilter: fileFilter);
            Commit(stagingRoot, destinationRoot, backupRoot);
            return locked;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(backupRoot);
        }
    }

    private static void Commit(string stagingRoot, string destinationRoot, string backupRoot)
    {
        string[] generatedFiles = ["edition.json", "plugins.lock.json"];
        var stagedPluginRoot = Path.Combine(stagingRoot, "plugins", "builtin");
        var destinationPluginRoot = Path.Combine(destinationRoot, "plugins", "builtin");
        var backupPluginRoot = Path.Combine(backupRoot, "plugins", "builtin");
        Directory.CreateDirectory(backupRoot);

        try
        {
            if (Directory.Exists(destinationPluginRoot))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPluginRoot)!);
                Directory.Move(destinationPluginRoot, backupPluginRoot);
            }

            foreach (var name in generatedFiles)
            {
                var current = Path.Combine(destinationRoot, name);
                if (File.Exists(current))
                    File.Move(current, Path.Combine(backupRoot, name));
            }

            if (Directory.Exists(stagedPluginRoot))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPluginRoot)!);
                Directory.Move(stagedPluginRoot, destinationPluginRoot);
            }

            foreach (var name in generatedFiles)
                File.Move(Path.Combine(stagingRoot, name), Path.Combine(destinationRoot, name));
        }
        catch
        {
            TryDeleteDirectory(destinationPluginRoot);
            foreach (var name in generatedFiles)
            {
                var current = Path.Combine(destinationRoot, name);
                if (File.Exists(current))
                    File.Delete(current);
            }

            if (Directory.Exists(backupPluginRoot))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPluginRoot)!);
                Directory.Move(backupPluginRoot, destinationPluginRoot);
            }

            foreach (var name in generatedFiles)
            {
                var backup = Path.Combine(backupRoot, name);
                if (File.Exists(backup))
                    File.Move(backup, Path.Combine(destinationRoot, name));
            }

            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A completed assembly remains valid if private scratch cleanup fails.
        }
    }
}
