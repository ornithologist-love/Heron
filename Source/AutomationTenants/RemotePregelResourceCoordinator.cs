using Heron.Pregel.Automation;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heron.Pregel;

namespace Heron.AutomationTenants;

/// <summary>Transport for the same Pregel resource contract used by local execution.</summary>
public sealed class RemotePregelResourceCoordinator(string serverUrl) : IPregelResourceCoordinator
{
    internal const string TenantVariable = "HERON_AUTOMATION_TENANT_ID";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _server = RemoteSubgraphExecutor.ResolveServer(serverUrl);
    internal static string ResourceDomainDirectory => Path.GetFullPath(RemoteAutomationEnvironment.Current.ResourceDomainDirectory());
    private static string ModePath => Path.Combine(ResourceDomainDirectory, PregelLocalResourceCoordinator.ExternalBindingFileName);

    public static RemotePregelResourceCoordinator? ForInstance(string? server = null)
    {
        var explicitServer = RemoteAutomationEnvironment.Current.ResourceServerUrl();
        if (!string.IsNullOrWhiteSpace(explicitServer)) return new(explicitServer);
        if (!File.Exists(ModePath)) return null;
        var mode = JsonSerializer.Deserialize<ResourceMode>(File.ReadAllText(ModePath), Json)
            ?? throw new InvalidDataException("Pregel 资源协调配置损坏，未退回独立本地锁。");
        if (server != null && RemoteSubgraphExecutor.ResolveServer(server) != mode.Server)
            throw new InvalidOperationException("实例已绑定另一资源协调服务，请先完成原运行再迁移。");
        return new(mode.Server);
    }

    internal static bool IsEnabled(string server) => File.Exists(ModePath)
        && ForInstance(server) != null;

    // Called while the AutomationStore update lock excludes a new local run.
    internal static bool Enable(string server)
    {
        server = RemoteSubgraphExecutor.ResolveServer(server);
        if (File.Exists(ModePath)) { _ = ForInstance(server); return true; }
        return PregelLocalResourceCoordinator.TryBindExternalCoordinator(ResourceDomainDirectory, () =>
        {
            if (File.Exists(ModePath)) { _ = ForInstance(server); return; }
            WriteJson(ModePath, new ResourceMode(server));
        });
    }

    public async Task<string> InspectAsync(string? machineName, CancellationToken token)
    {
        var response = await SendAsync("list", new PregelResourceMessage
            { MachineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName }, token);
        return JsonSerializer.Serialize(response.Leases, new JsonSerializerOptions(Json) { WriteIndented = true });
    }

    public async Task ReleaseOwnedAsync(string requestId, string? machineName, CancellationToken token)
    {
        var response = await SendAsync("release", new PregelResourceMessage
        {
            MachineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName,
            RequestId = requestId
        }, token);
        if (response.Error is { } error) throw new PregelException(error.Code, error.Message);
    }

    public static TimeSpan? ConfiguredQueueTimeout() => RemoteAutomationEnvironment.Current.ResourceQueueTimeout();

    /// <summary>All CLI graph entry points participate in an already bound resource domain.</summary>
    public static IDisposable EnterCurrentDomain()
    {
        var coordinator = ForInstance();
        return PregelResourceScope.Enter(coordinator,
            ConfiguredQueueTimeout() ?? (coordinator == null ? null : TimeSpan.FromMinutes(30)));
    }

