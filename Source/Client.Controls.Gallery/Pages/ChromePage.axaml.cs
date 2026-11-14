using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Heron.ClientControls.Gallery.Pages;

public sealed partial class ChromePage : UserControl
{
    public ChromePage()
    {
        InitializeComponent();
        var menu = (Heron.ClientControls.Chrome.ThemeModeFlyout)ThemeButton.Flyout!;
        menu.ModeSelected += (_, mode) => ThemeText.Text = mode switch
        {
            "light" => menu.LightLabel,
            "dark" => menu.DarkLabel,
            _ => menu.SystemLabel,
        };
    }

    private void OpenPopup(object? sender, RoutedEventArgs e) => CardPopup.IsOpen = !CardPopup.IsOpen;
}
