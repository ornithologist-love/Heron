namespace Heron.ClientFramework.PluginProbe.Contracts;
// Test-only contract used by Heron.ClientFramework.Tests; not the production desktop plugin API.
public interface IProbe : IAsyncDisposable
{
    string DependencyIdentity { get; }
    Task StartAsync(Action tick);
    Task StopAsync();
}
public static class ProbeEvents
{
    public static event Action? Pulse;
    public static void Raise() => Pulse?.Invoke();
}
