using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Heron.ClientFramework.Infrastructure;

public static class ClientLogExtensions
{
    public static ClientLogOperation BeginOperation(this ILogger logger, string name,
        LogLevel level = LogLevel.Information, string? taskId = null) => new(logger, name, level, taskId);

    /// <summary>Do not pass authenticated HTTP exceptions directly to ILogger: messages may contain credentials.</summary>
    public static void LogFailure(this ILogger logger, string operation, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var causes = new List<string>();
        for (Exception? cause = error; cause != null && causes.Count < 4; cause = cause.InnerException)
            causes.Add($"{cause.GetType().FullName} (0x{cause.HResult:X8})\n{cause.StackTrace}");
        logger.LogError(new EventId(1003, "OperationFailed"),
            "{Operation} failed ExceptionType={ExceptionType} HResult={HResult} ExceptionStack={ExceptionStack}",
            operation, error.GetType().FullName, error.HResult, string.Join("\nCaused by: ", causes));
    }
}

/// <summary>One operation, one terminal outcome. Dispose without an outcome is recorded as incomplete.</summary>
public sealed class ClientLogOperation : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly LogLevel _level;
    private readonly IDisposable? _scope;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _ended;
    private int _disposed;

    public string OperationId { get; } = Guid.NewGuid().ToString("N");

    internal ClientLogOperation(ILogger logger, string name, LogLevel level, string? taskId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (level is not (LogLevel.Trace or LogLevel.Debug or LogLevel.Information))
            throw new ArgumentOutOfRangeException(nameof(level));
        _logger = logger;
        _name = name;
        _level = level;
        var context = new Dictionary<string, object> { ["OperationId"] = OperationId, ["OperationName"] = name };
        if (!string.IsNullOrWhiteSpace(taskId)) context["TaskId"] = taskId;
        _scope = logger.BeginScope(context);
        _logger.Log(_level, new EventId(1000, "OperationStarted"), "{Operation} started", _name);
    }

    public void Complete() => End("succeeded", _level, new(1001, "OperationCompleted"));
    public void Cancel() => End("cancelled", _level, new(1002, "OperationCancelled"));

    public void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["ElapsedMs"] = _watch.ElapsedMilliseconds, ["Outcome"] = "failed" });
        _logger.LogFailure(_name, error);
    }

    private void End(string outcome, LogLevel level, EventId id)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _logger.Log(level, id, "{Operation} {Outcome} elapsed_ms={ElapsedMs}", _name, outcome, _watch.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { End("incomplete", LogLevel.Warning, new(1004, "OperationIncomplete")); }
        finally { _scope?.Dispose(); }
    }
}
