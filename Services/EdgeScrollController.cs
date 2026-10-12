using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

public enum EdgeZone { None, Right, Left, Bottom }

/// <summary>What the recognizer did with one frame — for --record and for tests.</summary>
/// <param name="Zone">Geometric hit test of the single contact this frame, regardless of state.</param>
/// <param name="TrackedZone">The zone the active scroll belongs to, or None when idle.</param>
/// <param name="Velocity">Last measured speed along the scroll axis, pad units per second.</param>
/// <param name="InertiaLaunch">Launch velocity in wheel units per 1/60 s on the frame inertia started, else 0.</param>
public readonly record struct EdgeScrollDiagnostics(
    int FingerCount, EdgeZone Zone, EdgeZone TrackedZone, string State, double Velocity, int EmittedDelta,
    double InertiaLaunch);

public sealed class EdgeScrollController : IDisposable
{
    private enum State { Idle, EdgeContact, Scrolling }

    /// <summary>Fired once per frame after the frame has been processed.</summary>
    public event Action<TouchFrame, EdgeScrollDiagnostics>? FrameProcessed;

    private readonly AppSettings _settings;
    private State _state = State.Idle;
    private int _trackedContactId = -1;
    private EdgeZone _zone = EdgeZone.None;  // zone of the active scroll
    private bool _isVertical;                // true = left/right edge, false = bottom edge
    private double _accumulator;             // fractional scroll units carried between frames
    private double _edgeContactDisplacement; // cumulative movement in EdgeContact; tap guard
    private const double ScrollActivationThreshold = 0.02; // 2% of touchpad — tap can't reach this
    private bool _cursorLocked;

    // Began-in-zone rule: eligibility is decided on a contact's first frame and held until all
    // fingers lift, so a finger that lands mid-pad can't scroll by sliding into a zone.
    private int _gestureContactId = -1;
    private bool _gestureBeganInZone;

    // Reference sample for velocity. It only advances once a frame is at least
    // MinVelocityWindowSec after it and has moved past the dead zone, so batched sub-ms frames
    // can't produce a near-zero dt and slow drifts still accumulate instead of being discarded.
    private double _refNormX, _refNormY, _refTime;
    private double _lastVelocity;            // pad units/s along the scroll axis
    // Launch estimate: a ~2-frame EWMA of the measured velocity, and when it was last fed.
    private double _launchVelocity;
    private double _launchVelocityTime;
    private bool _haveLaunchVelocity;
    private const double LaunchEwmaAlpha = 2.0 / 3.0;
    private const double MinVelocityWindowSec = 0.008;
    private const double StaleReferenceSec    = 0.05;   // a finger at rest this long resamples the reference
    private const double DeadZone             = 0.0005;
    private const double ExitHysteresisMm     = 2.0;

    // Frame-scoped: set at the top of OnFrame, read by everything downstream.
    private double _frameTime;    // monotonic seconds from the frame clock
    private int _frameEmitted;    // wheel units emitted by the gesture path this frame
    private double _frameLaunch;  // inertia launch velocity if it started this frame

    /// <summary>Touch surface size in mm from the last frame, or 0 when the pad does not report it.</summary>
    public double PadWidthMm { get; private set; }
    public double PadHeightMm { get; private set; }

    private readonly IScrollOutput _output;
    private readonly IInertia _inertia;
    private double _inertiaStartedAt = double.NegativeInfinity; // frame time captured in BeginInertia
    private const double PhantomGraceMs = 50;

    /// <summary>
    /// Speed, in pad lengths/s, above which a release gets the flick boost. This is Scroll.app's
    /// 30 px/event at its default gain of 8 (30 / 8). On the development pad it is the top ~15%
    /// of drag speeds (p50 1.8, p90 4.7).
    /// </summary>
    private const double FlickThreshold = 3.75;
    /// <summary>Wheel units per 1/60 s at which the coast stops; Scroll.app stops at 1.1 px.</summary>
    private const double InertiaTerminus = 2.0;

