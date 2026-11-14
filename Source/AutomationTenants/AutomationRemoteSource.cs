using Heron.Pregel.Automation;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Heron.Pregel.Tooling;

namespace Heron.AutomationTenants;

public sealed record AutomationRemotePackage(string Id, string Revision, string Archive, string Sha256,
    string Entry = "", string Title = "", string Description = "", string? TaskId = null, string? TenantId = null);
public sealed record AutomationRemoteManifest(AutomationRemotePackage Shared, AutomationRemotePackage[] Scripts);

/// <summary>Official, publisher-owned scripts. Download never evaluates source code.</summary>
public sealed class AutomationRemoteSource : IAutomationScriptSource
{
    public const string DirectoryLabel = "官方远程脚本源";
    string IAutomationScriptSource.DirectoryLabel => DirectoryLabel;
    public string Url => DefaultUrl;
    Task IAutomationScriptSource.RefreshAsync(CancellationToken token) => RefreshAsync(token);
    string IAutomationScriptSource.InstalledStamp(string path) => ReadInstalledStamp(path);
    IDisposable? IAutomationScriptSource.AcquireReadLease(string path) => AcquireReadLease(path);

    public Task InstallAsync(string path, Action<bool, Action> commit, CancellationToken token) =>
        EnsurePathAsync(path, change => commit(true, change), token);

    public IReadOnlyList<AutomationScriptEntry> ListEntries(IReadOnlyList<AutomationScriptEntry> local, Func<string, string?> loadedStamp) =>
        Manifest?.Scripts.Select(package =>
        {
            var path = Path.Combine(Root, package.Entry);
            var existing = local.FirstOrDefault(entry => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            var installed = InstalledStamp(package);
            var needsDownload = NeedsUpdate(package);
            var reload = existing?.IsLoaded == true && loadedStamp(path) != ReadInstalledStamp(path);
            var status = needsDownload ? installed.Length == 0 ? "未下载" : "有更新，需更新并重新加载"
                : reload ? "已更新，需重新加载" : existing?.Status ?? "已下载，未加载";
            return new AutomationScriptEntry(path, package.Entry, package.Title, status,
                existing?.IsLoaded ?? false, existing?.TemplateId, existing?.Error)
            {
                IsRemote = true, NeedsDownload = needsDownload, NeedsReload = reload, Description = package.Description,
                SourceInfo = $"远程 revision {package.Revision} · 本地 revision {(installed.Length == 0 ? "未下载" : installed.Split(':')[0])} · Shared revision {Manifest.Shared.Revision}"
            };
        }).ToArray() ?? [];
    public static string DefaultUrl => RemoteAutomationEnvironment.Current.OfficialSourceUrl;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    public string Root { get; } = RemoteAutomationEnvironment.Current.ScriptsRoot();
    public string? Error { get; private set; }
    public AutomationRemoteManifest? Manifest { get; private set; }
    public async Task RefreshAsync(CancellationToken token, string? sourceId = null)
    {
        if (sourceId != null && !AutomationScriptRevision.IsValidId(sourceId)) throw new ArgumentException("流程源标识无效");
        var catalogPath = AutomationPublishedSource.ManifestPath(sourceId);
        RejectLinks(Root);
        Directory.CreateDirectory(Root);
        try
        {
            var manifest = await Http.GetFromJsonAsync<AutomationRemoteManifest>(DefaultUrl + "catalog.json"
                + (sourceId == null ? "" : "?source_id=" + Uri.EscapeDataString(sourceId)), Json, token)
                ?? throw new InvalidDataException("远程脚本清单为空");
            Validate(manifest);
            Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
            var temporary = catalogPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, Json), token);
                File.Move(temporary, catalogPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Manifest = manifest;
            Error = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
            || ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            Error = "远程源暂不可用，显示上次清单；已有脚本可继续使用：" + ex.Message;
            var cachedPath = File.Exists(catalogPath) ? catalogPath : Path.Combine(Root, ".catalog.json");
            if (File.Exists(cachedPath))
            {
                var cached = JsonSerializer.Deserialize<AutomationRemoteManifest>(File.ReadAllText(cachedPath), Json)!;
                Validate(cached);
                Manifest = cached;
            }
        }
    }

