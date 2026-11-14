using global::Avalonia.Controls;
using global::Avalonia.Interactivity;
using global::Avalonia.Platform.Storage;
using Heron.Pregel.Avalonia.ViewModels;
using System.Linq;

namespace Heron.Pregel.Avalonia.Views;

public partial class AutomationScriptBrowserView : UserControl
{
    public AutomationScriptBrowserView() => InitializeComponent();

    public Control InitialFocus => ScriptSearchBox;

    private async void OpenScriptDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AutomationPageViewModel vm || vm.IsScriptBusy
            || TopLevel.GetTopLevel(this) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "注册脚本目录", AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.OpenScriptDirectoryAsync(path);
    }
}
