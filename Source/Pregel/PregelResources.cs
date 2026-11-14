using System.Collections.Concurrent;

namespace Heron.Pregel;

/// <summary>An opaque reference to a live resource scope. Its coordinator validates all inherited ownership.</summary>
public sealed record PregelResourceContext(string CoordinatorId, string LeaseId);

/// <summary>Identity and deadline of one resource admission; persisted before the first acquisition attempt.</summary>
public sealed record PregelResourceAdmission(string RequestId, DateTimeOffset? QueueDeadlineUtc = null)
{
    /// <summary>Local durable evidence that admission ended; the coordinator still validates ownership on reconnect.</summary>
    public bool Granted { get; init; }
}

/// <summary>Resolved MutexRule declarations for one complete graph scope, never inferred from machine or task names.</summary>
public sealed record PregelResourceRequest(string RequestId, string RunId,
    IReadOnlyList<PregelMutexRule> Rules, PregelResourceContext? ParentContext, DateTimeOffset? QueueDeadlineUtc);

public sealed record PregelResourceWaitState(string Key, int Used, int MaxConcurrency, bool QueuedAhead);

public interface IPregelResourceLease : IAsyncDisposable
{
    PregelResourceContext Context { get; }
}

public interface IPregelResourceCoordinator
{
    /// <summary>
    /// Acquires every resource in one scope or waits without partially occupying its new resources.
    /// Cancellation or mutex_queue_timeout must withdraw the request before returning; a cancelled
    /// request must never be granted later. Transport uncertainty must preserve its original identity.
    /// </summary>
    ValueTask<IPregelResourceLease> AcquireAsync(PregelResourceRequest request,
        Action<IReadOnlyList<PregelResourceWaitState>> waiting, CancellationToken cancellationToken);
}

/// <summary>
/// Reuses Pregel's capacity, inheritance and FIFO rules. Each public instance is an independent
/// resource pool; a server can own one instance per tenant without implementing another scheduler.
/// Persistence of remote requests and leases belongs to the hosting adapter.
/// </summary>
public sealed class PregelLocalResourceCoordinator : IPregelResourceCoordinator
{
    /// <summary>Host-owned binding marker. Core only enforces its presence; it never interprets host configuration.</summary>
    public const string ExternalBindingFileName = "coordinator.json";

    private static readonly PregelMutexCoordinator SharedLocalPool = new();
    private static readonly ConcurrentDictionary<string, PregelLocalResourceCoordinator> Local = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly PregelMutexCoordinator _pool;
    private readonly string? _resourceLockDirectory;
    private readonly string _coordinatorId;
    private readonly Dictionary<string, Entry> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    public PregelLocalResourceCoordinator(string? resourceLockDirectory = null, string? coordinatorId = null)
        : this(resourceLockDirectory, coordinatorId, new PregelMutexCoordinator()) { }

    /// <summary>
    /// Persists an external-coordinator binding only when no local queued or granted file lease
    /// remains active. The callback runs under the same gate as local resource admission and
    /// must atomically write ExternalBindingFileName before returning.
    /// </summary>
    public static bool TryBindExternalCoordinator(string resourceLockDirectory, Action persistBinding) =>
        FilePregelResourceLease.TryBindExternalCoordinator(resourceLockDirectory, persistBinding);

    private PregelLocalResourceCoordinator(string? resourceLockDirectory, string? coordinatorId, PregelMutexCoordinator pool)
    {
        _resourceLockDirectory = resourceLockDirectory is null ? null : Path.GetFullPath(resourceLockDirectory);
        _coordinatorId = coordinatorId ?? "local-" + Guid.NewGuid().ToString("N");
        ArgumentException.ThrowIfNullOrWhiteSpace(_coordinatorId);
        _pool = pool;
    }

    internal static PregelLocalResourceCoordinator ForDirectory(string? directory)
    {
        var full = directory is null ? null : Path.GetFullPath(directory);
        return Local.GetOrAdd(full ?? "", _ => new(full, null, SharedLocalPool));
    }

    internal bool HasActiveAdmission(string requestId)
    {
        lock (_gate) return _requests.ContainsKey(requestId);
    }

    internal bool IsDefaultLocal => ReferenceEquals(_pool, SharedLocalPool);

    public async ValueTask<IPregelResourceLease> AcquireAsync(PregelResourceRequest request,
        Action<IReadOnlyList<PregelResourceWaitState>> waiting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentNullException.ThrowIfNull(request.Rules);
        cancellationToken.ThrowIfCancellationRequested();
        request = request with { Rules = PregelMutexRules.Normalize(request.Rules) };
        Entry entry;
        var created = false;
        lock (_gate)
        {
            if (_requests.TryGetValue(request.RequestId, out entry!))
            {
                if (entry.Request.RunId != request.RunId || entry.Request.ParentContext != request.ParentContext
                    || entry.Request.QueueDeadlineUtc != request.QueueDeadlineUtc
                    || !entry.Request.Rules.SequenceEqual(request.Rules))
                    throw new PregelException("mutex_request_changed", "A resource request identity cannot be reused with different rules, ownership or deadline.");
            }
            else
            {
                Lease? parent = null;
                if (request.ParentContext is { } context)
                {
                    if (context.CoordinatorId != _coordinatorId)
                        throw new PregelException("mutex_coordinator_mismatch", "The inherited resource scope belongs to another coordinator.");
                    if (!_leases.TryGetValue(context.LeaseId, out parent) || parent.ReleaseRequested)
                        throw new PregelException("mutex_parent_missing", "The inherited resource lease is no longer active.");
                    parent.Borrowers++;
                }
                entry = new(request, parent);
                _requests.Add(request.RequestId, entry);
                created = true;
            }
        }
        using var cancellation = cancellationToken.Register(entry.Cancel);
        if (created) _ = GrantAsync(entry, waiting, entry.Cancellation.Token);
        // Await withdrawal itself. Returning early from a cancelled WaitAsync would leave a
        // request in the queue that could be granted after its graph has already failed.
        return await entry.Completion.Task.ConfigureAwait(false);
    }

