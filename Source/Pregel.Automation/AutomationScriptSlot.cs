namespace Heron.Pregel.Automation;

/// <summary>Owns one path's complete script instance across preparation, publication and release.</summary>
internal sealed class AutomationScriptSlot(AutomationHost host, string id) : IAsyncDisposable
{
    private AutomationScriptInstance? _current;
    public string Id => id;
    internal AutomationScriptTemplate? RegisteredTemplate => _current?.Template;
    public AutomationScriptTemplate Template => _current?.Template
        ?? throw new InvalidOperationException("Script is not loaded.");
    public string Hash => _current?.Hash ?? "";

    // The catalog supplies persistence and its projection; it never owns old/new instances.
    public async Task ChangeAsync(string? path, string stateRoot, Action<Action> commit, CancellationToken token)
    {
        host.EnsureScriptCanChange(id);
        AutomationScriptInstance? prepared = null;
        var previous = _current;
        var published = false;
        try
        {
            if (path != null)
                prepared = await Task.Run(() => AutomationScriptLoader.LoadAsync(path, id, stateRoot, host.ScriptReferences, token), token);
            token.ThrowIfCancellationRequested();
            host.CommitScriptChange(this, prepared, publish => commit(() =>
            {
                publish();
                _current = prepared;
            }));
            published = true;
            host.ScriptChangeCommitted();
        }
        finally
        {
            await AutomationScriptInstance.ReleaseAsync(published ? previous : prepared);
        }
    }

    public async ValueTask DisposeAsync()
    {
        host.DetachScript(this);
        var previous = _current;
        _current = null;
        await AutomationScriptInstance.ReleaseAsync(previous);
    }
}

internal sealed class AutomationScriptInstance(AutomationScriptTemplate template, string hash, IDisposable assemblyLoader) : IAsyncDisposable
{
    private int _disposed;
    public AutomationScriptTemplate Template => template;
    public string Hash => hash;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await template.DisposeAsync(); }
        finally { assemblyLoader.Dispose(); }
    }

    public static async ValueTask ReleaseAsync(object? instance)
    {
        try
        {
            if (instance is IAsyncDisposable asynchronous) await asynchronous.DisposeAsync();
            else if (instance is IDisposable synchronous) synchronous.Dispose();
        }
        catch (Exception ex)
        {
            // Cleanup cannot turn a committed publication into a reported load failure,
            // or hide the original preparation/persistence failure.
            Serilog.Log.Warning(ex, "[Automation] script instance release failed");
        }
    }
}
