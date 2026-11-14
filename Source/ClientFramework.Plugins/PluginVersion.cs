using System.Globalization;
using System.Text.RegularExpressions;

namespace Heron.ClientFramework.Plugins;

/// <summary>First-party releases use stable major.minor.patch versions; prereleases require a future schema.</summary>
public readonly record struct PluginVersion(int Major, int Minor, int Patch) : IComparable<PluginVersion>
{
    public static PluginVersion Parse(string value)
    {
        if (value is null || !Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$"))
            throw new InvalidDataException($"Invalid plugin version '{value}'; expected major.minor.patch.");
        var parts = value.Split('.');
        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var patch))
            throw new InvalidDataException($"Plugin version is out of range: {value}.");
        return new(major, minor, patch);
    }

    public int CompareTo(PluginVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>Exact versions or space-separated comparisons (intersection), for example >=1.0.0 &lt;2.0.0.</summary>
public sealed class PluginVersionRange
{
    private readonly (string Operator, PluginVersion Version)[] _terms;
    private PluginVersionRange((string, PluginVersion)[] terms) => _terms = terms;
    public static PluginVersionRange Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Empty plugin version range.");
        return new(value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(term =>
        {
            var match = Regex.Match(term, @"^(>=|<=|>|<|=)?(.+)$");
            return (match.Groups[1].Value, PluginVersion.Parse(match.Groups[2].Value));
        }).ToArray());
    }
    public bool Contains(PluginVersion version) => _terms.All(term =>
    {
        var comparison = version.CompareTo(term.Version);
        return term.Operator switch { ">=" => comparison >= 0, "<=" => comparison <= 0,
            ">" => comparison > 0, "<" => comparison < 0, _ => comparison == 0 };
    });
}
