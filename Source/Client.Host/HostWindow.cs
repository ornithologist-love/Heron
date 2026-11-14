using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Heron.ClientControls.Chrome;
using Heron.ClientFramework.Shell;

namespace Heron.ClientHost;

/// <summary>The launcher's window: the framework shell with a neutral title and no product areas.</summary>
internal sealed class HostWindow : ShellWindow
{
    public HostWindow(HostModel model)
    {
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
        var exit = new MenuCardItem { Icon = FluentIcons.Common.Icon.ArrowExit, Text = "退出" };
        exit.Click += (_, _) => Close();
        UserMenuItems.Add(exit);
    }
}
