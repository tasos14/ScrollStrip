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

    // Inertia: all fields guarded by _inertiaLock except _inertiaIsVertical (snapshot, write-before-start)
    private readonly object _inertiaLock = new();
    private double _velocity;           // EMA of scaled scroll delta (wheel units per HID event)
    private double _inertiaAccumulator;
    private bool _inertiaIsVertical;    // snapshot of _isVertical taken when inertia begins
    private System.Timers.Timer? _inertiaTimer;

    private const double VelocityAlpha         = 0.3;   // EMA weight for incoming samples
    private const double InertiaTickScale      = 2.0;   // approx HID events per 16 ms timer tick
    private const double InertiaStopThreshold  = 2.0;   // |velocity| below which inertia stops

    // Maps InertiaStrength 1–10 → decay 0.80–0.98 (step 0.02), with 5 → 0.88 matching the original constant
    private double InertiaDecay => 0.80 + (_settings.InertiaStrength - 1) / 9.0 * 0.18;

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
            StopInertia();
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

        lock (_inertiaLock)
            _velocity = _velocity * (1 - VelocityAlpha) + scaled * VelocityAlpha;

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

        double v;
        lock (_inertiaLock) v = _velocity;

        if (wasScrolling && _settings.InertiaEnabled && Math.Abs(v) >= InertiaStopThreshold)
            BeginInertia();
        else
            lock (_inertiaLock) { _velocity = 0; _inertiaAccumulator = 0; }
    }

    private void BeginInertia()
    {
        lock (_inertiaLock)
        {
            _inertiaAccumulator = 0;
            _inertiaIsVertical = _isVertical; // snapshot axis before state resets
        }

        _inertiaTimer?.Stop();
        _inertiaTimer?.Dispose();
        _inertiaTimer = new System.Timers.Timer(16) { AutoReset = true };
        _inertiaTimer.Elapsed += OnInertiaTimerElapsed;
        _inertiaTimer.Start();
    }

    private void StopInertia()
    {
        _inertiaTimer?.Stop();
        _inertiaTimer?.Dispose();
        _inertiaTimer = null;
        lock (_inertiaLock) { _velocity = 0; _inertiaAccumulator = 0; }
    }

    private void OnInertiaTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        int ticks;
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
            ticks = (int)(_inertiaAccumulator / WHEEL_DELTA);
            if (ticks == 0) return;
            _inertiaAccumulator -= ticks * WHEEL_DELTA;

            isVertical = _inertiaIsVertical;
            if (isVertical && _settings.InvertVerticalScroll)    ticks = -ticks;
            if (!isVertical && _settings.InvertHorizontalScroll) ticks = -ticks;
        }

        SendScrollEvent(ticks * WHEEL_DELTA, isVertical);
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