    public AutomationRemotePackage Find(string path) => Manifest?.Scripts.FirstOrDefault(p =>
        Path.GetFullPath(Path.Combine(Root, p.Entry)).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("脚本不在远程清单中，请重新检查更新");
    public string Stamp(AutomationRemotePackage p) => p.Revision + ":" + p.Sha256 + ":" + Manifest!.Shared.Revision + ":" + Manifest.Shared.Sha256;
    public bool SharedNeedsUpdate => !File.Exists(Path.Combine(Root, "Shared", ".source.json"))
        || File.ReadAllText(Path.Combine(Root, "Shared", ".source.json")) != Manifest!.Shared.Revision + ":" + Manifest.Shared.Sha256;
    public string InstalledStamp(AutomationRemotePackage p)
    {
        var stamp = Path.Combine(Root, p.Id, ".source.json");
        var shared = Path.Combine(Root, "Shared", ".source.json");
        return File.Exists(stamp) && File.Exists(shared)
            ? string.Join(":", File.ReadAllText(stamp).Split(':').Take(2)) + ":" + File.ReadAllText(shared) : "";
    }
    public string InstalledStamp(string path) => ReadInstalledStamp(path);

    internal static string ReadInstalledStamp(string path)
    {
        var root = RemoteAutomationEnvironment.Current.ScriptsRoot();
        // GetRelativePath returns an absolute path across Windows drives; that is not an official script.
        if (!Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "";
        var relative = Path.GetRelativePath(root, path);
        var stamp = Path.Combine(root, relative.Split(Path.DirectorySeparatorChar)[0], ".source.json");
        var shared = Path.Combine(root, "Shared", ".source.json");
        if (!File.Exists(shared)) return "";
        return (File.Exists(stamp) ? string.Join(":", File.ReadAllText(stamp).Split(':').Take(2)) : "bundled")
            + ":" + File.ReadAllText(shared);
    }

    internal static FileStream? AcquireReadLease(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(RemoteAutomationEnvironment.Current.ScriptsRoot() + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) return null;
        return Lease(FileAccess.Read, FileShare.Read);
    }

    /// <summary>The exclusive script-source lock, or null while another loader, run or installer holds it.</summary>
    internal static FileStream? TryExclusiveLease()
    {
        try { return Lease(FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private static FileStream Lease(FileAccess access, FileShare share)
    {
        var lockFile = Path.Combine(Path.GetDirectoryName(RemoteAutomationEnvironment.Current.ScriptsRoot())!, ".Pregel.source.lock");
        RejectLinks(lockFile);
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        // First creation and subsequent shared readers use the same cross-process lock file.
        if (!File.Exists(lockFile))
            try { using var created = new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite); }
            catch (IOException) when (File.Exists(lockFile)) { }
        try { return new FileStream(lockFile, FileMode.Open, access, share); }
        catch (IOException ex) { throw new IOException("脚本源正在更新或有脚本运行，请完成后再更新 / 重新加载", ex); }
    }

    public async Task EnsurePathAsync(string path, Action<Action> commit, CancellationToken token)
    {
        if (AutomationLocalSource.Suite(path) is not { } id)
        {
            if (AutomationPublishedSource.Contains(path))
                throw new IOException("发布安装目录只允许加载已登记的官方流程；本地开发请使用独立目录。");
            return;
        }
        await RefreshAsync(token, id);
        var script = Manifest?.Scripts.FirstOrDefault(p => p.Id == id);
        if (script == null)
            throw new IOException(Error ?? "官方目录中没有本机流程：" + id);
        if (Error != null)
        {
            // Offline execution can use an intact cached publication, never unverified local edits.
            if (!NeedsUpdate(script) && File.Exists(path)) { ProtectInstalled(script, commit); return; }
            throw new IOException(Error);
        }
        if (NeedsUpdate(script))
            await InstallAsync(script, commit, token);
        else ProtectInstalled(script, commit);
        if (NeedsUpdate(script) || !File.Exists(path))
            throw new IOException("审核发布包未完整安装，拒绝加载：" + id);
    }

    public bool NeedsUpdate(AutomationRemotePackage script) => InstalledStamp(script) != Stamp(script)
        || !Matches(script) || !Matches(Manifest!.Shared);

    private bool Matches(AutomationRemotePackage package)
    {
        var directory = Path.Combine(Root, package.Id);
        try
        {
            var baselinePath = Path.Combine(directory, ".baseline.json");
            if (!File.Exists(baselinePath)) return false;
            var baseline = JsonSerializer.Deserialize<AutomationLocalSource.Baseline>(AutomationStore.ReadShared(baselinePath), Json);
            if (baseline == null || baseline.Id != package.Id || baseline.Entry != package.Entry
                || baseline.Revision != package.Revision || !string.Equals(baseline.ArchiveSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
            var actual = AutomationLocalSource.ReadPackage(Root, package.Id, package.Entry);
            return !actual.Modified && (!AutomationScriptRevision.IsContentHash(package.Revision)
                || actual.ActualSha256.Equals(package.Revision, StringComparison.OrdinalIgnoreCase))
                && (package.Entry.Length == 0 || File.Exists(Path.Combine(Root, package.Entry)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private void ProtectInstalled(AutomationRemotePackage script, Action<Action> commit)
    {
        if (AutomationPublishedSource.IsReadOnly(Root)
            && AutomationPublishedSource.IsReadOnlyTree(Path.Combine(Root, script.Id))
            && AutomationPublishedSource.IsReadOnlyTree(Path.Combine(Root, "Shared"))) return;
        commit(() => { using var write = BeginWrite(); });
    }

    private IDisposable BeginWrite()
    {
        var lease = Lease(FileAccess.ReadWrite, FileShare.None);
        try
        {
            AutomationPublishedSource.MakeWritable(Root);
            Directory.CreateDirectory(Root);
            return new InstallLease(lease, Root);
        }
        catch
        {
            try { AutomationPublishedSource.Protect(Root); }
            finally { lease.Dispose(); }
            throw;
        }
    }

    private sealed class InstallLease(FileStream lease, string root) : IDisposable
    {
        public void Dispose() { try { AutomationPublishedSource.Protect(root); } finally { lease.Dispose(); } }
    }

    public async Task<bool> InstallAsync(AutomationRemotePackage script, Action<Action> commit, CancellationToken token)
    {
        var shared = Manifest!.Shared;
        // Stage outside the read-only installation. A failed download never changes installed code.
        var stage = Path.Combine(Path.GetDirectoryName(Root)!, ".Pregel.download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            // Shared is always fetched with the selected suite, including first download.
            foreach (var package in new[] { script, shared })
            {
                var archive = Path.Combine(stage, package.Id + ".zip");
                using var response = await Http.GetAsync(DefaultUrl + "files/" + package.Archive,
                    HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                await using (var stream = File.Create(archive))
                    await response.Content.CopyToAsync(stream, token);
                await using var input = File.OpenRead(archive);
                ScriptSourceContent.ValidateArchiveSize(package.Id, input.Length);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("脚本包 SHA256 不符：" + package.Id);
                using var zip = ZipFile.OpenRead(archive);
                var contentHash = AutomationScriptRevision.Compute(zip, package.Id, package.Entry);
                if (AutomationScriptRevision.IsContentHash(package.Revision)
                    && !contentHash.Equals(package.Revision, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("脚本内容 revision 不符：" + package.Id);
                long size = 0;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    SafePath(entry.FullName);
                    if (!entry.FullName.StartsWith(package.Id + "/", StringComparison.Ordinal)
                        || !paths.Add(entry.FullName) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                        throw new InvalidDataException("脚本包目录或大小无效：" + entry.FullName);
                    size += entry.Length;
                    ScriptSourceContent.ValidateFileSize(entry.FullName, entry.Length, size, paths.Count);
                    var file = Path.Combine(stage, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    entry.ExtractToFile(file);
                }
                AutomationLocalSource.WriteBaseline(Path.Combine(stage, package.Id), package.Id, package.Entry,
                    package.Revision, package.Sha256, contentHash);
            }
            if (!File.Exists(Path.Combine(stage, script.Entry))) throw new InvalidDataException("脚本包缺少入口");
            File.WriteAllText(Path.Combine(stage, script.Id, ".source.json"), Stamp(script));
            File.WriteAllText(Path.Combine(stage, "Shared", ".source.json"), shared.Revision + ":" + shared.Sha256);
            token.ThrowIfCancellationRequested();
            var installed = false;
            commit(() =>
            {
                using var deployment = BeginWrite();
                AutomationPublishedSource.PreserveInputs(Path.Combine(Root, script.Id));
                Replace(stage, script.Id, replaceShared: true);
                if (NeedsUpdate(script)) throw new IOException("安装后的发布包内容校验失败：" + script.Id);
                installed = true;
                // Still under the exclusive install lock: no load or run can be admitting a snapshot.
                try { AutomationContentStore.Collect(DateTimeOffset.UtcNow); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    Serilog.Log.Warning(error, "[Automation] 脚本存储回收未完成");
                }
            });
            return installed;
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                // Retain the replaced tree, including edits an external editor may have saved during the swap.
                if (Directory.EnumerateDirectories(stage, ".old-*").Any())
                {
                    var backup = Path.Combine(AutomationStore.DefaultRoot, "source-backups", Path.GetFileName(stage));
                    try { Directory.CreateDirectory(Path.GetDirectoryName(backup)!); Directory.Move(stage, backup); }
                    catch (IOException) { Serilog.Log.Warning("脚本更新备份保留在 {Directory}", stage); }
                }
                else Directory.Delete(stage, true);
            }
        }
    }

    private void Replace(string stage, string id, bool replaceShared)
    {
        var moved = new List<string>();
        var installed = new List<string>();
        try
        {
            foreach (var name in replaceShared ? new[] { id, "Shared" } : new[] { id })
            {
                var destination = Path.Combine(Root, name);
                RejectLinks(destination);
                if (Directory.Exists(destination))
                {
                    Directory.Move(destination, Path.Combine(stage, ".old-" + name));
                    moved.Add(name);
                }
                Directory.Move(Path.Combine(stage, name), destination);
                installed.Add(name);
            }
        }
        catch
        {
            foreach (var name in installed) Directory.Delete(Path.Combine(Root, name), true);
            foreach (var name in moved) Directory.Move(Path.Combine(stage, ".old-" + name), Path.Combine(Root, name));
            throw;
        }
    }

    private static void RejectLinks(string path)
    {
        for (var p = path; p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("远程脚本缓存不能是重解析点");
        if (Directory.Exists(path))
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                RejectLinks(child);
    }

    private static void Validate(AutomationRemoteManifest manifest)
    {
        if (manifest.Shared == null || manifest.Shared.Id != "Shared" || manifest.Scripts == null || manifest.Scripts.Length > 1000)
            throw new InvalidDataException("远程清单缺少 Shared 或脚本列表无效");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in manifest.Scripts.Append(manifest.Shared))
        {
            if (string.IsNullOrEmpty(p.Id) || !Regex.IsMatch(p.Id, @"\A[A-Za-z][A-Za-z0-9_-]*\z") || !ids.Add(p.Id)
                || !AutomationScriptRevision.IsValid(p.Revision)
                || !Regex.IsMatch(p.Sha256 ?? "", @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("脚本标识、revision 或哈希无效");
            SafePath(p.Archive);
            if (!p.Archive.EndsWith(".zip", StringComparison.Ordinal)) throw new InvalidDataException("脚本包必须是 ZIP");
            if (p.Id != "Shared")
            {
                if (p.TaskId != null && !Regex.IsMatch(p.TaskId, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z")
                    || !tasks.Add(p.TaskId ?? RemoteAutomationOfficialScripts.TaskId(p.Id)))
                    throw new InvalidDataException("云端任务标识无效或重复");
                SafePath(p.Entry);
                if (!p.Entry.StartsWith(p.Id + "/", StringComparison.Ordinal) || !p.Entry.EndsWith(".csx", StringComparison.Ordinal))
                    throw new InvalidDataException("远程脚本入口无效");
            }
        }
    }

    private static void SafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/')
            || path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("远程脚本包路径无效");
    }
}
