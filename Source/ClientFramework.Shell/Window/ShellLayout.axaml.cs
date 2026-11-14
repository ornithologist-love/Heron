using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Heron.ClientControls.Chrome;

namespace Heron.ClientFramework.Shell;

/// <summary>Visual tree of <see cref="ShellWindow"/>; the window owns the slots and the state.</summary>
public sealed partial class ShellLayout : UserControl
{
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan ReorderHold = TimeSpan.FromMilliseconds(450);

    private readonly ShellWindow _window;
    private readonly ThemeModeFlyout _themeMenu = new();
    private DateTime _userMenuClosedAt = DateTime.MinValue;

    public ShellLayout() : this(new ShellWindow()) { }

    internal ShellLayout(ShellWindow window)
    {
        _window = window;
        InitializeComponent();
        UserMenuHeader.DataContext = window;
        ThemeButton.Flyout = _themeMenu;
        _themeMenu.ModeSelected += (_, mode) => _window.State.SelectTheme(mode);
        SidebarPanel.PointerEntered += (_, _) => _window.State.IsSidebarPointerOver = true;
        SidebarPanel.PointerExited += (_, _) => _window.State.IsSidebarPointerOver = false;
        UserMenuButton.Click += (_, _) => ToggleUserMenu();
        UserMenuPopup.Closed += (_, _) => _userMenuClosedAt = DateTime.UtcNow;
        UserMenuEntries.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is MenuCardItem { ClosesMenu: true }) UserMenuPopup.IsOpen = false;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        window.UserMenuItems.CollectionChanged += (_, _) => ComposeUserMenu();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == ShellWindow.StateProperty) AttachState();
        };
        NavigationList.AddHandler(PointerPressedEvent, NavigationPressed, RoutingStrategies.Tunnel);
        NavigationList.AddHandler(PointerMovedEvent, NavigationMoved, RoutingStrategies.Tunnel);
        NavigationList.AddHandler(PointerReleasedEvent, NavigationReleased, RoutingStrategies.Tunnel);
        NavigationList.PointerCaptureLost += (_, _) => ResetDrag();
        AttachState();
        ComposeUserMenu();
        if (OperatingSystem.IsMacOS()) UseMacTitleBar();
    }

    internal Control UserChipAnchor => UserChipPanel;
    internal Popup UserMenu => UserMenuPopup;

    private ShellState? _attached;
    private ShellThemeMenuItem? _themeEntry;

    private void AttachState()
    {
        if (_attached != null) _attached.PropertyChanged -= StateChanged;
        _attached = _window.State;
        _attached.PropertyChanged += StateChanged;
        _themeMenu.SystemLabel = _attached.Text.ThemeSystem;
        _themeMenu.LightLabel = _attached.Text.ThemeLight;
        _themeMenu.DarkLabel = _attached.Text.ThemeDark;
        _themeMenu.Mode = _attached.ThemeMode;
        Avalonia.Automation.AutomationProperties.SetName(ThemeButton, _attached.Text.ThemeMenuName);
        _themeEntry?.Attach(_attached);
    }

    private void StateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellState.ThemeMode)) _themeMenu.Mode = _window.State.ThemeMode;
        if (e.PropertyName == nameof(ShellState.IsSidebarPinnedExpanded) && !_window.State.IsSidebarPinnedExpanded) ResetDrag();
    }

    private void ComposeUserMenu()
    {
        UserMenuEntries.Children.Clear();
        var entries = ShellMenuSlots.Compose<Control>(_window.UserMenuItems,
            entry => entry is ShellThemeMenuItem, () => new ShellThemeMenuItem());
        _themeEntry = entries.OfType<ShellThemeMenuItem>().First();
        _themeEntry.Attach(_window.State);
        UserMenuEntries.Children.AddRange(entries);
    }

    private void ToggleUserMenu()
    {
        // A light-dismiss click on the chip closes the menu first; that same click must not reopen it.
        if (!UserMenuPopup.IsOpen && DateTime.UtcNow - _userMenuClosedAt < ReopenGuard) return;
        UserMenuPopup.IsOpen = !UserMenuPopup.IsOpen;
    }

    private void UseMacTitleBar()
    {
        // Native traffic lights stay in this row; reserve their area on the left instead of the right.
        TitleBarContent.Margin = new Thickness(88, 0, 8, 0);
        TitleBarContent.Background = Avalonia.Media.Brushes.Transparent;
        WindowDecorationProperties.SetElementRole(TitleBarContent, WindowDecorationsElementRole.TitleBar);
        TitleBarContent.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var button in TitleBarContent.GetVisualDescendants().OfType<Button>())
                WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        });
    }

    // Long-press reordering of a pinned sidebar; a short press selects the entry.
    private IDisposable? _holdTimer;
    private object? _pressedItem;
    private ListBoxItem? _dragVisual;
    private Point _pressPoint;
    private bool _dragging;
    private bool _moved;

    private bool CanReorder => _window.CanReorderNavigation && _window.State.IsSidebarPinnedExpanded;

    private static ListBoxItem? ItemAt(ListBox list, Point position) => list.GetVisualDescendants().OfType<ListBoxItem>()
        .FirstOrDefault(item => item.TranslatePoint(default, list) is { } origin && new Rect(origin, item.Bounds.Size).Contains(position));

    private void NavigationPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanReorder || !e.GetCurrentPoint(NavigationList).Properties.IsLeftButtonPressed) return;
        var item = ItemAt(NavigationList, e.GetPosition(NavigationList));
        if (item?.DataContext is not { } model) return;
        ResetDrag();
        _pressedItem = model;
        _pressPoint = e.GetPosition(NavigationList);
        e.Pointer.Capture(NavigationList);
        e.Handled = true;
        _holdTimer = DispatcherTimer.RunOnce(() =>
        {
            if (_pressedItem == null || !CanReorder) return;
            _dragging = true;
            _dragVisual = item;
            item.Classes.Add("reordering");
            NavigationList.Cursor = new Cursor(StandardCursorType.SizeAll);
        }, ReorderHold);
    }

    private void NavigationMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedItem == null) return;
        if (!CanReorder) { ResetDrag(); e.Pointer.Capture(null); return; }
        var position = e.GetPosition(NavigationList);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _pressPoint.X) + Math.Abs(position.Y - _pressPoint.Y) > 6) { ResetDrag(); e.Pointer.Capture(null); }
            return;
        }
        if (ItemAt(NavigationList, position)?.DataContext is { } target && !ReferenceEquals(target, _pressedItem))
        {
            var selected = _window.SelectedNavigationItem;
            _window.MoveNavigationItem(_pressedItem, target);
            _window.SelectedNavigationItem = selected;
            _moved = true;
        }
        e.Handled = true;
    }

    private void NavigationReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedItem == null) return;
        if (!_dragging) _window.SelectedNavigationItem = _pressedItem;
        if (_moved) _window.CommitNavigationOrder();
        ResetDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ResetDrag()
    {
        _holdTimer?.Dispose(); _holdTimer = null;
        _dragVisual?.Classes.Remove("reordering"); _dragVisual = null;
        _pressedItem = null; _dragging = false; _moved = false;
        NavigationList.Cursor = null;
    }
}
