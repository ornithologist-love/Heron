using Avalonia;
using Avalonia.Controls;

namespace Heron.ClientControls.Chrome;

/// <summary>
///     Menu with the three theme modes (system, light, dark) and a check mark on the current one.
///     Choosing an entry sets <see cref="Mode"/> and raises <see cref="ModeSelected"/>; applying and persisting the
///     theme belongs to the caller. Labels are host text and default to neutral names.
/// </summary>
public class ThemeModeFlyout : MenuFlyout
{
    public const string SystemMode = "system";
    public const string LightMode = "light";
    public const string DarkMode = "dark";

    public static readonly StyledProperty<string> ModeProperty =
        AvaloniaProperty.Register<ThemeModeFlyout, string>(nameof(Mode), SystemMode);

    public static readonly StyledProperty<string> SystemLabelProperty =
        AvaloniaProperty.Register<ThemeModeFlyout, string>(nameof(SystemLabel), "跟随系统");

    public static readonly StyledProperty<string> LightLabelProperty =
        AvaloniaProperty.Register<ThemeModeFlyout, string>(nameof(LightLabel), "浅色");

    public static readonly StyledProperty<string> DarkLabelProperty =
        AvaloniaProperty.Register<ThemeModeFlyout, string>(nameof(DarkLabel), "深色");

    public ThemeModeFlyout() => Opening += (_, _) => Rebuild();

    /// <summary>Current choice: <c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public string SystemLabel
    {
        get => GetValue(SystemLabelProperty);
        set => SetValue(SystemLabelProperty, value);
    }

    public string LightLabel
    {
        get => GetValue(LightLabelProperty);
        set => SetValue(LightLabelProperty, value);
    }

    public string DarkLabel
    {
        get => GetValue(DarkLabelProperty);
        set => SetValue(DarkLabelProperty, value);
    }

    /// <summary>Raised with the chosen mode after the user picks an entry.</summary>
    public event EventHandler<string>? ModeSelected;

    private void Rebuild()
    {
        Items.Clear();
        foreach (var (mode, label) in new[] { (SystemMode, SystemLabel), (LightMode, LightLabel), (DarkMode, DarkLabel) })
        {
            var item = new MenuItem
            {
                Header = label,
                Icon = new TextBlock { Text = "✓", IsVisible = Mode == mode },
            };
            item.Click += (_, _) =>
            {
                Mode = mode;
                ModeSelected?.Invoke(this, mode);
            };
            Items.Add(item);
        }
    }
}
