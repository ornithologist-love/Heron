using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Heron.ClientControls.Chrome;
using Heron.ClientFramework.Shell;

namespace Heron.ClientHost;

/// <summary>The launcher's window: the framework shell with a neutral title and no product areas.</summary>
internal sealed class HostWindow : ShellWindow
{
    private readonly HostModel _model;
    private readonly ShellDiagnosticsManager _diagnostics;
    private readonly MenuCardItem _exitItem;
    private bool _isExiting;

    public HostWindow(HostModel model)
    {
        _model = model;
        _diagnostics = new ShellDiagnosticsManager(() => _isExiting);
        DataContext = model;
        Title = "Heron";
        Width = 1280;
        Height = 840;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        State = model.State;
        User = model.User;
        TitleContent = new TextBlock
        {
            Text = "Heron", FontSize = 14, FontWeight = Avalonia.Media.FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        Bind(NavigationItemsProperty, new Binding(nameof(HostModel.NavigationItems)));
        Bind(SelectedNavigationItemProperty, new Binding(nameof(HostModel.SelectedNavigationItem)) { Mode = BindingMode.TwoWay });
        Bind(MountedPagesProperty, new Binding(nameof(HostModel.MountedPages)));
        Bind(PageContentProperty, new Binding(nameof(HostModel.PageContent)));
        Bind(StatusesProperty, new Binding(nameof(HostModel.Statuses)));
        Bind(LeftStatusesProperty, new Binding(nameof(HostModel.LeftStatuses)));
        var error = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(HostModel.Error)));
        FooterContent = error;
        NavigationItemMoved += model.MoveNavigationItem;
        _exitItem = new MenuCardItem { Icon = FluentIcons.Common.Icon.ArrowExit, Text = "退出" };
        _exitItem.Click += (_, _) => Close();
        model.DiagnosticsChanged += RefreshUserMenu;
        RefreshUserMenu();
    }

    internal Task CloseDiagnosticsAsync()
    {
        _isExiting = true;
        return _diagnostics.CloseAndReleaseAllAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _isExiting = true;
        _model.DiagnosticsChanged -= RefreshUserMenu;
        _ = _diagnostics.CloseAndReleaseAllAsync();
        base.OnClosing(e);
    }

    private void RefreshUserMenu()
    {
        UserMenuItems.Clear();
        foreach (var (owner, window) in _model.DiagnosticsWindows)
        {
            var entryOwner = owner;
            var entrySource = window;
            var item = new MenuCardItem { Icon = FluentIcons.Common.Icon.Bug, Text = entrySource.Title };
            item.Click += (_, _) =>
            {
                CloseUserMenu();
                _diagnostics.OpenOrShow(entryOwner, entrySource, this);
            };
            UserMenuItems.Add(item);
        }
        UserMenuItems.Add(_exitItem);
    }
}
