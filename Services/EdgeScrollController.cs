using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

public enum EdgeZone { None, Right, Bottom }

/// <summary>What the recognizer did with one frame — for --record and for tests.</summary>
public readonly record struct EdgeScrollDiagnostics(
    int FingerCount, EdgeZone Zone, string State, int EmittedDelta);

public sealed class EdgeScrollController : IDisposable
{
    private enum State { Idle, EdgeContact, Scrolling }

    /// <summary>Fired once per frame after the frame has been processed.</summary>
    public event Action<TouchFrame, EdgeScrollDiagnostics>? FrameProcessed;

    private readonly AppSettings _settings;
    private State _state = State.Idle;
    private int _trackedContactId = -1;
    private double _lastNormY;
    private double _lastNormX;
    private bool _isVertical;    // true = right edge (vertical), false = bottom edge (horizontal)
    private double _accumulator;             // fractional scroll units carried between frames
    private double _edgeContactDisplacement; // cumulative movement in EdgeContact; tap guard
    private const double ScrollActivationThreshold = 0.02; // 2% of touchpad — tap can't reach this

    // Frame-scoped: set at the top of OnFrame, read by everything downstream.
    private double _frameTime;    // monotonic seconds from the HID scan time
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

        if (FrameProcessed != null)
        {
            var zone = _state == State.Idle
                ? (activeContacts.Count == 1 ? HitTest(activeContacts[0]) : EdgeZone.None)
                : (_isVertical ? EdgeZone.Right : EdgeZone.Bottom);
            FrameProcessed.Invoke(frame,
                new EdgeScrollDiagnostics(activeContacts.Count, zone, _state.ToString(), _frameEmitted));
        }
    }

    private void ProcessFrame(List<TouchContact> activeContacts)
    {
        if (!_settings.EdgeScrollEnabled)
        {
            if (_state != State.Idle) ExitScroll();
            StopInertia();
            return;
        }

        if (activeContacts.Count > 1)
        {
            if (_state != State.Idle) ExitScroll();
            // Touchpads emit phantom multi-touch frames within ~20 ms of a finger lifting.
            // Skip StopInertia for 50 ms so these artifacts don't cancel freshly-started inertia.
            if (InertiaGraceElapsedMs() >= 50) StopInertia();
            return;
        }

        if (activeContacts.Count == 0)
        {
            if (_state != State.Idle) ExitScroll();
            return;
        }

        var contact = activeContacts[0];

        switch (_state)
        {
            case State.Idle:
                TryEnterEdge(contact);
                break;

            case State.EdgeContact:
            case State.Scrolling:
                if (contact.ContactId != _trackedContactId)
                {
                    ExitScroll();
                    TryEnterEdge(contact);
                    return;
                }
                ContinueScroll(contact);
                break;
        }
    }

    private EdgeZone HitTest(TouchContact contact)
    {
        // Prefer the right edge when both zones overlap.
        if (_settings.RightEdgeEnabled && contact.NormX >= 1.0 - _settings.RightEdgeWidthPct / 100.0)
            return EdgeZone.Right;
        if (_settings.BottomEdgeEnabled && contact.NormY >= 1.0 - _settings.BottomEdgeHeightPct / 100.0)
            return EdgeZone.Bottom;
        return EdgeZone.None;
    }

    private void TryEnterEdge(TouchContact contact)
    {
        // Same phantom-touch guard: a spurious single contact in the edge zone right after lift
        // would restart scrolling and cancel inertia — ignore it during the grace window.
        if (InertiaGraceElapsedMs() < 50) return;

        StopInertia();

        var zone = HitTest(contact);
        if (zone == EdgeZone.None) return;

        _isVertical       = zone == EdgeZone.Right;
        _trackedContactId = contact.ContactId;
        _lastNormY        = contact.NormY;
        _lastNormX        = contact.NormX;
        _accumulator             = 0;
        _edgeContactDisplacement = 0;
        _velocityBuffer.Clear();
        _state            = State.EdgeContact;

        if (_settings.LockCursorDuringScroll) LockCursor();
    }

    private void ContinueScroll(TouchContact contact)
    {
        double delta = _isVertical
            ? _lastNormY - contact.NormY
            : _lastNormX - contact.NormX;

        _lastNormY = contact.NormY;
        _lastNormX = contact.NormX;

        if (Math.Abs(delta) < 0.0005) return; // dead zone

        if (_state == State.EdgeContact)
        {
            _edgeContactDisplacement += Math.Abs(delta);
            if (_edgeContactDisplacement < ScrollActivationThreshold) return;
        }
        _state = State.Scrolling;

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

    private void ExitScroll()
    {
        bool wasScrolling = _state == State.Scrolling;
        _state            = State.Idle;
        _trackedContactId = -1;
        _accumulator      = 0;
        if (_settings.LockCursorDuringScroll) UnlockCursor();

        double launchVelocity = 0;
        if (_velocityBuffer.Count > 0)
        {
            double sum = 0;
            foreach (var (_, wu) in _velocityBuffer) sum += wu;
            launchVelocity = sum / _velocityBuffer.Count;
        }

        if (wasScrolling && _settings.InertiaEnabled && Math.Abs(launchVelocity) >= InertiaStopThreshold)
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

    private static void LockCursor()
    {
        if (!GetCursorPos(out var pt)) return;
        var rect = new RECT { Left = pt.X, Top = pt.Y, Right = pt.X + 1, Bottom = pt.Y + 1 };
        ClipCursor(ref rect);
    }

    private static void UnlockCursor() => ClipCursor(IntPtr.Zero);

    public void Dispose() => StopInertia();
}
