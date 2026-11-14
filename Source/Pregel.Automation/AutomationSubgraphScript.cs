using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heron.Pregel;

namespace Heron.Pregel.Automation;

/// <summary>Runs one installed mutation template with encoded input, independently of the root task's business form.</summary>
internal sealed class AutomationSubgraphScript : IAutomationReviewTemplate, IAutomationSubgraphResultReader
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly string _root;
    private readonly CompiledPregelGraph _graph;
    private readonly PregelSubgraphExecutionRequest _request;
    private readonly PregelSubgraphDefinition _definition;
    private readonly PregelEngine _engine;

    internal AutomationSubgraphScript(string id, string path, string root, CompiledPregelGraph graph,
        PregelSubgraphExecutionRequest request)
    {
        Id = id; _path = path; _root = root; _graph = graph; _request = request;
        _definition = PregelSubgraphs.Resolve(graph, request);
        _engine = new PregelEngine(new FilePregelCheckpointStore(Path.Combine(root, "checkpoints")));
    }

    public string Id { get; }
    public string Title => _definition.Definition.GraphId;
    public IReadOnlyList<AutomationField> Fields => [];
    public string? Validate(AutomationTask task) => !string.IsNullOrWhiteSpace(_request.Target)
        && !string.Equals(_request.Target, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
        ? "子图目标机器与当前执行机器不一致" : null;
    public IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task) => AutomationStepTopology.FromGraph(_definition.Definition);

    public async Task<AutomationRunResult> RunAsync(AutomationRunContext context, CancellationToken token)
    {
        var state = new RunState(_request, 1, _definition.Definition.Finally != null);
        await WriteStateAsync(context.RunId, state).ConfigureAwait(false);
        return await ExecuteAsync(context, state, _request, token).ConfigureAwait(false);
    }

    public async Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, AutomationReview review,
        AutomationReviewDecision decision, CancellationToken token)
    {
        if (decision.Action is not (AutomationReviewAction.Approve or AutomationReviewAction.Abandon))
            throw new InvalidOperationException("子图只支持确认继续或终止");
        var state = await ReadStateAsync(context.RunId, token).ConfigureAwait(false);
        EnsureSameRequest(state.Request);
        var checkpoint = await _engine.GetStateAsync(new(_request.ChildRunId), token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("子图检查点不存在，未重新执行流程");
        var request = _request with { ResumeInterruptId = null, ResumeValue = null };
        var executeToken = token;
        var abandoning = state.AbandonRequested || decision.Action == AutomationReviewAction.Abandon;
        if (abandoning)
        {
            // A pending Finally is retried with a live token; cancelling a suspended body enters its Finally.
            if (checkpoint.Finalization == null) executeToken = new CancellationToken(true);
        }
        else if (checkpoint.Interrupt is { } interrupt)
        {
            if (review.Id.StartsWith("subgraph-recovery-", StringComparison.Ordinal))
            {
                var pending = FromCheckpoint(_request.ChildRunId, checkpoint);
                await WriteStateAsync(context.RunId, state with { Revision = state.Revision + 1, Result = pending }).ConfigureAwait(false);
                return Describe(pending, state.Revision + 1);
            }
            if (review.Id != interrupt.InterruptId || decision.ContentHash != Hash(InterruptContent(interrupt.Reason, interrupt.Payload)))
                throw new InvalidOperationException("子图中断已变化，请重新载入后再确认");
            request = request with { ResumeInterruptId = interrupt.InterruptId, ResumeValue = interrupt.Payload?.Copy() };
        }
        else if (checkpoint.Status == PregelRunStatus.Running && checkpoint.Finalization == null)
        {
            if (!state.HasFinally) throw new InvalidOperationException("子图未发现待收尾状态，未重新执行正文");
            executeToken = new CancellationToken(true);
        }
        state = state with { Revision = state.Revision + 1, AbandonRequested = abandoning };
        await WriteStateAsync(context.RunId, state).ConfigureAwait(false);
        return await ExecuteAsync(context, state, request, executeToken).ConfigureAwait(false);
    }

    public async Task<AutomationRunResult?> ReadCompletedResultAsync(AutomationRunRecord record, CancellationToken token)
    {
        var checkpoint = await _engine.GetStateAsync(new(_request.ChildRunId), token).ConfigureAwait(false);
        if (checkpoint?.Status != PregelRunStatus.Completed) return null;
        // Core only reads the committed output for a terminal child. It never executes its body again.
        var result = await _engine.RunSubgraphAsync(_graph, _request with { ResumeInterruptId = null, ResumeValue = null },
            new PregelRunOptions { ExecutionMachine = Environment.MachineName }, token).ConfigureAwait(false);
        var state = await ReadStateAsync(record.RunId, token).ConfigureAwait(false);
        EnsureSameRequest(state.Request);
        return await PersistResultAsync(record.RunId, state, result).ConfigureAwait(false);
    }

    private async Task<AutomationRunResult> ExecuteAsync(AutomationRunContext context, RunState state,
        PregelSubgraphExecutionRequest request, CancellationToken token)
    {
        var options = new PregelRunOptions(Observer: context.Observer,
            FinalizationCancellationToken: context.FinalizationCancellationToken)
        {
            ExecutionMachine = Environment.MachineName, SubgraphExecutor = context.SubgraphExecutor,
            SubgraphScope = new(request.RootGraphFingerprint, request.MutationPath.ToArray()),
            // Remote invocations cannot replace a suspended child's inherited parent lease.
            // Fail while this segment still owns its resources so normal cleanup can finish.
            RejectNodeInterruptsWith = new("subgraph_remote_review_unsupported",
                "跨机器子图暂不支持人工审核恢复，请将审核放在服务器父图或本地执行。")
        };
        var directory = Path.IsPathFullyQualified(_path) ? Path.GetDirectoryName(_path)! : RunDirectory(_root, context.RunId);
        using var aiScope = AutomationRuntime.EnterScriptScope(context.Task.Id, directory);
        PregelSubgraphExecutionResult result;
        try
        {
            result = await _engine.RunSubgraphAsync(_graph, request, options, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            PregelCheckpoint? checkpoint;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                checkpoint = await _engine.GetStateAsync(new(request.ChildRunId), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception readError)
            {
                return Pending(request.ChildRunId, state.Revision, "子图状态待读取", error.Message + "\n" + readError.Message);
            }
            if (checkpoint?.Status == PregelRunStatus.Completed)
                return Pending(request.ChildRunId, state.Revision, "子图结果待恢复", error.Message,
                    new(request.ChildRunId, PregelRunStatus.Completed));
            if (checkpoint != null)
            {
                var pending = FromCheckpoint(request.ChildRunId, checkpoint);
                await WriteStateAsync(context.RunId, state with { Result = pending }).ConfigureAwait(false);
                return Describe(pending, state.Revision, "子图执行被中断：" + error.Message);
            }
            var failed = new PregelSubgraphExecutionResult(request.ChildRunId, PregelRunStatus.Failed,
                Error: new(error is PregelException pregel ? pregel.Code : "subgraph_execution_failed", error.Message));
            return await PersistResultAsync(context.RunId, state, failed).ConfigureAwait(false);
        }
        return await PersistResultAsync(context.RunId, state, result).ConfigureAwait(false);
    }

    private async Task<AutomationRunResult> PersistResultAsync(string runId, RunState state, PregelSubgraphExecutionResult result)
    {
        if (result.Status == PregelRunStatus.Completed)
        {
            if (result.Output == null || result.Output.CodecId != _definition.OutputCodecId)
                throw new InvalidDataException("子图成功检查点缺少匹配的编码结果，未发布成功状态");
            var output = Path.Combine(RunDirectory(_root, runId), "result.json");
            await WriteAtomicAsync(output, JsonSerializer.Serialize(result.Output, Json)).ConfigureAwait(false);
        }
        await WriteStateAsync(runId, state with { Result = result }).ConfigureAwait(false);
        var finished = Describe(result, state.Revision);
        return result.Status == PregelRunStatus.Completed
            ? finished with { ArtifactPath = Path.Combine(RunDirectory(_root, runId), "result.json") } : finished;
    }

    private static AutomationRunResult Describe(PregelSubgraphExecutionResult result, int revision, string? detail = null)
    {
        AutomationRunResult value;
        if (result.Status == PregelRunStatus.Completed)
            value = result.Output == null
                ? Pending(result.ChildRunId, revision, "子图结果待恢复", "检查点已完成，编码结果尚未恢复", result)
                : AutomationRunResult.Succeeded("子图执行完成") with { EncodedOutput = result.Output.Copy() };
        else if (result.Status == PregelRunStatus.Cancelled) value = AutomationRunResult.Cancelled(result.Error?.Message ?? "子图已取消");
        else if (result.Status == PregelRunStatus.Failed) value = AutomationRunResult.Failed(result.Error?.Message ?? "子图执行失败");
        else if (result.Status == PregelRunStatus.Interrupted && result.Interrupt is { } interrupt)
        {
            var content = InterruptContent(interrupt.Reason, interrupt.Payload);
            value = AutomationRunResult.AwaitingReview(new(interrupt.InterruptId, revision, interrupt.Reason,
                content, Hash(content), DateTimeOffset.UtcNow, detail ?? "")
                { CanRevise = false, ApproveLabel = "确认并继续" }, "子图等待人工确认");
        }
        else value = Pending(result.ChildRunId, revision,
            result.Status == PregelRunStatus.FinalizationPending ? "子图待收尾" : "子图状态待确认",
            detail ?? result.Error?.Message ?? "原运行已保留，请读取检查点后继续处理", result);
        return value with { SubgraphResult = result };
    }

    private static AutomationRunResult Pending(string id, int revision, string title, string content,
        PregelSubgraphExecutionResult? result = null) => AutomationRunResult.AwaitingReview(new("subgraph-recovery-" + id,
            revision, title, content, Hash(content), DateTimeOffset.UtcNow)
        { CanRevise = false, ApproveLabel = "恢复原运行", Hint = "读取原运行的检查点，继续必要收尾或恢复已完成的结果。" }, title)
        with { SubgraphResult = result };

    private static PregelSubgraphExecutionResult FromCheckpoint(string id, PregelCheckpoint checkpoint) => new(id,
        checkpoint.Finalization != null ? PregelRunStatus.FinalizationPending : checkpoint.Status, null, checkpoint.Error,
        checkpoint.Interrupt is { } interrupt ? new(interrupt.InterruptId, interrupt.Reason, interrupt.Payload?.Copy()) : null);

    internal static AutomationRunResult? RecoverInterrupted(string stateRoot, string templateId, AutomationRunRecord record)
    {
        if (record.Subgraph is not { } request) return null;
        var root = Path.Combine(stateRoot, "script-runs", SafeSegment(templateId));
        RunState? state = null;
        try
        {
            var file = StateFile(root, record.RunId);
            if (!File.Exists(file)) return AutomationRunResult.Failed("子图进程在创建持久运行前退出");
            state = JsonSerializer.Deserialize<RunState>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException("子图运行记录损坏");
            if (state.Request.ChildRunId != request.ChildRunId || state.Request.GraphFingerprint != request.GraphFingerprint)
                throw new InvalidDataException("子图记录身份与请求不一致");
            if (state.Result is { Status: PregelRunStatus.Completed, Output: not null } saved)
                return Describe(saved, state.Revision) with { ArtifactPath = Path.Combine(RunDirectory(root, record.RunId), "result.json") };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var checkpoint = new FilePregelCheckpointStore(Path.Combine(root, "checkpoints"))
                .LoadLatestAsync(new(request.ChildRunId), timeout.Token).AsTask().GetAwaiter().GetResult();
            if (checkpoint == null) return AutomationRunResult.Failed("子图进程退出且未发现检查点");
            var result = FromCheckpoint(request.ChildRunId, checkpoint);
            if (checkpoint.Status == PregelRunStatus.Running && checkpoint.Finalization == null && !state.HasFinally)
                return AutomationRunResult.Cancelled("执行进程退出，子图运行中断") with { SubgraphResult = result };
            return Describe(result, state.Revision, "执行进程退出，原子图检查点已保留");
        }
        catch (Exception error)
        {
            return Pending(request.ChildRunId, state?.Revision ?? 1, "子图状态待读取", error.Message);
        }
    }

    private void EnsureSameRequest(PregelSubgraphExecutionRequest request)
    {
        if (request.ChildRunId != _request.ChildRunId || request.GraphFingerprint != _request.GraphFingerprint
            || request.RootGraphFingerprint != _request.RootGraphFingerprint || !request.MutationPath.SequenceEqual(_request.MutationPath))
            throw new InvalidDataException("持久化子图与本次请求不一致，未重新执行流程");
    }

    private async Task<RunState> ReadStateAsync(string runId, CancellationToken token) =>
        JsonSerializer.Deserialize<RunState>(await File.ReadAllTextAsync(StateFile(_root, runId), token).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("子图运行记录损坏");
    private Task WriteStateAsync(string runId, RunState state) => WriteAtomicAsync(StateFile(_root, runId), JsonSerializer.Serialize(state, Json));
    private static async Task WriteAtomicAsync(string file, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file + ".tmp", value, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
        File.Move(file + ".tmp", file, true);
    }
    private static string InterruptContent(string reason, PregelEncodedValue? payload) => reason
        + (payload == null ? "" : "\n\n" + Encoding.UTF8.GetString(payload.Data));
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string StateFile(string root, string id) => Path.Combine(RunDirectory(root, id), "subgraph.json");
    private static string RunDirectory(string root, string id) => Path.Combine(root, SafeSegment(id));
    private static string SafeSegment(string id) => !string.IsNullOrWhiteSpace(id) && id is not ("." or "..")
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains('/') && !id.Contains('\\')
        ? id : throw new InvalidDataException("无效子图存储标识");
    private sealed record RunState(PregelSubgraphExecutionRequest Request, int Revision, bool HasFinally,
        bool AbandonRequested = false, PregelSubgraphExecutionResult? Result = null);
}
