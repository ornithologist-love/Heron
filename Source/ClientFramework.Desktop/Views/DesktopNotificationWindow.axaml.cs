using Avalonia.Controls;
using Avalonia.Threading;
using Heron.ClientControls.Notifications;

namespace Heron.ClientFramework.Infrastructure;

public partial class DesktopNotificationWindow : Window
{
    private readonly bool _isAutomationTest = Array.Exists(
        Environment.GetCommandLineArgs(),
        argument => argument == "--test-download-notification-stack");
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ToastManager _toastManager = new();
    private readonly string _title;
    private readonly string _message;
    private readonly DesktopNotificationKind _kind;
    private readonly Action? _bodyAction;
    private ToastNotification? _notification;
    private bool _closing;

    public DesktopNotificationWindow()
        : this(string.Empty, string.Empty, DesktopNotificationKind.Success)
    {
    }

    public DesktopNotificationWindow(
        string title,
        string message,
        DesktopNotificationKind kind,
        Action? bodyAction = null)
    {
        _title = title;
        _message = message;
        _kind = kind;
        _bodyAction = bodyAction;
        InitializeComponent();
        Title = ClientHostProfile.DisplayName + "通知";
        ShowInTaskbar = _isAutomationTest;
        Toasts.Manager = _toastManager;
        _toastManager.ToastDismissed += OnToastDismissed;
        Opened += OnOpened;
    }

    public event EventHandler? ActivateRequested;

    public void CloseAnimated()
    {
        if (_closing)
            return;
        if (_notification is null || !_toastManager.Dismiss(_notification))
            CloseImmediately();
    }

    public void CloseImmediately()
    {
        _closing = true;
        _lifetimeCancellation.Cancel();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _toastManager.ToastDismissed -= OnToastDismissed;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        base.OnClosed(e);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _notification = _toastManager.Show(new ToastOptions
            {
                Title = _title,
                Message = _message,
                Kind = MapKind(_kind),
                Duration = ToastTiming.RecommendedDuration(_title, _message),
                PauseOnPointerOver = true,
                ShowCloseButton = true,
                DismissOnBodyClick = _bodyAction != null,
                BodyAction = _ => InvokeBodyAction()
            });
        }, DispatcherPriority.Render);
    }

    private void InvokeBodyAction()
    {
        if (_bodyAction != null)
        {
            try
            {
                _bodyAction();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Desktop notification action failed: {ex}");
            }

            return;
        }

        ActivateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnToastDismissed(object? sender, ToastDismissedEventArgs e)
    {
        if (_notification?.Id != e.Notification.Id || _closing)
            return;

        _closing = true;
        _ = CloseAfterToastAnimationAsync();
    }

    private async Task CloseAfterToastAnimationAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(240), _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Close();
    }

    private static ToastKind MapKind(DesktopNotificationKind kind) => kind switch
    {
        DesktopNotificationKind.Information => ToastKind.Information,
        DesktopNotificationKind.Warning => ToastKind.Warning,
        DesktopNotificationKind.Error => ToastKind.Error,
        _ => ToastKind.Success
    };
}
