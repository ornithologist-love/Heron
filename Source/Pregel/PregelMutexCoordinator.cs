namespace Heron.Pregel;

/// <summary>One resource pool with atomic admission and FIFO per resource.</summary>
internal sealed class PregelMutexCoordinator
{
    private readonly object Gate = new();
    private readonly Dictionary<string, Resource> Resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Request> Queue = new();

    public async Task<Lease> AcquireAsync(IReadOnlyList<PregelMutexRule> rules, Lease? parent,
        Action<IReadOnlyList<WaitState>> waiting, CancellationToken token, string? resourceLockDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        resourceLockDirectory ??= parent?.ResourceLockDirectory;
        var requested = new List<PregelMutexRule>();
        foreach (var rule in rules)
        {
            if (parent?.Find(rule.Key) is { } inherited)
            {
                EnsureSameLimit(rule, inherited.MaxConcurrency);
                continue;
            }
            requested.Add(rule);
        }
        if (requested.Count == 0) return new Lease(this, [], parent) { ResourceLockDirectory = resourceLockDirectory };

        var request = new Request(requested.ToArray(), parent);
        WaitState[]? waitState;
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            // Validate every key before registering any claim, including queued claims.
            foreach (var rule in request.Rules)
                if (Resources.TryGetValue(rule.Key, out var resource)) EnsureSameLimit(rule, resource.MaxConcurrency);
            foreach (var rule in request.Rules)
            {
                if (!Resources.TryGetValue(rule.Key, out var resource))
                    Resources.Add(rule.Key, resource = new Resource(rule.MaxConcurrency));
                resource.Claims++;
            }
            request.Position = Queue.AddLast(request);
            Dispatch();
            waitState = request.Position.List is null ? null : WaitingState(request);
        }
        try
        {
            if (waitState is not null) waiting(waitState);
            var lease = await request.Completion.Task.WaitAsync(token).ConfigureAwait(false);
            lease.ResourceLockDirectory = resourceLockDirectory;
            token.ThrowIfCancellationRequested();
            if (resourceLockDirectory != null)
                lease.External = await FilePregelResourceLease.AcquireAsync(resourceLockDirectory,
                    request.Rules, waiting, token).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            Lease? granted;
            lock (Gate)
            {
                if (request.Position.List is not null)
                {
                    Queue.Remove(request.Position);
                    RemoveClaims(request.Rules);
                    Dispatch();
                }
                // Cancellation can race with admission. Release an already granted lease as well.
                granted = request.Completion.Task.IsCompletedSuccessfully ? request.Completion.Task.Result : null;
            }
            granted?.Dispose();
            throw;
        }
    }

    private static void EnsureSameLimit(PregelMutexRule rule, int limit)
    {
        if (rule.MaxConcurrency != limit)
            throw new PregelException("mutex_limit_conflict",
                $"Mutex resource '{rule.Key}' has conflicting limits: {limit} and {rule.MaxConcurrency}.");
    }

    // Called under Gate. Earlier blocked requests reserve their queue order, but no resource slots.
    private void Dispatch()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var position = Queue.First; position is not null;)
        {
            var next = position.Next;
            var request = position.Value;
            if (request.Rules.Any(rule => blocked.Contains(rule.Key)
                || Resources[rule.Key].Used >= rule.MaxConcurrency))
            {
                foreach (var rule in request.Rules) blocked.Add(rule.Key);
            }
            else
            {
                Queue.Remove(position);
                foreach (var rule in request.Rules) Resources[rule.Key].Used++;
                request.Completion.SetResult(new Lease(this, request.Rules, request.Parent));
            }
            position = next;
        }
    }

    private WaitState[] WaitingState(Request request)
    {
        var earlier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var position = Queue.First; position is not null && position != request.Position; position = position.Next)
            foreach (var rule in position.Value.Rules) earlier.Add(rule.Key);
        return request.Rules.Where(rule => Resources[rule.Key].Used >= rule.MaxConcurrency || earlier.Contains(rule.Key))
            .Select(rule => new WaitState(rule.Key, Resources[rule.Key].Used, rule.MaxConcurrency, earlier.Contains(rule.Key)))
            .ToArray();
    }

    private void RemoveClaims(PregelMutexRule[] rules)
    {
        foreach (var rule in rules)
            if (--Resources[rule.Key].Claims == 0) Resources.Remove(rule.Key);
    }

    private sealed class Resource(int maxConcurrency)
    {
        public int MaxConcurrency { get; } = maxConcurrency;
        public int Used { get; set; }
        public int Claims { get; set; }
    }

    private sealed class Request(PregelMutexRule[] rules, Lease? parent)
    {
        public PregelMutexRule[] Rules { get; } = rules;
        public Lease? Parent { get; } = parent;
        public LinkedListNode<Request> Position { get; set; } = null!;
        public TaskCompletionSource<Lease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record WaitState(string Key, int Used, int MaxConcurrency, bool QueuedAhead);

    internal sealed class Lease(PregelMutexCoordinator coordinator, PregelMutexRule[] rules, Lease? parent) : IDisposable
    {
        internal IDisposable? External { get; set; }
        internal string? ResourceLockDirectory { get; set; }
        private int _disposed;
        public PregelMutexRule? Find(string key) => Volatile.Read(ref _disposed) == 0
            ? rules.FirstOrDefault(rule => string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase)) ?? parent?.Find(key)
            : null;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || rules.Length == 0) return;
            External?.Dispose();
            lock (coordinator.Gate)
            {
                foreach (var rule in rules) coordinator.Resources[rule.Key].Used--;
                coordinator.RemoveClaims(rules);
                coordinator.Dispatch();
            }
        }
    }
}
