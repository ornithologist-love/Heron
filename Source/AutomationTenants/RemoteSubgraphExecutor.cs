using Heron.Pregel.Automation;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heron.Pregel;

namespace Heron.AutomationTenants;

/// <summary>Dispatches complete installed subgraphs; the parent engine still owns dependencies and typed results.</summary>
public sealed class RemoteSubgraphExecutor(AutomationHost host, AutomationRunContext context, string? serverUrl)
    : IPregelSubgraphExecutor
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Lazy<string> _server = new(() => ResolveServer(serverUrl));

    public static void Configure(AutomationHost host, string? serverUrl = null)
    {
        host.SubgraphExecutorFactory = context => new RemoteSubgraphExecutor(host, context, serverUrl);
        host.ResourceCoordinatorFactory = _ => RemotePregelResourceCoordinator.ForInstance(serverUrl);
    }

    public static string ResolveServer(string? explicitUrl = null)
    {
        var configured = !string.IsNullOrWhiteSpace(explicitUrl) ? explicitUrl
            : RemoteAutomationEnvironment.Current.DefaultServerUrl();
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("远程图调度地址必须是 HTTP(S) 服务地址。");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public async ValueTask<PregelSubgraphExecutionResult> ExecuteAsync(PregelSubgraphExecutionRequest request,
        IPregelRunObserver observer, CancellationToken cancellationToken, CancellationToken finalizationCancellationToken)
    {
        var server = _server.Value;
        var resources = context.ResourceCoordinator as RemotePregelResourceCoordinator
            ?? throw new PregelException("subgraph_resources_missing", "跨机器子图需要先接入统一 Pregel 资源协调。");
        // Reconnecting the parent queries the original child; it never grants a new inherited scope.
        var definition = request with
        {
            ResumeValue = null, ResumeInterruptId = null, ParentResourceContext = null,
            ResourceAdmission = null, ResourceQueueTimeout = null
        };
        var directory = Path.Combine(context.StateRoot, "remote-subgraphs", context.RunId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Hash(request.ChildRunId) + ".json");
        var fingerprint = Hash(JsonSerializer.Serialize(definition, Json));
        var journal = File.Exists(path)
            ? JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(path, CancellationToken.None), Json)
                ?? throw new InvalidDataException("远程子图调用记录损坏。")
            : new Journal { Server = server, RequestHash = fingerprint };
        if (journal.Server != server || journal.RequestHash != fingerprint)
            throw new PregelException("subgraph_invocation_changed", "远程子图的服务地址、机器或输入已变化，保留原调用，未重新派发。");
        if (journal.Invocation == null)
        {
            if (request.ParentResourceContext == null)
                throw new PregelException("subgraph_resources_missing", "跨机器子图缺少父图的资源作用域。");
            var timeout = request.ResourceQueueTimeout ?? context.ResourceQueueTimeout;
            journal.Invocation = request with
            {
                ResumeValue = null, ResumeInterruptId = null,
                ResourceAdmission = request.ResourceAdmission ?? new PregelResourceAdmission(
                    "remote-resource-" + Hash(request.ChildRunId)[..32],
                    timeout is { } duration ? DateTimeOffset.UtcNow + duration : null)
            };
            Save(path, journal);
        }
        // Resource ownership is an execution-segment fact. A resumed parent may have a new
        // lease; an already dispatched child keeps its original request and is never rebound.
        var invocation = journal.Invocation;

        using var stopLifetime = new CancellationTokenSource();
        DateTimeOffset? stoppingAt = null;
        var connected = true;
        string? lastState = null;
        while (true)
        {
            var stopping = cancellationToken.IsCancellationRequested || finalizationCancellationToken.IsCancellationRequested;
            if (stopping && stoppingAt is null)
            {
                if (!journal.Submitted && journal.RemoteId.Length == 0)
                    return new(request.ChildRunId, PregelRunStatus.Cancelled);
                stoppingAt = DateTimeOffset.UtcNow;
                // The request body token is already cancelled. A separate bounded lifetime sends one durable stop.
                stopLifetime.CancelAfter(TimeSpan.FromSeconds(15));
            }
            if (stoppingAt is { } since && DateTimeOffset.UtcNow - since > TimeSpan.FromSeconds(15))
                return PendingStop(request, journal.RemoteId);

            try
            {
                if (journal.QueueFailure == null && !journal.QueueGranted
                    && invocation.ResourceAdmission is { QueueDeadlineUtc: { } deadline } admission
                    && DateTimeOffset.UtcNow >= deadline)
                {
                    var resource = await resources.ExpireAsync(admission.RequestId, request.ChildRunId, deadline,
                        request.Target, invocation.ParentResourceContext, stopLifetime.Token);
                    if (resource.State is "granted" or "releasing" or "released") journal.QueueGranted = true;
                    else if (resource.State is "timed_out" or "failed")
                        journal.QueueFailure = resource.Error ?? new("mutex_queue_timeout", "子图等待资源超时，未开始执行。");
                    Save(path, journal);
                }
                if (journal.QueueFailure is { } queueFailure)
                {
                    await SaveAndRequestFailedStopAsync(request, invocation, journal, queueFailure, server);
                    Notify(observer, request, "subgraph.queue_timeout", queueFailure.Message, journal.RemoteId);
                    return new(request.ChildRunId, PregelRunStatus.Failed, Error: queueFailure);
                }
                // Never abandon an uncertain POST. Repeating this immutable invocation resolves to its original child id.
                ChildRun run;
                if (journal.RemoteId.Length == 0)
                {
                    var snapshot = context.ReadSnapshot?.Invoke()
                        ?? throw new InvalidOperationException("父图尚无持久执行记录，不能派发子图。");
                    journal.Submitted = true;
                    Save(path, journal);
                    run = await PostAsync<ChildRun>("children", new
                    {
                        machine_name = Environment.MachineName,
                        parent_local_run_id = context.RunId,
                        parent_snapshot = snapshot,
                        target_machine_name = request.Target,
                        cancel_requested = stopping,
                        task_id = DispatchTaskId(),
                        parameters = context.Task.Parameters,
                        subgraph = invocation
                    }, stopLifetime.Token);
                    journal.RemoteId = run.Id;
                    Save(path, journal);
                    Notify(observer, request, "subgraph.dispatched", $"子图已派发到 {request.Target}", run.Id);
                }
                else run = await PostAsync<ChildRun>("children/" + journal.RemoteId, Owner(), stopLifetime.Token);

                if (stopping && !journal.StopSent)
                {
                    run = await PostAsync<ChildRun>("children/" + journal.RemoteId + "/stop", Owner(), stopLifetime.Token);
                    journal.StopSent = true;
                    Save(path, journal);
                }
                if (!connected)
                {
                    Notify(observer, request, "subgraph.reconnected", "已重新连接原子图，未创建新执行。", journal.RemoteId);
                    connected = true;
                }
                if (lastState != run.State)
                {
                    lastState = run.State;
                    Notify(observer, request, "subgraph.state", run.Detail.Length > 0 ? run.Detail : run.State, journal.RemoteId);
                }

                var moreLogs = await ForwardLogsAsync(request, journal, path, observer, stopLifetime.Token);
                var childResult = ReadResult(run);
                if (moreLogs) continue;
                if (run.State == "succeeded")
                {
                    if (run.Output is null) throw new InvalidDataException("远端子图已结束但缺少编码输出，未返回默认结果。");
                    if (run.LogsComplete) return new(request.ChildRunId, PregelRunStatus.Completed, run.Output);
                }
                if (run.State == "cancelled" && run.LogsComplete)
                    return childResult is { Status: PregelRunStatus.Cancelled } ? childResult
                        : new(request.ChildRunId, PregelRunStatus.Cancelled);
                if (run.State is "failed" or "skipped" && run.LogsComplete)
                    return childResult is { Status: PregelRunStatus.Failed } ? childResult
                        : new(request.ChildRunId, PregelRunStatus.Failed,
                            Error: new("remote_subgraph_failed", run.Detail.Length > 0 ? run.Detail : "远端子图执行失败。"));
                if (run.State == "unknown")
                    return new(request.ChildRunId, PregelRunStatus.Interrupted,
                        Error: new("remote_subgraph_unknown", "远端已无法确认子图结果；保留原调用，未重新派发。"));
                if (run.State == "awaiting_review" && !stopping)
                {
                    var reason = $"子图在 {request.Target} 等待处理。请在目标机器完成审核或收尾，再继续父图。";
                    return childResult is { Status: PregelRunStatus.FinalizationPending }
                        ? childResult
                        : new(request.ChildRunId, PregelRunStatus.Interrupted,
                            Interrupt: new(childResult?.Interrupt?.InterruptId ?? journal.RemoteId, reason, null));
                }
                if (moreLogs) continue;
            }
            catch (Exception error) when (IsTransient(error))
            {
                if (stoppingAt != null && stopLifetime.IsCancellationRequested) return PendingStop(request, journal.RemoteId);
                if (connected)
                {
                    Notify(observer, request, "subgraph.disconnected", "调度连接中断；等待恢复并查询原调用，不换机器重跑。", journal.RemoteId);
                    connected = false;
                }
            }
            catch (RemoteCallException error) when (journal.RemoteId.Length > 0)
            {
                return new(request.ChildRunId, PregelRunStatus.Interrupted,
                    Error: new("remote_subgraph_unavailable", error.Message));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stopLifetime.Token); }
            catch (OperationCanceledException) { return PendingStop(request, journal.RemoteId); }
        }
    }

    private object Owner() => new { machine_name = Environment.MachineName, parent_local_run_id = context.RunId };

    private async Task SaveAndRequestFailedStopAsync(PregelSubgraphExecutionRequest request,
        PregelSubgraphExecutionRequest invocation, Journal journal, PregelError failure, string server)
    {
        var snapshot = context.ReadSnapshot?.Invoke()
            ?? throw new InvalidOperationException("父图运行记录已不存在，无法关联子图超时。");
        var stop = new FailedChildStop(server, journal.RemoteId,
            JsonSerializer.SerializeToElement(new
            {
                machine_name = Environment.MachineName, parent_local_run_id = context.RunId,
                parent_snapshot = snapshot, target_machine_name = request.Target,
                cancel_requested = true, failure, task_id = DispatchTaskId(),
                parameters = context.Task.Parameters, subgraph = invocation
            }, Json), JsonSerializer.SerializeToElement(new
            {
                machine_name = Environment.MachineName, parent_local_run_id = context.RunId, failure
            }, Json));
        var stopPath = FailedStopPath(server, request.ChildRunId);
        RemotePregelResourceCoordinator.WriteJson(stopPath, stop);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await DeliverFailedStopAsync(stop, timeout.Token);
            File.Delete(stopPath);
        }
        catch (Exception error)
        {
            // The authoritative resource request is already expired. Business execution is
            // impossible; reporting the failure can continue from this durable outbox.
            Serilog.Log.Warning("Remote child failure notification pending: {ErrorType}", error.GetType().Name);
        }
    }

    internal static async Task FlushFailedStopsAsync(string server, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(FailedStopPath(server, "directory"))!;
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            FailedChildStop? stop;
            try { stop = JsonSerializer.Deserialize<FailedChildStop>(await File.ReadAllTextAsync(file, token), Json); }
            catch (FileNotFoundException) { continue; }
            if (stop == null || stop.Server != ResolveServer(server)) throw new InvalidDataException("子图超时通知记录损坏。");
            await DeliverFailedStopAsync(stop, token);
            File.Delete(file);
        }
    }

    private static string FailedStopPath(string server, string childId) => Path.Combine(RemoteAutomationEnvironment.Current.LocalRoot(),
        "remote-automation", Hash(ResolveServer(server)), "subgraph-stops", Hash(childId) + ".json");

    private static async Task DeliverFailedStopAsync(FailedChildStop stop, CancellationToken token)
    {
        var remoteId = stop.RemoteId;
        if (remoteId.Length == 0)
        {
            using var created = await Http.PostAsJsonAsync(stop.Server.TrimEnd('/') + "/api/agent/automation/children", stop.Create, Json, token);
            created.EnsureSuccessStatusCode();
            var run = await created.Content.ReadFromJsonAsync<ChildRun>(Json, token)
                ?? throw new InvalidDataException("子图超时通知没有返回运行身份。");
            remoteId = run.Id;
        }
        using var response = await Http.PostAsJsonAsync(stop.Server.TrimEnd('/') + "/api/agent/automation/children/" + remoteId + "/stop",
            stop.Stop, Json, token);
        response.EnsureSuccessStatusCode();
    }

    private string DispatchTaskId()
    {
        if (host.CloudTask(context.Task) is { } registered) return registered.Id;
        var path = host.Scripts.SavedScriptPath(context.Task.TemplateId);
        if (path is null) return context.Task.Id;
        var relative = Path.GetRelativePath(RemoteAutomationEnvironment.Current.ScriptsRoot(), path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !Path.IsPathRooted(relative) && parts.Length > 1 && parts[0] is not ("." or "..")
            ? RemoteAutomationOfficialScripts.TaskId(parts[0]) : context.Task.Id;
    }

    private async Task<bool> ForwardLogsAsync(PregelSubgraphExecutionRequest request, Journal journal, string journalPath,
        IPregelRunObserver observer, CancellationToken token)
    {
        var page = await PostAsync<LogPage>("children/" + journal.RemoteId + "/logs", new
        {
            machine_name = Environment.MachineName, parent_local_run_id = context.RunId,
            after = journal.Cursor, limit = 500
        }, token);
        foreach (var line in page.Lines)
        {
            if (line.Sequence <= journal.Cursor) continue;
            try
            {
                // A record may include separately stored nested evidence; the root stream already embeds those events.
                var value = JsonSerializer.Deserialize<PregelRunEvent>(line.Raw);
                if (value?.RunId.Value == request.ChildRunId) observer.OnEvent(value);
            }
            catch (JsonException)
            {
                Notify(observer, request, "subgraph.log", line.Text, journal.RemoteId);
            }
        }
        if (page.NextCursor < journal.Cursor) throw new InvalidDataException("子图日志游标倒退，保留原读取位置。");
        journal.Cursor = page.NextCursor;
        Save(journalPath, journal);
        return page.HasMore;
    }

    private static PregelSubgraphExecutionResult? ReadResult(ChildRun run)
    {
        if (run.Snapshot is not { ValueKind: JsonValueKind.Object } snapshot
            || !snapshot.TryGetProperty("subgraph_result", out var result) || result.ValueKind == JsonValueKind.Null) return null;
        return result.Deserialize<PregelSubgraphExecutionResult>(Json);
    }

    private async Task<T> PostAsync<T>(string path, object payload, CancellationToken token)
    {
        using var response = await Http.PostAsJsonAsync(_server.Value + "/api/agent/automation/" + path, payload, Json, token);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("远程图调度服务暂不可用。", null, response.StatusCode);
            var message = "远程图调度拒绝调用（" + (int)response.StatusCode + "）。";
            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, token);
                if (body.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String)
                    message = value.GetString() ?? message;
            }
            catch (JsonException) { }
            throw new RemoteCallException(message);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, token)
            ?? throw new InvalidDataException("远程图调度响应为空。");
    }

    private static bool IsTransient(Exception error) => error is HttpRequestException or TaskCanceledException
        || error is OperationCanceledException;
    private static PregelSubgraphExecutionResult PendingStop(PregelSubgraphExecutionRequest request, string remoteId) =>
        new(request.ChildRunId, PregelRunStatus.FinalizationPending,
            Error: new("remote_subgraph_stop_pending", "已请求停止远端子图，尚未确认收尾完成。请恢复连接后继续跟踪原调用 " + remoteId));
    private static void Notify(IPregelRunObserver observer, PregelSubgraphExecutionRequest request, string kind, string text, string remoteId) =>
        observer.OnEvent(new(new(request.ChildRunId), kind, text, Source: "remote",
            Data: JsonSerializer.SerializeToElement(new { machine_name = request.Target, remote_run_id = remoteId })));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static void Save(string path, Journal journal)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(journal, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed class RemoteCallException(string message) : Exception(message);
    private sealed class Journal
    {
        public string Server { get; init; } = "";
        public string RequestHash { get; init; } = "";
        public bool Submitted { get; set; }
        public string RemoteId { get; set; } = "";
        public long Cursor { get; set; }
        public bool StopSent { get; set; }
        public PregelSubgraphExecutionRequest? Invocation { get; set; }
        public bool QueueGranted { get; set; }
        public PregelError? QueueFailure { get; set; }
    }
    private sealed record FailedChildStop(string Server, string RemoteId, JsonElement Create, JsonElement Stop);
    private sealed record ChildRun(string Id, string State, string Detail, bool LogsComplete,
        PregelEncodedValue? Output, JsonElement? Snapshot);
    private sealed record LogLine(long Sequence, string Raw, string Text);
    private sealed record LogPage(LogLine[] Lines, long NextCursor, bool HasMore);
}
