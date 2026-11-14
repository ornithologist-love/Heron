using System.Diagnostics;

namespace Heron.ClientFramework.Infrastructure;

/// <summary>
///     Lightweight in-memory ring buffer + live event feed for a host's Debug tools page.
///     Persistence goes through the same global Serilog pipeline as everything else (one
///     run log per process). <see cref="Initialize"/> and <see cref="Write"/> are conditional
///     on the caller's DEBUG symbol, so a Release host compiles the calls away while the
///     package exposes one API for both configurations.
/// </summary>
public static class DebugLogService
{
    private const int MaxBufferedLines = 1000;
    private static readonly object Sync = new();
    private static readonly Queue<string> Lines = new();
    private static bool _initialized;

    public static event Action<string>? EntryAdded;

    [Conditional("DEBUG")]
    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
                return;
            _initialized = true;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Write("未处理异常", args.ExceptionObject?.ToString() ?? "未知异常");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("未观察任务异常", args.Exception.ToString());
            args.SetObserved();
        };

        Write("启动", $"{ClientHostProfile.DisplayName} Debug 会话开始 · PID {Environment.ProcessId}");
    }

    [Conditional("DEBUG")]
    public static void Write(string category, string message)
    {
        var normalized = (message ?? "").Replace("\r\n", " ⏎ ").Replace('\n', ' ');
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {normalized}";

        lock (Sync)
        {
            Lines.Enqueue(line);
            while (Lines.Count > MaxBufferedLines)
                Lines.Dequeue();
        }

        Serilog.Log.ForContext("SourceContext", category).Information("{Message}", normalized);
        EntryAdded?.Invoke(line);
    }

    public static string Snapshot()
    {
        lock (Sync)
        {
            return string.Join(Environment.NewLine, Lines);
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Lines.Clear();
        }

        Write("日志", "日志已清空（仅清空面板缓冲区，不影响运行日志文件）");
    }
}
