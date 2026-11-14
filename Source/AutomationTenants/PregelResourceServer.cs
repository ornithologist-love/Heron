using Heron.Pregel.Automation;
using System.Net;
using System.Text.Json;
using Heron.Pregel;

namespace Heron.AutomationTenants;

/// <summary>
/// Durable HTTP ownership around the Pregel coordinator. Admission, resource-key capacity,
/// grouped acquisition and inherited keys are decided by the same Core implementation.
/// </summary>
public sealed class PregelResourceServer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _tenant, _statePath;
    private readonly FileStream _writer;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly PregelLocalResourceCoordinator _coordinator;
    private readonly ResourceLedger _ledger;
    private readonly HashSet<Task> _requests = [];
    private Task? _listening;
    private volatile bool _faulted;
    private int _disposed;

    private PregelResourceServer(string tenant, string stateDirectory, CancellationToken token)
    {
        _tenant = tenant;
        Directory.CreateDirectory(stateDirectory);
        _statePath = Path.Combine(stateDirectory, "leases.json");
        _writer = new FileStream(Path.Combine(stateDirectory, "writer.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            _ledger = File.Exists(_statePath)
                ? JsonSerializer.Deserialize<ResourceLedger>(File.ReadAllText(_statePath), RemotePregelResourceCoordinator.Json)
                    ?? throw new InvalidDataException("Pregel 资源账本损坏。")
                : new ResourceLedger { TenantId = tenant };
            if (_ledger.TenantId != tenant || _ledger.Version != 1)
                throw new InvalidDataException("Pregel 资源数据目录属于其他租户或版本。");
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _coordinator = new PregelLocalResourceCoordinator(coordinatorId: _ledger.CoordinatorId + ":runtime");
        }
        catch { _writer.Dispose(); throw; }
    }

    public static async Task<PregelResourceServer> StartAsync(string listenUrl, string tenantId,
        string stateDirectory, CancellationToken token)
    {
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new ArgumentException("Pregel 资源服务只允许 HTTP loopback 根地址。");
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 96 || tenantId.Any(char.IsControl))
            throw new ArgumentException("服务器 Pregel 需要有效 tenant。");
        var server = new PregelResourceServer(tenantId, Path.GetFullPath(stateDirectory), token);
        try
        {
            await server.RestoreAsync(token).ConfigureAwait(false);
            server._listener.Prefixes.Add(uri.AbsoluteUri);
            server._listener.Start();
            server._listening = server.ListenAsync();
            return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    private async Task RestoreAsync(CancellationToken token)
    {
        // Granted work may still be running on another machine. Re-establish every old
        // grant before serving a new request; disconnects and restarts never expire grants.
        foreach (var entry in _ledger.Entries.Values.Where(e => e.State is "granted" or "releasing")
                     .OrderBy(Depth).ThenBy(e => e.Sequence))
        {
            ValidateDefinition(entry.Request);
            PregelResourceContext? parent = null;
            if (entry.Request.ParentContext is { } inherited)
                parent = Parent(inherited, requireOpen: false).Lease?.Context
                    ?? throw new InvalidDataException("资源账本的父租约尚未恢复。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            entry.Lease = await _coordinator.AcquireAsync(entry.Request with
            {
                ParentContext = parent, QueueDeadlineUtc = null
            }, _ => { }, timeout.Token).ConfigureAwait(false);
        }
        lock (_gate) Save();
        await ReleaseReadyAsync().ConfigureAwait(false);
        lock (_gate)
            foreach (var entry in _ledger.Entries.Values.Where(e => e.State == "pending").OrderBy(e => e.Sequence).ToArray())
                StartAcquisition(entry);
    }

    private int Depth(ResourceEntry entry)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (entry.Request.ParentContext is { } parent)
        {
            if (parent.CoordinatorId != _ledger.CoordinatorId || !seen.Add(parent.LeaseId) || seen.Count > 64
                || !_ledger.Entries.TryGetValue(parent.LeaseId, out entry!))
                throw new InvalidDataException("资源账本父子关系损坏。");
        }
        return seen.Count;
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token).ConfigureAwait(false);
                var task = HandleAsync(context);
                lock (_gate) _requests.Add(task);
                _ = task.ContinueWith(completed => { lock (_gate) _requests.Remove(completed); }, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested
            && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/health")
            {
                await RespondAsync(context, _faulted ? 503 : 200, new { healthy = !_faulted, tenant_id = _tenant });
                return;
            }
            if (_faulted) throw new IOException("资源账本写入失败，服务已停止新的资源准入。");
            if (context.Request.HttpMethod != "POST")
            {
                await RespondAsync(context, 405, Failure("mutex_method", "资源接口需要 POST。"));
                return;
            }
            var action = context.Request.Url?.AbsolutePath switch
            {
                "/resources/acquire" => "acquire", "/resources/state" => "state",
                "/resources/release" => "release", "/resources/expire" => "expire", "/resources/list" => "list", _ => ""
            };
            if (action.Length == 0)
            {
                await RespondAsync(context, 404, Failure("mutex_route", "资源接口不存在。"));
                return;
            }
            const int maximum = 4 * 1024 * 1024;
            if (context.Request.ContentLength64 > maximum) throw new InvalidDataException("资源请求过大。");
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await context.Request.InputStream.ReadAsync(buffer, _lifetime.Token)) != 0)
            {
                if (body.Length + count > maximum) throw new InvalidDataException("资源请求过大。");
                body.Write(buffer, 0, count);
            }
            var message = JsonSerializer.Deserialize<PregelResourceMessage>(body.GetBuffer().AsSpan(0, (int)body.Length),
                RemotePregelResourceCoordinator.Json) ?? throw new InvalidDataException("资源请求为空。");
            if (message.TenantId != _tenant || !ValidText(message.MachineName, 128))
                throw new PregelException("mutex_tenant_mismatch", "资源请求的租户或机器身份无效。");
            var response = action switch
            {
                "acquire" => Acquire(message), "state" => State(message), "list" => List(message),
                "expire" => Expire(message), _ => await ReleaseAsync(message)
            };
            if (_faulted) throw new IOException("资源账本写入失败，服务已停止新的资源准入。");
            await RespondAsync(context, 200, response);
        }
        catch (PregelException error)
        {
            await RespondAsync(context, 409, Failure(error.Code, error.Message));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            await RespondAsync(context, 400, Failure("mutex_request_invalid", error.Message));
        }
        catch (Exception error)
        {
            Serilog.Log.Warning("Pregel resource service request failed: {ErrorType}", error.GetType().Name);
            await RespondAsync(context, 503, Failure("mutex_service_unavailable", "资源服务暂不可用，原占用已保留。"));
        }
        finally { context.Response.Close(); }
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, object value)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers["Cache-Control"] = "no-store";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RemotePregelResourceCoordinator.Json);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException) { }
    }

    private PregelResourceReply Acquire(PregelResourceMessage message)
    {
        var request = message.Request ?? throw new InvalidDataException("缺少 Pregel 资源申请。");
        ValidateDefinition(request);
        lock (_gate)
        {
            if (_ledger.Entries.TryGetValue(request.RequestId, out var existing))
            {
                Authorize(existing, message);
                if (existing.Request.RunId.Length > 0 && existing.Request.RunId != request.RunId)
                    throw new PregelException("mutex_request_changed", "资源申请身份已用于另一次运行。");
                if (existing.HasDefinition && !SameRequest(existing.Request, request))
                    throw new PregelException("mutex_request_changed", "资源键、容量、父租约或排队期限已变化。");
                return Reply(existing);
            }
            if (request.ParentContext is { } inherited)
            {
                var parent = Parent(inherited, requireOpen: true);
                if (!parent.OwnerMachine.Equals(message.MachineName, StringComparison.OrdinalIgnoreCase)
                    && (message.DelegatedParentRunId != parent.Request.RunId
                        || !parent.OwnerMachine.Equals(message.DelegatedParentMachineName, StringComparison.OrdinalIgnoreCase)))
                    throw new PregelException("mutex_delegation_missing", "跨机器继承缺少已登记父子图调用的授权。");
            }
            var entry = new ResourceEntry
            {
                Request = request with { Rules = request.Rules.ToArray() }, OwnerMachine = message.MachineName,
                HasDefinition = true, Sequence = ++_ledger.LastSequence
            };
            _ledger.Entries.Add(request.RequestId, entry);
            Save();
            StartAcquisition(entry);
            return Reply(entry);
        }
    }

    private void StartAcquisition(ResourceEntry entry)
    {
        if (entry.Acquisition != null || entry.State != "pending") return;
        entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        entry.Acquisition = AcquireEntryAsync(entry);
    }

    private async Task AcquireEntryAsync(ResourceEntry entry)
    {
        IPregelResourceLease? acquired = null;
        try
        {
            PregelResourceContext? parent;
            lock (_gate) parent = entry.Request.ParentContext is { } inherited
                ? Parent(inherited, requireOpen: false).Lease?.Context
                    ?? throw new PregelException("mutex_parent_pending", "父资源租约尚未取得。")
                : null;
            acquired = await _coordinator.AcquireAsync(entry.Request with { ParentContext = parent }, waiting =>
            {
                lock (_gate) entry.Waiting = waiting.ToArray();
            }, entry.Cancellation!.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (entry.State == "pending" && !_lifetime.IsCancellationRequested)
                {
                    entry.Lease = acquired;
                    entry.State = "granted";
                    entry.Waiting = [];
                    Save(); // Persist ownership before any caller can observe the grant.
                    acquired = null;
                }
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (entry.State == "pending" && !_lifetime.IsCancellationRequested)
                {
                    var code = error is PregelException pregel ? pregel.Code
                        : error is OperationCanceledException ? "mutex_request_cancelled" : "mutex_acquire_failed";
                    entry.State = code == "mutex_queue_timeout" ? "timed_out"
                        : code == "mutex_request_cancelled" ? "cancelled" : "failed";
                    entry.Error = new(code, error is OperationCanceledException ? "资源申请已撤销。" : error.Message);
                    Save();
                }
            }
        }
        finally
        {
            if (acquired != null) await acquired.DisposeAsync().ConfigureAwait(false);
            if (!_lifetime.IsCancellationRequested) await ReleaseReadyAsync().ConfigureAwait(false);
        }
    }

    private PregelResourceReply State(PregelResourceMessage message)
    {
        lock (_gate)
        {
            ValidateId(message.RequestId);
            if (!_ledger.Entries.TryGetValue(message.RequestId, out var entry)) return new("missing");
            Authorize(entry, message);
            return Reply(entry);
        }
    }

    private PregelResourceReply List(PregelResourceMessage message)
    {
        lock (_gate) return new("listed")
        {
            Leases = _ledger.Entries.Values.Where(entry => entry.OwnerMachine.Equals(message.MachineName, StringComparison.OrdinalIgnoreCase)
                    && entry.State is "pending" or "granted" or "releasing")
                .OrderBy(entry => entry.Sequence).Select(entry => new PregelResourceLeaseView(entry.Request.RequestId,
                    entry.Request.RunId, entry.State, entry.Request.Rules, entry.Request.ParentContext, entry.Request.QueueDeadlineUtc)).ToArray()
        };
    }

    private PregelResourceReply Expire(PregelResourceMessage message)
    {
        CancellationTokenSource? cancel = null;
        PregelResourceReply reply;
        lock (_gate)
        {
            ValidateId(message.RequestId);
            if (message.QueueDeadlineUtc is not { } deadline || !ValidText(message.RunId, 256))
                throw new InvalidDataException("排队超时需要原运行身份与固定截止时间。");
            if (_ledger.Entries.TryGetValue(message.RequestId, out var entry))
            {
                Authorize(entry, message);
                if (entry.Request.RunId != message.RunId || entry.Request.QueueDeadlineUtc != deadline)
                    throw new PregelException("mutex_request_changed", "排队期限或运行身份与原申请不一致。");
                if (entry.State != "pending" || DateTimeOffset.UtcNow < deadline) return Reply(entry);
            }
            else
            {
                if (DateTimeOffset.UtcNow < deadline) return new("missing");
                var owner = string.IsNullOrEmpty(message.OwnerMachineName) ? message.MachineName : message.OwnerMachineName;
                if (!ValidText(owner, 128)) throw new InvalidDataException("子图执行机器无效。");
                if (!owner.Equals(message.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.ParentContext == null) throw new PregelException("mutex_parent_missing", "父图必须携带原资源租约。");
                    var parent = ParentIdentity(message.ParentContext);
                    if (!parent.OwnerMachine.Equals(message.MachineName, StringComparison.OrdinalIgnoreCase))
                        throw new PregelException("mutex_owner_mismatch", "父资源租约不属于请求机器。");
                }
                entry = new ResourceEntry
                {
                    Request = new(message.RequestId, message.RunId, [], message.ParentContext, deadline),
                    OwnerMachine = owner, Sequence = ++_ledger.LastSequence
                };
                _ledger.Entries.Add(message.RequestId, entry);
            }
            entry.State = "timed_out";
            entry.Error = new("mutex_queue_timeout", "等待 Pregel 资源超时，未执行图正文。");
            Save(); // This tombstone also rejects a worker which receives the task much later.
            cancel = entry.Cancellation;
            reply = Reply(entry);
        }
        cancel?.Cancel();
        return reply;
    }

    private async Task<PregelResourceReply> ReleaseAsync(PregelResourceMessage message)
    {
        CancellationTokenSource? cancel = null;
        ResourceEntry entry;
        lock (_gate)
        {
            ValidateId(message.RequestId);
            if (!_ledger.Entries.TryGetValue(message.RequestId, out entry!))
            {
                // Cancellation can overtake the first acquire request. Keep a durable tombstone.
                entry = new ResourceEntry
                {
                    Request = new(message.RequestId, message.RunId, [], message.ParentContext, null),
                    OwnerMachine = message.MachineName, Sequence = ++_ledger.LastSequence, State = "cancelled"
                };
                _ledger.Entries.Add(message.RequestId, entry);
                Save();
                return Reply(entry);
            }
            if (!entry.OwnerMachine.Equals(message.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new PregelException("mutex_owner_mismatch", "只有实际执行机器可以确认释放资源；父图应请求子图停止并等待执行端收尾。");
            if (message.Context != null && message.Context != Context(entry))
                throw new PregelException("mutex_context_mismatch", "资源租约身份不匹配。");
            if (entry.State == "pending") { entry.State = "cancelled"; cancel = entry.Cancellation; }
            else if (entry.State == "granted") entry.State = "releasing";
            Save();
        }
        cancel?.Cancel();
        await ReleaseReadyAsync().ConfigureAwait(false);
        lock (_gate) return Reply(entry);
    }

    private async Task ReleaseReadyAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            ResourceEntry? ready;
            IPregelResourceLease? lease;
            lock (_gate)
            {
                ready = _ledger.Entries.Values.FirstOrDefault(entry => entry.State == "releasing" && !entry.ReleasingNow
                    && !_ledger.Entries.Values.Any(child => child.Request.ParentContext == Context(entry)
                        && child.State is "pending" or "granted" or "releasing"));
                if (ready == null) return;
                ready.ReleasingNow = true;
                lease = ready.Lease;
                // Commit the completed owner's release before waking another waiter. Otherwise
                // that waiter's grant could persist alongside this old grant after a crash.
                ready.State = "released";
                Save();
                ready.Lease = null;
                ready.ReleasingNow = false;
            }
            if (lease != null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ResourceEntry Parent(PregelResourceContext context, bool requireOpen)
    {
        if (context.CoordinatorId != _ledger.CoordinatorId || !_ledger.Entries.TryGetValue(context.LeaseId, out var parent)
            || parent.State is not ("granted" or "releasing") || requireOpen && parent.State != "granted")
            throw new PregelException("mutex_parent_released", "父资源租约不存在或已进入释放，不能新增子申请。");
        return parent;
    }

    private ResourceEntry ParentIdentity(PregelResourceContext context)
    {
        if (context.CoordinatorId != _ledger.CoordinatorId || !_ledger.Entries.TryGetValue(context.LeaseId, out var parent))
            throw new PregelException("mutex_parent_missing", "父资源身份不属于此协调器。");
        return parent;
    }

    private void Authorize(ResourceEntry entry, PregelResourceMessage message)
    {
        if (entry.OwnerMachine.Equals(message.MachineName, StringComparison.OrdinalIgnoreCase)) return;
        if (message.ParentContext != null && message.ParentContext == entry.Request.ParentContext)
        {
            var parent = ParentIdentity(message.ParentContext);
            if (parent.OwnerMachine.Equals(message.MachineName, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new PregelException("mutex_owner_mismatch", "资源申请不属于请求机器或其直接子图。");
    }

    private PregelResourceContext Context(ResourceEntry entry) => new(_ledger.CoordinatorId, entry.Request.RequestId);
    private PregelResourceReply Reply(ResourceEntry entry) => new(entry.State)
    {
        Context = entry.State is "granted" or "releasing" ? Context(entry) : null,
        Waiting = entry.Waiting, Error = entry.Error
    };
    private static PregelResourceReply Failure(string code, string message) => new("failed") { Error = new(code, message) };
    private static bool ValidText(string value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
    private static void ValidateId(string value)
    {
        if (!ValidText(value, 256)) throw new InvalidDataException("资源申请身份无效。");
    }
    private static void ValidateDefinition(PregelResourceRequest request)
    {
        ValidateId(request.RequestId);
        if (!ValidText(request.RunId, 256) || request.Rules == null || request.Rules.Count > 4096
            || request.Rules.Any(rule => !ValidText(rule.Key, 4096)))
            throw new InvalidDataException("Pregel 资源申请格式无效。");
    }
    private static bool SameRequest(PregelResourceRequest left, PregelResourceRequest right) =>
        left.RequestId == right.RequestId && left.RunId == right.RunId && left.ParentContext == right.ParentContext
        && left.QueueDeadlineUtc == right.QueueDeadlineUtc && left.Rules.SequenceEqual(right.Rules);
    private void Save()
    {
        try { RemotePregelResourceCoordinator.WriteJson(_statePath, _ledger); }
        catch { _faulted = true; throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _listener.Close();
        if (_listening != null) try { await _listening.ConfigureAwait(false); } catch (OperationCanceledException) { }
        Task[] pending;
        lock (_gate) pending = _requests.Concat(_ledger.Entries.Values.Select(e => e.Acquisition).OfType<Task>()).ToArray();
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception error) { Serilog.Log.Warning("Pregel resource service stopped with pending transport: {ErrorType}", error.GetType().Name); }
        // Dispose process-local handles only. Persistent grants survive until their owners
        // confirm release; the next process restores them before accepting new work.
        foreach (var entry in _ledger.Entries.Values)
        {
            if (entry.Lease != null) await entry.Lease.DisposeAsync().ConfigureAwait(false);
            entry.Cancellation?.Dispose();
        }
        _writer.Dispose();
        _lifetime.Dispose();
    }

    private sealed record ResourceLedger
    {
        public int Version { get; init; } = 1;
        public string TenantId { get; init; } = "";
        public string CoordinatorId { get; init; } = Guid.NewGuid().ToString("N");
        public long LastSequence { get; set; }
        public Dictionary<string, ResourceEntry> Entries { get; init; } = new(StringComparer.Ordinal);
    }
    private sealed class ResourceEntry
    {
        public PregelResourceRequest Request { get; init; } = new("", "", [], null, null);
        public string OwnerMachine { get; init; } = "";
        public long Sequence { get; init; }
        public bool HasDefinition { get; init; }
        public string State { get; set; } = "pending";
        public PregelError? Error { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<PregelResourceWaitState> Waiting { get; set; } = [];
        [System.Text.Json.Serialization.JsonIgnore] public IPregelResourceLease? Lease { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource? Cancellation { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public Task? Acquisition { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool ReleasingNow { get; set; }
    }
}