    public async ValueTask<IPregelResourceLease> AcquireAsync(PregelResourceRequest request,
        Action<IReadOnlyList<PregelResourceWaitState>> waiting, CancellationToken cancellationToken)
    {
        var message = new PregelResourceMessage { MachineName = Environment.MachineName, Request = request };
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var response = await SendAsync("acquire", message, cancellationToken).ConfigureAwait(false);
                    if (response.State == "granted" && response.Context != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return new RemoteLease(this, request.RequestId, response.Context, request.ParentContext);
                    }
                    ThrowIfFailed(response);
                    waiting(response.Waiting);
                }
                catch (Exception error) when (Transient(error) && !cancellationToken.IsCancellationRequested)
                {
                    // A lost reply does not imply that the server failed to grant the lease.
                    // The same request identity is used until it is confirmed or cancelled.
                }
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            await ReleaseDurablyAsync(request.RequestId, null, request.ParentContext,
                confirmWithdrawal: error is OperationCanceledException).ConfigureAwait(false);
            throw;
        }
    }

    internal Task<PregelResourceReply> ExpireAsync(string requestId, string runId, DateTimeOffset deadline,
        string ownerMachine, PregelResourceContext? parentContext, CancellationToken token) => SendAsync("expire",
        new PregelResourceMessage
        {
            MachineName = Environment.MachineName, OwnerMachineName = ownerMachine,
            RequestId = requestId, RunId = runId, QueueDeadlineUtc = deadline, ParentContext = parentContext
        }, token);

    internal async Task<PregelResourceReply> SendAsync(string action, PregelResourceMessage message, CancellationToken token)
    {
        if (message.TenantId.Length == 0 && Environment.GetEnvironmentVariable(TenantVariable) is { Length: > 0 } tenant)
            message = message with { TenantId = tenant };
        using var response = await Http.PostAsJsonAsync(_server.TrimEnd('/') + "/api/agent/automation/resources/" + action,
            message, Json, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("Pregel 资源协调服务暂不可用。", null, response.StatusCode);
            try
            {
                var failed = JsonSerializer.Deserialize<PregelResourceReply>(body, Json);
                if (failed?.Error is { } error) throw new PregelException(error.Code, error.Message);
            }
            catch (JsonException) { }
            throw new PregelException("mutex_remote_rejected", "资源协调请求被拒绝（" + (int)response.StatusCode + "）。");
        }
        return JsonSerializer.Deserialize<PregelResourceReply>(body, Json)
            ?? throw new InvalidDataException("Pregel 资源协调响应为空。");
    }

    internal static void ThrowIfFailed(PregelResourceReply response)
    {
        if (response.Error is { } error) throw new PregelException(error.Code, error.Message);
        if (response.State is "released" or "releasing")
            throw new PregelException("mutex_lease_released", "原资源租约已释放。");
        if (response.State == "cancelled")
            throw new PregelException("mutex_request_cancelled", "资源申请已撤销，不能再次执行。");
    }

    private async ValueTask ReleaseDurablyAsync(string requestId, PregelResourceContext? context,
        PregelResourceContext? parentContext, bool confirmWithdrawal = false)
    {
        var message = new PregelResourceMessage
        {
            MachineName = Environment.MachineName, RequestId = requestId,
            Context = context, ParentContext = parentContext,
            TenantId = Environment.GetEnvironmentVariable(TenantVariable) ?? ""
        };
        var path = ReleasePath(_server, requestId);
        WriteJson(path, message);
        do
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendAsync("release", message, timeout.Token).ConfigureAwait(false);
                File.Delete(path);
                return;
            }
            catch (Exception error)
            {
                // A pending acquisition must be confirmed withdrawn before its caller can
                // publish cancellation. Completed-body releases can be retried by the Agent.
                Serilog.Log.Warning("Pregel resource release pending: {RequestId} {ErrorType}", requestId, error.GetType().Name);
                if (confirmWithdrawal && !Transient(error)) throw;
            }
            if (confirmWithdrawal) await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } while (confirmWithdrawal);
    }

    internal static async Task FlushReleasesAsync(string server, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(ReleasePath(server, "directory"))!;
        if (!Directory.Exists(directory)) return;
        var coordinator = new RemotePregelResourceCoordinator(server);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            PregelResourceMessage? message;
            try { message = JsonSerializer.Deserialize<PregelResourceMessage>(await File.ReadAllTextAsync(path, token), Json); }
            catch (FileNotFoundException) { continue; }
            if (message == null) throw new InvalidDataException("待释放资源记录损坏。");
            await coordinator.SendAsync("release", message, token).ConfigureAwait(false);
            File.Delete(path);
        }
    }

    private static string ReleasePath(string server, string requestId) => Path.Combine(RemoteAutomationEnvironment.Current.LocalRoot(),
        "remote-automation", Hash(RemoteSubgraphExecutor.ResolveServer(server)), "resource-releases", Hash(requestId) + ".json");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool Transient(Exception error) => error is HttpRequestException or TaskCanceledException;
    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value, Json);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ResourceMode(string Server);
    private sealed class RemoteLease(RemotePregelResourceCoordinator owner, string requestId,
        PregelResourceContext context, PregelResourceContext? parentContext) : IPregelResourceLease
    {
        private int _released;
        public PregelResourceContext Context => context;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0
            ? owner.ReleaseDurablyAsync(requestId, context, parentContext) : ValueTask.CompletedTask;
    }
}

internal sealed record PregelResourceMessage
{
    public string MachineName { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string OwnerMachineName { get; init; } = "";
    public string DelegatedParentRunId { get; init; } = "";
    public string DelegatedParentMachineName { get; init; } = "";
    public PregelResourceRequest? Request { get; init; }
    public string RequestId { get; init; } = "";
    public string RunId { get; init; } = "";
    public DateTimeOffset? QueueDeadlineUtc { get; init; }
    public PregelResourceContext? Context { get; init; }
    public PregelResourceContext? ParentContext { get; init; }
}

internal sealed record PregelResourceReply(string State)
{
    public PregelResourceContext? Context { get; init; }
    public IReadOnlyList<PregelResourceWaitState> Waiting { get; init; } = [];
    public PregelError? Error { get; init; }
    public IReadOnlyList<PregelResourceLeaseView> Leases { get; init; } = [];
}

internal sealed record PregelResourceLeaseView(string RequestId, string RunId, string State,
    IReadOnlyList<PregelMutexRule> Rules, PregelResourceContext? ParentContext, DateTimeOffset? QueueDeadlineUtc);
