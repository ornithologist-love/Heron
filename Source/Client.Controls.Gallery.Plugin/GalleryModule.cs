using Heron.ClientControls.Gallery;
using Heron.ClientFramework.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Heron.Plugins.Gallery;

/// <summary>The whole plugin: one page whose view is the control Gallery, following the host's theme.</summary>
public sealed class GalleryModule : IClientPluginModule
{
    private IDisposable? _page;

    public void ConfigureServices(IServiceCollection services) { }

    public Task StartAsync(IClientPluginContext context, CancellationToken cancellationToken)
    {
        _page = context.RegisterPage(new ClientPluginPage("heron.gallery", "控件 Gallery", "Grid", 900,
            () => new GalleryView(ownsTheme: false)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _page?.Dispose();
        _page = null;
        return Task.CompletedTask;
    }
}
