using System.Diagnostics;
using System.Text;

namespace Heron.Core.Process;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Bounds retained stdout/stderr while callbacks continue receiving every line.</summary>
public sealed record ProcessOutputCapture(int MaxCharactersPerStream, int MaxLinesPerStream = 10_000)
{
    public static ProcessOutputCapture Tail(int maxCharactersPerStream) => new(maxCharactersPerStream);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        Action<string, string>? output = null,
        CancellationToken cancellationToken = default,
        ProcessOutputCapture? capture = null);
}

public sealed class DefaultProcessRunner : IProcessRunner
{
    private readonly string _processDomain;

    public DefaultProcessRunner(string processDomain = OwnedProcessDomains.General)
    {
        _processDomain = processDomain;
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null,
        Action<string, string>? output = null,
        CancellationToken cancellationToken = default,
        ProcessOutputCapture? capture = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput != null,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment != null)
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (capture is { MaxCharactersPerStream: <= 0 } or { MaxLinesPerStream: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(capture));
        var stdout = new CapturedProcessOutput(capture?.MaxCharactersPerStream, capture?.MaxLinesPerStream);
        var stderr = new CapturedProcessOutput(capture?.MaxCharactersPerStream, capture?.MaxLinesPerStream);
        var stdoutCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stdoutCompleted.TrySetResult();
                return;
            }
            stdout.AppendLine(e.Data);
            output?.Invoke("stdout", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stderrCompleted.TrySetResult();
                return;
            }
            stderr.AppendLine(e.Data);
            output?.Invoke("stderr", e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程: {executable}");
        OwnedProcessRegistry.Register(process, _processDomain);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            if (standardInput != null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutCompleted.Task, stderrCompleted.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                output?.Invoke("stderr", "等待进程退出：" + ex.Message);
            }
            // Do not release the caller's operation gate while the cancelled process can
            // still write files. If termination fails, keep waiting for its natural exit.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutCompleted.Task, stderrCompleted.Task).ConfigureAwait(false);
            throw;
        }
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class CapturedProcessOutput(int? maxCharacters, int? maxLines)
    {
        private const string TruncatedMarker = "[... earlier process output truncated ...]";
        private readonly object _gate = new();
        private readonly Queue<string>? _tail = maxCharacters.HasValue ? new Queue<string>() : null;
        private readonly StringBuilder? _full = maxCharacters.HasValue ? null : new StringBuilder();
        private int _characters;
        private bool _truncated;

        public void AppendLine(string line)
        {
            lock (_gate)
            {
                if (_full != null)
                {
                    _full.AppendLine(line);
                    return;
                }

                var limit = maxCharacters!.Value;
                if (line.Length + Environment.NewLine.Length > limit)
                {
                    line = line[^Math.Min(line.Length, limit)..];
                    _truncated = true;
                }
                _tail!.Enqueue(line);
                _characters += line.Length + Environment.NewLine.Length;
                while ((_characters > limit || _tail.Count > maxLines!.Value) && _tail.Count > 1)
                {
                    var removed = _tail.Dequeue();
                    _characters -= removed.Length + Environment.NewLine.Length;
                    _truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                if (_full != null)
                    return _full.ToString();
                var text = string.Join(Environment.NewLine, _tail!);
                if (!_truncated)
                    return text;
                var limit = maxCharacters!.Value;
                var prefix = TruncatedMarker + Environment.NewLine;
                if (prefix.Length >= limit)
                    return text.Length <= limit ? text : text[^limit..];
                var tailBudget = limit - prefix.Length;
                if (text.Length > tailBudget)
                    text = text[^tailBudget..];
                return prefix + text;
            }
        }
    }
}
