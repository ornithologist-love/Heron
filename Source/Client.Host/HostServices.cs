using Heron.ClientFramework.Abstractions;
using Heron.ClientFramework.Infrastructure;

namespace Heron.ClientHost;

/// <summary>Host endpoints for plugins. The launcher has no server, sign-in or product edition.</summary>
internal sealed class HostEnvironment : IClientEnvironment, IClientPageNavigation, IClientAppPresence
{
    public string ServerUrl => "";
    public string Username => Environment.UserName;
    public string HostVersion => typeof(HostEnvironment).Assembly.GetName().Version?.ToString() ?? "";

    public event Action<string>? PageNavigationRequested;
    public string? CurrentPageId { get; private set; }
    public event Action<string?>? CurrentPageChanged;
    public void NavigateToPage(string pageId) => PageNavigationRequested?.Invoke(pageId);

    internal void NotifyCurrentPage(string? pageId)
    {
        if (CurrentPageId == pageId) return;
        CurrentPageId = pageId;
        CurrentPageChanged?.Invoke(pageId);
    }

    // The launcher shows only the workbench; it has no AI chat surface.
    public AppPresence Presence => AppPresence.WorkBenchOnly;
    public bool IsAiChatOpen => false;
    public void Signal(AppSignal signal) { }
    public IDisposable RegisterAiChatFace(Func<IClientAppFace> factory) => new Nothing();

    private sealed class Nothing : IDisposable { public void Dispose() { } }
}
