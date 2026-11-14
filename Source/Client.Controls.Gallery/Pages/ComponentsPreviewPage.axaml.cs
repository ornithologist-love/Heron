using Avalonia;
using Avalonia.Controls;
#if !GALLERY_PLUGIN
using Avalonia.Headless;
#endif
using Avalonia.Input;

namespace Heron.ClientControls.Gallery.Pages;

public sealed partial class ComponentsPreviewPage : UserControl
{
    public ComponentsPreviewPage() => InitializeComponent();

    internal void ApplyState(Window window, string state, bool simulatePointer = true)
    {
        Samples.IsEnabled = state != "disabled";
        if (state == "focus") SampleInput.Focus(NavigationMethod.Tab);
        else if (state == "popup") SampleSelect.IsDropDownOpen = true;
#if !GALLERY_PLUGIN
        else if (simulatePointer && (state is "hover" or "pressed"))
        {
            var point = SampleButton.TranslatePoint(new Avalonia.Point(SampleButton.Bounds.Width / 2, SampleButton.Bounds.Height / 2), window);
            if (point == null) return;
            window.MouseMove(point.Value);
            if (state == "pressed") window.MouseDown(point.Value, MouseButton.Left);
        }
#endif
    }
}