    // Frame time elapsed since inertia last started. HID callback thread only.
    private double InertiaGraceElapsedMs() => (_frameTime - _inertiaStartedAt) * 1000.0;

    /// <param name="output">Wheel and cursor output; real Win32 input when null.</param>
    /// <param name="inertia">The coast engine; a timer-driven <see cref="InertiaScroller"/> on <paramref name="output"/> when null.</param>
    public EdgeScrollController(AppSettings settings, IScrollOutput? output = null, IInertia? inertia = null)
    {
        _settings = settings;
        _output   = output ?? new Win32ScrollOutput();
        _inertia  = inertia ?? new InertiaScroller(_output.Scroll);
    }

    public void OnFrame(TouchFrame frame)
    {
        _frameTime    = frame.TimestampSec;
        _frameEmitted = 0;
        _frameLaunch  = 0;
        if (frame.PadWidthMm > 0) { PadWidthMm = frame.PadWidthMm; PadHeightMm = frame.PadHeightMm; }

        // Non-confident contacts are the firmware's palm call — drop them before counting fingers.
        var activeContacts = frame.ConfidentDown.ToList();

        ProcessFrame(activeContacts);

        FrameProcessed?.Invoke(frame, new EdgeScrollDiagnostics(
            activeContacts.Count,
            activeContacts.Count == 1 ? HitTest(activeContacts[0]) : EdgeZone.None,
            _zone,
            _state.ToString(),
            _lastVelocity,
            _frameEmitted,
            _frameLaunch));
    }

    private void ProcessFrame(List<TouchContact> activeContacts)
    {
        if (!_settings.EdgeScrollEnabled)
        {
            if (_state != State.Idle) ExitScroll(allowInertia: false);
            StopInertia();
            return;
        }

        if (activeContacts.Count > 1)
        {
            if (_state != State.Idle) ExitScroll(allowInertia: true);
            // Touchpads emit phantom multi-touch frames within ~20 ms of a finger lifting.
            // Skip StopInertia for 50 ms so these artifacts don't cancel freshly-started inertia.
            if (InertiaGraceElapsedMs() >= PhantomGraceMs) StopInertia();
            return;
        }

        if (activeContacts.Count == 0)
        {
            if (_state != State.Idle) ExitScroll(allowInertia: true);
            _gestureContactId = -1;
            return;
        }

        var contact = activeContacts[0];
        var zone    = HitTest(contact);

        if (contact.ContactId != _gestureContactId)
        {
            _gestureContactId   = contact.ContactId;
            _gestureBeganInZone = zone != EdgeZone.None;
        }

        switch (_state)
        {
            case State.Idle:
                TryEnterEdge(contact, zone);
                break;

            case State.EdgeContact:
            case State.Scrolling:
                if (contact.ContactId != _trackedContactId)
                {
                    ExitScroll(allowInertia: true);
                    TryEnterEdge(contact, zone);
                    return;
                }
                // Scroll.app ends the scroll on leaving the zone, and with no inertia.
                if (_settings.StopWhenLeavingZone && !InZone(contact, _zone, ExitHysteresisMm))
                {
                    ExitScroll(allowInertia: false);
                    return;
                }
                ContinueScroll(contact);
                break;
        }
    }

    // ── Zone geometry ─────────────────────────────────────────────────────

    /// <summary>
    /// Zone thickness as a fraction of the pad along the zone's axis. Physical when the pad
    /// reports its size, otherwise the per-edge percentage. Without physical units the hysteresis
    /// is approximated as 1% of the pad per mm.
    /// </summary>
    private double EdgeThicknessNorm(EdgeZone zone, double extraMm)
    {
        double padMm = zone == EdgeZone.Bottom ? PadHeightMm : PadWidthMm;
        if (padMm > 0)
            return Math.Clamp((_settings.EdgeZoneWidthMm + extraMm) / padMm, 0.005, 0.5);

        double pct = zone switch
        {
            EdgeZone.Right => _settings.RightEdgeWidthPct,
            EdgeZone.Left  => _settings.LeftEdgeWidthPct,
            _              => _settings.BottomEdgeHeightPct,
        };
        return Math.Clamp((pct + extraMm) / 100.0, 0.005, 0.5);
    }

