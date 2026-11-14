namespace Heron.ClientHost;

/// <summary>
///     Command line: <c>--plugins &lt;dir&gt;</c> (repeatable), <c>--edition &lt;json&gt;</c>, <c>--smoke</c> (start, report, exit)
///     and <c>--capture &lt;dir&gt; [--width &lt;px&gt;]</c> (render the window headless in light and dark).
/// </summary>
internal sealed record HostOptions(string[] PluginDirectories, string? Edition, bool Smoke,
    string? Capture = null, int Width = 1440, string? DataDirectory = null, string[]? Scripts = null)
{
    public bool Assembles => PluginDirectories.Length > 0 || Edition != null;

    public static HostOptions Parse(string[] args)
    {
        var plugins = new List<string>();
        var scripts = new List<string>();
        string? edition = null;
        string? dataDirectory = null;
        var smoke = false;
        string? capture = null;
        var width = 1440;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--script": scripts.Add(Path.GetFullPath(Value(args, ref i))); break;
                case "--data": dataDirectory = Path.GetFullPath(Value(args, ref i)); break;
                case "--plugins": plugins.Add(Path.GetFullPath(Value(args, ref i))); break;
                case "--edition": edition = Path.GetFullPath(Value(args, ref i)); break;
                case "--smoke": smoke = true; break;
                case "--capture": capture = Path.GetFullPath(Value(args, ref i)); break;
                case "--width": width = int.Parse(Value(args, ref i)); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'. Use --plugins <dir>, --edition <json>, --smoke, --capture <dir>, --data <dir> or --script <csx>.");
            }
        }
        return new HostOptions(plugins.ToArray(), edition, smoke, capture, width, dataDirectory, scripts.ToArray());
    }

    private static string Value(string[] args, ref int index) =>
        ++index < args.Length ? args[index] : throw new ArgumentException($"{args[index - 1]} needs a value.");
}
