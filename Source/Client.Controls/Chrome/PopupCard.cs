using Avalonia.Controls;

namespace Heron.ClientControls.Chrome;

/// <summary>
///     Self-drawn card for <see cref="Avalonia.Controls.Primitives.Popup"/> content: rounded surface, divider border
///     and drop shadow, with an outer margin that leaves room for the shadow. Use a Popup instead of a Flyout so no
///     presenter background shows around the rounded corners (HeronTheme keeps <c>PopupRoot</c> transparent).
///     Compensate the margin in the popup offsets when aligning the card to its anchor.
/// </summary>
public class PopupCard : Border
{
}
