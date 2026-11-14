using System;
using Heron.ClientFramework.Abstractions;
using Heron.ClientFramework.Infrastructure;
using Heron.ClientFramework.Runtime;

namespace Heron.ClientFramework.Shell;

/// <summary>Plugin-scoped UI ports. Hiding a plugin never changes its background service lifetime.</summary>
public sealed class PluginPresentation(string owner, ClientPluginRegistry registry,
    IClientAppPresence presence, IClientPageNavigation navigation) : IClientAppPresence, IClientPageNavigation
{
    private Action<string?>? _pageChanged;
    private int _faces;
    private bool _observing;
    private bool _visible = registry.IsOwnerVisible(owner);

    public AppPresence Presence => presence.Presence;
    public bool IsAiChatOpen => registry.IsOwnerVisible(owner) && presence.IsAiChatOpen;
    public string? CurrentPageId => registry.IsOwnerVisible(owner) ? navigation.CurrentPageId : null;

    public event Action<string?>? CurrentPageChanged
    {
        add { _pageChanged += value; Observe(); }
        remove { _pageChanged -= value; UnobserveIfUnused(); }
    }

    public void NavigateToPage(string pageId)
    {
        if (registry.IsOwnerVisible(owner)) navigation.NavigateToPage(pageId);
    }

    public void Signal(AppSignal signal)
    {
        if (registry.IsOwnerVisible(owner) || (signal == AppSignal.CloseAiChat && _faces > 0))
            presence.Signal(signal);
    }

    public IDisposable RegisterAiChatFace(Func<IClientAppFace> factory)
    {
        var registration = presence.RegisterAiChatFace(factory);
        _faces++;
        Observe();
        if (!_visible) presence.Signal(AppSignal.CloseAiChat);
        return new Registration(() =>
        {
            registration.Dispose();
            _faces--;
            UnobserveIfUnused();
        });
    }

    private void Observe()
    {
        if (_observing) return;
        _visible = registry.IsOwnerVisible(owner);
        registry.Changed += VisibilityChanged;
        navigation.CurrentPageChanged += PageChanged;
        _observing = true;
    }

    private void UnobserveIfUnused()
    {
        if (!_observing || _faces != 0 || _pageChanged != null) return;
        registry.Changed -= VisibilityChanged;
        navigation.CurrentPageChanged -= PageChanged;
        _observing = false;
    }

    private void PageChanged(string? pageId)
    {
        if (registry.IsOwnerVisible(owner)) _pageChanged?.Invoke(pageId);
    }

    private void VisibilityChanged()
    {
        var visible = registry.IsOwnerVisible(owner);
        if (_visible == visible) return;
        _visible = visible;
        if (!visible && _faces > 0) presence.Signal(AppSignal.CloseAiChat);
        _pageChanged?.Invoke(CurrentPageId);
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
