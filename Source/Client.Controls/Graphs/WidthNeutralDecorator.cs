using Avalonia;
using Avalonia.Controls;

namespace Heron.ClientControls.Graphs;

/// <summary>
/// Lays out its child in whatever width the parent gives, but reports zero desired width, so fast-changing or long
/// content (live logs, progress, errors) never drives the width of a size-to-content container such as a graph node.
/// </summary>
public sealed class WidthNeutralDecorator : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child == null) return default;
        Child.Measure(availableSize);
        return new Size(0, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
