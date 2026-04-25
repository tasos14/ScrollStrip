using System.Diagnostics;
using System.IO;
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
    private bool _isVertical; // true = right edge (vertical scroll), false = bottom edge (horizontal)
    private double _accumulator; // sub-tick scroll accumulation

    // Inertia: _velocity and _inertiaAccumulator guarded by _inertiaLock.
    // _inertiaIsVertical: written before timer starts (write-before-start).
    private readonly object _inertiaLock = new();
    private double _velocity;           // launch velocity set in BeginInertia, decayed by timer
    private double _inertiaAccumulator;
    private bool _inertiaIsVertical;    // snapshot of _isVertical taken when inertia begins
    private System.Timers.Timer? _inertiaTimer;
    private long _inertiaStartedAt; // Stopwatch timestamp set when inertia begins
    // HID callback thread only — no lock needed.
    private double InertiaGraceElapsedMs() =>
        (double)(Stopwatch.GetTimestamp() - _inertiaStartedAt) / Stopwatch.Frequency * 1000.0;

    // Velocity buffer: HID callback thread only — no lock needed.
    // Stores (Stopwatch.GetTimestamp(), scaled wheel units) for the trailing 100 ms of gesture motion.
    private readonly Queue<(long Ts, double WheelUnits)> _velocityBuffer = new();
    private static readonly long VelocityWindowTicks = Stopwatch.Frequency / 10; // 100 ms

    private const double InertiaTickScale     = 2.0;  // approx HID events per 16 ms timer tick
    private const double InertiaStopThreshold = 2.0;  // |velocity| below which inertia stops

    // Maps InertiaStrength 1–10 → decay per 16 ms tick.
    // Strength 1 → 0.92 (~590 ms to 5%), Strength 5 → ~0.951 (~1090 ms), Strength 10 → 0.99 (~4760 ms).
    private double InertiaDecay => 0.92 + (_settings.InertiaStrength - 1) / 9.0 * 0.07;

    private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "scroll_debug.log");
    private static void Log(string msg) => File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");

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
            // Touchpads often emit a phantom multi-touch report within ~20ms of a finger lifting.
            // Protect inertia from being cancelled by these artifacts for 50ms after launch.
            var graceMs = InertiaGraceElapsedMs();
            if (graceMs >= 50)
                StopInertia();
            else
                Log($"[INERTIA] Phantom multi-touch ignored ({graceMs:F1} ms after start)");
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
        // Ignore phantom touches within 50ms of inertia starting (lift artifacts on some touchpads).
        var graceMs = InertiaGraceElapsedMs();
        if (graceMs < 50)
        {
            Log($"[INERTIA] Phantom edge-contact ignored ({graceMs:F1} ms after start)");
            return;
        }
        StopInertia();

        bool inRightEdge = _settings.RightEdgeEnabled &&
            contact.NormX >= 1.0 - (_settings.RightEdgeWidthPct / 100.0);

        bool inBottomEdge = _settings.BottomEdgeEnabled &&
            contact.NormY >= 1.0 - (_settings.BottomEdgeHeightPct / 100.0);

        if (!inRightEdge && !inBottomEdge) return;

        _isVertical = inRightEdge; // prefer right edge if both
        _trackedContactId = contact.ContactId;
        _lastNormY = contact.NormY;
        _lastNormX = contact.NormX;
        _accumulator = 0;
        _velocityBuffer.Clear();
        _state = State.EdgeContact;

        if (_settings.LockCursorDuringScroll) LockCursor();
    }

    private void ContinueScroll(TouchContact contact)
    {
        double delta = _isVertical
            ? _lastNormY - contact.NormY   // moving up = positive scroll (scroll up)
            : _lastNormX - contact.NormX;  // moving left = scroll right

        _lastNormY = contact.NormY;
        _lastNormX = contact.NormX;

        if (Math.Abs(delta) < 0.0005) return; // dead zone

        _state = State.Scrolling;

        double speed = _isVertical ? _settings.VerticalScrollSpeedMultiplier : _settings.HorizontalScrollSpeedMultiplier;
        double scaled = delta * 2000.0 * speed;
        _accumulator += scaled;

        long now = Stopwatch.GetTimestamp();
        _velocityBuffer.Enqueue((now, scaled));
        while (_velocityBuffer.Count > 0 && now - _velocityBuffer.Peek().Ts > VelocityWindowTicks)
            _velocityBuffer.Dequeue();

        int ticks = (int)(_accumulator / WHEEL_DELTA);
        if (ticks == 0) return;
        _accumulator -= ticks * WHEEL_DELTA;

        if (_isVertical && _settings.InvertVerticalScroll)    ticks = -ticks;
        if (!_isVertical && _settings.InvertHorizontalScroll) ticks = -ticks;

        SendScrollEvent(ticks * WHEEL_DELTA, _isVertical);
    }

    private void ExitScroll()
    {
        bool wasScrolling = _state == State.Scrolling;
        _state = State.Idle;
        _trackedContactId = -1;
        _accumulator = 0;
        if (_settings.LockCursorDuringScroll) UnlockCursor();

        double launchVelocity = 0;
        if (_velocityBuffer.Count > 0)
        {
            double sum = 0;
            foreach (var (_, wu) in _velocityBuffer) sum += wu;
            launchVelocity = sum / _velocityBuffer.Count;
        }

        Log($"[INERTIA] ExitScroll: wasScrolling={wasScrolling} bufCount={_velocityBuffer.Count} launchV={launchVelocity:F2} threshold={InertiaStopThreshold}");

        if (wasScrolling && _settings.InertiaEnabled && Math.Abs(launchVelocity) >= InertiaStopThreshold)
            BeginInertia(launchVelocity);
        else
            lock (_inertiaLock) { _velocity = 0; _inertiaAccumulator = 0; }
    }

    private int _inertiaTick;

    private void BeginInertia(double launchVelocity)
    {
        Log($"[INERTIA] START launchV={launchVelocity:F2} decay={InertiaDecay:F4} strength={_settings.InertiaStrength}");
        _inertiaStartedAt = Stopwatch.GetTimestamp();
        lock (_inertiaLock)
        {
            _velocity = launchVelocity;       // written before timer starts — write-before-start
            _inertiaAccumulator = 0;
            _inertiaIsVertical = _isVertical; // snapshot axis before state resets
        }
        _inertiaTick = 0;

        _inertiaTimer?.Stop();
        _inertiaTimer?.Dispose();
        _inertiaTimer = new System.Timers.Timer(16) { AutoReset = true };
        _inertiaTimer.Elapsed += OnInertiaTimerElapsed;
        _inertiaTimer.Start();
    }

    private void StopInertia()
    {
        if (_inertiaTimer != null)
            Log($"[INERTIA] StopInertia() called at tick={_inertiaTick} (external cancel)");
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

        int tick = Interlocked.Increment(ref _inertiaTick);

        lock (_inertiaLock)
        {
            _velocity *= InertiaDecay;

            if (tick % 10 == 1)
                Log($"[INERTIA] tick={tick} v={_velocity:F2} threshold={InertiaStopThreshold}");

            if (Math.Abs(_velocity) < InertiaStopThreshold)
            {
                Log($"[INERTIA] STOP at tick={tick} v={_velocity:F2} ({tick * 16} ms total)");
                _velocity = 0;
                _inertiaTimer?.Stop();
                return;
            }

            // Accumulate and send the raw integer amount each tick (sub-WHEEL_DELTA allowed).
            // This mirrors native two-finger inertia: small continuous values that smooth-decelerate,
            // rather than waiting for 120-unit chunks which causes pauses at low velocity.
            _inertiaAccumulator += _velocity * InertiaTickScale;
            amount = (int)_inertiaAccumulator;
            if (amount == 0) return;
            _inertiaAccumulator -= amount;

            isVertical = _inertiaIsVertical;
            if (isVertical && _settings.InvertVerticalScroll)    amount = -amount;
            if (!isVertical && _settings.InvertHorizontalScroll) amount = -amount;
        }

        SendScrollEvent(amount, isVertical);
    }

    private static void SendScrollEvent(int wheelAmount, bool vertical)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                mouseData = (uint)wheelAmount,
                dwFlags = vertical ? MOUSEEVENTF_WHEEL : MOUSEEVENTF_HWHEEL,
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
