using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heron.ClientFramework.Plugins;

namespace Heron.ClientFramework.Runtime;

/// <summary>
///     E2E smoke-run contract for <c>--smoke-result &lt;path&gt;</c>: writes a machine-readable JSON receipt
///     atomically via temporary file + overwrite rename so harnesses never read a half-written result.
/// </summary>
public static class ClientSmokeRun
{
    private const string Argument = "--smoke-result";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The result path when this process was started with <c>--smoke-result</c>; otherwise null.</summary>
    public static string? ResultPath { get; } = ReadResultPath(Environment.GetCommandLineArgs());

    public static string? ReadResultPath(string[] arguments)
    {
        var index = Array.IndexOf(arguments, Argument);
        return index >= 0 && index + 1 < arguments.Length ? Path.GetFullPath(arguments[index + 1]) : null;
    }

    public static void WriteReady(ClientPluginHostCore host, ClientPluginRegistry registry, string? resultPath = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(registry);
        WriteReady(host.Edition?.Name ?? "", host.Error, host.RecoveryMessage, host.Status,
            registry.Pages.Select(page => page.Id), resultPath);
    }

    public static void WriteReady(string edition, string? hostError, string? recoveryMessage,
        IEnumerable<PluginStatus> plugins, IEnumerable<string> pages, string? resultPath = null) =>
        Write(new SmokeResult(
            Ready: true,
            Edition: edition,
            HostError: hostError,
            RecoveryMessage: recoveryMessage,
            Plugins: plugins.Select(status => new SmokePlugin(status.Id, status.Version, status.State, status.Error)).ToArray(),
            Pages: pages.ToArray(),
            StartupError: null,
            ReadyAfterMs: ElapsedSinceProcessStart()), resultPath);

    public static void WriteStartupFailure(Exception error, string? resultPath = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        Write(new SmokeResult(
            Ready: false,
            Edition: "",
            HostError: null,
            RecoveryMessage: null,
            Plugins: [],
            Pages: [],
            StartupError: error.ToString(),
            ReadyAfterMs: ElapsedSinceProcessStart()), resultPath);
    }

    private static long ElapsedSinceProcessStart() =>
        (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    private static void Write(SmokeResult result, string? resultPath)
    {
        var path = resultPath ?? ResultPath ?? throw new InvalidOperationException("Not a smoke run.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record SmokeResult(bool Ready, string Edition, string? HostError, string? RecoveryMessage,
        SmokePlugin[] Plugins, string[] Pages, string? StartupError, long ReadyAfterMs);

    private sealed record SmokePlugin(string Id, string Version, PluginState State, string? Error);
}
