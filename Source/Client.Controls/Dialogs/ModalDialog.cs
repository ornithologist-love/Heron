using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Heron.ClientControls.Dialogs;

/// <summary>
/// A modal surface embedded as the last child of a page's root panel.
/// The caller owns the content and business actions; this control owns the chrome,
/// viewport limits, keyboard dismissal and focus restoration. Bind IsOpen two-way
/// and disable the underlying page while any of its dialogs is open.
/// </summary>
public class ModalDialog : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ModalDialog, bool>(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ModalDialog, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<ModalDialog, string?>(nameof(Description));
    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<ModalDialog, object?>(nameof(Footer));
    public static readonly StyledProperty<double> DialogMaxWidthProperty =
        AvaloniaProperty.Register<ModalDialog, double>(nameof(DialogMaxWidth), 640);
    public static readonly StyledProperty<double> DialogMaxHeightProperty =
        AvaloniaProperty.Register<ModalDialog, double>(nameof(DialogMaxHeight), 720);
    public static readonly StyledProperty<double> DialogHeightProperty =
        AvaloniaProperty.Register<ModalDialog, double>(nameof(DialogHeight), double.NaN);
    public static readonly StyledProperty<Thickness> DialogMarginProperty =
        AvaloniaProperty.Register<ModalDialog, Thickness>(nameof(DialogMargin), new Thickness(24));
    public static readonly StyledProperty<IBrush?> OverlayBrushProperty =
        AvaloniaProperty.Register<ModalDialog, IBrush?>(nameof(OverlayBrush));
    public static readonly StyledProperty<Control?> InitialFocusProperty =
        AvaloniaProperty.Register<ModalDialog, Control?>(nameof(InitialFocus));

    private Button? _closeButton;
    private Border? _panel;
    private TextBlock? _description;
    private Control? _footer;
    private InputElement? _previousFocus;

    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
    public double DialogMaxWidth { get => GetValue(DialogMaxWidthProperty); set => SetValue(DialogMaxWidthProperty, value); }
    public double DialogMaxHeight { get => GetValue(DialogMaxHeightProperty); set => SetValue(DialogMaxHeightProperty, value); }
    public double DialogHeight { get => GetValue(DialogHeightProperty); set => SetValue(DialogHeightProperty, value); }
    public Thickness DialogMargin { get => GetValue(DialogMarginProperty); set => SetValue(DialogMarginProperty, value); }
    public IBrush? OverlayBrush { get => GetValue(OverlayBrushProperty); set => SetValue(OverlayBrushProperty, value); }
    public Control? InitialFocus { get => GetValue(InitialFocusProperty); set => SetValue(InitialFocusProperty, value); }

    public event EventHandler? Closed;

    public ModalDialog()
    {
        IsVisible = false;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
    }

    public void Close() => SetCurrentValue(IsOpenProperty, false);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_closeButton != null) _closeButton.Click -= CloseClicked;
        base.OnApplyTemplate(e);
        _closeButton = e.NameScope.Find<Button>("PART_Close");
        _panel = e.NameScope.Find<Border>("PART_Panel");
        _description = e.NameScope.Find<TextBlock>("PART_Description");
        _footer = e.NameScope.Find<Control>("PART_Footer");
        if (_closeButton != null) _closeButton.Click += CloseClicked;
        UpdateLayoutLimits();
        UpdateOptionalContent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == DialogMaxHeightProperty || change.Property == DialogMarginProperty)
            UpdateLayoutLimits();
        if (change.Property == DescriptionProperty || change.Property == FooterProperty) UpdateOptionalContent();
        if (change.Property != IsOpenProperty) return;
        SetCurrentValue(IsVisibleProperty, IsOpen);
        if (IsOpen)
        {
            _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as InputElement;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsOpen || VisualRoot == null) return;
                if (InitialFocus?.Focus() != true) _closeButton?.Focus();
            });
        }
        else
        {
            Closed?.Invoke(this, EventArgs.Empty);
            _previousFocus?.Focus();
            _previousFocus = null;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsOpen || e.Handled || e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void UpdateLayoutLimits()
    {
        if (_panel != null)
            _panel.MaxHeight = Math.Max(0, Math.Min(DialogMaxHeight, Bounds.Height - DialogMargin.Top - DialogMargin.Bottom));
    }

    private void UpdateOptionalContent()
    {
        if (_description != null) _description.IsVisible = !string.IsNullOrWhiteSpace(Description);
        if (_footer != null) _footer.IsVisible = Footer != null;
    }
}
