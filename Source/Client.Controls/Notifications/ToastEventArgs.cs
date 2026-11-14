namespace Heron.ClientControls.Notifications;

public sealed class ToastQueuedEventArgs(ToastNotification notification) : EventArgs
{
    public ToastNotification Notification { get; } = notification;
}

public sealed class ToastDismissedEventArgs(
    ToastNotification notification,
    ToastDismissReason reason) : EventArgs
{
    public ToastNotification Notification { get; } = notification;

    public ToastDismissReason Reason { get; } = reason;
}
