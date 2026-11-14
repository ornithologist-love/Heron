using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Heron.Pregel;

/// <summary>Optional process-independent admission for existing mutex rules. Open handles own claims.</summary>
internal sealed class FilePregelResourceLease(FileStream handle) : IDisposable
{
    private sealed record Claim(string Id, long Created, PregelMutexRule[] Rules, bool Granted);

    internal static bool TryBindExternalCoordinator(string root, Action persistBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(persistBinding);
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        using var gate = new Mutex(false, GateName(root));
        try { if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Resource admission is busy."); }
        catch (AbandonedMutexException) { }
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.lease"))
            {
                try
                {
                    // Queued and granted claims both retain a writable handle that denies
                    // another writer. A closed/stale claim does not prevent migration.
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (FileNotFoundException) { }
                catch (IOException) { return false; }
            }
            persistBinding();
            return true;
        }
        finally { gate.ReleaseMutex(); }
    }

    private static string GateName(string root) => "Pregel.Resources."
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));

    public static async Task<FilePregelResourceLease> AcquireAsync(string root, PregelMutexRule[] rules,
        Action<IReadOnlyList<PregelMutexCoordinator.WaitState>> waiting, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(root, id + ".lease");
        var gateName = GateName(root);
        FileStream? handle = null;
        try
        {
            var claim = new Claim(id, DateTime.UtcNow.Ticks, rules, false);
            bool reported = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                PregelMutexCoordinator.WaitState[] blocked;
                using (var gate = new Mutex(false, gateName))
                {
                    try { if (!gate.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Resource admission is busy."); }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        if (File.Exists(Path.Combine(root, PregelLocalResourceCoordinator.ExternalBindingFileName)))
                            throw new PregelException("mutex_external_coordinator_required",
                                "资源锁目录已绑定外部协调器；请通过已配置的 Pregel 资源协调器执行。");
                        handle ??= new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                        Write(handle, claim);
                        var claims = new List<Claim>();
                        foreach (var otherPath in Directory.EnumerateFiles(root, "*.lease"))
                        {
                            if (otherPath == path) continue;
                            FileStream? stale = null;
                            try { stale = new FileStream(otherPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); }
                            catch (IOException) { }
                            if (stale != null) { stale.Dispose(); File.Delete(otherPath); continue; }
                            using var reader = new FileStream(otherPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            claims.Add(JsonSerializer.Deserialize<Claim>(reader)
                                ?? throw new IOException("Invalid resource claim: " + otherPath));
                        }
                        foreach (var rule in rules)
                            foreach (var other in claims.SelectMany(c => c.Rules).Where(r => r.Key.Equals(rule.Key, StringComparison.OrdinalIgnoreCase)))
                                if (other.MaxConcurrency != rule.MaxConcurrency)
                                    throw new PregelException("mutex_limit_conflict", $"Mutex resource '{rule.Key}' has conflicting limits.");
                        blocked = rules.Select(rule =>
                        {
                            var matching = claims.Where(c => c.Rules.Any(r => r.Key.Equals(rule.Key, StringComparison.OrdinalIgnoreCase))).ToArray();
                            var used = matching.Count(c => c.Granted);
                            var earlier = matching.Any(c => !c.Granted && (c.Created < claim.Created || c.Created == claim.Created && string.CompareOrdinal(c.Id, claim.Id) < 0));
                            return new PregelMutexCoordinator.WaitState(rule.Key, used, rule.MaxConcurrency, earlier);
                        }).Where(s => s.Used >= s.MaxConcurrency || s.QueuedAhead).ToArray();
                        if (blocked.Length == 0)
                        {
                            Write(handle, claim with { Granted = true });
                            return new FilePregelResourceLease(handle);
                        }
                    }
                    finally { gate.ReleaseMutex(); }
                }
                if (!reported) { waiting(blocked); reported = true; }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
        // Closed handles make abandoned claims reclaimable at the next admission.
        catch { handle?.Dispose(); throw; }
    }

    private static void Write(FileStream stream, Claim claim)
    {
        stream.Position = 0;
        JsonSerializer.Serialize(stream, claim);
        stream.SetLength(stream.Position);
        stream.Flush();
    }

    public void Dispose()
    {
        handle.Dispose();
    }
}
