using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Heron.Pregel;
using Heron.Pregel.Tooling;

namespace Heron.Pregel.Automation;

/// <summary>Shared persistent execution for graph-returning scripts and graph-backed templates.</summary>
[DisplayDescription("运行本地 CSX 流程")]
public sealed class AutomationGraphScript : IAutomationReviewTemplate, IAutomationInputSchemaConsumer, IAutomationReexecutionTemplate, IAutomationGraphTemplate
{
    public bool SupportsReexecution => true;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly DynamicPregelGraph _graph;
    private readonly PregelEngine _engine;
    private readonly string _root;
    private readonly string _path;
    private readonly Type? _inputType;
    private readonly Func<AutomationTask, object?>? _input;
    private readonly Func<AutomationTask, object?, AutomationRunResult>? _complete;
    private readonly string _failureSummary;
    private AutomationInputSchema? _inputSchema;
    internal bool WriteResultArtifact { private get; init; } = true;
    public AutomationGraphScript(string id, string path, string stateRoot, DynamicPregelGraph graph,
        Func<AutomationTask, object?>? input = null, Func<AutomationTask, object?, AutomationRunResult>? complete = null,
        string failureSummary = "脚本执行失败")
    {
        Id = id; _path = path; _graph = graph; _inputType = graph.InputType;
        _input = input; _complete = complete;
        _failureSummary = failureSummary;
        _inputSchema = _inputType is null ? null : AutomationInputSchema.Annotated(_inputType);
        _root = RunRoot(stateRoot, id);
        _engine = new PregelEngine(new FilePregelCheckpointStore(Path.Combine(_root, "checkpoints")));
    }

    void IAutomationInputSchemaConsumer.ConfigureInput(PregelInputSchema definition)
    {
        if (_inputSchema != null) _inputSchema = _inputSchema.WithDefinition(definition);
    }

    public static IAutomationTemplate Create(DynamicPregelGraph graph, string path, string id, string stateRoot) =>
        new AutomationGraphScript(id, path, stateRoot, graph);

    private object Input(AutomationTask task)
    {
        if (_inputSchema is { } schema) return schema.Read(task);
        return _inputType == typeof(string) ? task.Parameter("input")
            : AutomationScriptInput.Parse(task.Parameter("input")).Values.Deserialize(_inputType!, Json)
                ?? throw new InvalidDataException("输入不能为 null");
    }

    public string Id { get; }
    public CompiledPregelGraph CompiledGraph => _graph.Compiled;
    public IAutomationTemplate BindSubgraph(PregelSubgraphExecutionRequest request) =>
        new AutomationSubgraphScript(Id, _path, _root, _graph.Compiled, request);
    public string Title => Path.GetFileNameWithoutExtension(_path);
    public IReadOnlyList<AutomationField> Fields => _inputSchema?.Fields ?? (_inputType == null ? [] : [
        new("input", _inputType == typeof(string) ? "输入" : "输入 JSON", DisplayFieldKind.Multiline,
            _inputType == typeof(string) ? "" : "{}", Hint: "类型：" + _inputType.Name)]);
    public string? Validate(AutomationTask task)
    {
        try { if (_inputType != null) Input(task); return null; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException) { return "输入格式不正确：" + ex.Message; }
    }
    public IReadOnlyList<AutomationStep> PlanSteps(AutomationTask task) => AutomationStepTopology.FromGraph(_graph.Compiled.Definition);

    public async Task<AutomationRunResult> RunAsync(AutomationRunContext context, CancellationToken token)
    {
        if (context.Reexecution is { } request && context.ReexecutionSourceRunId is { } sourceId)
        {
            var sourceState = await SourceStateAsync(sourceId, token);
            var source = new PregelRunId(sourceState.RunId);
            var id = new PregelRunId(Guid.NewGuid().ToString("N"));
            // A derived run keeps the source's manual selection, so retrying part of a graph stays partial.
            var options = Options(context, sourceState);
            await _engine.CreateReexecutionAsync(_graph.Compiled, source, id, request, options, token);
            await WriteStateAsync(context, new(id.Value, 1, _graph.Compiled.Definition.Finally != null)
                { SkipNodes = sourceState.SkipNodes, SkipStages = sourceState.SkipStages }, CancellationToken.None);
            var derived = await _graph.RestoreAsync(id, _engine, options, token);
            return await ExecuteAsync(context, derived, 1, () => derived.RunAsync(token), token);
        }
        var initial = new RunState("", 1, _graph.Compiled.Definition.Finally != null)
        {
            SkipNodes = context.ManualSkip?.Nodes.Select(node => node.Value).Order(StringComparer.Ordinal).ToArray(),
            SkipStages = context.ManualSkip?.Stages.Order(StringComparer.Ordinal).ToArray()
        };
        var run = _graph.Prepare(_input != null ? _input(context.Task) : _inputType == null ? null : Input(context.Task),
            Options(context, initial));
        // Identity and the manual selection must be durable before any node can execute.
        await WriteStateAsync(context, initial with { RunId = run.Id.Value }, CancellationToken.None);
        return await ExecuteAsync(context, run, 1, () => run.RunAsync(token), token);
    }

