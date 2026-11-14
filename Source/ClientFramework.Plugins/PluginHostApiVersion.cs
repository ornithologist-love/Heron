using System.Globalization;
using System.Text.RegularExpressions;

namespace Heron.ClientFramework.Plugins;

/// <summary>A plugin requires the same host API major and at least its declared minor.</summary>
public readonly record struct PluginHostApiVersion
{
    public int Major { get; }
    public int Minor { get; }

    private PluginHostApiVersion(int major, int minor) => (Major, Minor) = (major, minor);

    public static PluginHostApiVersion Parse(string value)
    {
        var match = Regex.Match(value ?? "", @"\A(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?\z", RegexOptions.CultureInvariant);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : "0", NumberStyles.None,
                CultureInfo.InvariantCulture, out var minor))
            throw new InvalidDataException($"Invalid host_api '{value}'; expected major.minor (a legacy integer means major.0).");
        return new(major, minor);
    }

    public bool Supports(PluginHostApiVersion required) => Major == required.Major && Minor >= required.Minor;

    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}");
}
