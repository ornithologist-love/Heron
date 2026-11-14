using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Heron.ClientControls.Chrome;

/// <summary>
///     Entry of a self-drawn menu card (see <see cref="PopupCard"/>): icon, label and optional trailing detail,
///     styled by <c>Button.menuCardItem</c>. Set <see cref="Trailing"/> to place a control such as a toggle at the
///     end of the row; a row with a trailing control does not ask its menu to close when clicked.
/// </summary>
public class MenuCardItem : Button
{
    public static readonly StyledProperty<Icon> IconProperty =
        AvaloniaProperty.Register<MenuCardItem, Icon>(nameof(Icon), Icon.Circle);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MenuCardItem, string?>(nameof(Text));

    public static readonly StyledProperty<string?> DetailProperty =
        AvaloniaProperty.Register<MenuCardItem, string?>(nameof(Detail));

    public static readonly StyledProperty<Control?> TrailingProperty =
        AvaloniaProperty.Register<MenuCardItem, Control?>(nameof(Trailing));

    public static readonly StyledProperty<IconVariant> IconVariantProperty =
        AvaloniaProperty.Register<MenuCardItem, IconVariant>(nameof(IconVariant), IconVariant.Regular);

    private readonly TextBlock _detail = new() { Classes = { "menuCardDetail" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _trailing = new() { VerticalAlignment = VerticalAlignment.Center };

    protected override Type StyleKeyOverride => typeof(Button);

    public MenuCardItem()
    {
        Classes.Add("menuCardItem");
        var icon = new FluentIcon
        {
            Classes = { "menuCardIcon" }, IconSize = IconSize.Size16, FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Bind(FluentIcon.IconProperty, this.GetObservable(IconProperty));
        icon.Bind(FluentIcon.IconVariantProperty, this.GetObservable(IconVariantProperty));
        var text = new TextBlock { Classes = { "menuCardText" }, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        _detail.Bind(TextBlock.TextProperty, this.GetObservable(DetailProperty));
        _detail.IsVisible = false;
        _trailing.IsVisible = false;
        text.Bind(TextBlock.FontWeightProperty, this.GetObservable(FontWeightProperty));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        Grid.SetColumn(_detail, 2); row.Children.Add(_detail);
        Grid.SetColumn(_trailing, 4); row.Children.Add(_trailing);
        Content = row;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DetailProperty) _detail.IsVisible = !string.IsNullOrEmpty(Detail);
        else if (change.Property == TrailingProperty)
        {
            _trailing.Content = Trailing;
            _trailing.IsVisible = Trailing != null;
        }
        else if (change.Property == TextProperty) Avalonia.Automation.AutomationProperties.SetName(this, Text);
    }

    public Icon Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IconVariant IconVariant
    {
        get => GetValue(IconVariantProperty);
        set => SetValue(IconVariantProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Secondary text after the label, such as a version number.</summary>
    public string? Detail
    {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    /// <summary>Control aligned to the end of the row, such as a toggle switch.</summary>
    public Control? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    /// <summary>True when clicking the entry should close the menu that contains it.</summary>
    public bool ClosesMenu => Trailing == null && Flyout == null;
}
