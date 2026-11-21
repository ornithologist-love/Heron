using System.Diagnostics;
using System.Text.Json;

namespace Heron.Pregel;

public sealed record PregelRunEvent(
    PregelRunId RunId,
    string Kind,
    string Message,
    NodeId? NodeId = null,
    PregelInvocationId? InvocationId = null,
    PregelAttemptId? AttemptId = null,
    int? AttemptNumber = null,
    string? Source = null,
    long Sequence = 0,
    DateTimeOffset Timestamp = default,
    string? ExecutionId = null,
    PregelError? Error = null,
    JsonElement? Data = null)
{
    public string? WorkingDirectory { get; init; }
    public string? NodeTitle { get; init; }
}

/// <summary>Called serially within a run. Observers must return promptly; UI observers should post to their dispatcher.</summary>
public interface IPregelRunObserver
{
    void OnEvent(PregelRunEvent value);
    string? GetRunDirectory(PregelRunId runId) => null;
}

/// <summary>Optional hooks. Override only the events your consumer needs; defaults do nothing.</summary>
public abstract class PregelRunObserver : IPregelRunObserver
{
    public virtual void OnEvent(PregelRunEvent value)
    {
        switch (value.Kind)
        {
            case "run.started": OnRunStarted(value); break;
            case "run.finished": OnRunFinished(value); break;
            case "node.started": OnNodeStarted(value); break;
            case "node.finished": OnNodeFinished(value); break;
            case "node.retry": OnNodeRetry(value); break;
            case "node.output": OnNodeOutput(value); break;
            case PregelProgress.EventKind:
                if (PregelProgress.TryRead(value, out var progress)) OnNodeProgress(value, progress);
                break;
            default: OnNotification(value); break;
        }
    }

    protected virtual void OnRunStarted(PregelRunEvent value) { }
    protected virtual void OnRunFinished(PregelRunEvent value) { }
    protected virtual void OnNodeStarted(PregelRunEvent value) { }
    protected virtual void OnNodeFinished(PregelRunEvent value) { }
    protected virtual void OnNodeRetry(PregelRunEvent value) { }
    protected virtual void OnNodeOutput(PregelRunEvent value) { }
    protected virtual void OnNodeProgress(PregelRunEvent value, PregelProgress progress) { }
    protected virtual void OnNotification(PregelRunEvent value) { }
}

internal sealed class PregelRunEventStream(IPregelRunObserver? observer, string workingDirectory,
    PregelGraphDefinition graph) : IPregelRunObserver
{
    private readonly object _gate = new();
    private long _sequence;
    private PregelGraphDefinition _graph = graph;
    private readonly string _executionId = Guid.NewGuid().ToString("N");
    internal void UpdateGraph(PregelGraphDefinition definition) { lock (_gate) _graph = definition; }

    public void OnEvent(PregelRunEvent value)
    {
        lock (_gate)
        {
            value = value with { Sequence = ++_sequence, Timestamp = DateTimeOffset.UtcNow,
                ExecutionId = _executionId, WorkingDirectory = workingDirectory,
                NodeTitle = value.NodeTitle ?? (value.NodeId is { } id && _graph.Nodes.TryGetValue(id, out var node)
                    ? node.Metadata?.Title ?? _graph.Stages.FirstOrDefault(stage => stage.EntryNode == id)?.Title
                    : null) };
            // Observability must not change node success, trigger retries, or hide a created process.
            try { observer?.OnEvent(value); }
            catch (Exception exception) { Trace.TraceWarning("Pregel observer failed: {0}", exception); }
        }
    }
}

/// <summary>Writes a readable run.log and lossless events.jsonl. Never owns the original software's log files.</summary>
public sealed class FilePregelRunObserver(string rootDirectory) : IPregelRunObserver
{
    private readonly object _gate = new();
    private readonly string _rootDirectory = Path.GetFullPath(rootDirectory);
    private readonly PregelRunLogFormatter _formatter = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string GetRunDirectory(PregelRunId runId)
    {
        var id = PregelId.Validate(runId.Value, nameof(runId));
        // Default GUIDs and ordinary lowercase IDs stay readable. Encode other IDs injectively,
        // including case and trailing dots, for case-insensitive Windows filesystems.
        var safe = !id.StartsWith("encoded-", StringComparison.Ordinal)
            && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');
        var name = safe ? id : "encoded-" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(id));
        return Path.Combine(_rootDirectory, "run-" + name);
    }

    public void OnEvent(PregelRunEvent value)
    {
        lock (_gate)
        {
            var directory = value.WorkingDirectory ?? GetRunDirectory(value.RunId);
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "events.jsonl"), JsonSerializer.Serialize(value, Json) + Environment.NewLine,
                new System.Text.UTF8Encoding(false));
            var lines = _formatter.Format(value);
            // Keep the file present even when the current chunk is only a partial line.
            var log = Path.Combine(directory, "run.log");
            if (lines.Count > 0)
                File.AppendAllText(log, string.Join(Environment.NewLine, lines) + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            else if (!File.Exists(log)) File.WriteAllText(log, "", new System.Text.UTF8Encoding(false));
        }
    }
}

public sealed class CompositePregelRunObserver(params IPregelRunObserver[] observers) : IPregelRunObserver
{
    public string? GetRunDirectory(PregelRunId runId) =>
        observers.Select(observer => observer.GetRunDirectory(runId)).FirstOrDefault(directory => directory != null);

    public void OnEvent(PregelRunEvent value)
    {
        foreach (var observer in observers)
        {
            try { observer.OnEvent(value); }
            catch (Exception exception) { Trace.TraceWarning("Pregel observer failed: {0}", exception); }
        }
    }
}
