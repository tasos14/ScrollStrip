using System.Diagnostics;
using static ScrollStrip.NativeMethods;

namespace ScrollStrip;

/// <summary>
/// Inertia physics after Scroll.app (docs/scroll-re-analysis.md §6), with no threads or clock —
/// the caller supplies real elapsed time. Velocity is in wheel units per 1/60 s tick.
/// </summary>
public sealed class InertiaPhysics
{
    public const double TickHz = 60.0;

    /// <summary>Launch boost for an ordinary release and for a flick, from Scroll.app.</summary>
    public const double NormalBoost = 3.0;
    public const double FlickBoost  = 8.0;

    private readonly double _decel;
    private readonly double _terminus;
    private double _accumulator;

    public double Velocity { get; private set; }

    /// <summary>True once |velocity| has decayed to the terminus.</summary>
    public bool IsDone => Math.Abs(Velocity) <= _terminus;

    public InertiaPhysics(double launchPerTick, double decel, double terminus)
    {
        Velocity  = launchPerTick;
        _decel    = Math.Clamp(decel, 0.001, 0.999);
        _terminus = terminus;
    }

    /// <summary>
    /// Launch velocity in wheel units per tick. The finger's speed is converted to a per-tick
    /// rate, then boosted ×3, or ×8 above the flick threshold — this boost is what makes a
    /// flick coast far (total coast ≈ launch / decel).
    /// </summary>
    /// <param name="axisVelocity">Finger speed along the scroll axis, pad lengths per second.</param>
    /// <param name="gain">Wheel units per pad length, i.e. the drag gain.</param>
    /// <param name="flickThreshold">|axisVelocity| above which the flick boost applies.</param>
    public static double LaunchVelocity(double axisVelocity, double gain, double flickThreshold)
    {
        double perTick = axisVelocity * gain / TickHz;
        return perTick * (Math.Abs(axisVelocity) > flickThreshold ? FlickBoost : NormalBoost);
    }

    /// <summary>
    /// Per-tick deceleration for InertiaStrength 1–10. Strength 5 is Scroll.app's default 0.08;
    /// each 4 steps halves or doubles it (1 → 0.16, 10 → 0.034).
    /// </summary>
    public static double DecelForStrength(int strength) => 0.08 * Math.Pow(2, (5 - strength) / 4.0);

    /// <summary>
    /// Advances by <paramref name="dt"/> seconds and returns the whole wheel units to emit.
    /// Both the decay and the emitted distance scale with real dt, so a late timer tick
    /// neither shortens nor lengthens the coast.
    /// </summary>
    public int Step(double dt)
    {
        if (IsDone || dt <= 0) return 0;

        double ticks = dt * TickHz;
        Velocity *= Math.Pow(1.0 - _decel, ticks);
        _accumulator += Velocity * ticks;

        int amount = (int)_accumulator;
        _accumulator -= amount;
        return amount;
    }
}

/// <summary>
/// Runs <see cref="InertiaPhysics"/> at ~60 Hz on a dedicated thread driven by a
/// high-resolution waitable timer, posting each step through the scroll sink.
/// </summary>
/// <summary>The coast engine the recognizer drives. Lets tests observe launches without a timer thread.</summary>
public interface IInertia : IDisposable
{
    /// <summary>Starts a coast, replacing any in progress.</summary>
    void Start(double launchPerTick, bool vertical, double decel, double terminus);
    /// <summary>Stops the coast. No step is posted after this returns.</summary>
    void Stop();
    /// <summary>True while a coast is running. Lock-free: read from a low-level hook.</summary>
    bool IsActive { get; }
}

public sealed class InertiaScroller : IInertia
{
    private const long TickDue100ns = -166_667;   // relative 16.67 ms
    private const double MaxStepSec = 0.05;       // a stalled thread (sleep, debugger) mustn't jump

    private readonly Action<int, bool> _sink;
    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly IntPtr _timer;

    // Guarded by _lock. Null means idle.
    private InertiaPhysics? _physics;
    private bool _vertical;
    private long _lastTick;

    private volatile bool _disposed;
    // Mirrors _physics != null. Written under _lock, read without it: IsActive is called from the
    // low-level mouse hook, which must never wait on the lock the inertia thread holds in SendInput.
    private volatile bool _active;

    public bool IsActive => _active;

    /// <param name="sink">Posts a wheel amount; true = vertical. Called on the inertia thread.</param>
    public InertiaScroller(Action<int, bool> sink)
    {
        _sink = sink;

        // The high-resolution flag needs Windows 10 1803+; without it the timer rounds to the
        // ~15.6 ms system tick, which dt-scaled physics tolerates.
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (_timer == IntPtr.Zero)
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name         = "ScrollStrip-Inertia",
            Priority     = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Start(double launchPerTick, bool vertical, double decel, double terminus)
    {
        lock (_lock)
        {
            _physics  = new InertiaPhysics(launchPerTick, decel, terminus);
            _active   = true;
            _vertical = vertical;
            _lastTick = Stopwatch.GetTimestamp();
        }
        _wake.Set();
    }

    /// <summary>Stops the coast. No step is posted after this returns.</summary>
    public void Stop()
    {
        lock (_lock) { _physics = null; _active = false; }
    }

    private void Run()
    {
        while (!_disposed)
        {
            bool active;
            lock (_lock) active = _physics != null;

            if (!active)
            {
                _wake.WaitOne();
                continue;
            }

            WaitTick();

            lock (_lock)
            {
                if (_physics == null || _disposed) continue;

                long now = Stopwatch.GetTimestamp();
                double dt = Math.Min((now - _lastTick) / (double)Stopwatch.Frequency, MaxStepSec);
                _lastTick = now;

                // Posting under the lock is what lets Stop() promise no late step; SendInput is µs.
                int amount = _physics.Step(dt);
                if (amount != 0) _sink(amount, _vertical);
                if (_physics.IsDone) { _physics = null; _active = false; }
            }
        }
    }

    private void WaitTick()
    {
        if (_timer == IntPtr.Zero) { Thread.Sleep(16); return; }

        long due = TickDue100ns;
        if (!SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            Thread.Sleep(16);
            return;
        }
        WaitForSingleObject(_timer, 100);
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _wake.Set();
        _thread.Join(200);
        if (_timer != IntPtr.Zero) CloseHandle(_timer);
        _wake.Dispose();
    }
}
