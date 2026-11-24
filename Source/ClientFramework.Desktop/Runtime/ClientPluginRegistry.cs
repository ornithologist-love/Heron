using Heron.ClientFramework.Abstractions;
namespace Heron.ClientFramework.Runtime;

/// <summary>All access occurs on the desktop dispatcher. Removal is synchronous so the shell detaches views before stop.</summary>
public sealed class ClientPluginRegistry
{
    private HashSet<string> _hiddenOwners = new(StringComparer.OrdinalIgnoreCase);
    public bool IsOwnerVisible(string owner) => Volatile.Read(ref _hiddenOwners).Contains(owner) == false;
    public void SetHiddenOwners(IEnumerable<string> owners)
    {
        var hidden = owners.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_hiddenOwners.SetEquals(hidden)) return;
        Volatile.Write(ref _hiddenOwners, hidden);
        Changed?.Invoke();
    }

    private readonly Dictionary<string, (string Owner, ClientPluginStatus Status)> _statuses = new();
    public IReadOnlyList<ClientPluginStatus> Statuses => _statuses.Values.Where(s => IsOwnerVisible(s.Owner)).Select(s => s.Status).ToArray();
    public IDisposable RegisterStatus(ClientPluginStatus status) => RegisterStatus(status.Id, status);
    public IDisposable RegisterStatus(string owner, ClientPluginStatus status)
    {
        if (!_statuses.TryAdd(status.Id, (owner, status))) throw new InvalidOperationException($"Duplicate status '{status.Id}'.");
        try { Changed?.Invoke(); } catch { _statuses.Remove(status.Id); throw; }
        return new Registration(() => { _statuses.Remove(status.Id); Changed?.Invoke(); });
    }
    private readonly Dictionary<(string, Type), (string Version, object Service)> _exports = new();
    public IDisposable Export<T>(string owner, string version, T service) where T : class
    {
        var key = (owner.ToLowerInvariant(), typeof(T));
        if (!_exports.TryAdd(key, (version, service))) throw new InvalidOperationException($"Duplicate service {owner}/{typeof(T)}.");
        try { Changed?.Invoke(); } catch { _exports.Remove(key); throw; }
        return new Registration(() => { _exports.Remove(key); Changed?.Invoke(); });
    }
    public IPluginService<T> ImportOptional<T>(string owner, string range) where T : class => new ServiceHandle<T>(this, owner.ToLowerInvariant(), Heron.ClientFramework.Plugins.PluginVersionRange.Parse(range));
    public IReadOnlyList<(string Owner, T Service)> ImportVisible<T>() where T : class =>
        _exports
            .Where(entry => entry.Key.Item2 == typeof(T) && IsOwnerVisible(entry.Key.Item1))
            .OrderBy(entry => entry.Key.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(entry => (entry.Key.Item1, (T)entry.Value.Service))
            .ToArray();
    private sealed class ServiceHandle<T>(ClientPluginRegistry registry, string owner, Heron.ClientFramework.Plugins.PluginVersionRange range) : IPluginService<T> where T : class
    {
        public T? Value => registry._exports.TryGetValue((owner, typeof(T)), out var export)
            && range.Contains(Heron.ClientFramework.Plugins.PluginVersion.Parse(export.Version)) ? (T)export.Service : null;
    }
    private readonly Dictionary<string, (string Owner, ClientPluginPage Page)> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Owner, ClientPluginCommand Command)> _commands = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;
    public IReadOnlyList<ClientPluginPage> AllPages => _pages.Values.Select(p => p.Page).OrderBy(p => p.Order).ThenBy(p => p.Id).ToArray();
    public IReadOnlyList<ClientPluginPage> Pages => _pages.Values.Where(p => IsOwnerVisible(p.Owner)).Select(p => p.Page).OrderBy(p => p.Order).ThenBy(p => p.Id).ToArray();
    public IReadOnlyList<ClientPluginCommand> Commands => _commands.Values.Where(c => IsOwnerVisible(c.Owner)).Select(c => c.Command).ToArray();

    public IDisposable RegisterPage(string owner, ClientPluginPage page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page.Id);
        if (!_pages.TryAdd(page.Id, (owner, page))) throw new InvalidOperationException($"Duplicate page '{page.Id}'.");
        try { Changed?.Invoke(); }
        catch { _pages.Remove(page.Id); throw; }
        return new Registration(() => { _pages.Remove(page.Id); Changed?.Invoke(); });
    }

    public IDisposable RegisterCommand(string owner, ClientPluginCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Id);
        if (!_commands.TryAdd(command.Id, (owner, command))) throw new InvalidOperationException($"Duplicate command '{command.Id}'.");
        try { Changed?.Invoke(); } catch { _commands.Remove(command.Id); throw; }
        return new Registration(() => { _commands.Remove(command.Id); Changed?.Invoke(); });
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
