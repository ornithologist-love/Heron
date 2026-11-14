namespace Heron.ClientFramework.Plugins;

/// <summary>Tracks work started by a feature so stopping can reject new work and await existing operations.</summary>
public sealed class AsyncOperationSet
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = [];
    private bool _stopping;
    public Task Run(Func<Task> action)
    {
        lock (_gate)
        {
            if (_stopping) return Task.FromCanceled(new CancellationToken(true));
            var task = action();
            Track(task);
            return task;
        }
    }
    public Task<T> Run<T>(Func<Task<T>> action)
    {
        lock (_gate)
        {
            if (_stopping) return Task.FromCanceled<T>(new CancellationToken(true));
            var task = action();
            Track(task);
            return task;
        }
    }
    private void Track(Task task)
    {
        _tasks.Add(task);
        _ = task.ContinueWith(done => { lock (_gate) _tasks.Remove(done); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public void StopAccepting() { lock (_gate) _stopping = true; }
    public async Task DrainAsync()
    {
        Task[] tasks;
        lock (_gate) tasks = _tasks.ToArray();
        try { await Task.WhenAll(tasks); }
        catch { /* Every task has ended. Its failure belongs to the original caller, not resource disposal. */ }
    }
}
