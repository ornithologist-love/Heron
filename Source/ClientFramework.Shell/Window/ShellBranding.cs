using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Heron.ClientFramework.Shell;

/// <summary>
///     Shared desktop branding icons configured once by the host at startup and picked up
///     automatically by <see cref="ShellWindow" />, <see cref="ShellDiagnosticsWindow" /> and <see cref="ClientTrayHost" />.
/// </summary>
public static class ShellBranding
{
    public static WindowIcon? WindowIcon { get; private set; }
    public static IImage? TitleIcon { get; private set; }

    public static void Configure(Uri iconAssetUri)
    {
        ArgumentNullException.ThrowIfNull(iconAssetUri);
        using var source = AssetLoader.Open(iconAssetUri);
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        buffer.Position = 0;
        var windowIcon = new WindowIcon(buffer);
        buffer.Position = 0;
        var titleIcon = new Bitmap(buffer);
        Configure(windowIcon, titleIcon);
    }

    public static void Configure(WindowIcon? windowIcon, IImage? titleIcon)
    {
        WindowIcon = windowIcon;
        TitleIcon = titleIcon;
    }
}
