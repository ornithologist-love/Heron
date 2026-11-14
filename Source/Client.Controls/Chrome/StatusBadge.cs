using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Heron.ClientControls.Chrome;

/// <summary>
///     Flat status entry for a footer or toolbar: a coloured light with a breathing halo followed by text,
///     styled by <c>Button.statusBadge</c>. The halo uses an opacity mask instead of an effect so it renders
///     the same in every backend.
/// </summary>
public class StatusBadge : Button
{
    public static readonly StyledProperty<IBrush?> LightBrushProperty =
        AvaloniaProperty.Register<StatusBadge, IBrush?>(nameof(LightBrush), Brushes.Gray);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusBadge, string?>(nameof(Text));

    protected override Type StyleKeyOverride => typeof(Button);

    public StatusBadge()
    {
        Classes.AddRange(["contentButton", "buttonQuiet", "buttonSmall", "statusBadge"]);
        var glow = new Ellipse
        {
            Classes = { "statusGlow" }, Width = 22, Height = 22, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            OpacityMask = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.Parse("#FFFFFFFF"), 0),
                    new GradientStop(Color.Parse("#66FFFFFF"), 0.45),
                    new GradientStop(Color.Parse("#00FFFFFF"), 1),
                },
            },
        };
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        glow.Bind(Shape.FillProperty, this.GetObservable(LightBrushProperty));
        dot.Bind(Shape.FillProperty, this.GetObservable(LightBrushProperty));
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5,
            Children =
            {
                new Panel { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Children = { glow, dot } },
                text,
            },
        };
    }

    /// <summary>Colour of the status light and its halo.</summary>
    public IBrush? LightBrush
    {
        get => GetValue(LightBrushProperty);
        set => SetValue(LightBrushProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
