using Heron.Pregel.Automation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Heron.AutomationTenants;

/// <summary>
///     Content-addressed store for admitted execution snapshots. Each distinct file is kept once under
///     <c>blobs/&lt;sha256&gt;</c>; a version is a directory of hard links (copies where links are unavailable) plus a
///     manifest of the blobs it uses. Integrity comes from content verification on every admission, not from
///     read-only marks, and protection never travels with a file copied out of a snapshot.
/// </summary>
public static class AutomationContentStore
{
    /// <summary>Versions used within this window are retained.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    /// <summary>The most recently used versions of each script retained beyond the window.</summary>
    public const int RetainedVersionsPerScript = 2;

    private static readonly TimeSpan StaleStage = TimeSpan.FromDays(1);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal sealed record VersionManifest(string SourceId, DateTimeOffset LastUsed, string[] Blobs);

    internal static string Root => Path.Combine(AutomationPublishedSource.StateRoot, "source-store");
    internal static string BlobRoot => Path.Combine(Root, "blobs");
    internal static string VersionRoot => Path.Combine(Root, "versions");
    internal static string CacheRoot => Path.Combine(AutomationPublishedSource.StateRoot, "cache");

    /// <summary>Returns the verified version directory (containing <c>Pregel/&lt;id&gt;</c> and <c>Pregel/Shared</c>).</summary>
    internal static string Materialize(AutomationExecutionVersion version, string scriptsRoot, string logicalPath, CancellationToken token)
    {
        var bundle = version.BundleSha256["sha256-".Length..];
        var target = Path.Combine(VersionRoot, bundle);
        if (Directory.Exists(target))
        {
            if (Matches(target, version))
            {
                Touch(bundle, version.SourceId, force: true);
                return target;
            }
            // A damaged snapshot is rebuilt from the verified installation instead of blocking admission.
            Serilog.Log.Warning("[Automation] 执行快照校验失败，按安装件重建：{Bundle}", bundle);
            DeleteVersion(bundle);
        }
        EnsureBlobRoot();
        var stage = target + ".tmp-" + Guid.NewGuid().ToString("N");
        var blobs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var id in new[] { version.SourceId, "Shared" })
            {
                var source = Path.Combine(scriptsRoot, id);
                foreach (var file in AutomationLocalSource.ContentFiles(source))
                {
                    token.ThrowIfCancellationRequested();
                    var destination = Path.Combine(stage, "Pregel", id, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var blob = Store(file, token);
                    blobs.Add(Path.GetFileName(blob));
                    Link(blob, destination);
                }
            }
            if (!Matches(stage, version)) throw new IOException("执行快照内容校验失败");
            if (AutomationLocalSource.Describe(logicalPath)?.BundleSha256 != version.BundleSha256)
                throw new IOException("复制期间本地脚本发生修改，请重新发起");
            File.WriteAllText(Path.Combine(stage, "source-version.json"), JsonSerializer.Serialize(version, Json));
            WriteManifest(bundle, new(version.SourceId, DateTimeOffset.UtcNow, blobs.Order(StringComparer.Ordinal).ToArray()));
            try { Directory.Move(stage, target); }
            catch (IOException) when (Directory.Exists(target)) { }
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        if (!Matches(target, version)) throw new IOException("执行快照内容校验失败");
        return target;
    }

