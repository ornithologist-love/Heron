using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     Single-line text that stays static with an ellipsis by default. When the pointer hovers
///     over overflowing content, a second copy follows after a small gap so the suffix flows
///     into the next copy's prefix without reversing direction or visibly jumping.
/// </summary>
public partial class MarqueeTextBlock : Avalonia.Controls.UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, string?>(nameof(Text));
    public static readonly StyledProperty<FontWeight> LabelFontWeightProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, FontWeight>(nameof(LabelFontWeight), FontWeight.Normal);
    public static readonly StyledProperty<double> LabelFontSizeProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(LabelFontSize), 14);
    public static readonly StyledProperty<IBrush?> LabelForegroundProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, IBrush?>(nameof(LabelForeground));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontWeight LabelFontWeight { get => GetValue(LabelFontWeightProperty); set => SetValue(LabelFontWeightProperty, value); }
    public double LabelFontSize { get => GetValue(LabelFontSizeProperty); set => SetValue(LabelFontSizeProperty, value); }
    public IBrush? LabelForeground { get => GetValue(LabelForegroundProperty); set => SetValue(LabelForegroundProperty, value); }

    private const double CopyGap = 28;
    private const double PixelsPerSecond = 6;

    private readonly TranslateTransform _transform = new();
    private DispatcherTimer? _timer;
    private double _cycleWidth;
    private double _lastViewportWidth = -1;
    private bool _hasOverflow;
    private bool _isPointerOver;
    private bool _isAttached;

    public MarqueeTextBlock()
    {
        InitializeComponent();
        UpdateViewportHeight();
        Track.RenderTransform = _transform;
        PointerEntered += (_, _) =>
        {
            _isPointerOver = true;
            UpdateAnimationState();
        };
        PointerExited += (_, _) =>
        {
            _isPointerOver = false;
            UpdateAnimationState();
        };
        TextProperty.Changed.AddClassHandler<MarqueeTextBlock>((view, _) => view.Restart());
        LabelFontWeightProperty.Changed.AddClassHandler<MarqueeTextBlock>((view, _) => view.Restart());
        LabelFontSizeProperty.Changed.AddClassHandler<MarqueeTextBlock>((view, _) =>
        {
            view.UpdateViewportHeight();
            view.Restart();
        });
        Viewport.SizeChanged += (_, args) =>
        {
            // Only a genuine container width change should restart the animation. Render
            // transforms do not affect layout, but unrelated layout passes can still raise a
            // size notification with the same effective width.
            if (Math.Abs(args.NewSize.Width - _lastViewportWidth) < 0.5) return;
            _lastViewportWidth = args.NewSize.Width;
            Restart();
        };
    }

    private void Restart()
    {
        StopTimer();
        _transform.X = 0;
        _hasOverflow = false;
        StaticLabel.IsVisible = true;
        Viewport.Opacity = 0;
        DuplicateLabel.IsVisible = false;
        // Deferred: Label's DesiredSize is only valid after one layout pass with the new text/size.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isAttached) return;
            var available = Viewport.Bounds.Width;
            var needed = Label.DesiredSize.Width;
            _hasOverflow = available > 0 && needed - available > 1;
            _cycleWidth = needed + CopyGap;
            UpdateAnimationState();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateAnimationState()
    {
        var shouldAnimate = _isAttached && _isPointerOver && _hasOverflow;
        StopTimer();
        _transform.X = 0;
        StaticLabel.IsVisible = !shouldAnimate;
        Viewport.Opacity = shouldAnimate ? 1 : 0;
        DuplicateLabel.IsVisible = shouldAnimate;
        if (shouldAnimate) StartTimer();
    }

    private void UpdateViewportHeight() => Viewport.Height = LabelFontSize * 1.4;

    private void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        const double stepSeconds = 0.033;
        var next = _transform.X - PixelsPerSecond * stepSeconds;
        if (next <= -_cycleWidth) next += _cycleWidth;
        _transform.X = next;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        base.OnDetachedFromVisualTree(e);
        StopTimer();
    }

}
