using System.Text.Json;

namespace Heron.Pregel.Tooling;

public sealed record ProcessExit(int ProcessId, int ExitCode);

/// <summary>Optional process observations. Exit codes are facts, not a success/failure policy.</summary>
public abstract class ProcessRunObserver : PregelRunObserver
{
    protected override void OnNotification(PregelRunEvent value)
    {
        if (value.Kind == "process.exited" && value.Data is { } data)
            OnProcessExited(value, data.Deserialize<ProcessExit>()!);
        else
            base.OnNotification(value);
    }

    protected virtual void OnProcessExited(PregelRunEvent value, ProcessExit process) { }
}
