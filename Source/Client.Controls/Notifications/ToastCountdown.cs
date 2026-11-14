namespace Heron.ClientControls.Notifications;

/// <summary>
/// UI-independent countdown state used by toast progress animation.
/// </summary>
public sealed class ToastCountdown
{
    public ToastCountdown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        Duration = duration;
        Remaining = duration;
    }

    public TimeSpan Duration { get; }

    public TimeSpan Remaining { get; private set; }

    public bool IsPaused { get; set; }

    public bool IsExpired => Remaining <= TimeSpan.Zero;

    public double Progress => Math.Clamp(Remaining.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);

    public bool Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (IsPaused || IsExpired)
            return IsExpired;

        Remaining = elapsed >= Remaining ? TimeSpan.Zero : Remaining - elapsed;
        return IsExpired;
    }
}