    private async Task GrantAsync(Entry entry, Action<IReadOnlyList<PregelResourceWaitState>> waiting,
        CancellationToken cancellationToken)
    {
        PregelMutexCoordinator.Lease? granted = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timerLifetime = new CancellationTokenSource();
        var expiration = entry.Request.QueueDeadlineUtc is { } until
            ? ExpireAsync(until, deadline, timerLifetime.Token) : Task.CompletedTask;
        try
        {
            granted = await _pool.AcquireAsync(entry.Request.Rules, entry.Parent?.Inner,
                resources => waiting(resources.Select(resource => new PregelResourceWaitState(
                    resource.Key, resource.Used, resource.MaxConcurrency, resource.QueuedAhead)).ToArray()),
                deadline.Token, _resourceLockDirectory).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Request.QueueDeadlineUtc is { } expires && DateTimeOffset.UtcNow >= expires)
                throw QueueTimeout();
            var lease = new Lease(this, entry, granted, new(_coordinatorId, entry.Request.RequestId));
            lock (_gate) _leases.Add(lease.Context.LeaseId, lease);
            granted = null;
            entry.Completion.TrySetResult(lease);
        }
        catch (Exception error)
        {
            granted?.Dispose();
            lock (_gate)
            {
                _requests.Remove(entry.Request.RequestId);
                ReleaseBorrower(entry.Parent);
            }
            entry.Completion.TrySetException(error is OperationCanceledException && !cancellationToken.IsCancellationRequested
                && deadline.IsCancellationRequested ? QueueTimeout() : error);
        }
        finally
        {
            timerLifetime.Cancel();
            await expiration.ConfigureAwait(false);
            entry.Cancellation.Dispose();
        }
    }

    private static async Task ExpireAsync(DateTimeOffset until, CancellationTokenSource deadline, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var remaining = until - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) { deadline.Cancel(); return; }
                // Timer APIs impose a finite maximum delay. Re-evaluate long absolute
                // deadlines without shortening the configured resource queue budget.
                await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static PregelException QueueTimeout() => new("mutex_queue_timeout", "等待 Mutex 资源超过排队时限。");

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (lease.ReleaseRequested) return;
            lease.ReleaseRequested = true;
            _requests.Remove(lease.Entry.Request.RequestId);
            _leases.Remove(lease.Context.LeaseId);
            ReleaseIfUnborrowed(lease);
        }
    }

    // Existing children keep inherited resources alive until their real execution and cleanup have ended.
    private void ReleaseIfUnborrowed(Lease lease)
    {
        if (!lease.ReleaseRequested || lease.Borrowers != 0) return;
        lease.Inner.Dispose();
        ReleaseBorrower(lease.Entry.Parent);
    }

    private void ReleaseBorrower(Lease? parent)
    {
        if (parent is null) return;
        parent.Borrowers--;
        ReleaseIfUnborrowed(parent);
    }

    private sealed class Entry(PregelResourceRequest request, Lease? parent)
    {
        public PregelResourceRequest Request { get; } = request;
        public Lease? Parent { get; } = parent;
        public TaskCompletionSource<IPregelResourceLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public void Cancel()
        {
            if (Completion.Task.IsCompleted) return;
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class Lease(PregelLocalResourceCoordinator owner, Entry entry,
        PregelMutexCoordinator.Lease inner, PregelResourceContext context) : IPregelResourceLease
    {
        public Entry Entry { get; } = entry;
        public PregelMutexCoordinator.Lease Inner { get; } = inner;
        public PregelResourceContext Context { get; } = context;
        public int Borrowers { get; set; }
        public bool ReleaseRequested { get; set; }
        public ValueTask DisposeAsync() { owner.Release(this); return ValueTask.CompletedTask; }
    }
}

public static class PregelResourceScope
{
    internal sealed record Frame(IPregelResourceCoordinator Coordinator, IPregelResourceLease Lease, TimeSpan? QueueTimeout);
    private sealed record Settings(IPregelResourceCoordinator? Coordinator, TimeSpan? QueueTimeout);
    private static readonly AsyncLocal<Frame?> Scope = new();
    private static readonly AsyncLocal<Settings?> Ambient = new();
    internal static Frame? Current { get => Scope.Value; set => Scope.Value = value; }

    /// <summary>Supplies host resource coordination to graphs that construct their own engines/options.</summary>
    public static IDisposable Enter(IPregelResourceCoordinator? coordinator, TimeSpan? queueTimeout = null)
    {
        if (queueTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(queueTimeout), "Resource queue timeout must be positive.");
        var previous = Ambient.Value;
        Ambient.Value = new(coordinator, queueTimeout);
        return new Restore(() => Ambient.Value = previous);
    }

    internal static PregelRunOptions Resolve(PregelRunOptions options) => options with
    {
        ResourceCoordinator = options.ResourceCoordinator ?? Current?.Coordinator ?? Ambient.Value?.Coordinator,
        ResourceQueueTimeout = options.ResourceQueueTimeout ?? Current?.QueueTimeout ?? Ambient.Value?.QueueTimeout
    };

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
