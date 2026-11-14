namespace Heron.ClientControls.Buttons;

/// <summary>
/// Describes where the text is placed relative to the icon inside an <see cref="IconButton"/>.
/// Left/Right produce a horizontal layout; Top/Bottom produce a vertical layout.
/// </summary>
public enum IconButtonTextPlacement
{
    /// <summary>Text is placed to the left of the icon; horizontal layout.</summary>
    Left,

    /// <summary>Text is placed to the right of the icon; horizontal layout. Default.</summary>
    Right,

    /// <summary>Text is placed above the icon; vertical layout.</summary>
    Top,

    /// <summary>Text is placed below the icon; vertical layout.</summary>
    Bottom
}
