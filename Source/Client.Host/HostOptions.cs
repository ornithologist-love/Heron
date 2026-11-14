namespace Heron.ClientHost;

/// <summary>
///     Command line: <c>--plugins &lt;dir&gt;</c> (repeatable), <c>--edition &lt;json&gt;</c>, <c>--smoke</c> (start, report, exit)
///     and <c>--capture &lt;dir&gt; [--width &lt;px&gt;]</c> (render the window headless in light and dark).
/// </summary>
internal sealed record HostOptions(string[] PluginDirectories, string? Edition, bool Smoke, string? Capture = null, int Width = 1440)
{
    public bool Assembles => PluginDirectories.Length > 0 || Edition != null;

    public static HostOptions Parse(string[] args)
    {
        var plugins = new List<string>();
        string? edition = null;
        var smoke = false;
        string? capture = null;
        var width = 1440;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plugins": plugins.Add(Path.GetFullPath(Value(args, ref i))); break;
                case "--edition": edition = Path.GetFullPath(Value(args, ref i)); break;
                case "--smoke": smoke = true; break;
                case "--capture": capture = Path.GetFullPath(Value(args, ref i)); break;
                case "--width": width = int.Parse(Value(args, ref i)); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'. Use --plugins <dir>, --edition <json>, --smoke or --capture <dir>.");
            }
        }
        return new HostOptions(plugins.ToArray(), edition, smoke, capture, width);
    }

    private static string Value(string[] args, ref int index) =>
        ++index < args.Length ? args[index] : throw new ArgumentException($"{args[index - 1]} needs a value.");
}
