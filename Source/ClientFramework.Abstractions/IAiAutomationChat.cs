namespace Heron.ClientFramework.Abstractions;

/// <summary>Minimal host capability for transferring an automation conversation into an AI frontend.</summary>
public interface IAiAutomationChat
{
    bool IsActive { get; }
    IDisposable RegisterChat(Func<AiAutomationChatSession, string, CancellationToken, Task> open);
}

/// <summary>Stable session metadata passed from the host automation service to an AI plugin.</summary>
public sealed record AiAutomationChatSession(string Provider, string SessionId, string Workspace, string Model, string Title);
