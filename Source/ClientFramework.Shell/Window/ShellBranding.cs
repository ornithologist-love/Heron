using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Shared desktop branding icons picked up by <see cref="ShellWindow" />, <see cref="ShellDiagnosticsWindow" />,
///     <see cref="ClientTrayHost" />, and <see cref="ShellUser" />. Defaults to the built-in Heron mark until the
///     host calls <see cref="Configure(Uri)" /> or <see cref="Configure(WindowIcon?, IImage?)" />.
/// </summary>
public static class ShellBranding
{
    public static readonly Uri DefaultIconUri = new("avares://Heron.ClientFramework.Shell/Assets/heron-64.png");

    private static readonly object Gate = new();
    private static bool _configured;
    private static WindowIcon? _windowIcon;
    private static IImage? _titleIcon;

    public static WindowIcon? WindowIcon
    {
        get
        {
            EnsureDefaultsLoaded();
            return _windowIcon;
        }
    }

    public static IImage? TitleIcon
    {
        get
        {
            EnsureDefaultsLoaded();
            return _titleIcon;
        }
    }

    public static void Configure(Uri iconAssetUri)
    {
        ArgumentNullException.ThrowIfNull(iconAssetUri);
        var (windowIcon, titleIcon) = LoadFromUri(iconAssetUri);
        Configure(windowIcon, titleIcon);
    }

    public static void Configure(WindowIcon? windowIcon, IImage? titleIcon)
    {
        lock (Gate)
        {
            _windowIcon = windowIcon;
            _titleIcon = titleIcon;
            _configured = true;
        }
    }

    public static void ResetToDefaults()
    {
        lock (Gate)
        {
            _configured = false;
            _windowIcon = null;
            _titleIcon = null;
        }
    }

    private static void EnsureDefaultsLoaded()
    {
        lock (Gate)
        {
            if (_configured) return;
            if (_windowIcon != null && _titleIcon != null) return;
            if (Application.Current == null) return;
            try
            {
                var (windowIcon, titleIcon) = LoadFromUri(DefaultIconUri);
                _windowIcon = windowIcon;
                _titleIcon = titleIcon;
                _configured = true;
            }
            catch
            {
                // Headless or pre-platform environments may not have an asset loader ready yet.
            }
        }
    }

    private static (WindowIcon WindowIcon, IImage TitleIcon) LoadFromUri(Uri uri)
    {
        using var source = AssetLoader.Open(uri);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        var windowIcon = new WindowIcon(buffer);
        buffer.Position = 0;
        var titleIcon = new Bitmap(buffer);
        return (windowIcon, titleIcon);
    }
}
