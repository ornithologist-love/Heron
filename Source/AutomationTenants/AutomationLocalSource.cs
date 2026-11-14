using Heron.Pregel.Automation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heron.Pregel.Tooling;

namespace Heron.AutomationTenants;

public static class AutomationLocalSource
{
    internal sealed record Baseline(string Id, string Entry, string Revision, string ArchiveSha256, string ContentSha256);
    internal sealed record Package(string Id, string Directory, string Entry, string Revision, string? BaseSha256,
        string ActualSha256, string[] Files)
    {
        public bool Modified => BaseSha256 == null || !ActualSha256.Equals(BaseSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static string? Suite(string path)
    {
        var root = Path.GetFullPath(RemoteAutomationEnvironment.Current.ScriptsRoot()) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var id = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)[0];
        return id != "Shared" && AutomationScriptRevision.IsValidId(id) ? id : null;
    }

    internal static string[] ContentFiles(string directory)
    {
        if (!System.IO.Directory.Exists(directory)) return [];
        List<string> files = [];
        void Visit(string folder, int depth)
        {
            if (depth > 64) throw new IOException("脚本目录层级过深");
            foreach (var item in System.IO.Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(item);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "__pycache__") continue;
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("脚本内容不能包含符号链接：" + item);
                if ((attributes & FileAttributes.Directory) != 0) Visit(item, depth + 1);
                else
                {
                    files.Add(item);
                    if (files.Count > ScriptSourceContent.MaxFiles) throw new IOException("脚本文件数量超过上限");
                }
            }
        }
        Visit(directory, 0);
        return files.ToArray();
    }

    internal static string ContentHash(string directory, string id, string entry, string[]? files = null)
    {
        files ??= ContentFiles(directory);
        long total = 0; var count = 0;
        return ScriptSourceContent.ComputeManifest(entry, files.Select(file =>
        {
            var relative = id + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/');
            using var stream = File.OpenRead(file);
            ScriptSourceContent.ValidateFileSize(relative, stream.Length, total += stream.Length, ++count);
            return (relative, Convert.ToHexString(SHA256.HashData(stream)));
        }));
    }

    internal static Package ReadPackage(string root, string id, string entry)
    {
        var directory = Path.Combine(root, id);
        var baselineFile = Path.Combine(directory, ".baseline.json");
        Baseline? baseline = File.Exists(baselineFile)
            ? JsonSerializer.Deserialize<Baseline>(AutomationStore.ReadShared(baselineFile), Json) : null;
        if (baseline != null && (baseline.Id != id || !AutomationScriptRevision.IsContentHash(baseline.ContentSha256)))
            throw new IOException("发布包基线记录无效，请重新下载：" + id);
        var stampFile = Path.Combine(directory, ".source.json");
        var revision = baseline?.Revision ?? (File.Exists(stampFile) ? File.ReadAllText(stampFile).Split(':')[0] : "");
        var contentEntry = baseline?.Entry ?? entry;
        var files = ContentFiles(directory);
        var hash = ContentHash(directory, id, contentEntry, files);
        var baseHash = baseline?.ContentSha256 ?? (AutomationScriptRevision.IsContentHash(revision) ? revision : null);
        return new(id, directory, contentEntry, revision, baseHash, hash, files);
    }

    internal static void WriteBaseline(string directory, string id, string entry, string revision, string archiveHash, string contentHash)
    {
        System.IO.Directory.CreateDirectory(directory);
        AutomationStore.WriteAtomic(Path.Combine(directory, ".baseline.json"),
            JsonSerializer.Serialize(new Baseline(id, entry, revision, archiveHash, contentHash), Json));
    }

    public static AutomationExecutionVersion? Describe(string path)
    {
        if (Suite(path) is not { } id) return null;
        var root = RemoteAutomationEnvironment.Current.ScriptsRoot();
        var directory = Path.Combine(root, id);
        if (!System.IO.Directory.Exists(directory) || !File.Exists(Path.Combine(directory, ".baseline.json"))
            && !File.Exists(Path.Combine(directory, ".source.json")) && ContentFiles(directory).Length == 0) return null;
        var entry = id + "/" + Path.GetRelativePath(Path.Combine(root, id), path).Replace('\\', '/');
        // Nested entry aliases use the suite's publication entry when available.
        var scopedCatalog = AutomationPublishedSource.ManifestPath(id);
        var catalog = File.Exists(scopedCatalog) ? scopedCatalog : File.Exists(AutomationPublishedSource.CatalogPath)
            ? AutomationPublishedSource.CatalogPath : Path.Combine(root, ".catalog.json");
        if (File.Exists(catalog))
        {
            var manifest = JsonSerializer.Deserialize<AutomationRemoteManifest>(AutomationStore.ReadShared(catalog), AutomationRemoteSource.Json);
            entry = manifest?.Scripts.FirstOrDefault(p => p.Id == id)?.Entry ?? entry;
        }
        var source = ReadPackage(root, id, entry);
        var shared = ReadPackage(root, "Shared", "");
        var bundle = ScriptSourceContent.ComputeManifest("", new[]
        {
            (id, source.ActualSha256["sha256-".Length..]), ("Shared", shared.ActualSha256["sha256-".Length..])
        });
        var known = source.BaseSha256 != null && shared.BaseSha256 != null;
        var modified = source.Modified || shared.Modified;
        // ReadPackage may recover the publication entry from its baseline when the catalog omits this suite.
        // Snapshot verification must use that same hash input; ExecutionEntry separately selects what to run.
        return new(id, source.Entry, source.Revision, source.BaseSha256, source.ActualSha256,
            shared.Revision, shared.BaseSha256, shared.ActualSha256, bundle, known, modified,
            !known ? "local-baseline-unknown" : modified ? "local-modified" : "published")
            { ExecutionEntry = Path.GetRelativePath(root, path).Replace('\\', '/') };
    }

    public static (string Path, AutomationExecutionVersion? Version) Freeze(string logicalPath, string stateRoot, CancellationToken token)
    {
        var version = Describe(logicalPath);
        if (version == null) return (logicalPath, null);
        if (!version.BaselineKnown || version.LocalModified)
            throw new IOException("发布安装目录内容与审核版本不一致，请重新下载；本地开发请使用安装目录外的工作副本。");
        var root = RemoteAutomationEnvironment.Current.ScriptsRoot();
        var entry = Path.GetRelativePath(root, logicalPath);
        // The store is shared by every automation instance of this script root; stateRoot only scopes local state.
        var target = AutomationContentStore.Materialize(version, root, logicalPath, token);
        return (Path.Combine(target, "Pregel", entry), version with { SnapshotRoot = Path.Combine(target, "Pregel") });
    }
}
