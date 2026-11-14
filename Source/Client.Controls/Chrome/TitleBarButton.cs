using Avalonia.Controls;

namespace Heron.ClientControls.Chrome;

/// <summary>
///     Button placed in a self-drawn title bar. It stays clickable inside the caption area
///     (<see cref="Win32Properties.NonClientHitTestResult"/> is Client) and uses the capsule look
///     (<c>Button.titleBarButton</c>); add the <c>iconOnly</c> class for a borderless square icon button.
/// </summary>
public class TitleBarButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public TitleBarButton()
    {
        Classes.Add("titleBarButton");
        Win32Properties.SetNonClientHitTestResult(this, Win32Properties.Win32HitTestValue.Client);
    }
}
