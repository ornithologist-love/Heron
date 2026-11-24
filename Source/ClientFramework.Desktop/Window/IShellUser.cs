using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     What the shell shows about the signed-in person. It is display only: signing in, sessions and
///     credentials belong to the host, which raises <see cref="INotifyPropertyChanged.PropertyChanged"/> when
///     the values change.
/// </summary>
public interface IShellUser : INotifyPropertyChanged
{
    /// <summary>Short name on the sidebar chip.</summary>
    string DisplayName { get; }

    /// <summary>Full account name in the menu header.</summary>
    string AccountName { get; }

    /// <summary>Organization line under the account name; empty hides nothing but shows no text.</summary>
    string Organization { get; }

    /// <summary>Text drawn on the avatar when there is no picture.</summary>
    string Initials { get; }

    IImage? Avatar { get; }

    /// <summary>Tool tip of the sidebar chip.</summary>
    string Description { get; }
}

/// <summary>Settable <see cref="IShellUser"/> for hosts that do not keep their own user model.</summary>
public sealed partial class ShellUser : ObservableObject, IShellUser
{
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private string _organization = "";
    [ObservableProperty] private string _initials = "";
    [ObservableProperty] private IImage? _avatar = ShellBranding.TitleIcon;
    [ObservableProperty] private string _description = "";
}
