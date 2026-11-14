using System.Security.Cryptography;
using System.Text;
using Heron.Pregel;

namespace Heron.Pregel.Automation;

public sealed partial class AutomationHost
{
    /// <summary>Describes an already registered graph without evaluating or loading another script.</summary>
    public string? GraphFingerprint(string taskId)
        => DescribeTaskGraph(taskId)?.Fingerprint;

    public PregelGraphDefinition? DescribeTaskGraph(string taskId)
    {
        lock (_gate)
        {
            var task = _document.Tasks.FirstOrDefault(item => item.Id == taskId);
            if (task == null || !_templates.TryGetValue(task.TemplateId, out var template)
                || template is not IAutomationGraphTemplate graph) return null;
            try { return graph.CompiledGraph.Definition; }
            catch (Exception error)
            {
                Serilog.Log.Debug(error, "[Automation] could not describe graph for {TaskId}", taskId);
                return null;
            }
        }
    }

    /// <summary>Completes result publication after a child committed its terminal checkpoint before its owner exited.</summary>
    public async Task<AutomationRunRecord?> RecoverSubgraphOutputAsync(string taskId, string runId,
        CancellationToken token = default)
    {
        AutomationRunRecord? record;
        IAutomationSubgraphResultReader? reader;
        IDisposable? lease;
        lock (_gate)
        {
            record = FindRun(taskId, runId);
            if (record?.Subgraph == null || record.EncodedOutput != null || record.OwnerIsAlive
                || record.TaskSnapshot is not { } task || !_templates.TryGetValue(task.TemplateId, out var template)) return record;
            if (record.Status is AutomationRunStatus.Failed or AutomationRunStatus.Cancelled or AutomationRunStatus.Skipped)
                return record;
            try { reader = BindSubgraph(template, record.Subgraph) as IAutomationSubgraphResultReader; }
            catch (Exception error)
            {
                Serilog.Log.Debug(error, "[Automation] child result remains pending for {RunId}", runId);
                return record;
            }
            if (reader == null) return record;
            lease = _store.TryAcquireTask(ExecutionLeaseKey(taskId, record.RemoteRequestId, record.Subgraph));
            if (lease == null) return record;
        }
        using var resultLease = lease;
        AutomationRunResult? result;
        try { result = await reader.ReadCompletedResultAsync(record, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Serilog.Log.Debug(error, "[Automation] could not restore encoded output for {RunId}", runId);
            return record;
        }
        if (result == null) return record;
        lock (_gate)
        {
            using var update = _store.BeginUpdate();
            var current = FindRun(taskId, runId);
            if (current == null || current.OwnerIsAlive || current.EncodedOutput != null || current.Revision != record.Revision) return current;
            var completed = AutomationRunRecorder.Finish(current, result);
            if (!_store.UpsertIfTaskExists(completed)) return null;
            if (!_lastRuns.TryGetValue(taskId, out var last) || completed.QueuedAt >= last.QueuedAt) _lastRuns[taskId] = completed;
            record = completed;
        }
        Changed?.Invoke();
        return record;
    }

    private static IAutomationTemplate BindSubgraph(IAutomationTemplate template, PregelSubgraphExecutionRequest request) =>
        template is IAutomationGraphTemplate graph ? graph.BindSubgraph(request)
            : throw new InvalidOperationException("此模板不支持完整子图执行");

    private static string ExecutionLeaseKey(string taskId, string? remoteRequestId, PregelSubgraphExecutionRequest? subgraph) =>
        subgraph == null ? taskId : "remote-child-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(remoteRequestId ?? throw new InvalidOperationException("远程子图缺少请求标识"))));

    private static PregelSubgraphExecutionRequest CopySubgraph(PregelSubgraphExecutionRequest request) => request with
    {
        MutationPath = request.MutationPath.ToArray(), Input = request.Input.Copy(), ResumeValue = request.ResumeValue?.Copy()
    };
}

internal interface IAutomationSubgraphResultReader
{
    Task<AutomationRunResult?> ReadCompletedResultAsync(AutomationRunRecord record, CancellationToken token);
}
