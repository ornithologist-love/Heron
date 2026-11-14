using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heron.Pregel.Tooling;

/// <summary>Built-in file archival node, alongside the shell and process adapters.</summary>
public static class ArtifactNodes
{
    public const string RegisteredEvent = "artifact.registered";

    /// <summary>Records a delivery observation; it does not transfer the file or change node success.</summary>
    public static void ReportDelivery(string source, string destination, string state, string? location,
        PregelNodeContext context, string? error = null)
    {
        if (string.IsNullOrWhiteSpace(destination) || state is not ("uploading" or "succeeded" or "failed" or "canceled"))
            throw new ArgumentException("制品去向或状态无效");
        var path = Path.GetFullPath(source);
        var file = new FileInfo(path);
        context.Notify(RegisteredEvent, "制品流向：" + destination, Describe(path, file.Name, "file", file.Exists ? file.Length : null, null) with
        {
            Deliveries = [new(destination, state, location, error)]
        });
    }

    private static ArtifactRecord Describe(string source, string name, string kind, long? bytes, string? sha256)
    {
        var path = Path.GetFullPath(source);
        var identity = Environment.MachineName.ToUpperInvariant() + "\0"
            + (OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
        return new()
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant(),
            Name = name, Kind = kind, MachineName = Environment.MachineName, SourcePath = path, Bytes = bytes, Sha256 = sha256
        };
    }

    private static void PublishArchive(ArtifactManifest manifest, string directory, PregelNodeContext context)
    {
        foreach (var item in manifest.Artifacts)
        {
            var saved = Path.GetFullPath(Path.Combine(directory, item.Name));
            context.Notify(RegisteredEvent, "已归档制品：" + item.Name,
                Describe(item.Source, item.Name, item.Kind, item.Bytes, item.Sha256) with
                {
                    ArchivedPath = saved,
                    Deliveries = [new("本地归档", "succeeded", saved)]
                });
        }
    }

    /// <summary>Archives the previous node's file in this run's artifacts directory and returns its saved path.</summary>
    [DisplayName("归档产物")]
    public static async Task<string> Archive(string source, PregelNodeContext context, CancellationToken token)
    {
        var file = Path.GetFullPath(source);
        var name = Path.GetFileName(file);
        var directory = Path.Combine(context.WorkingDirectory, "artifacts");
        var manifest = await Archive(new ArtifactArchiveRequest(directory, name, [new ArtifactSource(file, name)]), context, token);
        return Path.Combine(Path.GetDirectoryName(manifest)!, name);
    }

    [DisplayName("归档产物")]
    public static async Task<string> Archive(ArtifactArchiveRequest request, PregelNodeContext context, CancellationToken token)
    {
        // Frozen input may name the source run's archive. A derived run must never replace or reuse that mutable destination.
        var destination = context.Reexecution == null ? Path.GetFullPath(request.Directory)
            : Path.Combine(context.WorkingDirectory, "artifacts", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.NodeId.Value))));
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("归档目录不能是根目录");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new List<(string Source, string Name, string Kind)>();
        var names = new HashSet<string>(comparer) { "manifest.json" };

        string ChildPath(string root, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':')
                || name.Split('/', '\\').Any(part => part is "" or "." or ".."))
                throw new ArgumentException("产物名称必须是相对路径：" + name);
            var result = Path.GetFullPath(Path.Combine(root, name));
            if (!result.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                throw new ArgumentException("产物路径越界：" + name);
            return result;
        }
        void AddFile(string source, string name, string kind)
        {
            token.ThrowIfCancellationRequested();
            ChildPath(destination, name);
            if (!names.Add(name.Replace('\\', '/'))) throw new ArgumentException("产物名称重复：" + name);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("归档不跟随文件链接：" + source);
            files.Add((source, name.Replace('\\', '/'), kind));
        }
        void AddDirectory(string source, string name, string kind)
        {
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("归档不跟随目录链接：" + source);
            foreach (var file in Directory.EnumerateFiles(source).OrderBy(value => value, comparer)) AddFile(file, name + "/" + Path.GetFileName(file), kind);
            foreach (var folder in Directory.EnumerateDirectories(source).OrderBy(value => value, comparer)) AddDirectory(folder, name + "/" + Path.GetFileName(folder), kind);
        }
        foreach (var source in request.Sources)
        {
            token.ThrowIfCancellationRequested();
            ChildPath(destination, source.Name);
            var full = Path.GetFullPath(source.Path);
            if (source.IsDirectory && (destination.Equals(full, comparison) || destination.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)))
                throw new ArgumentException("归档目录不能位于源目录中");
            if (!(source.IsDirectory ? Directory.Exists(full) : File.Exists(full)))
            {
                if (source.Required) throw new FileNotFoundException("待归档产物不存在", full);
                continue;
            }
            if (source.IsDirectory) AddDirectory(full, source.Name, source.Kind);
            else AddFile(full, source.Name, source.Kind);
        }
        if (files.Count == 0) throw new ArgumentException("归档清单不能为空");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var manifestPath = Path.Combine(destination, "manifest.json");
        if (Directory.Exists(destination))
        {
            // Resume after the directory rename but before the node checkpoint was committed.
            var existing = JsonSerializer.Deserialize<ArtifactManifest>(await File.ReadAllTextAsync(manifestPath, token), json)
                ?? throw new IOException("归档清单损坏");
            if (existing.Schema != 1 || existing.Title != request.Title || existing.Artifacts.Length != files.Count)
                throw new IOException("归档目录已经包含不同产物，不覆盖原版本");
            foreach (var file in files)
            {
                var item = existing.Artifacts.SingleOrDefault(value => comparer.Equals(value.Name, file.Name));
                if (item == null || !comparer.Equals(item.Source, file.Source) || item.Kind != file.Kind)
                    throw new IOException("归档清单与本次输入不一致");
                foreach (var path in new[] { file.Source, ChildPath(destination, file.Name) })
                {
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length != item.Bytes || Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != item.Sha256)
                        throw new IOException("产物内容已变化，不能复用旧归档：" + file.Name);
                }
            }
            context.Log("复用已完成归档：" + manifestPath);
            PublishArchive(existing, destination, context);
            return manifestPath;
        }
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var artifacts = new List<ArchivedArtifact>();
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var outputPath = ChildPath(staging, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using (var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, token);
                await using var saved = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                artifacts.Add(new(file.Name, file.Kind, file.Source, saved.Length, Convert.ToHexString(await SHA256.HashDataAsync(saved, token))));
            }
            var manifest = new ArtifactManifest(1, request.Title, DateTimeOffset.UtcNow, artifacts.ToArray());
            await File.WriteAllTextAsync(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(manifest, json), new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            context.Log($"已归档 {artifacts.Count} 个文件：{manifestPath}");
            PublishArchive(manifest, destination, context);
            return manifestPath;
        }
        finally
        {
            // Only our freshly generated sibling staging directory can be removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