    public async Task<PregelReexecutionPreview> PreviewReexecutionAsync(string sourceRunId, PregelReexecutionRequest request,
        CancellationToken token) =>
        await _engine.PreviewReexecutionAsync(_graph.Compiled, new PregelRunId((await SourceStateAsync(sourceRunId, token)).RunId), request,
            cancellationToken: token);

    // Every engine call of one automation run uses the selection saved with that run: the engine does not persist it.
    private PregelRunOptions Options(AutomationRunContext context, RunState state) =>
        new(Observer: context.Observer, FinalizationCancellationToken: context.FinalizationCancellationToken)
        {
            Engine = _engine, ExecutionMachine = Environment.MachineName, SubgraphExecutor = context.SubgraphExecutor,
            ManualSkip = state.SkipNodes is { Length: > 0 } || state.SkipStages is { Length: > 0 }
                ? new((state.SkipNodes ?? []).Select(node => new NodeId(node)).ToHashSet(), (state.SkipStages ?? []).ToHashSet(),
                    AutomationManualSelection.SkipReason)
                : null
        };

    private async Task<RunState> SourceStateAsync(string automationRunId, CancellationToken token)
    {
        string json;
        try { json = await File.ReadAllTextAsync(StateFile(_root, automationRunId), token); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException("原运行没有持久化的图记录，无法从检查点派生。", ex);
        }
        return JsonSerializer.Deserialize<RunState>(json) ?? throw new InvalidDataException("原运行的图记录损坏");
    }

    public async Task<AutomationRunResult> ReviewAsync(AutomationRunContext context, AutomationReview review,
        AutomationReviewDecision decision, CancellationToken token)
    {
        var state = JsonSerializer.Deserialize<RunState>(await File.ReadAllTextAsync(StateFile(context), token))
            ?? throw new InvalidDataException("脚本运行记录损坏");
        var runId = new PregelRunId(state.RunId);
        var run = await _graph.RestoreAsync(runId, _engine, Options(context, state), token);
        if (decision.Action is not (AutomationReviewAction.Abandon or AutomationReviewAction.Approve))
            throw new InvalidOperationException("普通 Graph 只支持确认继续或终止");
        if (decision.Action == AutomationReviewAction.Abandon || state.AbandonRequested)
        {
            var stopped = state with { Revision = state.Revision + 1, AbandonRequested = true };
            await WriteStateAsync(context, stopped, CancellationToken.None);
            // An already pending Finally must receive a live token so it can finish.
            // For a suspended body, a cancelled token enters Finally without executing ordinary nodes.
            var stopToken = run.Result?.Checkpoint.Finalization != null ? token : new CancellationToken(true);
            return await ExecuteAsync(context, run, stopped.Revision, () => run.RunAsync(stopToken), token, abandoning: true);
        }
        Func<Task<PregelRunResult>> execute;
        if (run.Result?.Interrupt is { } interrupt)
        {
            if (review.Id.StartsWith("recovery-", StringComparison.Ordinal))
                return PendingInterrupt(interrupt, state.Revision + 1, Path.GetFileName(_path));
            if (interrupt.InterruptId != review.Id || Hash(Content(interrupt)) != decision.ContentHash)
                throw new InvalidOperationException("中断内容已经变化，请重新载入运行记录");
            execute = interrupt.Payload is { } payload
                ? () => run.ResumeWithValueAsync(interrupt.InterruptId, payload, token)
                : () => run.ResumeAsync(interrupt.InterruptId, token);
        }
        else
        {
            // A recovery approval must never restart ordinary nodes after a failed state lookup.
            if (run.Result is { Status: PregelRunStatus.Running, Checkpoint.Finalization: null })
            {
                if (!state.HasFinally) throw new InvalidOperationException("未发现待收尾状态，已保留原运行，请核对 checkpoint；未重新执行流程节点");
                execute = () => run.RunAsync(new CancellationToken(true));
            }
            else execute = () => run.RunAsync(token);
        }
        var revision = state.Revision + 1;
        await WriteStateAsync(context, state with { Revision = revision }, CancellationToken.None);
        return await ExecuteAsync(context, run, revision, execute, token);
    }

