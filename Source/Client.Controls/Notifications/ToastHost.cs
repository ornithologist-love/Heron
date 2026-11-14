using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Heron.ClientControls.Notifications;

// Adapted from SukiUI 6.1.1 SukiToastHost under the MIT license. See THIRD-PARTY-NOTICES.md.
public sealed class ToastHost : ItemsControl
{
    private IToastManager? _attachedManager;
    private bool _isAttached;

    public static readonly StyledProperty<IToastManager?> ManagerProperty =
        AvaloniaProperty.Register<ToastHost, IToastManager?>(nameof(Manager));

    public static readonly StyledProperty<int> MaxToastsProperty =
        AvaloniaProperty.Register<ToastHost, int>(nameof(MaxToasts), 4);

    public static readonly StyledProperty<ToastPlacement> PlacementProperty =
        AvaloniaProperty.Register<ToastHost, ToastPlacement>(nameof(Placement), ToastPlacement.BottomRight);

    public IToastManager? Manager { get => GetValue(ManagerProperty); set => SetValue(ManagerProperty, value); }
    public int MaxToasts { get => GetValue(MaxToastsProperty); set => SetValue(MaxToastsProperty, value); }
    public ToastPlacement Placement { get => GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        AttachManager(Manager);
        UpdatePlacement();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        AttachManager(null);
        Items.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ManagerProperty && _isAttached)
            AttachManager(change.NewValue as IToastManager);
        else if (change.Property == PlacementProperty)
        {
            UpdatePlacement();
            foreach (var card in Items.OfType<ToastCard>())
                card.Placement = Placement;
        }
        else if (change.Property == MaxToastsProperty && Manager is not null)
            Manager.EnsureMaximum(Math.Max(0, MaxToasts));
    }

    private void AttachManager(IToastManager? manager)
    {
        if (ReferenceEquals(_attachedManager, manager))
            return;
        if (_attachedManager is not null)
        {
            _attachedManager.ToastQueued -= OnToastQueued;
            _attachedManager.ToastDismissed -= OnToastDismissed;
        }

        Items.Clear();

        _attachedManager = manager;
        if (_attachedManager is null)
            return;

        _attachedManager.ToastQueued += OnToastQueued;
        _attachedManager.ToastDismissed += OnToastDismissed;
        foreach (var notification in _attachedManager.Active)
            AddToast(notification);
        _attachedManager.EnsureMaximum(Math.Max(0, MaxToasts));
    }

    private void OnToastQueued(object? sender, ToastQueuedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            AddToast(e.Notification);
            Manager?.EnsureMaximum(Math.Max(0, MaxToasts));
        });

    private void OnToastDismissed(object? sender, ToastDismissedEventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = RemoveToastAsync(e.Notification));

    private void AddToast(ToastNotification notification)
    {
        if (Items.OfType<ToastCard>().Any(card => card.Notification?.Id == notification.Id) || Manager is null)
            return;

        var card = new ToastCard { Placement = Placement };
        card.Initialize(notification, Manager);
        Items.Add(card);
        card.AnimateIn();
    }

    private async Task RemoveToastAsync(ToastNotification notification)
    {
        var card = Items.OfType<ToastCard>().FirstOrDefault(item => item.Notification?.Id == notification.Id);
        if (card is null)
            return;

        await card.AnimateOutAsync();
        Items.Remove(card);
    }

    private void UpdatePlacement()
    {
        HorizontalAlignment = Placement is ToastPlacement.TopLeft or ToastPlacement.BottomLeft
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        VerticalAlignment = Placement is ToastPlacement.TopLeft or ToastPlacement.TopRight
            ? VerticalAlignment.Top
            : VerticalAlignment.Bottom;
    }
}
