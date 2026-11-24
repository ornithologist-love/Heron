using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Velopack.Locators;

namespace Heron.ClientFramework.Infrastructure;

public sealed record InstallRootHolder(int Pid, string Name, string File);

/// <summary>Finds processes that would make the installer fail to rename the install root.</summary>
/// <remarks>
///     Velopack renames the whole root before replacing files and only reports "access denied" once this host has
///     exited, so foreign holders are named while an update can still be stopped. The host's own process tree is
///     excluded because the update exit path stops it.
/// </remarks>
public static class InstallRootHolders
{
    /// <summary>Throws with holding processes when any process outside the host's tree holds the install root.</summary>
    /// <remarks>A query that cannot run is logged and does not block the update.</remarks>
    public static async Task EnsureFreeAsync(
        string? rootDirectory = null,
        string? fileLocksmithCliPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = rootDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            var locator = VelopackLocator.Current;
            if (locator.IsPortable || locator.RootAppDir is not { } appRoot) return;
            root = appRoot;
        }

        var cli = fileLocksmithCliPath ?? Path.Combine(AppContext.BaseDirectory, "Tools", "FileLocksmith", "FileLocksmithCLI.exe");
        if (!File.Exists(cli)) return;

        List<InstallRootHolder> holders;
        try
        {
            holders = await QueryAsync(cli, Path.TrimEndingDirectorySeparator(root), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException
                                          or Win32Exception
                                          || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Serilog.Log.Warning(error, "Install root holder query failed: root={Root}", root);
            return;
        }

        var own = WebView2ProcessLifetime.FindDescendantProcessIds([Environment.ProcessId]);
        var foreign = holders
            .Where(holder => holder.Pid != Environment.ProcessId && !own.Contains(holder.Pid))
            .ToList();
        if (foreign.Count == 0) return;

        foreach (var holder in foreign)
            Serilog.Log.Warning("Install root held: pid={Pid} name={Name} file={File}", holder.Pid, holder.Name, holder.File);
        throw new InvalidOperationException("以下程序占用了安装目录，现在更新会失败："
            + string.Join("；", foreign.Select(holder => $"{holder.Name}（PID {holder.Pid}，{holder.File}）"))
            + "。请关闭它们后重试；VS Code 可执行「Developer: Reload Window」。");
    }

    public static List<InstallRootHolder> ParseFileLocksmithJson(string json, int excludePid)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("processes", out var processes) || processes.ValueKind != JsonValueKind.Array)
            throw new JsonException("File Locksmith result is missing processes array.");

        var list = new List<InstallRootHolder>();
        foreach (var entry in processes.EnumerateArray())
        {
            var pid = entry.GetProperty("pid").GetInt32();
            if (pid == excludePid) continue;
            var name = entry.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var file = entry.TryGetProperty("files", out var filesProp) && filesProp.GetArrayLength() > 0
                ? filesProp[0].GetString() ?? ""
                : "";
            list.Add(new InstallRootHolder(pid, name, file));
        }
        return list;
    }

    private static async Task<List<InstallRootHolder>> QueryAsync(string cli, string root, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add(root);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("File Locksmith failed to start.");
        var queryPid = process.Id;
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"File Locksmith exited with code {process.ExitCode}: {(await errorTask.ConfigureAwait(false)).Trim()}");
            return ParseFileLocksmithJson(await outputTask.ConfigureAwait(false), queryPid);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
            }
        }
    }
}
