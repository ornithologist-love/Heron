using Avalonia.Controls;

namespace Heron.ClientControls.Gallery.Pages;

public partial class PopupComparisonPage : UserControl
{
    private static readonly string[] EnginePathSuggestions =
    {
        "C:\\Dev\\ExampleProject",
        "D:\\Projects\\Meadow",
        "D:\\Toolchains\\Sample-2.0",
    };

    public PopupComparisonPage()
    {
        InitializeComponent();
        EnginePathAutoComplete.ItemsSource = EnginePathSuggestions;
    }
}
