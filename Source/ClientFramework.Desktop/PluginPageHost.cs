using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Heron.ClientFramework.Infrastructure;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Stacks mounted plugin pages in one cell. Only the active page is visible and hit-testable; inactive pages
///     collapse to a minimal size so kept-alive views and native web views keep their state without drawing over it.
/// </summary>
public sealed class PluginPageHost : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public PluginPageHost()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new Grid());
        ItemTemplate = new FuncDataTemplate<PluginPageViewModel>((_, _) =>
        {
            var host = new ContentControl();
            host.Bind(ContentControl.ContentProperty, new Binding(nameof(PluginPageViewModel.View)));
            host.Bind(MaxWidthProperty, new Binding(nameof(PluginPageViewModel.HostMaxWidth)));
            host.Bind(MaxHeightProperty, new Binding(nameof(PluginPageViewModel.HostMaxHeight)));
            host.Bind(NativeWebViewPageVisibility.IsActiveProperty, new Binding(nameof(PluginPageViewModel.IsActive)));
            host.Bind(OpacityProperty, new Binding(nameof(PluginPageViewModel.HostOpacity)));
            host.Bind(IsHitTestVisibleProperty, new Binding(nameof(PluginPageViewModel.IsActive)));
            host.Bind(ZIndexProperty, new Binding(nameof(PluginPageViewModel.HostZIndex)));
            return host;
        });
    }
}
