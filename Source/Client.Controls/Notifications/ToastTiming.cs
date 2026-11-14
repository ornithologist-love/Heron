namespace Heron.ClientControls.Notifications;

public static class ToastTiming
{
    private static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Keeps short notifications brisk while giving longer wrapped text enough reading time.
    /// </summary>
    public static TimeSpan RecommendedDuration(string? title, string? message)
    {
        var characterCount = (title?.Length ?? 0) + (message?.Length ?? 0);
        var extraSeconds = Math.Max(0, characterCount - 40) / 18d;
        return TimeSpan.FromSeconds(Math.Clamp(
            MinimumDuration.TotalSeconds + extraSeconds,
            MinimumDuration.TotalSeconds,
            MaximumDuration.TotalSeconds));
    }
}
