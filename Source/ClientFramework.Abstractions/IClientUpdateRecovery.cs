namespace Heron.ClientFramework.Abstractions;

/// <summary>AI reports readiness to the host-owned, cross-restart update transaction.</summary>
public interface IClientUpdateRecovery
{
    void AiStartupCompleted();
    /// <returns>True when host rollback is scheduled and plugin startup fallback must stop.</returns>
    bool AiStartupFailed(string error);
}
