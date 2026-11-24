using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Host window with a self-drawn title bar, a pin/hover sidebar with navigation and the user chip, a content
///     area for native and plugin pages and a status footer. The host fills the slots: title and title bar actions,
///     banner, user menu entries, chip accessories, footer items and overlays. Sign-in, product copy and brand
///     stay in the host; plugins add footer statuses through <c>ClientPluginStatus</c>.
/// </summary>
public class ShellWindow : Window
{
    public static readonly StyledProperty<ShellState> StateProperty =
        AvaloniaProperty.Register<ShellWindow, ShellState>(nameof(State));

    public static readonly StyledProperty<IImage?> TitleIconProperty =
        AvaloniaProperty.Register<ShellWindow, IImage?>(nameof(TitleIcon));

    public static readonly StyledProperty<object?> TitleContentProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(TitleContent));

    public static readonly StyledProperty<object?> TitleBarActionsProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(TitleBarActions));

    public static readonly StyledProperty<bool> ShowThemeButtonProperty =
        AvaloniaProperty.Register<ShellWindow, bool>(nameof(ShowThemeButton), true);

    public static readonly StyledProperty<object?> BannerContentProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(BannerContent));

    public static readonly StyledProperty<IEnumerable?> NavigationItemsProperty =
        AvaloniaProperty.Register<ShellWindow, IEnumerable?>(nameof(NavigationItems));

    public static readonly StyledProperty<object?> SelectedNavigationItemProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(SelectedNavigationItem), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<object?> PageContentProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(PageContent));

    public static readonly StyledProperty<IEnumerable?> MountedPagesProperty =
        AvaloniaProperty.Register<ShellWindow, IEnumerable?>(nameof(MountedPages));

    public static readonly StyledProperty<bool> IsContentVisibleProperty =
        AvaloniaProperty.Register<ShellWindow, bool>(nameof(IsContentVisible), true);

    public static readonly StyledProperty<bool> IsWorkspaceVisibleProperty =
        AvaloniaProperty.Register<ShellWindow, bool>(nameof(IsWorkspaceVisible), true);

    public static readonly StyledProperty<IShellUser?> UserProperty =
        AvaloniaProperty.Register<ShellWindow, IShellUser?>(nameof(User));

    public static readonly StyledProperty<object?> UserChipAccessoryProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(UserChipAccessory));

    public static readonly StyledProperty<object?> UserChipOverlayProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(UserChipOverlay));

    public static readonly StyledProperty<IEnumerable?> LeftStatusesProperty =
        AvaloniaProperty.Register<ShellWindow, IEnumerable?>(nameof(LeftStatuses));

    public static readonly StyledProperty<IEnumerable?> StatusesProperty =
        AvaloniaProperty.Register<ShellWindow, IEnumerable?>(nameof(Statuses));

    public static readonly StyledProperty<object?> FooterContentProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(FooterContent));

    public static readonly StyledProperty<object?> FooterTrailingContentProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(FooterTrailingContent));

    public static readonly StyledProperty<object?> OverlayContentProperty =
        AvaloniaProperty.Register<ShellWindow, object?>(nameof(OverlayContent));

    private readonly ShellLayout _layout;
    private readonly ShellState _defaultState = new();

    public ShellWindow()
    {
        SetCurrentValue(StateProperty, _defaultState);
        if (ShellBranding.WindowIcon != null) Icon = ShellBranding.WindowIcon;
        if (ShellBranding.TitleIcon != null) SetCurrentValue(TitleIconProperty, ShellBranding.TitleIcon);
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = WindowDecorations.Full;
        ExtendClientAreaTitleBarHeightHint = 40;
        WindowDecorationsTheme = CreateDecorationsTheme();
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Heron.ClientFramework.Shell/"))
        {
            Source = new Uri("avares://Heron.ClientFramework.Shell/Themes/ShellStyles.axaml"),
        });
        _layout = new ShellLayout(this);
        Content = _layout;
    }

    protected override Type StyleKeyOverride => typeof(Window);

    /// <summary>
    ///     Sidebar and theme state; replace it to supply host text and pin persistence. A supplied state belongs to
    ///     the host and may outlive the window; the window disposes only the default one it created.
    /// </summary>
    public ShellState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>Optional brand icon placed between the sidebar pin button and <see cref="TitleContent" />.</summary>
    public IImage? TitleIcon { get => GetValue(TitleIconProperty); set => SetValue(TitleIconProperty, value); }

    /// <summary>Brand area after the pin button: mark, product name, edition badge.</summary>
    public object? TitleContent { get => GetValue(TitleContentProperty); set => SetValue(TitleContentProperty, value); }

    /// <summary>Title bar controls after the theme button; use <c>TitleBarButton</c> so they stay clickable.</summary>
    public object? TitleBarActions { get => GetValue(TitleBarActionsProperty); set => SetValue(TitleBarActionsProperty, value); }

    public bool ShowThemeButton { get => GetValue(ShowThemeButtonProperty); set => SetValue(ShowThemeButtonProperty, value); }

    /// <summary>Full-width row above sidebar and content, outside any native page area.</summary>
    public object? BannerContent { get => GetValue(BannerContentProperty); set => SetValue(BannerContentProperty, value); }

    /// <summary>Sidebar entries, usually <see cref="NavigationItemViewModel"/>.</summary>
    public IEnumerable? NavigationItems { get => GetValue(NavigationItemsProperty); set => SetValue(NavigationItemsProperty, value); }

    public object? SelectedNavigationItem { get => GetValue(SelectedNavigationItemProperty); set => SetValue(SelectedNavigationItemProperty, value); }

    /// <summary>Active page rendered through data templates; null when a mounted plugin page is active.</summary>
    public object? PageContent { get => GetValue(PageContentProperty); set => SetValue(PageContentProperty, value); }

    /// <summary>Kept-alive plugin pages (<see cref="PluginPageViewModel"/>) stacked by <see cref="PluginPageHost"/>.</summary>
    public IEnumerable? MountedPages { get => GetValue(MountedPagesProperty); set => SetValue(MountedPagesProperty, value); }

    /// <summary>False hides the page area, e.g. while a dialog must not be covered by a native child window.</summary>
    public bool IsContentVisible { get => GetValue(IsContentVisibleProperty); set => SetValue(IsContentVisibleProperty, value); }

    /// <summary>False hides sidebar, pages and footer, leaving the title bar and overlays.</summary>
    public bool IsWorkspaceVisible { get => GetValue(IsWorkspaceVisibleProperty); set => SetValue(IsWorkspaceVisibleProperty, value); }

    /// <summary>Person shown on the sidebar chip and menu header; null hides the chip.</summary>
    public IShellUser? User { get => GetValue(UserProperty); set => SetValue(UserProperty, value); }

    /// <summary>Controls after the user name while the sidebar is expanded.</summary>
    public object? UserChipAccessory { get => GetValue(UserChipAccessoryProperty); set => SetValue(UserChipAccessoryProperty, value); }

    /// <summary>Control drawn over the avatar while the sidebar is collapsed.</summary>
    public object? UserChipOverlay { get => GetValue(UserChipOverlayProperty); set => SetValue(UserChipOverlayProperty, value); }

    /// <summary>
    ///     Host entries of the user menu in display order, typically <c>MenuCardItem</c>. Entries that close the menu
    ///     do so after their click; a <see cref="ShellThemeMenuItem"/> is added first unless the host places one.
    /// </summary>
    public Avalonia.Controls.Controls UserMenuItems { get; } = new();

    /// <summary>Plugin statuses with their own left footer view (<c>ClientPluginStatus.LeftContent</c>).</summary>
    public IEnumerable? LeftStatuses { get => GetValue(LeftStatusesProperty); set => SetValue(LeftStatusesProperty, value); }

    /// <summary>Plugin statuses shown as badges at the right end of the footer.</summary>
    public IEnumerable? Statuses { get => GetValue(StatusesProperty); set => SetValue(StatusesProperty, value); }

    /// <summary>Host footer items between the left statuses and the right group.</summary>
    public object? FooterContent { get => GetValue(FooterContentProperty); set => SetValue(FooterContentProperty, value); }

    /// <summary>Host footer items directly before the plugin status badges.</summary>
    public object? FooterTrailingContent { get => GetValue(FooterTrailingContentProperty); set => SetValue(FooterTrailingContentProperty, value); }

    /// <summary>Layers above the workspace below the title bar, such as sign-in or update dialogs.</summary>
    public object? OverlayContent { get => GetValue(OverlayContentProperty); set => SetValue(OverlayContentProperty, value); }

    /// <summary>Anchor for host popups that open from the user chip.</summary>
    public Control UserChipAnchor => _layout.UserChipAnchor;

    public bool IsUserMenuOpen
    {
        get => _layout.UserMenu.IsOpen;
        set => _layout.UserMenu.IsOpen = value;
    }

    public void CloseUserMenu() => IsUserMenuOpen = false;

    /// <summary>Raised while a pinned sidebar entry is dragged over another; the host moves it in its list.</summary>
    public event Action<object, object>? NavigationItemMoved;

    /// <summary>Raised once after a drag changed the order; the host persists it.</summary>
    public event Action? NavigationReordered;

    internal bool CanReorderNavigation => NavigationItemMoved != null;
    internal void MoveNavigationItem(object item, object target) => NavigationItemMoved?.Invoke(item, target);
    internal void CommitNavigationOrder() => NavigationReordered?.Invoke();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _defaultState.Dispose();
    }

    /// <summary>The application draws its own title; keep the standard caption buttons and drop full screen.</summary>
    internal static ControlTheme CreateDecorationsTheme()
    {
        var theme = new ControlTheme(typeof(WindowDrawnDecorations))
        {
            BasedOn = Application.Current?.TryFindResource(typeof(WindowDrawnDecorations), out var basedOn) == true
                ? basedOn as ControlTheme : null,
        };
        theme.Children.Add(new Style(x => x.Nesting().Class(":has-titlebar").Template().OfType<Panel>().Name("PART_TitleTextPanel"))
        {
            Setters = { new Setter(IsVisibleProperty, false) },
        });
        theme.Children.Add(new Style(x => x.Nesting().Template().OfType<Button>().Name("PART_FullScreenButton"))
        {
            Setters = { new Setter(IsVisibleProperty, false) },
        });
        return theme;
    }
}
