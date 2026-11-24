using Avalonia.Controls;
using Avalonia.Media;
using Heron.ClientFramework.Abstractions;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Tracks non-modal <see cref="ShellDiagnosticsWindow" /> instances by key so repeated clicks
///     bring forward the existing window and shutdown cleanly returns every panel to its plugin.
/// </summary>
public sealed class ShellDiagnosticsManager
{
    private readonly Dictionary<string, ShellDiagnosticsWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<bool> _isExiting;

    public ShellDiagnosticsManager(Func<bool>? isExiting = null) =>
        _isExiting = isExiting ?? (static () => false);

    public IReadOnlyCollection<string> OpenKeys => _windows.Keys;

    public ShellDiagnosticsWindow OpenOrShow(
        string key,
        IClientDiagnosticsWindow source,
        Window owner,
        IImage? titleIcon = null,
        bool show = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(owner);

        if (_windows.TryGetValue(key, out var existing))
        {
            if (ReferenceEquals(existing.Source, source))
            {
                if (show) existing.Reshow(owner);
                return existing;
            }
            _windows.Remove(key);
            existing.CloseForExit();
            _ = ReleaseAsync(existing);
        }

        var window = new ShellDiagnosticsWindow(source, owner, titleIcon, _isExiting);
        _windows[key] = window;
        window.Closed += (_, _) =>
        {
            if (_windows.TryGetValue(key, out var current) && ReferenceEquals(current, window))
                _windows.Remove(key);
            if (_isExiting()) _ = ReleaseAsync(window);
        };
        if (show)
        {
            window.Show(owner);
            window.Activate();
        }
        return window;
    }

    public void Hide(string key)
    {
        if (_windows.TryGetValue(key, out var window)) window.HideForBackground();
    }

    public void HideAll()
    {
        foreach (var window in _windows.Values.ToArray()) window.HideForBackground();
    }

    public async Task CloseAndReleaseAllAsync()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            window.CloseForExit();
            await ReleaseAsync(window);
        }
        _windows.Clear();
    }

    private static async Task ReleaseAsync(ShellDiagnosticsWindow window)
    {
        try { await window.ReleaseAsync(); }
        catch (Exception error)
        {
            Serilog.Log.ForContext("SourceContext", "Window").Warning(error, "Diagnostics window release failed");
        }
    }
}