    /// <summary>Records that a run started on a snapshot, so retention measures recent use rather than creation.</summary>
    public static void MarkUsed(string snapshotPath)
    {
        try
        {
            var full = Path.GetFullPath(snapshotPath);
            var root = Path.GetFullPath(VersionRoot) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            var bundle = full[root.Length..].Split(Path.DirectorySeparatorChar)[0];
            if (ReadManifest(bundle) is { } manifest) Touch(bundle, manifest.SourceId, force: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Debug(error, "[Automation] 无法记录执行快照使用时间");
        }
    }

    /// <summary>Writable cache for an installed or admitted script directory; null for local working copies.</summary>
    public static string? CacheDirectory(string scriptDirectory)
    {
        var full = Path.GetFullPath(scriptDirectory);
        foreach (var root in new[] { AutomationPublishedSource.Root, VersionRoot })
        {
            var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = full[prefix.Length..].Split(Path.DirectorySeparatorChar);
            // versions/<bundle>/Pregel/<id>/... and <scripts>/<id>/... both name the suite.
            var id = root == AutomationPublishedSource.Root ? parts[0] : parts.Length > 2 ? parts[2] : "";
            return AutomationScriptRevision.IsValidId(id) ? Path.Combine(CacheRoot, id) : null;
        }
        return null;
    }

    /// <summary>Guidance appended to a step error that refers to a read-only package or snapshot.</summary>
    public static string? ErrorHint(string message)
    {
        var denied = message.Contains("denied", StringComparison.OrdinalIgnoreCase) || message.Contains("拒绝")
            || message.Contains("read-only", StringComparison.OrdinalIgnoreCase) || message.Contains("只读");
        if (!denied) return null;
        var paths = new[] { AutomationPublishedSource.Root, Root, Path.Combine(AutomationStore.DefaultRoot, "source-snapshots") };
        return paths.Any(path => message.Contains(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            ? "流程包与执行快照只读：每轮产物写到节点上下文的 WorkingDirectory，持久缓存使用 AutomationScriptStorage.CacheDirectory；"
              + "需要修改的配置先复制出包目录再改。"
            : null;
    }

    /// <summary>Best-effort retention pass; skipped while another process holds the script-source lock.</summary>
    public static void TryCollect()
    {
        FileStream? lease;
        try { lease = AutomationRemoteSource.TryExclusiveLease(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        if (lease == null) return;
        using (lease)
            try { Collect(DateTimeOffset.UtcNow); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Serilog.Log.Warning(error, "[Automation] 脚本存储回收未完成");
            }
    }

    /// <summary>Removes versions outside retention and the blobs no retained version uses. Requires the exclusive source lock.</summary>
    internal static void Collect(DateTimeOffset now)
    {
        CollectLegacy(now);
        if (!Directory.Exists(VersionRoot)) return;
        var manifests = Directory.EnumerateFiles(VersionRoot, "*.json")
            .Select(file => (Bundle: Path.GetFileNameWithoutExtension(file), Manifest: ReadManifest(Path.GetFileNameWithoutExtension(file))))
            .ToArray();
        var retained = manifests.Where(item => item.Manifest != null)
            .GroupBy(item => item.Manifest!.SourceId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderByDescending(item => item.Manifest!.LastUsed)
                .Where((item, index) => index < RetainedVersionsPerScript || now - item.Manifest!.LastUsed <= RetentionWindow))
            .Select(item => item.Bundle).ToHashSet(StringComparer.Ordinal);
        foreach (var (bundle, _) in manifests.Where(item => !retained.Contains(item.Bundle)))
            DeleteVersion(bundle);
        foreach (var directory in Directory.EnumerateDirectories(VersionRoot))
        {
            var name = Path.GetFileName(directory);
            var stale = name.Contains(".tmp-", StringComparison.Ordinal) && now - Directory.GetLastWriteTimeUtc(directory) > StaleStage;
            // A version directory without a manifest was never completed.
            if (stale || !name.Contains('.') && !File.Exists(Path.Combine(VersionRoot, name + ".json")))
                TryDelete(directory);
        }
        if (!Directory.Exists(BlobRoot)) return;
        var used = retained.Select(ReadManifest).Where(manifest => manifest != null)
            .SelectMany(manifest => manifest!.Blobs).ToHashSet(StringComparer.Ordinal);
        foreach (var blob in Directory.EnumerateFiles(BlobRoot))
        {
            var name = Path.GetFileName(blob);
            if (name.StartsWith(".tmp-", StringComparison.Ordinal) ? now - File.GetLastWriteTimeUtc(blob) > StaleStage : !used.Contains(name))
                TryDelete(blob);
        }
    }

    /// <summary>Backups of replaced installations kept for recovery.</summary>
    public const int RetainedInstallBackups = 3;

    /// <summary>
    ///     Full-copy snapshots from earlier releases and replaced-install backups are bounded too: snapshots unused
    ///     for the retention window and backups beyond the newest few are removed.
    /// </summary>
    private static void CollectLegacy(DateTimeOffset now)
    {
        var legacy = Path.Combine(AutomationStore.DefaultRoot, "source-snapshots");
        if (Directory.Exists(legacy))
            foreach (var directory in Directory.EnumerateDirectories(legacy)
                         .Where(directory => now - Directory.GetLastWriteTimeUtc(directory) > RetentionWindow))
                DeleteMarked(directory);
        var backups = Path.Combine(AutomationStore.DefaultRoot, "source-backups");
        if (Directory.Exists(backups))
            foreach (var directory in Directory.EnumerateDirectories(backups)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(RetainedInstallBackups))
                DeleteMarked(directory);
    }

    /// <summary>
    ///     Earlier snapshots marked their files read-only and replaced installations keep their directory rule;
    ///     both must be lifted before Windows deletes them.
    /// </summary>
    private static void DeleteMarked(string directory)
    {
        try { AutomationPublishedSource.MakeWritable(directory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        TryDelete(directory);
    }

    private static bool Matches(string directory, AutomationExecutionVersion version)
    {
        try
        {
            return AutomationLocalSource.ContentHash(Path.Combine(directory, "Pregel", version.SourceId), version.SourceId, version.Entry)
                       == version.ActualSha256
                   && AutomationLocalSource.ContentHash(Path.Combine(directory, "Pregel", "Shared"), "Shared", "")
                       == version.SharedActualSha256;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Stores one file's bytes and returns the verified blob path.</summary>
    private static string Store(string file, CancellationToken token)
    {
        var hash = Hash(file);
        var blob = Path.Combine(BlobRoot, hash);
        if (File.Exists(blob))
        {
            if (Hash(blob) == hash) return blob;
            // The bytes behind this name changed; links in other versions fail their own verification.
            File.Delete(blob);
        }
        var temporary = Path.Combine(BlobRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Created inside the protected directory: the creator's handle may write, later handles may not.
            using (var input = File.OpenRead(file))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);
            token.ThrowIfCancellationRequested();
            if (Hash(temporary) != hash) throw new IOException("脚本文件在复制期间发生变化：" + file);
            try { File.Move(temporary, blob); }
            catch (IOException) when (File.Exists(blob) && Hash(blob) == hash) { }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return blob;
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void Link(string blob, string destination)
    {
        var linked = OperatingSystem.IsWindows()
            ? CreateHardLink(destination, blob, IntPtr.Zero)
            : link(blob, destination) == 0;
        // Volumes without hard links still get a correct, verified (if larger) snapshot.
        if (!linked) File.Copy(blob, destination);
        else if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(destination) | UnixFileMode.UserWrite);
    }

    private static void EnsureBlobRoot()
    {
        Directory.CreateDirectory(BlobRoot);
        if (OperatingSystem.IsWindows()) ProtectBlobRoot();
    }

    /// <summary>
    ///     Files created in the blob directory inherit a deny for data writes. An inherited entry is not copied with
    ///     a file, unlike an explicit entry or the read-only attribute, and still permits the attribute access a hard
    ///     link needs. Version directories carry no access rule, because changing one would re-propagate inheritance
    ///     onto the shared files.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ProtectBlobRoot()
    {
        var directory = new DirectoryInfo(BlobRoot);
        var security = directory.GetAccessControl();
        var rule = BlobRule();
        if (security.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(existing => existing.IdentityReference == rule.IdentityReference && existing.AccessControlType == AccessControlType.Deny
                && existing.FileSystemRights == rule.FileSystemRights && existing.InheritanceFlags == rule.InheritanceFlags
                && existing.PropagationFlags == rule.PropagationFlags)) return;
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    internal static FileSystemAccessRule BlobRule() => new(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        FileSystemRights.WriteData | FileSystemRights.AppendData, InheritanceFlags.ObjectInherit,
        PropagationFlags.InheritOnly, AccessControlType.Deny);

    private static void Touch(string bundle, string sourceId, bool force)
    {
        var existing = ReadManifest(bundle);
        if (existing == null) return;
        if (!force && DateTimeOffset.UtcNow - existing.LastUsed < TouchInterval) return;
        WriteManifest(bundle, existing with { SourceId = sourceId, LastUsed = DateTimeOffset.UtcNow });
    }

    private static VersionManifest? ReadManifest(string bundle)
    {
        var path = Path.Combine(VersionRoot, bundle + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<VersionManifest>(AutomationStore.ReadShared(path), Json); }
        catch (JsonException) { return null; }
    }

    private static void WriteManifest(string bundle, VersionManifest manifest)
    {
        Directory.CreateDirectory(VersionRoot);
        AutomationStore.WriteAtomic(Path.Combine(VersionRoot, bundle + ".json"), JsonSerializer.Serialize(manifest, Json));
    }

    private static void DeleteVersion(string bundle)
    {
        TryDelete(Path.Combine(VersionRoot, bundle));
        if (!Directory.Exists(Path.Combine(VersionRoot, bundle))) TryDelete(Path.Combine(VersionRoot, bundle + ".json"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open by a running tool keeps its version until the next pass.
            Serilog.Log.Debug(error, "[Automation] 暂时无法回收 {Path}", path);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
}
