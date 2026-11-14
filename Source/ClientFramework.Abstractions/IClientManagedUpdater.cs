namespace Heron.ClientFramework.Abstractions;

/// <summary>Host-owned machine maintenance and status shown by the Agent plugin.</summary>
public interface IClientManagedUpdater
{
    string StatusText { get; }
    Task RunAsync(CancellationToken cancellationToken);
}
