namespace Heron.ClientControls.Notifications;

public sealed record ToastOptions
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public ToastKind Kind { get; init; } = ToastKind.Information;

    public TimeSpan? Duration { get; init; } = TimeSpan.FromSeconds(5);

    public bool PauseOnPointerOver { get; init; } = true;

    public bool DismissOnBodyClick { get; init; }

    public bool ShowCloseButton { get; init; } = true;

    public Action<ToastNotification>? BodyAction { get; init; }
}
