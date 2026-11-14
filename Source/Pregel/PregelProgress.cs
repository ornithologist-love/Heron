using System.Text.Json;

namespace Heron.Pregel;

/// <summary>Progress of the current node attempt, not the whole graph. Null means indeterminate.
/// Reaching 1 does not finish the node; its execution result remains authoritative.</summary>
public sealed record PregelProgress(double? Fraction, string Detail)
{
    public const string EventKind = "node.progress";

    public static double? Normalize(double? fraction) =>
        fraction is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : null;

    public static bool TryRead(PregelRunEvent value, out PregelProgress progress)
    {
        progress = null!;
        if (value.Kind != EventKind || value.Data is not { } data) return false;
        try
        {
            if (data.Deserialize<PregelProgress>() is not { } parsed) return false;
            progress = parsed with { Fraction = Normalize(parsed.Fraction), Detail = parsed.Detail ?? "" };
            return true;
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>Maps a nested graph's node progress to its owning outer node. No implicit graph averaging.</summary>
public class PregelProgressForwarder(PregelNodeContext parent) : PregelRunObserver
{
    protected PregelNodeContext Parent { get; } = parent;
    protected override void OnNodeStarted(PregelRunEvent value) => Parent.ReportProgress(null);
    protected override void OnNodeProgress(PregelRunEvent value, PregelProgress progress) =>
        Parent.ReportProgress(progress.Fraction, progress.Detail);
}
