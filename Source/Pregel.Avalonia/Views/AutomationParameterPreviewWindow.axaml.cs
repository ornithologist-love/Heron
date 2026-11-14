using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using Heron.Pregel.Avalonia.ViewModels;

namespace Heron.Pregel.Avalonia.Views;

public partial class AutomationParameterPreviewWindow : Window
{
    public AutomationParameterPreviewWindow() => InitializeComponent();

    private async void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AutomationEditorViewModel { ParameterPreview: { } text } && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
