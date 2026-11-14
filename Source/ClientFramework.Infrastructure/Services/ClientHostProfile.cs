namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     Product identity and storage the host supplies before any infrastructure service runs:
///     log and cache directories, the log file prefix, the name shown in windows and logs, and
///     where a theme choice is persisted. The framework owns no product path or configuration file.
/// </summary>
public static class ClientHostProfile
{
    private static string? _logsDirectory;
    private static string? _cacheDirectory;

    /// <summary>Name shown in window titles and session messages.</summary>
    public static string DisplayName { get; private set; } = "Heron";

    /// <summary>Prefix of per-run log files; a run is <c>{prefix}_{timestamp}_{pid}.log</c>.</summary>
    public static string LogFilePrefix { get; private set; } = "HeronClient";

    /// <summary>Name written to the first and last line of each run log.</summary>
    public static string LogProductName { get; private set; } = "Heron Client";

    public static string LogsDirectory => _logsDirectory ?? throw NotConfigured();
    public static string CacheDirectory => _cacheDirectory ?? throw NotConfigured();

    /// <summary>Persists the user's theme mode ("system", "light" or "dark"); null keeps it in memory only.</summary>
    public static Action<string>? PersistThemeMode { get; private set; }

    public static void Configure(string displayName, string logFilePrefix, string logProductName,
        string logsDirectory, string cacheDirectory, Action<string>? persistThemeMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(logProductName);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        DisplayName = displayName;
        LogFilePrefix = logFilePrefix;
        LogProductName = logProductName;
        _logsDirectory = logsDirectory;
        _cacheDirectory = cacheDirectory;
        PersistThemeMode = persistThemeMode;
    }

    private static InvalidOperationException NotConfigured() =>
        new("ClientHostProfile.Configure must run before client logging or WebView storage is used.");
}
