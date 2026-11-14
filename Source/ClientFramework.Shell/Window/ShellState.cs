using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Heron.ClientFramework.Infrastructure;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     View state of the shell window: the sidebar pin and hover state machine and the theme choice.
///     The sidebar is expanded while it is pinned or the pointer is over it. Pages hosted in native child
///     windows cannot be overlapped by the hovering sidebar, so for them the content edge follows the drawer;
///     other pages keep the pinned width and let the drawer float over them.
/// </summary>
public sealed partial class ShellState : ObservableObject, IDisposable
{
    public const double ExpandedSidebarWidth = 184;
    public const double CollapsedSidebarWidth = 60;

    private readonly Action<bool>? _persistPinned;

    /// <param name="text">Labels for the shell's own controls.</param>
    /// <param name="pinned">Initial pin state, usually read from the host's settings.</param>
    /// <param name="persistPinned">Called after the user changes the pin; null keeps it in memory.</param>
    public ShellState(ShellText? text = null, bool pinned = true, Action<bool>? persistPinned = null)
    {
        Text = text ?? new ShellText();
        _isSidebarPinnedExpanded = pinned;
        _persistPinned = persistPinned;
        ThemeService.ThemeChanged += OnThemeChanged;
    }

    public ShellText Text { get; }

    [ObservableProperty] private bool _isSidebarPinnedExpanded;
    [ObservableProperty] private bool _isSidebarPointerOver;

    /// <summary>True while the active page is a native child window that the drawer must not cover.</summary>
    [ObservableProperty] private bool _contentFollowsSidebar;

    public bool IsSidebarExpanded => IsSidebarPinnedExpanded || IsSidebarPointerOver;
    public double SidebarWidth => IsSidebarExpanded ? ExpandedSidebarWidth : CollapsedSidebarWidth;
    public double SidebarTextOpacity => IsSidebarExpanded ? 1 : 0;

    public Thickness SidebarContentMargin => new(ContentFollowsSidebar
        ? SidebarWidth
        : IsSidebarPinnedExpanded ? ExpandedSidebarWidth : CollapsedSidebarWidth, 0, 0, 0);

    public Icon SidebarPinIcon => IsSidebarPinnedExpanded ? Icon.PanelLeftContract : Icon.Pin;
    public string SidebarPinToolTip => IsSidebarPinnedExpanded ? Text.UnpinSidebar : Text.PinSidebar;

    /// <summary>The user's choice: <c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string ThemeMode => ThemeService.Mode;
    public bool IsDarkTheme => ThemeService.Current == "dark";
    public string ThemeText => ThemeMode == "system" ? Text.ThemeSystem : IsDarkTheme ? Text.ThemeDark : Text.ThemeLight;
    public Icon ThemeIcon => IsDarkTheme ? Icon.WeatherMoon : Icon.WeatherSunny;

    [RelayCommand]
    private void ToggleSidebarPinned()
    {
        IsSidebarPinnedExpanded = !IsSidebarPinnedExpanded;
        _persistPinned?.Invoke(IsSidebarPinnedExpanded);
    }

    /// <summary>Applies and persists a theme mode through <see cref="ThemeService"/>.</summary>
    [RelayCommand]
    public void SelectTheme(string mode) => ThemeService.Set(mode, persist: true);

    partial void OnIsSidebarPinnedExpandedChanged(bool value) => NotifySidebar();
    partial void OnIsSidebarPointerOverChanged(bool value) => NotifySidebar();
    partial void OnContentFollowsSidebarChanged(bool value) => OnPropertyChanged(nameof(SidebarContentMargin));

    private void NotifySidebar()
    {
        OnPropertyChanged(nameof(IsSidebarExpanded));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(SidebarTextOpacity));
        OnPropertyChanged(nameof(SidebarContentMargin));
        OnPropertyChanged(nameof(SidebarPinIcon));
        OnPropertyChanged(nameof(SidebarPinToolTip));
    }

    private void OnThemeChanged(string theme)
    {
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ThemeText));
        OnPropertyChanged(nameof(ThemeIcon));
    }

    public void Dispose() => ThemeService.ThemeChanged -= OnThemeChanged;
}
