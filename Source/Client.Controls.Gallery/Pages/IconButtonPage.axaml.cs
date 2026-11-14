using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Heron.ClientControls.Gallery.Pages;

public sealed partial class IconButtonPage : UserControl
{
    private int _clicks;

    public IconButtonPage()
    {
        InitializeComponent();
    }

    private void OnCounterClick(object? sender, RoutedEventArgs e) =>
        CounterText.Text = $"已点击 {++_clicks} 次";
}
