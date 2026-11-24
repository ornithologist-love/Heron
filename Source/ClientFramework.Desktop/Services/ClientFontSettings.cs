using Avalonia;
using Avalonia.Media;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>Configures shared CJK and UI font fallback chains for Avalonia hosts.</summary>
public static class ClientFontSettings
{
    private static readonly string[] DefaultFallbackFamilies =
    [
        "Noto Sans SC",
        "Noto Sans CJK SC",
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "Segoe UI",
    ];

    public static AppBuilder Configure(AppBuilder builder, string? bundledFamilyUri = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var families = string.IsNullOrWhiteSpace(bundledFamilyUri)
            ? DefaultFallbackFamilies
            : [bundledFamilyUri, .. DefaultFallbackFamilies];
        var familyList = string.Join(',', families);

        return builder.With(new FontManagerOptions
        {
            DefaultFamilyName = familyList,
            FontFallbacks = families.Select(name => new FontFallback { FontFamily = new FontFamily(name) }).ToArray(),
        }).AfterSetup(_ =>
        {
            // Semi controls and the Avalonia default must resolve through the same family chain.
            if (Application.Current is { } app)
                app.Resources["SemiFontFamilyRegular"] = new FontFamily(familyList);
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(familyList)), out var glyphs))
                Serilog.Log.Information("[Font] family={Family} resolved={Resolved} glyphs={GlyphCount}",
                    familyList, glyphs.FamilyName, glyphs.GlyphCount);
        });
    }
}
