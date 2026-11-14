using System.Diagnostics;

namespace Heron.Pregel.Tooling;

public sealed record LocalToolExecutionOptions(
    string BashExecutable = "bash",
    string PowerShellExecutable = "pwsh");

public sealed class LocalToolExecutionRunner : IToolExecutionRunner
{
    private readonly LocalToolExecutionOptions _options;

    public LocalToolExecutionRunner(LocalToolExecutionOptions? options = null) =>
        _options = options ?? new LocalToolExecutionOptions();

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecution execution,
        CancellationToken cancellationToken = default) => ExecuteCoreAsync(execution, null, cancellationToken);

    public ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecution execution, Action<string, string> output,
        CancellationToken cancellationToken = default) => ExecuteCoreAsync(execution, output, cancellationToken);

    private async ValueTask<ToolExecutionResult> ExecuteCoreAsync(ToolExecution execution,
        Action<string, string>? output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        try
        {
            return await ProcessExecution.RunAsync(CreateStartInfo(execution), output, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new PregelException("tool_process_start_failed", $"Could not start {ToolName(execution)} execution.", exception);
        }
    }

    internal ProcessStartInfo CreateStartInfo(ToolExecution execution)
    {
        var startInfo = execution switch
        {
            BashExecution => CreateBashStartInfo(),
            PSExecution => CreatePowerShellStartInfo(),
            _ => throw new PregelException(
                "unsupported_tool_execution",
                $"Unsupported tool execution type '{execution.GetType().Name}'."),
        };
        startInfo.ArgumentList.Add(execution.Script);
        if (!string.IsNullOrWhiteSpace(execution.WorkingDirectory))
        {
            startInfo.WorkingDirectory = execution.WorkingDirectory;
        }

        if (execution.Environment is { } environment)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    private ProcessStartInfo CreateBashStartInfo()
    {
        var startInfo = BaseStartInfo(_options.BashExecutable);
        startInfo.ArgumentList.Add("-lc");
        return startInfo;
    }

    private ProcessStartInfo CreatePowerShellStartInfo()
    {
        var startInfo = BaseStartInfo(_options.PowerShellExecutable);
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        return startInfo;
    }

    private static ProcessStartInfo BaseStartInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };

    private static string ToolName(ToolExecution execution) => execution switch
    {
        BashExecution => "Bash",
        PSExecution => "PowerShell",
        _ => execution.GetType().Name,
    };

}
