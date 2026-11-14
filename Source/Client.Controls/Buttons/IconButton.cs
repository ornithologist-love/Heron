using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Heron.ClientControls.Buttons;

/// <summary>
/// A button composed of an icon and optional text. The text can be placed on any of the
/// four sides of the icon (horizontal or vertical layout), and the corner radius,
/// background, shadow, and status dot are all configurable. Click, command, and state
/// semantics are inherited from <see cref="Button"/>.
/// </summary>
/// <remarks>
/// Layout structure (three-slot template, scale feedback) adapted from the SukiUI Button
/// theme at tag 6.1.1 (MIT). See THIRD-PARTY-NOTICES.md.
/// </remarks>
public class IconButton : Button
{
    private const string IconOnlyPseudoClass = ":icononly";
    private const string BorderDotPseudoClass = ":borderdot";

    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<IconButton, object?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconButton, string?>(nameof(Text));

    public static readonly StyledProperty<IconButtonTextPlacement> TextPlacementProperty =
        AvaloniaProperty.Register<IconButton, IconButtonTextPlacement>(
            nameof(TextPlacement), IconButtonTextPlacement.Right);

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(IconSize), 16);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(Spacing), 6);

    public static readonly StyledProperty<BoxShadows> ShadowProperty =
        AvaloniaProperty.Register<IconButton, BoxShadows>(nameof(Shadow));

    public static readonly StyledProperty<bool> ShowIntroGlowProperty =
        AvaloniaProperty.Register<IconButton, bool>(nameof(ShowIntroGlow));

    public static readonly StyledProperty<bool> ShowDotProperty =
        AvaloniaProperty.Register<IconButton, bool>(nameof(ShowDot));

    public static readonly StyledProperty<IBrush?> DotBrushProperty =
        AvaloniaProperty.Register<IconButton, IBrush?>(nameof(DotBrush));

    public static readonly StyledProperty<IconButtonDotPlacement> DotPlacementProperty =
        AvaloniaProperty.Register<IconButton, IconButtonDotPlacement>(
            nameof(DotPlacement), IconButtonDotPlacement.IconTopRight);

    public static readonly StyledProperty<double> DotSizeProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(DotSize), 8);

    private StackPanel? _layout;
    private Panel? _iconSlot;
    private Border? _root;
    private ContentPresenter? _textHost;
    private Ellipse? _iconDot;
    private Ellipse? _borderDot;

    public IconButton()
    {
        PseudoClasses.Add(IconOnlyPseudoClass);
        PseudoClasses.Add(BorderDotPseudoClass);
        UpdateIconOnlyPseudoClass();
    }

    /// <summary>Icon content. Any control works; PathIcon/FluentIcon are scaled to <see cref="IconSize"/>.</summary>
    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Optional label. When null or empty, the button collapses to an icon-only shape.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Where the text sits relative to the icon. Default: right (horizontal).</summary>
    public IconButtonTextPlacement TextPlacement
    {
        get => GetValue(TextPlacementProperty);
        set => SetValue(TextPlacementProperty, value);
    }

    /// <summary>Final rendered icon size in logical pixels. Default: 16.</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Gap between icon and text. Default: 6.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Drop shadow of the button surface. Default: none.</summary>
    public BoxShadows Shadow
    {
        get => GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    /// <summary>Briefly pulses a halo three times when the entry is first introduced.</summary>
    public bool ShowIntroGlow
    {
        get => GetValue(ShowIntroGlowProperty);
        set => SetValue(ShowIntroGlowProperty, value);
    }

    /// <summary>Shows a badge dot when true and <see cref="DotBrush"/> is set.</summary>
    public bool ShowDot
    {
        get => GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }

    /// <summary>Fill of the status dot overlay.</summary>
    public IBrush? DotBrush
    {
        get => GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    /// <summary>Which top-right corner owns the dot. Default: the icon.</summary>
    public IconButtonDotPlacement DotPlacement
    {
        get => GetValue(DotPlacementProperty);
        set => SetValue(DotPlacementProperty, value);
    }

    /// <summary>Diameter of the dot in logical pixels. Default: 8.</summary>
    public double DotSize
    {
        get => GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _root = e.NameScope.Find<Border>("PART_Root");
        _layout = e.NameScope.Find<StackPanel>("PART_Layout");
        _iconSlot = e.NameScope.Find<Panel>("PART_IconSlot");
        _textHost = e.NameScope.Find<ContentPresenter>("PART_TextHost");
        _iconDot = e.NameScope.Find<Ellipse>("PART_IconDot");
        _borderDot = e.NameScope.Find<Ellipse>("PART_BorderDot");
        ArrangeContent();
        UpdateDot();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            UpdateIconOnlyPseudoClass();
            ArrangeContent();
        }
        else if (change.Property == TextPlacementProperty ||
                 change.Property == IconProperty ||
                 change.Property == ContentProperty)
        {
            UpdateIconOnlyPseudoClass();
            ArrangeContent();
        }
        else if (change.Property == ShowDotProperty ||
                 change.Property == DotBrushProperty ||
                 change.Property == DotPlacementProperty ||
                 change.Property == DotSizeProperty)
        {
            UpdateDot();
        }
    }

    private void UpdateIconOnlyPseudoClass()
        => PseudoClasses.Set(IconOnlyPseudoClass, Icon is not null && TextContent is null);

    private object? TextContent => !string.IsNullOrEmpty(Text) ? Text : Content;

    private void ArrangeContent()
    {
        if (_layout is null || _iconSlot is null || _textHost is null)
            return;

        var hasIcon = Icon is not null;
        var textContent = TextContent;
        var hasTextContent = textContent is not null;
        var placement = TextPlacement;
        _layout.Orientation = placement is IconButtonTextPlacement.Left or IconButtonTextPlacement.Right
            ? Orientation.Horizontal
            : Orientation.Vertical;
        _textHost.Content = textContent;

        _layout.Children.Clear();
        var iconFirst = placement is IconButtonTextPlacement.Right or IconButtonTextPlacement.Bottom;
        if (iconFirst)
        {
            if (hasIcon)
                _layout.Children.Add(_iconSlot);
            if (hasTextContent)
                _layout.Children.Add(_textHost);
        }
        else
        {
            if (hasTextContent)
                _layout.Children.Add(_textHost);
            if (hasIcon)
                _layout.Children.Add(_iconSlot);
        }
    }

    private void UpdateDot()
    {
        var isVisible = ShowDot && DotBrush is not null;
        var showIconDot = isVisible && DotPlacement == IconButtonDotPlacement.IconTopRight;
        var showBorderDot = isVisible && DotPlacement == IconButtonDotPlacement.BorderTopRight;

        if (_iconDot is not null)
            _iconDot.IsVisible = showIconDot;
        if (_borderDot is not null)
            _borderDot.IsVisible = showBorderDot;

        PseudoClasses.Set(BorderDotPseudoClass, showBorderDot);
        if (_root is not null)
        {
            var gutter = showBorderDot ? Math.Max(0, DotSize - 3) : 0;
            _root.Margin = new Thickness(0, gutter, gutter, 0);
        }
    }
}