    private bool InZone(TouchContact c, EdgeZone zone, double extraMm = 0)
    {
        double t = EdgeThicknessNorm(zone, extraMm);
        return zone switch
        {
            EdgeZone.Right  => c.NormX >= 1.0 - t,
            EdgeZone.Left   => c.NormX <= t,
            EdgeZone.Bottom => c.NormY >= 1.0 - t,
            _               => false,
        };
    }

    private EdgeZone HitTest(TouchContact c)
    {
        // Vertical edges win where they overlap the bottom edge's corners.
        if (_settings.RightEdgeEnabled  && InZone(c, EdgeZone.Right))  return EdgeZone.Right;
        if (_settings.LeftEdgeEnabled   && InZone(c, EdgeZone.Left))   return EdgeZone.Left;
        if (_settings.BottomEdgeEnabled && InZone(c, EdgeZone.Bottom)) return EdgeZone.Bottom;
        return EdgeZone.None;
    }

    // ── Recognizer ────────────────────────────────────────────────────────

    private void TryEnterEdge(TouchContact contact, EdgeZone zone)
    {
        // Same phantom-touch guard: a spurious single contact in the edge zone right after lift
        // would restart scrolling and cancel inertia — ignore it during the grace window.
        if (InertiaGraceElapsedMs() < PhantomGraceMs) return;

        // Any touch stops a coasting scroll, in a zone or not.
        StopInertia();

        if (zone == EdgeZone.None) return;
        if (_settings.RequireBeginInZone && !_gestureBeganInZone) return;

        _zone             = zone;
        _isVertical       = zone != EdgeZone.Bottom;
        _trackedContactId = contact.ContactId;
        _refNormX         = contact.NormX;
        _refNormY         = contact.NormY;
        _refTime          = _frameTime;
        _lastVelocity     = 0;
        _haveLaunchVelocity = false;
        _launchVelocity     = 0;
        _accumulator             = 0;
        _edgeContactDisplacement = 0;
        _state            = State.EdgeContact;
    }

    private void ContinueScroll(TouchContact contact)
    {
        double dt    = _frameTime - _refTime;
        double delta = _isVertical
            ? _refNormY - contact.NormY
            : _refNormX - contact.NormX;

        // Keep the reference until the window spans >= 8 ms — the movement carries over.
        if (dt < MinVelocityWindowSec) return;

        if (Math.Abs(delta) < DeadZone)
        {
            // A resting finger: resample so a later flick isn't averaged over the rest.
            if (dt > StaleReferenceSec)
            {
                _refNormX = contact.NormX;
                _refNormY = contact.NormY;
                _refTime  = _frameTime;
                _lastVelocity = 0;
                _haveLaunchVelocity = false;  // lifting a resting finger must not coast
                _launchVelocity     = 0;
            }
            return;
        }

        _refNormX = contact.NormX;
        _refNormY = contact.NormY;
        _refTime  = _frameTime;

        double velocity = delta / dt;
        _lastVelocity = velocity;
        _launchVelocity = _haveLaunchVelocity
            ? LaunchEwmaAlpha * velocity + (1 - LaunchEwmaAlpha) * _launchVelocity
            : velocity;
        _haveLaunchVelocity = true;
        _launchVelocityTime = _frameTime;

        if (_state == State.EdgeContact)
        {
            _edgeContactDisplacement += Math.Abs(delta);
            if (_edgeContactDisplacement < ScrollActivationThreshold) return;
            if (_settings.MinStartVelocity > 0 && Math.Abs(velocity) < _settings.MinStartVelocity) return;

            _state = State.Scrolling;
            // Pin the cursor only now: a finger resting or drifting in the zone must not freeze it.
            if (_settings.LockCursorDuringScroll) LockCursor();
        }

        double speed  = _isVertical ? _settings.VerticalScrollSpeedMultiplier : _settings.HorizontalScrollSpeedMultiplier;
        double scaled = delta * 2000.0 * speed;
        _accumulator += scaled;

        int amount = (int)_accumulator;
        if (amount == 0) return;
        _accumulator -= amount;

        if (_isVertical  && _settings.InvertVerticalScroll)   amount = -amount;
        if (!_isVertical && _settings.InvertHorizontalScroll) amount = -amount;

        _frameEmitted += amount;
        _output.Scroll(amount, _isVertical);
    }