    private async Task<AutomationRunResult> ExecuteAsync(AutomationRunContext context, PregelRun run,
        int revision, Func<Task<PregelRunResult>> execute, CancellationToken token, bool abandoning = false)
    {
        PregelRunResult result;
        // Built-in graphs pass a display title, not a script path. Their AI base belongs to this run.
        var baseDirectory = Path.IsPathFullyQualified(_path)
            ? Path.GetDirectoryName(_path)!
            : Path.GetDirectoryName(StateFile(context))!;
        using var aiScope = AutomationRuntime.EnterScriptScope(context.Task.Id, baseDirectory);
        try { result = await execute(); }
        catch (Exception ex)
        {
            PregelCheckpoint? checkpoint = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                checkpoint = await _engine.GetStateAsync(run.Id, timeout.Token);
            }
            catch (Exception readError)
            {
                Serilog.Log.Warning(readError, "[Automation] could not read graph run {RunId}", run.Id);
                return PendingStateRead(run.Id.Value, revision, Path.GetFileName(_path), ex.Message + "\n" + readError.Message);
            }
            if (checkpoint?.Finalization is null)
            {
                if (checkpoint?.Status == PregelRunStatus.Running && _graph.Compiled.Definition.Finally != null)
                    return PendingBodyCancellation(run.Id.Value, revision, Path.GetFileName(_path),
                        "正文已中断，收尾尚未完成：" + ex.Message);
                throw;
            }
            Serilog.Log.Warning(ex, "[Automation] graph run {RunId} awaits finalization", run.Id);
            return PendingFinalization(checkpoint, revision, Path.GetFileName(_path), ex.Message);
        }
        return await FinishAsync(context, result, revision, abandoning);
    }

    private async Task<AutomationRunResult> FinishAsync(AutomationRunContext context, PregelRunResult result, int revision,
        bool abandoning = false)
    {
        if (result.Status == PregelRunStatus.Interrupted && result.Interrupt is { } interrupt)
        {
            return PendingInterrupt(interrupt, revision, Path.GetFileName(_path));
        }
        if (result.Checkpoint.Finalization != null)
            return PendingFinalization(result.Checkpoint, revision, Path.GetFileName(_path), result.Error?.Message ?? "收尾已停止");
        if (abandoning)
            return AutomationRunResult.Cancelled("已终止本次，必要收尾已完成，后续正文未执行");
        if (result.Status == PregelRunStatus.Cancelled) return AutomationRunResult.Cancelled(result.Error?.Message ?? "已取消");
        if (result.Status != PregelRunStatus.Completed) return AutomationRunResult.Failed(result.Error?.Message ?? _failureSummary);
        if (!WriteResultArtifact || _graph.OutputType == typeof(PregelUnit))
            return _complete?.Invoke(context.Task, _graph.Read(result)) ?? AutomationRunResult.Succeeded("脚本执行完成");
        var output = Path.Combine(Path.GetDirectoryName(StateFile(context))!, "result.json");
        var value = _graph.Read(result);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false), CancellationToken.None);
        var completed = _complete?.Invoke(context.Task, value) ?? AutomationRunResult.Succeeded("脚本执行完成，结果已保存");
        return completed with { ArtifactPath = completed.ArtifactPath ?? output };
    }

    // No script evaluation is needed to find pending cleanup. This runs before templates are restored or scheduled.
    internal static AutomationRunResult? RecoverInterrupted(string stateRoot, string templateId, string runId)
    {
        // Legacy, non-graph templates need not use file-safe ids and have no graph run directory.
        if (!ValidTemplateId(templateId)) return null;
        var root = RunRoot(stateRoot, templateId);
        var file = StateFile(root, runId);
        RunState? state = null;
        try
        {
            string json;
            try { json = File.ReadAllText(file); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            state = JsonSerializer.Deserialize<RunState>(json) ?? throw new InvalidDataException("脚本运行记录损坏");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var checkpoint = new FilePregelCheckpointStore(Path.Combine(root, "checkpoints"))
                .LoadLatestAsync(new PregelRunId(state.RunId), timeout.Token).AsTask().GetAwaiter().GetResult();
            if (checkpoint?.Finalization != null)
                return PendingFinalization(checkpoint, state.Revision, templateId, "客户端在收尾期间退出");
            if (checkpoint?.Interrupt is { } interrupt)
                return PendingInterrupt(interrupt, state.Revision, templateId);
            if (state.HasFinally && checkpoint?.Status == PregelRunStatus.Running)
                return PendingBodyCancellation(state.RunId, state.Revision, templateId, "客户端退出，正文已中断。");
            if (state.AbandonRequested && checkpoint?.Status is
                (PregelRunStatus.Completed or PregelRunStatus.Failed or PregelRunStatus.Cancelled))
                return AutomationRunResult.Cancelled("已终止本次，必要收尾已完成，后续正文未执行");
            return checkpoint?.Status switch
            {
                PregelRunStatus.Failed => AutomationRunResult.Failed(checkpoint.Error?.Message ?? "流程执行失败"),
                PregelRunStatus.Cancelled => AutomationRunResult.Cancelled(checkpoint.Error?.Message ?? "已取消"),
                PregelRunStatus.Completed => AutomationRunResult.Succeeded("流程已执行完成"),
                _ => null
            };
        }
        catch (Exception ex)
        {
            // An unreadable durable run may still own external resources. Keep its entry and block replacement.
            Serilog.Log.Warning(ex, "[Automation] could not reconcile graph run {RunId}", runId);
            return PendingStateRead(state?.RunId ?? runId, state?.Revision ?? 1, templateId, ex.Message);
        }
    }

    private static AutomationRunResult PendingBodyCancellation(string runId, int revision, string detail, string reason)
    {
        var content = reason + "\n本次正文不会继续执行，需要完成原运行的收尾。";
        return AutomationRunResult.AwaitingReview(new("recovery-" + runId, revision,
            "运行已中断，待收尾", content, Hash(content), DateTimeOffset.UtcNow, detail)
        { CanRevise = false, ApproveLabel = "完成收尾", Hint = "完成收尾或终止本次都会只执行必要收尾，不会重新执行正文。" }, content);
    }

    private static AutomationRunResult PendingStateRead(string runId, int revision, string detail, string error)
    {
        var content = "无法读取原运行的持久状态，尚不能确认收尾是否完成：" + error;
        return AutomationRunResult.AwaitingReview(new("recovery-" + runId, revision,
            "运行状态待核对", content, Hash(content), DateTimeOffset.UtcNow, detail)
        { CanRevise = false, ApproveLabel = "核对并重试收尾", Hint = "恢复存储访问后重试；不会重新执行普通流程节点。" }, content);
    }

    private static AutomationRunResult PendingFinalization(PregelCheckpoint checkpoint, int revision, string detail, string reason)
    {
        var finalization = checkpoint.Finalization!;
        var status = finalization.Status switch { PregelRunStatus.Cancelled => "已取消", PregelRunStatus.Completed => "成功", _ => "执行失败" };
        var content = $"原始结果：{status}\n\n原始原因：{finalization.Error?.Message ?? "无"}"
            + $"\n\n收尾未完成：{finalization.RecoveryError?.Message ?? reason}"
            + string.Concat(finalization.Errors.Select(pair => $"\n{pair.Key}: {pair.Value.Message}"));
        return AutomationRunResult.AwaitingReview(new("recovery-" + checkpoint.RunId.Value, revision, "流程收尾未完成", content,
            Hash(content), DateTimeOffset.UtcNow, detail)
        { CanRevise = false, ApproveLabel = "重试收尾", Hint = "继续完成原运行的收尾，不会重新执行流程节点。" },
            "流程收尾未完成，原运行已保留，请重试收尾");
    }

    private static AutomationRunResult PendingInterrupt(PregelInterruptState interrupt, int revision, string detail)
    {
        var content = Content(interrupt);
        return AutomationRunResult.AwaitingReview(new(interrupt.InterruptId, revision, interrupt.Reason, content,
            Hash(content), DateTimeOffset.UtcNow, detail) { CanRevise = false, ApproveLabel = "确认并继续" }, "流程等待人工确认");
    }

    private static string Content(PregelInterruptState interrupt) => interrupt.Reason +
        (interrupt.Payload is { } payload ? "\n\n" + Encoding.UTF8.GetString(payload.Data) : "");
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string StateFile(AutomationRunContext context) => StateFile(_root, context.RunId);
    private static string StateFile(string root, string runId)
    {
        if (string.IsNullOrEmpty(runId) || runId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidOperationException("无效运行标识");
        return Path.Combine(root, runId, "run.json");
    }
    private static string RunRoot(string stateRoot, string id)
    {
        if (!ValidTemplateId(id))
            throw new InvalidOperationException("无效模板标识");
        return Path.Combine(stateRoot, "script-runs", id);
    }
    private static bool ValidTemplateId(string id) => !string.IsNullOrWhiteSpace(id) && id is not ("." or "..")
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    private async Task WriteStateAsync(AutomationRunContext context, RunState state, CancellationToken token)
    {
        var file = StateFile(context);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(state), token);
        File.Move(file + ".tmp", file, true);
    }
    private sealed record RunState(string RunId, int Revision, bool HasFinally = false, bool AbandonRequested = false)
    {
        public string[]? SkipNodes { get; init; }
        public string[]? SkipStages { get; init; }
    }
}
