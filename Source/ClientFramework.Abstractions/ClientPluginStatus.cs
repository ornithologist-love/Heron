using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Heron.ClientFramework.Abstractions;

public partial class ClientPluginStatus(string id, Func<Task> execute) : ObservableObject
{
    public string Id { get; } = id;
    public IAsyncRelayCommand Command { get; } = new AsyncRelayCommand(execute);
    /// <summary>Plugin-owned view in the left footer slot; null keeps the default right badge.</summary>
    public object? LeftContent { get; init; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private IBrush _brush = Brushes.Gray;

    /// <summary>Optional icon displayed instead of the status light in the footer entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private object? _iconContent;
    public bool HasIcon => IconContent != null;
    [ObservableProperty] private bool _showIntroGlow;

    /// <summary>Optional popup content shown above the footer badge when clicked.</summary>
    [ObservableProperty] private object? _popupContent;

    /// <summary>Popup open state (two-way bound to the host footer Popup control).</summary>
    [ObservableProperty] private bool _isPopupOpen;
}
