using System.Diagnostics;
using System.Runtime.InteropServices;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

public sealed class EdgeScrollController : IDisposable
{
    private enum State { Idle, EdgeContact, Scrolling }

    private readonly AppSettings _settings;
    private State _state = State.Idle;
    private int _trackedContactId = -1;
    private double _lastNormY;
    private double _lastNormX;
    private bool _isVertical;    // true = right edge (vertical), false = bottom edge (horizontal)
    private double _accumulator;             // fractional scroll units carried between HID events
    private double _edgeContactDisplacement; // cumulative movement in EdgeContact; tap guard
    private const double ScrollActivationThreshold = 0.02; // 2% of touchpad — tap can't reach this

    // Inertia state — _velocity and _inertiaAccumulator are guarded by _inertiaLock.
    // _inertiaIsVertical is written before the timer starts (write-before-start guarantee).
    private readonly object _inertiaLock = new();
    private double _velocity;
    private double _inertiaAccumulator;
    private bool _inertiaIsVertical;
    private System.Timers.Timer? _inertiaTimer;
    private long _inertiaStartedAt; // Stopwatch.GetTimestamp() captured in BeginInertia

    // Velocity ring buffer — HID callback thread only, no lock needed.
    // Holds (timestamp, wheel-units) for the last 100 ms of gesture motion.
    private readonly Queue<(long Ts, double WheelUnits)> _velocityBuffer = new();
    private static readonly long VelocityWindowTicks = Stopwatch.Frequency / 10; // 100 ms

    private const double InertiaTickScale     = 2.0; // approx HID events per 16 ms timer tick
    private const double InertiaStopThreshold = 2.0; // |velocity| below which inertia stops

    // Strength 1–10 maps to per-tick decay factor:
    //   1 → 0.92  (~590 ms to 5% of launch velocity)
    //   5 → 0.951 (~1090 ms)
    //  10 → 0.99  (~4760 ms)
    private double InertiaDecay => 0.92 + (_settings.InertiaStrength - 1) / 9.0 * 0.07;

    // Milliseconds elapsed since inertia last started. HID callback thread only.
    private double InertiaGraceElapsedMs() =>
        (double)(Stopwatch.GetTimestamp() - _inertiaStartedAt) / Stopwatch.Frequency * 1000.0;

    public EdgeScrollController(AppSettings settings) => _settings = settings;

    public void OnContacts(List<TouchContact> contacts, int reportedCount)
    {
        if (!_settings.EdgeScrollEnabled)
        {
            if (_state != State.Idle) ExitScroll();
            StopInertia();
            return;
        }

        var activeContacts = contacts.Where(c => c.TipDown).ToList();
        int fingerCount = reportedCount > 0 ? reportedCount : activeContacts.Count;

        if (fingerCount > 1)
        {
            if (_state != State.Idle) ExitScroll();
            // Touchpads emit phantom multi-touch reports within ~20 ms of a finger lifting.
            // Skip StopInertia for 50 ms so these artifacts don't cancel freshly-started inertia.
            if (InertiaGraceElapsedMs() >= 50) StopInertia();
            return;
        }

        if (activeContacts.Count == 0)
        {
            if (_state != State.Idle) ExitScroll();
            return;
        }

        var contact = activeContacts.FirstOrDefault();
        if (contact == default) { if (_state != State.Idle) ExitScroll(); return; }

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

    private void TryEnterEdge(TouchContact contact)
    {
        // Same phantom-touch guard: a spurious single-contact in the edge zone right after lift
        // would restart scrolling and cancel inertia — ignore it during the grace window.
        if (InertiaGraceElapsedMs() < 50) return;

        StopInertia();

        bool inRightEdge  = _settings.RightEdgeEnabled  && contact.NormX >= 1.0 - _settings.RightEdgeWidthPct  / 100.0;
        bool inBottomEdge = _settings.BottomEdgeEnabled && contact.NormY >= 1.0 - _settings.BottomEdgeHeightPct / 100.0;

        if (!inRightEdge && !inBottomEdge) return;

        _isVertical       = inRightEdge; // prefer right edge when both zones overlap
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

        long now = Stopwatch.GetTimestamp();
        _velocityBuffer.Enqueue((now, scaled));
        while (_velocityBuffer.Count > 0 && now - _velocityBuffer.Peek().Ts > VelocityWindowTicks)
            _velocityBuffer.Dequeue();

        int amount = (int)_accumulator;
        if (amount == 0) return;
        _accumulator -= amount;

        if (_isVertical  && _settings.InvertVerticalScroll)   amount = -amount;
        if (!_isVertical && _settings.InvertHorizontalScroll) amount = -amount;

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
        _inertiaStartedAt = Stopwatch.GetTimestamp();
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
