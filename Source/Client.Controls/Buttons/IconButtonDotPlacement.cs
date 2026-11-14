namespace Heron.ClientControls.Buttons;

/// <summary>
/// Describes which visual element owns the status dot in an <see cref="IconButton"/>.
/// </summary>
public enum IconButtonDotPlacement
{
    /// <summary>The dot overlays the icon's top-right corner and follows the icon when content is rearranged.</summary>
    IconTopRight,

    /// <summary>The dot overlays the button border's top-right corner and remains independent of text placement.</summary>
    BorderTopRight
}
