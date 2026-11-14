using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Heron.ClientFramework.Plugins;

public sealed record PluginArchiveSource(string Archive, string Sha256);

public sealed record PluginActivationState
{
    public PluginLockEntry[] Active { get; init; } = [];
    public PluginLockEntry[] Previous { get; init; } = [];
    public PluginLockEntry[]? Pending { get; init; }
    public bool Booting { get; init; }
    public bool HasActive { get; init; }
}

/// <summary>Immutable version directories and one atomic group journal. Caller supplies trusted archive hashes.</summary>
public sealed class PluginVersionStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private readonly string _root;
    private string StatePath => Path.Combine(_root, "activation.json");
    public PluginVersionStore(string root) => _root = Path.GetFullPath(root);

    /// <summary>Shared by plugin staging and host update. FileShare.None also excludes another process.</summary>
    public IDisposable AcquireUpdateLock()
    {
        Directory.CreateDirectory(_root);
        return new FileStream(Path.Combine(_root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public PluginActivationState ReadState()
    {
        var state = File.Exists(StatePath)
            ? JsonSerializer.Deserialize<PluginActivationState>(File.ReadAllText(StatePath), Json)
                ?? throw new InvalidDataException("Plugin activation state is null.") : new();
        if (state.Active == null || state.Previous == null || (state.Booting && state.Pending == null))
            throw new InvalidDataException("Invalid plugin activation journal.");
        foreach (var group in new[] { state.Active, state.Previous, state.Pending ?? [] })
        {
            if (group.Any(entry => entry == null || string.IsNullOrWhiteSpace(entry.Id))
                || group.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != group.Length)
                throw new InvalidDataException("Invalid pinned plugin identities.");
            foreach (var entry in group) PluginVersion.Parse(entry.Version);
        }
        return state;
    }

    public async Task<PluginPackage> InstallArchiveAsync(string archivePath, string expectedSha256, CancellationToken token = default)
    {
        using var updateLock = AcquireUpdateLock();
        return await InstallArchiveCoreAsync(archivePath, expectedSha256, token);
    }

    private async Task<PluginPackage> InstallArchiveCoreAsync(string archivePath, string expectedSha256, CancellationToken token)
    {
        await using var input = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin archive SHA-256 mismatch.");
        input.Position = 0;
        var staging = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 10000) throw new InvalidDataException("Plugin archive has too many entries.");
            long total = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var relative = entry.FullName.Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Split('/').Any(part => part is ".." or "." || part.Contains(':')))
                    throw new InvalidDataException("Plugin archive contains an unsafe path.");
                var path = Path.GetFullPath(Path.Combine(staging, relative));
                if (!path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !paths.Add(path)) throw new InvalidDataException("Plugin archive contains duplicate or escaping paths.");
                // Do not materialize Unix symlinks or Windows reparse entries from an archive.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                    throw new InvalidDataException("Plugin archive links are not supported.");
                if (relative.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                total = checked(total + entry.Length);
                if (total > 1024L * 1024 * 1024) throw new InvalidDataException("Plugin archive exceeds 1 GiB extracted size.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var source = entry.Open();
                await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination, token);
            }
            var package = PluginPackage.Read(Path.Combine(staging, "plugin.json"));
            var final = Path.Combine(_root, "versions", package.Manifest.Id, package.Manifest.Version);
            if (Directory.Exists(final))
            {
                var incoming = Directory.GetFiles(staging, "*", SearchOption.AllDirectories);
                var existing = Directory.GetFiles(final, "*", SearchOption.AllDirectories);
                if (incoming.Length != existing.Length || incoming.Any(file =>
                {
                    var target = Path.Combine(final, Path.GetRelativePath(staging, file));
                    return !File.Exists(target) || !SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target)));
                })) throw new InvalidOperationException("Plugin version already installed with different contents; versions are immutable.");
                return PluginPackage.Read(Path.Combine(final, "plugin.json"));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            Directory.Move(staging, final);
            return PluginPackage.Read(Path.Combine(final, "plugin.json"));
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    public IReadOnlyList<PluginPackage> InstalledPackages() => Directory.Exists(Path.Combine(_root, "versions"))
        ? Directory.EnumerateFiles(Path.Combine(_root, "versions"), "plugin.json", SearchOption.AllDirectories).Select(PluginPackage.Read).ToArray()
        : [];

    /// <summary>Stage a complete compatible edition, not a set of independently switched DLLs.</summary>
    public void Stage(PluginEditionLock desired, IEnumerable<PluginPackage> available)
    {
        using var updateLock = AcquireUpdateLock();
        StageCore(desired, available);
    }

    private void StageCore(PluginEditionLock desired, IEnumerable<PluginPackage> available)
    {
        var packages = Select(desired.Plugins, available);
        PluginDependencyGraph.Resolve(packages.Select(p => p.Manifest), desired.Plugins.Select(p => p.Id), desired.HostApi);
        var state = ReadState();
        if (state.Booting || state.Pending != null) throw new InvalidOperationException("A plugin activation is already pending.");
        WriteState(state with { Pending = desired.Plugins });
    }

    public async Task InstallAndStageAsync(IReadOnlyList<PluginArchiveSource> archives,
        IReadOnlyList<PluginPackage> current, string hostApi, CancellationToken token = default)
    {
        using var updateLock = AcquireUpdateLock();
        var state = ReadState();
        if (state.Booting || state.Pending != null) throw new InvalidOperationException("A plugin activation is already pending.");
        if (archives.Count == 0) throw new InvalidDataException("Update group is empty.");
        var replacements = new Dictionary<string, PluginPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            var installed = await InstallArchiveCoreAsync(archive.Archive, archive.Sha256, token);
            if (current.Any(p => p.Manifest.Id == installed.Manifest.Id && p.Manifest.Version == installed.Manifest.Version))
                throw new InvalidOperationException("The selected plugin version is already current; publish a new version to update it.");
            if (!replacements.TryAdd(installed.Manifest.Id, installed))
                throw new InvalidDataException("An update group cannot contain multiple versions of one plugin.");
        }
        var desired = current.Where(p => !replacements.ContainsKey(p.Manifest.Id)).Concat(replacements.Values).ToArray();
        var edition = new PluginEdition("custom", desired.Select(p => p.Manifest.Id).ToArray());
        token.ThrowIfCancellationRequested();
        StageCore(PluginEditionLock.Resolve(edition, desired, hostApi), desired);
    }

    /// <summary>Called before loading code. An interrupted trial falls back to the last healthy group.</summary>
    public PluginLockEntry[] BeginStartup()
    {
        using var updateLock = AcquireUpdateLock();
        var state = ReadState();
        if (state.Booting)
        {
            WriteState(state with { Pending = null, Booting = false });
            return state.Active;
        }
        if (state.Pending == null) return state.Active;
        WriteState(state with { Booting = true });
        return state.Pending;
    }

    public void MarkHealthy()
    {
        using var updateLock = AcquireUpdateLock();
        var state = ReadState();
        if (!state.Booting || state.Pending == null) return;
        WriteState(new() { Active = state.Pending, Previous = state.Active, HasActive = true });
    }

    public void ResetToBuiltin()
    {
        using var updateLock = AcquireUpdateLock();
        WriteState(new());
    }

    public void RejectPending()
    {
        using var updateLock = AcquireUpdateLock();
        var state = ReadState();
        WriteState(state with { Pending = null, Booting = false });
    }

    public static PluginPackage[] Select(IEnumerable<PluginLockEntry> entries, IEnumerable<PluginPackage> available)
    {
        var packages = available.ToArray();
        return entries.Select(entry => packages.SingleOrDefault(p => p.Manifest.Id == entry.Id && p.Manifest.Version == entry.Version)
            ?? throw new InvalidDataException($"Missing pinned plugin {entry.Id}@{entry.Version}.")).ToArray();
    }

    private void WriteState(PluginActivationState state)
    {
        var temporary = Path.Combine(_root, $"activation-{Guid.NewGuid():N}.tmp");
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(output, state, Json);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, StatePath, overwrite: true);
    }
}
