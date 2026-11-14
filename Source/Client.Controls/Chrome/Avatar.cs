using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Heron.ClientControls.Chrome;

/// <summary>
///     Round user mark: an image when one is available, otherwise initials on the background brush.
///     The circle follows the control size, so the same control serves chips, menu headers and lists.
/// </summary>
public class Avatar : TemplatedControl
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<Avatar, IImage?>(nameof(Source));

    public static readonly StyledProperty<string?> InitialsProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(Initials));

    private readonly TextBlock _initials = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _circle = new() { ClipToBounds = true };

    public Avatar()
    {
        _circle.Child = new Panel { Children = { _initials, _image } };
        LogicalChildren.Add(_circle);
        VisualChildren.Add(_circle);
        _circle.Bind(Border.BackgroundProperty, this.GetObservable(BackgroundProperty));
        _initials.Bind(TextBlock.TextProperty, this.GetObservable(InitialsProperty));
        _initials.Bind(TextBlock.ForegroundProperty, this.GetObservable(ForegroundProperty));
        _initials.Bind(TextBlock.FontSizeProperty, this.GetObservable(FontSizeProperty));
        _initials.Bind(TextBlock.FontWeightProperty, this.GetObservable(FontWeightProperty));
        _image.Bind(Image.SourceProperty, this.GetObservable(SourceProperty));
    }

    /// <summary>Photo or picture; when null the initials are shown.</summary>
    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Text drawn when no image is available, usually one or two characters.</summary>
    public string? Initials
    {
        get => GetValue(InitialsProperty);
        set => SetValue(InitialsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _circle.Measure(availableSize);
        return _circle.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _circle.CornerRadius = new CornerRadius(Math.Min(finalSize.Width, finalSize.Height) / 2);
        _circle.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
