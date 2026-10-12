using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

public enum EdgeZone { None, Right, Left, Bottom }

/// <summary>What the recognizer did with one frame — for --record and for tests.</summary>
/// <param name="Zone">Geometric hit test of the single contact this frame, regardless of state.</param>
/// <param name="TrackedZone">The zone the active scroll belongs to, or None when idle.</param>
/// <param name="Velocity">Last measured speed along the scroll axis, pad units per second.</param>
public readonly record struct EdgeScrollDiagnostics(
    int FingerCount, EdgeZone Zone, EdgeZone TrackedZone, string State, double Velocity, int EmittedDelta);

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
    private const double MinVelocityWindowSec = 0.008;
    private const double StaleReferenceSec    = 0.05;   // a finger at rest this long resamples the reference
    private const double DeadZone             = 0.0005;
    private const double ExitHysteresisMm     = 2.0;

    // Frame-scoped: set at the top of OnFrame, read by everything downstream.
    private double _frameTime;    // monotonic seconds from the frame clock
    private int _frameEmitted;    // wheel units emitted by the gesture path this frame

    /// <summary>Touch surface size in mm from the last frame, or 0 when the pad does not report it.</summary>
    public double PadWidthMm { get; private set; }
    public double PadHeightMm { get; private set; }

    // Inertia state — _velocity and _inertiaAccumulator are guarded by _inertiaLock.
    // _inertiaIsVertical is written before the timer starts (write-before-start guarantee).
    private readonly object _inertiaLock = new();
    private double _velocity;
    private double _inertiaAccumulator;
    private bool _inertiaIsVertical;
    private System.Timers.Timer? _inertiaTimer;
    private double _inertiaStartedAt = double.NegativeInfinity; // frame time captured in BeginInertia
    private const double PhantomGraceMs = 50;

    // Velocity ring buffer — HID callback thread only, no lock needed.
    // Holds (frame time, wheel-units) for the last 100 ms of gesture motion.
    private readonly Queue<(double T, double WheelUnits)> _velocityBuffer = new();
    private const double VelocityWindowSec = 0.1;

    private const double InertiaTickScale     = 2.0; // approx frames per 16 ms timer tick
    private const double InertiaStopThreshold = 2.0; // |velocity| below which inertia stops

    // Strength 1–10 maps to per-tick decay factor:
    //   1 → 0.92  (~590 ms to 5% of launch velocity)
    //   5 → 0.951 (~1090 ms)
    //  10 → 0.99  (~4760 ms)
    private double InertiaDecay => 0.92 + (_settings.InertiaStrength - 1) / 9.0 * 0.07;

    // Frame time elapsed since inertia last started. HID callback thread only.
    private double InertiaGraceElapsedMs() => (_frameTime - _inertiaStartedAt) * 1000.0;

    public EdgeScrollController(AppSettings settings) => _settings = settings;

    public void OnFrame(TouchFrame frame)
    {
        _frameTime    = frame.TimestampSec;
        _frameEmitted = 0;
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
            _frameEmitted));
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
        _accumulator             = 0;
        _edgeContactDisplacement = 0;
        _velocityBuffer.Clear();
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
            }
            return;
        }

        _refNormX = contact.NormX;
        _refNormY = contact.NormY;
        _refTime  = _frameTime;

        double velocity = delta / dt;
        _lastVelocity = velocity;

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

        _velocityBuffer.Enqueue((_frameTime, scaled));
        while (_velocityBuffer.Count > 0 && _frameTime - _velocityBuffer.Peek().T > VelocityWindowSec)
            _velocityBuffer.Dequeue();

        int amount = (int)_accumulator;
        if (amount == 0) return;
        _accumulator -= amount;

        if (_isVertical  && _settings.InvertVerticalScroll)   amount = -amount;
        if (!_isVertical && _settings.InvertHorizontalScroll) amount = -amount;

        _frameEmitted += amount;
        SendScrollEvent(amount, _isVertical);
    }

    private void ExitScroll(bool allowInertia)
    {
        bool wasScrolling = _state == State.Scrolling;
        _state            = State.Idle;
        _trackedContactId = -1;
        _zone             = EdgeZone.None;
        _accumulator      = 0;
        UnlockCursor();

        double launchVelocity = 0;
        if (_velocityBuffer.Count > 0)
        {
            double sum = 0;
            foreach (var (_, wu) in _velocityBuffer) sum += wu;
            launchVelocity = sum / _velocityBuffer.Count;
        }

        if (allowInertia && wasScrolling && _settings.InertiaEnabled && Math.Abs(launchVelocity) >= InertiaStopThreshold)
            BeginInertia(launchVelocity);
        else
            lock (_inertiaLock) { _velocity = 0; _inertiaAccumulator = 0; }
    }

    private void BeginInertia(double launchVelocity)
    {
        _inertiaStartedAt = _frameTime;
        lock (_inertiaLock)
        {
            _velocity          = launchVelocity; // written before timer starts — write-before-start
            _inertiaAccumulator = 0;
            _inertiaIsVertical = _isVertical;
        }

        _inertiaTimer?.Stop();
        _inertiaTimer?.Dispose();
        _inertiaTimer         = new System.Timers.Timer(16) { AutoReset = true };
        _inertiaTimer.Elapsed += OnInertiaTimerElapsed;
        _inertiaTimer.Start();
    }

    private void StopInertia()
    {
        _inertiaTimer?.Stop();
        _inertiaTimer?.Dispose();
        _inertiaTimer = null;
        lock (_inertiaLock) { _velocity = 0; _inertiaAccumulator = 0; }
        _velocityBuffer.Clear();
    }

    private void OnInertiaTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        int amount;
        bool isVertical;

        lock (_inertiaLock)
        {
            _velocity *= InertiaDecay;

            if (Math.Abs(_velocity) < InertiaStopThreshold)
            {
                _velocity = 0;
                _inertiaTimer?.Stop();
                return;
            }

            _inertiaAccumulator += _velocity * InertiaTickScale;
            amount = (int)_inertiaAccumulator;
            if (amount == 0) return;
            _inertiaAccumulator -= amount;

            isVertical = _inertiaIsVertical;
            if (isVertical  && _settings.InvertVerticalScroll)   amount = -amount;
            if (!isVertical && _settings.InvertHorizontalScroll) amount = -amount;
        }

        SendScrollEvent(amount, isVertical);
    }

    private static void SendScrollEvent(int wheelAmount, bool vertical)
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

    private void LockCursor()
    {
        if (_cursorLocked || !GetCursorPos(out var pt)) return;
        var rect = new RECT { Left = pt.X, Top = pt.Y, Right = pt.X + 1, Bottom = pt.Y + 1 };
        _cursorLocked = ClipCursor(ref rect);
    }

    private void UnlockCursor()
    {
        if (!_cursorLocked) return;
        ClipCursor(IntPtr.Zero);
        _cursorLocked = false;
    }

    public void Dispose()
    {
        StopInertia();
        UnlockCursor();
    }
}
