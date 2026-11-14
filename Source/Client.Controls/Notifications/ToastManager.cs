namespace Heron.ClientControls.Notifications;

/// <summary>
/// A window-scoped toast queue. Give every top-level window its own manager and host.
/// </summary>
public sealed class ToastManager : IToastManager
{
    private readonly object _gate = new();
    private readonly List<ToastNotification> _active = [];

    public event EventHandler<ToastQueuedEventArgs>? ToastQueued;

    public event EventHandler<ToastDismissedEventArgs>? ToastDismissed;

    public IReadOnlyList<ToastNotification> Active
    {
        get
        {
            lock (_gate)
                return _active.ToArray();
        }
    }

    public ToastNotification Show(ToastOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Title) && string.IsNullOrWhiteSpace(options.Message))
            throw new ArgumentException("A toast needs a title or message.", nameof(options));
        if (options.Duration is { } duration && duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Duration must be positive or null.");

        var notification = new ToastNotification(options);
        lock (_gate)
            _active.Add(notification);

        ToastQueued?.Invoke(this, new ToastQueuedEventArgs(notification));
        return notification;
    }

    public bool Dismiss(
        ToastNotification notification,
        ToastDismissReason reason = ToastDismissReason.Programmatic)
    {
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            if (!_active.Remove(notification))
                return false;
        }

        ToastDismissed?.Invoke(this, new ToastDismissedEventArgs(notification, reason));
        return true;
    }

    public void DismissAll(ToastDismissReason reason = ToastDismissReason.Programmatic)
    {
        ToastNotification[] removed;
        lock (_gate)
        {
            removed = _active.ToArray();
            _active.Clear();
        }

        foreach (var notification in removed)
            ToastDismissed?.Invoke(this, new ToastDismissedEventArgs(notification, reason));
    }

    public void EnsureMaximum(int maximum)
    {
        if (maximum < 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));

        ToastNotification[] removed;
        lock (_gate)
        {
            var overflow = Math.Max(0, _active.Count - maximum);
            removed = _active.Take(overflow).ToArray();
            if (overflow > 0)
                _active.RemoveRange(0, overflow);
        }

        foreach (var notification in removed)
            ToastDismissed?.Invoke(
                this,
                new ToastDismissedEventArgs(notification, ToastDismissReason.Capacity));
    }
}
