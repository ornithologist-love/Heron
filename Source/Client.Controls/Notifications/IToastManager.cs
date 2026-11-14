namespace Heron.ClientControls.Notifications;

public interface IToastManager
{
    event EventHandler<ToastQueuedEventArgs>? ToastQueued;

    event EventHandler<ToastDismissedEventArgs>? ToastDismissed;

    IReadOnlyList<ToastNotification> Active { get; }

    ToastNotification Show(ToastOptions options);

    bool Dismiss(
        ToastNotification notification,
        ToastDismissReason reason = ToastDismissReason.Programmatic);

    void DismissAll(ToastDismissReason reason = ToastDismissReason.Programmatic);

    void EnsureMaximum(int maximum);
}
