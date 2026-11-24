using Serilog;
using Serilog.Events;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using System.Text.Json;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     One process-wide Client logging pipeline, with bounded file segments per session. Every
///     subsystem (host, plugins, business services) writes through the standard
///     <see cref="Microsoft.Extensions.Logging.ILogger{T}"/>; Serilog's SourceContext (the
///     type name behind ILogger&lt;T&gt;) identifies the logging category, so callers never
///     touch a file path, a lock, or a rotation policy — they
///     just inject ILogger&lt;T&gt; and log.
/// </summary>
public static class ClientLogging
{
    private const int RetainedRunCount = 20;
    private const long SegmentBytes = 32 * 1024 * 1024;

    private const string SnapshotDirectoryName = "feedback";
    private const int SnapshotRetentionDays = 3;
    public static string SessionId { get; } = Guid.NewGuid().ToString("N");
    public static ClientLogPolicy Levels { get; } = CreateDefaultPolicy();
    private static string? _currentLogFile;
    private static string ExpectationFile => Path.Combine(ClientHostProfile.LogsDirectory, "update-expected-version.txt");

    /// <summary>Path of the log file for the currently running process, once <see cref="Initialize"/> has run.</summary>
    public static string? CurrentLogFile => ResolveCurrentLogFile(_currentLogFile);

    internal static string? ResolveCurrentLogFile(string? sessionLogFile)
    {
        if (string.IsNullOrWhiteSpace(sessionLogFile)) return null;
        try
        {
            var directory = Path.GetDirectoryName(sessionLogFile)!;
            var stem = Path.GetFileNameWithoutExtension(sessionLogFile);
            var extension = Path.GetExtension(sessionLogFile);
            // Serilog keeps the original name for segment zero, then adds _001, _002, etc.
            // Sort numerically: timestamps can tie and segment _1000 follows _999.
            return Directory.EnumerateFiles(directory, stem + "*" + extension)
                .Select(path => (Path: path, Segment: SegmentNumber(path)))
                .Where(item => item.Segment >= 0)
                .OrderByDescending(item => item.Segment)
                .Select(item => item.Path)
                .FirstOrDefault();

            int SegmentNumber(string path)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Equals(stem, StringComparison.OrdinalIgnoreCase)) return 0;
                var suffix = name[stem.Length..];
                return suffix.StartsWith('_') && suffix.Length > 1
                       && suffix[1..].All(char.IsAsciiDigit)
                       && int.TryParse(suffix[1..], out var segment) ? segment : -1;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    ///     Call once, as early as possible in Main — before any other code that might log.
    ///     Creates this run's log file (name stamped with the current start time) and prunes
    ///     old runs beyond <see cref="RetainedRunCount"/>.
    /// </summary>
    public static void Initialize()
    {
        Directory.CreateDirectory(ClientHostProfile.LogsDirectory);
        PruneOldRuns();

        var logFile = Path.Combine(ClientHostProfile.LogsDirectory,
            $"{ClientHostProfile.LogFilePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");
        _currentLogFile = logFile;

        var invalidSettings = ApplyEnvironment();
        Log.Logger = CreateLogger(logFile, SessionId, Levels);
        if (invalidSettings) Log.Warning("Invalid HERON_LOG_LEVEL or HERON_LOG_OVERRIDES ignored; use standard LogLevel names");

        Log.Information("{Product} starting. PID={Pid} Log={LogFile}", ClientHostProfile.LogProductName, Environment.ProcessId, logFile);
        Log.Information("Runtime version={Version} os={OS} framework={Framework} arch={Architecture} executable={Executable}",
            System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
            Environment.OSVersion, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, Environment.ProcessPath);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    internal static Serilog.Core.Logger CreateLogger(string logFile, string sessionId, ClientLogPolicy policy) =>
        new LoggerConfiguration()
            // Filtering happens once here for injected ILogger<T> and legacy Serilog alike.
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("SessionId", sessionId)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .Enrich.With(new ThreadEnricher())
            .Filter.With(policy)
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: SegmentBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedRunCount,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] [session={SessionId} pid={ProcessId} tid={ThreadId}] {Message:lj} {Properties:j}{NewLine}{Exception}",
                // Serilog's File sink defaults to Encoding.UTF8, whose .NET static instance
                // writes a BOM. Pass an explicit no-BOM UTF8Encoding so every tool that reads
                // this file (grep, diff, log viewers) sees plain UTF-8 from byte zero.
                encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                buffered: false)
            .CreateLogger();

