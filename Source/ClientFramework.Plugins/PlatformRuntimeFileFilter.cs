namespace Heron.ClientFramework.Plugins;

/// <summary>Excludes <c>runtimes/&lt;rid&gt;/...</c> native payloads belonging to other target platforms.</summary>
public sealed class PlatformRuntimeFileFilter : IPluginFileFilter
{
    private readonly string _platform;

    private PlatformRuntimeFileFilter(string platform) => _platform = platform;

    public static PlatformRuntimeFileFilter ForPlatform(string? targetPlatform = null) =>
        new(PluginEdition.NormalizePlatform(targetPlatform ?? PluginEdition.CurrentPlatform));

    public string Fingerprint => $"platform-runtimes-{_platform.ToLowerInvariant()}-v1";

    public bool Include(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (!normalized.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)) return true;
        var rest = normalized.AsSpan("runtimes/".Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0) return true;
        var rid = rest[..slash].ToString();
        return _platform switch
        {
            "Win64" => rid.Equals("any", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("win", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("win-x64", StringComparison.OrdinalIgnoreCase)
                || (rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) && rid.EndsWith("-x64", StringComparison.OrdinalIgnoreCase)),
            "Mac" => rid.Equals("any", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("unix", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("osx", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("osx-u", StringComparison.OrdinalIgnoreCase)
                || rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase),
            "Linux" => rid.Equals("any", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("unix", StringComparison.OrdinalIgnoreCase)
                || rid.Equals("linux", StringComparison.OrdinalIgnoreCase)
                || rid.StartsWith("linux-", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }
}
