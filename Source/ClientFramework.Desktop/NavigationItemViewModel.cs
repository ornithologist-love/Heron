using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     ViewModel for a navigation item in the sidebar.
/// </summary>
public class NavigationItemViewModel : ObservableObject
{
    public string Title { get; init; } = "";
    public Icon Icon { get; init; }
    public string PageKey { get; init; } = "";
    public int Order { get; init; }
}
