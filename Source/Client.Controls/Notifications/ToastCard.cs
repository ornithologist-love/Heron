using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Heron.ClientControls.Notifications;

// Adapted from SukiUI 6.1.1 SukiToast under the MIT license. See THIRD-PARTY-NOTICES.md.
[TemplatePart("PART_Card", typeof(Border))]
[TemplatePart("PART_CloseButton", typeof(Button))]
public sealed class ToastCard : ContentControl
{
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(220);

    private readonly TranslateTransform _translation = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Border? _card;
    private Button? _closeButton;
    private ToastCountdown? _countdown;
    private long _lastTick;
    private bool _isDismissing;

    public ToastCard()
    {
        Opacity = 0;
        MaxHeight = 0;
        RenderTransform = _translation;
        Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = ExitDuration,
                Easing = new CubicEaseOut()
            },
            new DoubleTransition
            {
                Property = MaxHeightProperty,
                Duration = EnterDuration,
                Easing = new CubicEaseOut()
            }
        };
        _translation.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = ExitDuration,
                Easing = new CubicEaseOut()
            },
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = EnterDuration,
                Easing = new CubicEaseOut()
            }
        };
        _timer.Tick += OnTimerTick;
    }

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ToastCard, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<ToastCard, string>(nameof(Message), string.Empty);

    public static readonly StyledProperty<ToastKind> KindProperty =
        AvaloniaProperty.Register<ToastCard, ToastKind>(nameof(Kind));

    public static readonly StyledProperty<double> DismissProgressProperty =
        AvaloniaProperty.Register<ToastCard, double>(nameof(DismissProgress), 1);

    public static readonly StyledProperty<bool> IsTimedProperty =
        AvaloniaProperty.Register<ToastCard, bool>(nameof(IsTimed));

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<ToastCard, bool>(nameof(ShowCloseButton), true);

    public static readonly StyledProperty<string> IconTextProperty =
        AvaloniaProperty.Register<ToastCard, string>(nameof(IconText), "i");

    public static readonly StyledProperty<IBrush> AccentBrushProperty =
        AvaloniaProperty.Register<ToastCard, IBrush>(nameof(AccentBrush), Brushes.DodgerBlue);

    public static readonly StyledProperty<IBrush> AccentBackgroundProperty =
        AvaloniaProperty.Register<ToastCard, IBrush>(nameof(AccentBackground), Brushes.AliceBlue);

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public ToastKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double DismissProgress { get => GetValue(DismissProgressProperty); private set => SetValue(DismissProgressProperty, value); }
    public bool IsTimed { get => GetValue(IsTimedProperty); private set => SetValue(IsTimedProperty, value); }
    public bool ShowCloseButton { get => GetValue(ShowCloseButtonProperty); set => SetValue(ShowCloseButtonProperty, value); }
    public string IconText { get => GetValue(IconTextProperty); private set => SetValue(IconTextProperty, value); }
    public IBrush AccentBrush { get => GetValue(AccentBrushProperty); private set => SetValue(AccentBrushProperty, value); }
    public IBrush AccentBackground { get => GetValue(AccentBackgroundProperty); private set => SetValue(AccentBackgroundProperty, value); }

    internal ToastNotification? Notification { get; private set; }
    internal IToastManager? Manager { get; private set; }
    internal ToastPlacement Placement { get; set; } = ToastPlacement.BottomRight;

    internal void Initialize(ToastNotification notification, IToastManager manager)
    {
        Notification = notification;
        Manager = manager;
        Title = notification.Options.Title;
        Message = notification.Options.Message;
        Kind = notification.Options.Kind;
        ShowCloseButton = notification.Options.ShowCloseButton;
        IsTimed = notification.Options.Duration.HasValue;
        _countdown = notification.Options.Duration is { } duration ? new ToastCountdown(duration) : null;
        ApplyKind(notification.Options.Kind);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_card is not null)
            _card.PointerPressed -= OnCardPressed;
        if (_closeButton is not null)
            _closeButton.Click -= OnCloseClicked;

        base.OnApplyTemplate(e);
        _card = e.NameScope.Find<Border>("PART_Card");
        _closeButton = e.NameScope.Find<Button>("PART_CloseButton");
        if (_card is not null)
            _card.PointerPressed += OnCardPressed;
        if (_closeButton is not null)
            _closeButton.Click += OnCloseClicked;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastTick = Stopwatch.GetTimestamp();
        if (_countdown is not null)
            _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (_countdown is not null && Notification?.Options.PauseOnPointerOver == true)
            _countdown.IsPaused = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_countdown is not null)
        {
            _countdown.IsPaused = false;
            _lastTick = Stopwatch.GetTimestamp();
        }
    }

    internal void AnimateIn()
    {
        _translation.X = 0;
        _translation.Y = Placement is ToastPlacement.TopLeft or ToastPlacement.TopRight ? -14 : 14;
        Dispatcher.UIThread.Post(() =>
        {
            MaxHeight = 480;
            Opacity = 1;
            _translation.Y = 0;
        }, DispatcherPriority.Render);
    }

    internal async Task AnimateOutAsync()
    {
        if (_isDismissing)
            return;

        _isDismissing = true;
        _timer.Stop();
        Opacity = 0;
        _translation.X = Placement is ToastPlacement.TopLeft or ToastPlacement.BottomLeft ? -42 : 42;
        MaxHeight = 0;
        await Task.Delay(ExitDuration);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_countdown is null || Notification is null || Manager is null)
            return;

        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
        _lastTick = now;
        if (_countdown.Advance(elapsed))
        {
            _timer.Stop();
            DismissProgress = 0;
            Manager.Dismiss(Notification, ToastDismissReason.Timeout);
            return;
        }

        DismissProgress = _countdown.Progress;
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Notification is null ||
            e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;

        Notification.Options.BodyAction?.Invoke(Notification);
        if (Notification.Options.DismissOnBodyClick)
            Manager?.Dismiss(Notification, ToastDismissReason.BodyClick);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Notification is not null)
            Manager?.Dismiss(Notification, ToastDismissReason.CloseButton);
    }

    private void ApplyKind(ToastKind kind)
    {
        var (icon, accent, background) = kind switch
        {
            ToastKind.Success => ("✓", "SuccessFgBrush", "SuccessBgBrush"),
            ToastKind.Warning => ("!", "WarningFgBrush", "WarningBgBrush"),
            ToastKind.Error => ("×", "DangerFgBrush", "DangerBgBrush"),
            _ => ("i", "InfoFgBrush", "InfoBgBrush")
        };
        IconText = icon;
        this.Bind(AccentBrushProperty, new DynamicResourceExtension(accent));
        this.Bind(AccentBackgroundProperty, new DynamicResourceExtension(background));
    }
}
