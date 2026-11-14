using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Heron.ClientFramework.Abstractions;

public interface IClientPluginModule
{
    void ConfigureServices(IServiceCollection services);
    Task StartAsync(IClientPluginContext context, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IClientPluginContext
{
    IServiceProvider Services { get; }
    IDisposable RegisterPage(ClientPluginPage page);
    IDisposable RegisterCommand(ClientPluginCommand command);
    IDisposable RegisterStatus(ClientPluginStatus status);
    void Export<T>(T service) where T : class;
    IPluginService<T> ImportOptional<T>(string pluginId) where T : class;
}

/// <summary>Resolve when starting an operation; never cache an optional provider across operations.</summary>
public interface IPluginService<out T> where T : class { T? Value { get; } }

public sealed record ClientPluginPage(string Id, string Title, string Icon, int Order,
    Func<Control> CreateView, bool KeepMounted = false)
{
    /// <summary>Release only the inactive view; keep models/services alive. False pins a view still in use.</summary>
    public Func<Control, bool>? TryReleaseView { get; init; }
}
public sealed record ClientPluginCommand(string Id, string Title, Func<CancellationToken, Task> Execute);

/// <summary>Page-only work; deactivation must not cancel user-owned background operations.</summary>
public interface IClientPageActivity
{
    void SetPageActive(bool active);
}