    private void ExitScroll(bool allowInertia)
    {
        bool wasScrolling = _state == State.Scrolling;
        _state            = State.Idle;
        _trackedContactId = -1;
        _zone             = EdgeZone.None;
        _accumulator      = 0;
        UnlockCursor();

        // A launch estimate older than a resting pause means the finger stopped before lifting.
        bool freshVelocity = _haveLaunchVelocity && _frameTime - _launchVelocityTime <= StaleReferenceSec;

        if (allowInertia && wasScrolling && _settings.InertiaEnabled && freshVelocity)
            BeginInertia();
    }

    private void BeginInertia()
    {
        double speed  = _isVertical ? _settings.VerticalScrollSpeedMultiplier : _settings.HorizontalScrollSpeedMultiplier;
        double launch = InertiaPhysics.LaunchVelocity(_launchVelocity, 2000.0 * speed, FlickThreshold);

        if (_isVertical  && _settings.InvertVerticalScroll)   launch = -launch;
        if (!_isVertical && _settings.InvertHorizontalScroll) launch = -launch;

        if (Math.Abs(launch) <= InertiaTerminus) return;

        _inertiaStartedAt = _frameTime;
        _frameLaunch      = launch;
        _inertia.Start(launch, _isVertical,
            InertiaPhysics.DecelForStrength(_settings.InertiaStrength), InertiaTerminus);
    }

    private void StopInertia() => _inertia.Stop();

    private void LockCursor()
    {
        if (_cursorLocked) return;
        _cursorLocked = _output.LockCursor();
    }

    private void UnlockCursor()
    {
        if (!_cursorLocked) return;
        _output.UnlockCursor();
        _cursorLocked = false;
    }

    public void Dispose()
    {
        _inertia.Dispose();
        UnlockCursor();
    }
}

/// <summary>Where the recognizer's output goes. The default posts real input; tests record it.</summary>
public interface IScrollOutput
{
    /// <summary>Posts a wheel amount; true = vertical. Called on the HID thread and on the inertia thread.</summary>
    void Scroll(int wheelAmount, bool vertical);
    /// <summary>Pins the cursor where it is. Returns false if it could not.</summary>
    bool LockCursor();
    void UnlockCursor();
}

/// <summary>Posts wheel events with SendInput and pins the cursor with a 1×1 ClipCursor rect.</summary>
public sealed class Win32ScrollOutput : IScrollOutput
{
    public void Scroll(int wheelAmount, bool vertical)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi   = new MOUSEINPUT
            {
                mouseData = (uint)wheelAmount,
                dwFlags   = vertical ? MOUSEEVENTF_WHEEL : MOUSEEVENTF_HWHEEL,
            }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public bool LockCursor()
    {
        if (!GetCursorPos(out var pt)) return false;
        var rect = new RECT { Left = pt.X, Top = pt.Y, Right = pt.X + 1, Bottom = pt.Y + 1 };
        return ClipCursor(ref rect);
    }

    public void UnlockCursor() => ClipCursor(IntPtr.Zero);
}
