using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heron.Pregel.Tooling;

/// <summary>Canonical source identity, shared by authoring clients and the publication service.</summary>
public static class ScriptSourceContent
{
    public const long MaxArchiveBytes = 1024L * 1024 * 1024;
    public const long MaxExpandedBytes = 2 * MaxArchiveBytes;
    public const long MaxSourceFileBytes = 64L * 1024 * 1024;
    public const int MaxFiles = 2000;
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csx", ".cs", ".csproj", ".props", ".targets", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".py", ".ps1", ".sh", ".cmd", ".bat", ".rs", ".json", ".yaml", ".yml", ".toml", ".xml",
        ".config", ".ini", ".cfg", ".md", ".vue", ".css", ".html"
    };

    public static void ValidateFileSize(string path, long bytes, long total, int count)
    {
        // Logs and binary/data attachments have their own budget, independent of source text.
        var limit = SourceExtensions.Contains(Path.GetExtension(path)) ? MaxSourceFileBytes : MaxArchiveBytes;
        if (bytes < 0 || bytes > limit)
            throw new InvalidDataException($"文件「{path}」为 {bytes / 1048576d:F2} MiB，超过单文件 {limit / 1048576} MiB 上限");
        if (total > MaxExpandedBytes || count > MaxFiles)
            throw new InvalidDataException($"脚本包解压后超过 {MaxExpandedBytes / 1048576} MiB 或 {MaxFiles} 个文件");
    }

    public static void ValidateArchiveSize(string id, long bytes)
    {
        if (bytes > MaxArchiveBytes)
            throw new InvalidDataException($"「{id}」归档为 {bytes / 1048576d:F2} MiB，超过单包 {MaxArchiveBytes / 1048576} MiB 上限");
    }

    private sealed record ContentFile(string Path, string Sha256);
    private sealed record ContentManifest(string Format, string Entry, ContentFile[] Files);
    public static bool IsValidId(string? id) => Regex.IsMatch(id ?? "", @"\A[A-Za-z][A-Za-z0-9_-]*\z");
    public static bool IsContentHash(string? revision) => Regex.IsMatch(revision ?? "", @"\Asha256-[0-9a-fA-F]{64}\z");
    public static bool IsValid(string? revision) => IsContentHash(revision) || Regex.IsMatch(revision ?? "", @"\A[0-9]+\z");
    public static string ArchivePath(string id, string revision, string sha256) =>
        $"{id}/{revision}/{(IsContentHash(revision) ? sha256.ToLowerInvariant() + ".zip" : "scripts.zip")}";

    public static string Compute(ZipArchive archive, string id, string entry)
    {
        if (!IsValidId(id) || id.Length > 128) throw new InvalidDataException("脚本 id 无效");
        var entries = archive.Entries.OrderBy(item => item.FullName, StringComparer.Ordinal).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var file in entries)
        {
            if (!file.FullName.StartsWith(id + "/", StringComparison.Ordinal) || file.FullName.Contains('\\') || file.FullName.Contains(':')
                || file.FullName.Length > 1024 || file.FullName.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                    || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<','>','"','|','?','*']) >= 0 || part.Any(char.IsControl)) || !paths.Add(file.FullName)
                || (file.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("脚本包目录或大小无效：" + file.FullName);
            size += file.Length;
            ValidateFileSize(file.FullName, file.Length, size, paths.Count);
        }
        if (entries.Length == 0 || id != "Shared" && !entries.Any(file => file.FullName == entry))
            throw new InvalidDataException("脚本包缺少文件或入口：" + id);
        return ComputeManifest(entry, entries.Select(file =>
        {
            using var stream = file.Open();
            return (file.FullName, Convert.ToHexString(SHA256.HashData(stream)));
        }));
    }

    // Callers comparing a local checkout with an upload use paths relative to each package root.
    public static string ComputeManifest(string entry, IEnumerable<(string Path, string Sha256)> content)
    {
        var files = content.OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => new ContentFile(file.Path, file.Sha256.ToLowerInvariant())).ToArray();
        return "sha256-" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new ContentManifest("pregel-source-v1", entry, files), new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToLowerInvariant();
    }
}
