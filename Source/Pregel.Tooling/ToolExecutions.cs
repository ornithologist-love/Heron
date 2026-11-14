namespace Heron.Pregel.Tooling;

public abstract record ToolExecution
{
    private protected ToolExecution(
        string script,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        Script = script;
        WorkingDirectory = workingDirectory;
        Environment = environment;
    }

    public string Script { get; }
    public string? WorkingDirectory { get; }
    public IReadOnlyDictionary<string, string>? Environment { get; }
}

public sealed record BashExecution : ToolExecution
{
    internal BashExecution(
        string script,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
        : base(script, workingDirectory, environment)
    {
    }
}

public sealed record PSExecution : ToolExecution
{
    internal PSExecution(
        string script,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
        : base(script, workingDirectory, environment)
    {
    }
}

public static class ExecutionTools
{
    public static BashExecution NewBashExecution(
        string script,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new(script, workingDirectory, environment);

    public static PSExecution NewPSExecution(
        string script,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null) =>
        new(script, workingDirectory, environment);
}

public sealed record ToolExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IToolExecutionRunner
{
    ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolExecution execution,
        CancellationToken cancellationToken = default);

    async ValueTask<ToolExecutionResult> ExecuteAsync(
        ToolExecution execution, Action<string, string> output,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(execution, cancellationToken).ConfigureAwait(false);
        if (result.StandardOutput.Length > 0) output(result.StandardOutput, "stdout");
        if (result.StandardError.Length > 0) output(result.StandardError, "stderr");
        return result;
    }
}
