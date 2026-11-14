using Heron.ClientControls.Buttons;
using Heron.ClientFramework.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Heron.ClientFramework.PluginProbe;

public sealed class BaselineModule : IClientPluginModule
{
    private IDisposable? _command;
    public void ConfigureServices(IServiceCollection services) { }

    public Task StartAsync(IClientPluginContext context, CancellationToken cancellationToken)
    {
        _command = context.RegisterCommand(new ClientPluginCommand("fixture.baseline", typeof(IconButton).Assembly.GetName().Name!,
            _ => Task.CompletedTask));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _command?.Dispose();
        _command = null;
        return Task.CompletedTask;
    }
}
