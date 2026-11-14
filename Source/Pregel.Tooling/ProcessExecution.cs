using System.Diagnostics;
using System.Text;

namespace Heron.Pregel.Tooling;

/// <summary>Serializable identity; PID alone is not enough to reopen a process safely.</summary>
public sealed record StartedProcess(int Id, long StartTimeUtcTicks)
{
    public Process? Open()
    {
        Process? process = null;
        var matched = false;
        try
        {
            process = Process.GetProcessById(Id);
            matched = process.StartTime.ToUniversalTime().Ticks == StartTimeUtcTicks && !process.HasExited;
            return matched ? process : null;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        finally { if (!matched) process?.Dispose(); }
    }

}

public static class ProcessExecution
{
    /// <summary>Runs a Windows batch script through cmd.exe, streams node output, and fails on a nonzero exit.</summary>
    public static Task RunScriptAsync(string path, string workingDirectory, PregelNodeContext context,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(context);
        if (path.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0)
            throw new ArgumentException("脚本路径不能包含双引号、%、! 或换行。", nameof(path));
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /s /c \"\"{path}\"\"",
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true
        };
        return RunCheckedAsync(start, context, token);
    }

    /// <summary>Run a command with node output; a nonzero exit fails the node and stops its outgoing chain.</summary>
    public static async Task RunCheckedAsync(ProcessStartInfo start, PregelNodeContext context,
        CancellationToken cancellationToken = default)
    {
        context.Log($"执行：{start.FileName} {start.Arguments} {string.Join(" ", start.ArgumentList)}\n工作目录：{start.WorkingDirectory}");
        var result = await RunAsync(start, context.Output, cancellationToken, captureOutput: false).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new PregelException("tool_execution_failed", $"{Path.GetFileName(start.FileName)} 执行失败（退出码 {result.ExitCode}）。");
        context.Log($"{Path.GetFileName(start.FileName)} 执行完成。");
    }

    /// <summary>Observe a user-owned process. Any exit completes normally; cancellation only stops observing.</summary>
    public static async Task WaitForExitAsync(Process process, PregelNodeContext context,
        CancellationToken cancellationToken = default)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        context.Notify("process.exited", $"Process exited: PID {process.Id}", new ProcessExit(process.Id, process.ExitCode));
    }

    /// <summary>Completes when creation succeeds. Cancellation after creation does not kill a user-owned process.</summary>
    public static StartedProcess Start(ProcessStartInfo start, PregelNodeContext context, CancellationToken token = default)
    {
        if (start.RedirectStandardOutput || start.RedirectStandardError)
            throw new ArgumentException("Detached processes must not leave redirected pipes without a reader.", nameof(start));
        token.ThrowIfCancellationRequested();
        if (!start.UseShellExecute) ToolProcessEnvironment.Apply(start, token);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
        var identity = new StartedProcess(process.Id, process.StartTime.ToUniversalTime().Ticks);
        context.Log($"Process created: PID {identity.Id}");
        return identity;
    }

    /// <summary>Waits for exit, streams both pipes, and kills/reaps the owned process on cancellation.</summary>
    public static async Task<ToolExecutionResult> RunAsync(ProcessStartInfo start,
        Action<string, string>? output = null, CancellationToken cancellationToken = default, bool captureOutput = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ToolProcessEnvironment.ApplyAsync(start, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
        using var reading = new CancellationTokenSource();
        var stdout = PumpAsync(process.StandardOutput, "stdout", output, reading.Token, captureOutput);
        var stderr = PumpAsync(process.StandardError, "stderr", output, reading.Token, captureOutput);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            reading.Cancel();
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task<string> PumpAsync(StreamReader reader, string source,
        Action<string, string>? output, CancellationToken token, bool captureOutput)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) is var count && count > 0)
        {
            var chunk = new string(buffer, 0, count);
            if (captureOutput) text.Append(chunk);
            output?.Invoke(chunk, source);
        }
        return text.ToString();
    }
}
