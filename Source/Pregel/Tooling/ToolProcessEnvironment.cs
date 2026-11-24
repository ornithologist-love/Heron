using System.Diagnostics;

namespace Heron.Pregel.Tooling;

/// <summary>Host-owned child-process configuration, scoped to one asynchronous execution.</summary>
public static class ToolProcessEnvironment
{
    private static readonly AsyncLocal<Func<ProcessStartInfo, CancellationToken, Task>?> Current = new();

    public static IDisposable Enter(Func<ProcessStartInfo, CancellationToken, Task> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var previous = Current.Value;
        Current.Value = configure;
        return new Scope(() => Current.Value = previous);
    }

    internal static async Task ApplyAsync(ProcessStartInfo start, CancellationToken token)
    {
        if (Current.Value is not { } configure) return;
        // Preparing a host environment may itself launch tools; do not recursively apply the same preparation.
        Current.Value = null;
        try { await configure(start, token).ConfigureAwait(false); }
        finally { Current.Value = configure; }
    }

    internal static void Apply(ProcessStartInfo start, CancellationToken token)
    {
        if (Current.Value != null)
            Task.Run(() => ApplyAsync(start, token), token).GetAwaiter().GetResult();
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
