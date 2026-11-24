using System.Collections.Concurrent;
using System.Diagnostics;

namespace Heron.Core.Process;

public static class OwnedProcessDomains
{
    public const string General = "general";
    public const string P4 = "p4";
    public const string Pcg = "pcg";
    public const string Editor = "editor";
    public const string Adb = "adb";
    public const string WebView = "webview";
    public const string Rdp = "rdp";
    public const string Ai = "ai";
}

/// <summary>
///     Tracks processes started by this host instance. Identity includes PID, start time and
///     executable path so a recycled PID can never make an update kill an unrelated process.
/// </summary>
public static class OwnedProcessRegistry
{
    private static readonly ConcurrentDictionary<int, OwnedProcessIdentity> Processes = new();

    public static void Register(System.Diagnostics.Process process, string domain = OwnedProcessDomains.General) =>
        Register(process, domain, stopOnHostExit: true);

    public static void Register(System.Diagnostics.Process process, string domain, bool stopOnHostExit)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Id <= 0) throw new InvalidOperationException("Process must be started before registration.");

        var identity = CaptureIdentity(process, domain) with { StopOnHostExit = stopOnHostExit };
        Processes[identity.ProcessId] = identity;
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Processes.TryRemove(
                new KeyValuePair<int, OwnedProcessIdentity>(identity.ProcessId, identity));
        }
        catch (InvalidOperationException)
        {
            Processes.TryRemove(new KeyValuePair<int, OwnedProcessIdentity>(identity.ProcessId, identity));
        }
    }

    public static IReadOnlyList<OwnedProcessIdentity> Snapshot(string? domain = null)
    {
        PruneExitedOrReusedProcesses();
        return Processes.Values
            .Where(value => domain == null || value.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ProcessId)
            .ToArray();
    }

    public static Task StopAllAsync(CancellationToken cancellationToken = default) =>
        StopAsync(domain: null, cancellationToken, hostExitOnly: false);

    public static IReadOnlyList<OwnedProcessIdentity> SnapshotForHostExit() =>
        Snapshot().Where(identity => identity.StopOnHostExit).ToArray();

    public static Task StopForHostExitAsync(CancellationToken cancellationToken = default) =>
        StopAsync(domain: null, cancellationToken, hostExitOnly: true);

    public static Task StopDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return StopAsync(domain, cancellationToken, hostExitOnly: false);
    }

    private static async Task StopAsync(string? domain, CancellationToken cancellationToken, bool hostExitOnly)
    {
        IReadOnlyList<OwnedProcessIdentity> Targets() => hostExitOnly ? SnapshotForHostExit() : Snapshot(domain);
        var identities = Targets();
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            System.Diagnostics.Process? process = null;
            try
            {
                process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
                if (!Matches(process, identity)) continue;
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally
            {
                process?.Dispose();
            }
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Targets().Count == 0) return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        var remaining = Targets();
        if (remaining.Count > 0)
            throw new InvalidOperationException("等待托管子进程退出超时：" +
                                                string.Join(", ", remaining.Select(x => $"{x.Domain}:{x.ProcessId}")));
    }

    private static OwnedProcessIdentity CaptureIdentity(System.Diagnostics.Process process, string domain)
    {
        process.Refresh();
        string executablePath;
        try { executablePath = process.MainModule?.FileName ?? string.Empty; }
        catch (System.ComponentModel.Win32Exception) { executablePath = string.Empty; }
        return new OwnedProcessIdentity(process.Id, process.StartTime.ToUniversalTime(), executablePath,
            string.IsNullOrWhiteSpace(domain) ? OwnedProcessDomains.General : domain.Trim());
    }

    private static bool Matches(System.Diagnostics.Process process, OwnedProcessIdentity identity)
    {
        process.Refresh();
        if (process.HasExited || process.StartTime.ToUniversalTime() != identity.StartedAtUtc) return false;
        if (identity.ExecutablePath.Length == 0) return true;
        try
        {
            return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                Path.GetFullPath(identity.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void PruneExitedOrReusedProcesses()
    {
        foreach (var (processId, identity) in Processes)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                if (Matches(process, identity)) continue;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            Processes.TryRemove(new KeyValuePair<int, OwnedProcessIdentity>(processId, identity));
        }
    }
}

public sealed record OwnedProcessIdentity(
    int ProcessId,
    DateTime StartedAtUtc,
    string ExecutablePath,
    string Domain)
{
    public bool StopOnHostExit { get; init; } = true;
}
