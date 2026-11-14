using Heron.ClientFramework.PluginProbe.Contracts;
namespace Heron.ClientFramework.PluginProbe;
public sealed class ManagedProbe : IProbe
{
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Action? _handler;
    public string DependencyIdentity => Private.Info.Identity;
    public Task StartAsync(Action tick)
    {
        _stop = new();
        var token = _stop.Token;
        _handler = tick;
        ProbeEvents.Pulse += _handler;
        _worker = Task.Run(async () =>
        {
            try { while (true) { await Task.Delay(15, token); tick(); } }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
        return Task.CompletedTask;
    }
    public async Task StopAsync()
    {
        if (_handler != null) ProbeEvents.Pulse -= _handler;
        _handler = null;
        _stop?.Cancel();
        if (_worker != null) await _worker;
        _worker = null;
        _stop?.Dispose();
        _stop = null;
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