    private static ClientLogPolicy CreateDefaultPolicy()
    {
        var policy = new ClientLogPolicy();
        policy.SetOverride("Microsoft", LogLevel.Warning);
        return policy;
    }

    private static bool ApplyEnvironment()
    {
        var invalid = false;
        var level = Environment.GetEnvironmentVariable("HERON_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(level))
        {
            if (Enum.TryParse<LogLevel>(level, true, out var parsed) && Enum.IsDefined(parsed)) Levels.MinimumLevel = parsed;
            else invalid = true;
        }
        var overrides = Environment.GetEnvironmentVariable("HERON_LOG_OVERRIDES");
        if (string.IsNullOrWhiteSpace(overrides)) return invalid;
        try
        {
            var rules = JsonSerializer.Deserialize<Dictionary<string, string>>(overrides);
            if (rules == null) return true;
            foreach (var (category, value) in rules)
                if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<LogLevel>(value, true, out var parsed) && Enum.IsDefined(parsed))
                    Levels.SetOverride(category, parsed);
                else invalid = true;
        }
        catch (JsonException) { invalid = true; }
        return invalid;
    }

    private sealed class ThreadEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }

    /// <summary>
    ///     Copies the newest segment of the current session to a stable feedback snapshot.
    ///
    ///     Uploading the live log can fail because the logging pipeline holds it open, and its
    ///     size can grow during a long upload, making Content-Length inaccurate. Reading through
    ///     a shared handle and uploading the static copy avoids both problems.
    ///
    ///     Returns the snapshot path, or null if it could not be created. The caller can then
    ///     fall back to attaching the live log.
    /// </summary>
    public static string? TrySnapshotCurrentLog() =>
        TrySnapshotLog(_currentLogFile, Path.Combine(ClientHostProfile.LogsDirectory, SnapshotDirectoryName));

    internal static string? TrySnapshotLog(string? sessionLogFile, string directory)
    {
        var source = ResolveCurrentLogFile(sessionLogFile);
        if (string.IsNullOrWhiteSpace(source)) return null;

        try
        {
            Directory.CreateDirectory(directory);
            PruneSnapshots(directory);

            var destination = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(source)}_{Guid.NewGuid():N}.log");
            using var input = SharedFileAccess.OpenRead(source);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
            return destination;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void PruneSnapshots(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-SnapshotRetentionDays);
            var stale = Directory.EnumerateFiles(directory, ClientHostProfile.LogFilePrefix + "_*.log")
                .Where(file => File.GetLastWriteTimeUtc(file) < cutoff);
            foreach (var file in stale)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Call once, as the very last step before the process actually exits.</summary>
    public static void Shutdown()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Log.Information("{Product} exiting.", ClientHostProfile.LogProductName);
        Log.CloseAndFlush();
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        Log.Fatal("Unhandled exception terminating={Terminating}", args.IsTerminating);
        if (args.ExceptionObject is Exception error) ClientDiagnostics.Failure("Runtime", "unhandled", error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        ClientDiagnostics.Failure("Runtime", "unobserved task", args.Exception);

    private static void PruneOldRuns()
    {
        try
        {
            var runs = Directory.EnumerateFiles(ClientHostProfile.LogsDirectory, ClientHostProfile.LogFilePrefix + "_*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(RetainedRunCount);
            foreach (var stale in runs)
                try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    ///     Call right before asking Velopack to apply an update and restart. Survives the
    ///     restart because it lives in the user data dir, not the install dir Velopack
    ///     replaces. The next startup calls <see cref="CheckAndClearUpdateExpectation"/> to
    ///     find out whether the version that actually came back up matches what was promised.
    ///     This is a plain marker value, not a log line, so it is stored outside the Serilog
    ///     pipeline where it can be read back precisely on the next run.
    /// </summary>
    public static void MarkUpdateExpected(string targetVersion)
    {
        try
        {
            Directory.CreateDirectory(ClientHostProfile.LogsDirectory);
            File.WriteAllText(ExpectationFile, targetVersion);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    ///     Call once at startup, before any other update check. Returns the version that a
    ///     previous run expected to be running after an update-triggered restart, or null if
    ///     no update was pending (normal startup). Always clears the marker so a stale value
    ///     never leaks into an unrelated later restart.
    /// </summary>
    public static string? CheckAndClearUpdateExpectation()
    {
        try
        {
            if (!File.Exists(ExpectationFile)) return null;
            var expected = File.ReadAllText(ExpectationFile).Trim();
            File.Delete(ExpectationFile);
            return expected.Length == 0 ? null : expected;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
