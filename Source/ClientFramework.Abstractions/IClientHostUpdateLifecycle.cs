namespace Heron.ClientFramework.Abstractions;

/// <summary>Minimal host-owned Velopack operations used by product update transactions.</summary>
public interface IClientHostUpdateLifecycle
{
    string PackagesDirectory { get; }
    string CurrentPackageFileName { get; }
    Task ApplyRollbackAsync(string packageFileName, Action restoreProductState, Action restoreJournal,
        CancellationToken cancellationToken);
}
