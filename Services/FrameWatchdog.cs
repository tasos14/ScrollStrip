namespace ScrollStrip;

/// <summary>
/// Detects a contact that has gone silent. A precision touchpad keeps reporting ~every 11 ms while
/// a finger rests, so silence with a contact down means the lift report was lost — observed on the
/// development pad as a new contact ID appearing after a 0.36–1.8 s gap with no tip-up in between.
/// Without this the recognizer stays mid-gesture until the next touch, keeping the cursor pinned.
///
/// Pure logic: the caller supplies wall-clock seconds and polls <see cref="CheckStalled"/>.
/// </summary>
public sealed class FrameWatchdog
{
    /// <summary>
    /// Silence that counts as a lost lift. The same pad also pauses for up to ~235 ms with the
    /// finger genuinely still down (same contact ID resumes), so this must sit above that.
    /// </summary>
    public const double StallTimeoutSec = 0.25;

    private double _lastFrame;
    private bool _contactDown;

    /// <summary>True while the last frame had a contact down and it has not yet been declared lost.</summary>
    public bool ContactDown => _contactDown;

    public void OnFrame(double wallSeconds, bool anyContactDown)
    {
        _lastFrame   = wallSeconds;
        _contactDown = anyContactDown;
    }

    /// <summary>Returns true exactly once when a down contact has been silent past the timeout.</summary>
    public bool CheckStalled(double wallSeconds)
    {
        if (!_contactDown || wallSeconds - _lastFrame <= StallTimeoutSec) return false;
        _contactDown = false;
        return true;
    }

    public void Reset() => _contactDown = false;
}
