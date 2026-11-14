namespace Heron.ClientControls.Notifications;

public sealed class ToastNotification
{
    internal ToastNotification(ToastOptions options)
    {
        Id = Guid.NewGuid();
        Options = options;
    }

    public Guid Id { get; }

    public ToastOptions Options { get; }
}
