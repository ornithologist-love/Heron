using Avalonia.Controls;
using Heron.ClientControls.Chrome;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     The shell's theme entry for the user menu. The shell adds one at the top of the menu; a host that wants it
///     elsewhere declares it among its own entries at the position it wants.
/// </summary>
public sealed class ShellThemeMenuItem : MenuCardItem
{
    private ShellState? _state;
    private readonly ThemeModeFlyout _menu = new();

    public ShellThemeMenuItem()
    {
        Flyout = _menu;
        _menu.ModeSelected += (_, mode) => _state?.SelectTheme(mode);
    }

    internal void Attach(ShellState state)
    {
        if (_state != null) _state.PropertyChanged -= StateChanged;
        _state = state;
        _menu.SystemLabel = state.Text.ThemeSystem;
        _menu.LightLabel = state.Text.ThemeLight;
        _menu.DarkLabel = state.Text.ThemeDark;
        state.PropertyChanged += StateChanged;
        Update();
    }

    private void StateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        if (_state == null) return;
        _menu.Mode = _state.ThemeMode;
        Icon = _state.ThemeIcon;
        Text = _state.ThemeText;
        ToolTip.SetTip(this, _state.ThemeText);
    }
}
