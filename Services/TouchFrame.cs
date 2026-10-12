using System.Diagnostics;

namespace ScrollStrip;

/// <summary>Where a frame's timestamp came from.</summary>
public enum FrameClock { ScanTime, WallClock }

/// <summary>
/// One complete touch frame: every contact the device reported for a single scan, with a
/// monotonic timestamp. In hybrid mode a frame spans several HID reports.
/// </summary>
public sealed record TouchFrame(
    IReadOnlyList<TouchContact> Contacts,
    int ReportedContactCount,
    double TimestampSec,
    FrameClock Clock,
    ushort ScanTimeRaw,
    double PadWidthMm,
    double PadHeightMm)
{
    /// <summary>Contacts the firmware is confident about — its own palm rejection, for free.</summary>
    public IEnumerable<TouchContact> ConfidentDown => Contacts.Where(c => c.TipDown && c.Confident);
}

/// <summary>
/// Turns the 16-bit HID Scan Time (100 µs units) into monotonic seconds.
///
/// Scan time wraps every 6.5536 s, so an idle gap longer than that cannot be unwrapped from the
/// counter alone; the wall clock takes over for those gaps and for devices with no scan time.
///
/// The counter is also cross-checked against the wall clock, because some firmware declares
/// Scan Time and then never advances it. A frozen counter would otherwise freeze frame time and
/// stall every timing rule built on it, so a counter that does not move while frames keep
/// arriving is abandoned for the rest of the session.
/// </summary>
public sealed class ScanTimeClock
{
    private const double TickSeconds    = 1e-4;   // HID scan time is in 100 µs units
    private const double WrapSeconds    = 65536 * TickSeconds;
    private const double TrustWindowSec = WrapSeconds / 2;

    /// <summary>Wall time that must pass before a frame with an unchanged counter counts as stuck.</summary>
    private const double StuckWallSec = 0.002;
    /// <summary>Consecutive stuck frames (~130 ms of input) before the counter is abandoned.</summary>
    private const int StuckFramesToDistrust = 16;

    private bool _trusted = true;
    private int _stuckFrames;
    private bool _haveRaw;
    private ushort _lastRaw;
    private readonly Func<double> _wallSeconds;
    private bool _haveWall;
    private double _lastWall;
    private double _seconds;

    /// <param name="wallSeconds">Monotonic wall clock in seconds; Stopwatch when null. Injected by tests.</param>
    public ScanTimeClock(Func<double>? wallSeconds = null)
    {
        _wallSeconds = wallSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    /// <summary>Advances the clock by one frame and returns its timestamp in seconds.</summary>
    public double Advance(bool hasScanTime, ushort raw, out FrameClock source)
    {
        double now = _wallSeconds();
        double wallDelta = _haveWall ? now - _lastWall : 0;
        _haveWall = true;
        _lastWall = now;

        source = FrameClock.WallClock;

        if (!hasScanTime || !_trusted)
        {
            _haveRaw  = false;
            _seconds += wallDelta;
            return _seconds;
        }

        // A gap long enough for the counter to have wrapped (or the first frame after a reset)
        // makes the delta meaningless — fall back to the wall clock for that one step.
        if (!_haveRaw || wallDelta > TrustWindowSec)
        {
            _haveRaw  = true;
            _lastRaw  = raw;
            _seconds += wallDelta;
            return _seconds;
        }

        double scanDelta = (ushort)(raw - _lastRaw) * TickSeconds; // unsigned wrap-around is the unwrap
        _lastRaw = raw;

        // Frames arrive far slower than the 100 µs tick, so a zero delta means the counter is
        // not running. Keep time moving on the wall clock, and give up on it if it persists.
        if (scanDelta == 0 && wallDelta > StuckWallSec)
        {
            if (++_stuckFrames >= StuckFramesToDistrust) _trusted = false;
            _seconds += wallDelta;
            return _seconds;
        }

        _stuckFrames = 0;
        _seconds += scanDelta;
        source = FrameClock.ScanTime;
        return _seconds;
    }

    public void Reset()
    {
        _haveRaw     = false;
        _haveWall    = false;
        _trusted     = true;
        _stuckFrames = 0;
    }
}

/// <summary>
/// Assembles HID reports into frames. A precision touchpad in hybrid mode carries only a few
/// contact slots per report and spreads a frame over several reports: the first report of the
/// frame carries Contact Count, the rest carry 0.
/// </summary>
public sealed class TouchFrameAssembler
{
    private readonly ScanTimeClock _clock;
    private readonly List<TouchContact> _pending = new();

    /// <param name="clock">Frame clock; a Stopwatch-backed one when null. Injected by tests.</param>
    public TouchFrameAssembler(ScanTimeClock? clock = null) => _clock = clock ?? new ScanTimeClock();

    private bool _assembling;
    private int _expected;
    private bool _frameHasScanTime;
    private ushort _frameScanTime;

    /// <summary>Returns the completed frame, or null while a hybrid-mode frame is still arriving.</summary>
    public TouchFrame? Add(ReportParse report, double padWidthMm, double padHeightMm)
    {
        if (!report.AnySlotValid) return null;

        // No Contact Count in the descriptor: every report is a whole frame.
        if (!report.HasContactCount)
        {
            var down = report.Slots.Where(c => c.TipDown).ToList();
            return Emit(down, down.Count, report.HasScanTime, report.ScanTime, padWidthMm, padHeightMm);
        }

        // A report carrying a count starts a frame. So does a zero count with nothing in flight,
        // which is how some pads announce "all fingers up".
        if (report.ContactCount > 0 || !_assembling)
        {
            _assembling       = true;
            _expected         = Math.Max(0, report.ContactCount);
            _frameHasScanTime = report.HasScanTime;
            _frameScanTime    = report.ScanTime;
            _pending.Clear();
        }

        // Only the first (_expected) slots of a frame hold live data; the rest are stale.
        int take = Math.Min(report.Slots.Count, _expected - _pending.Count);
        for (int i = 0; i < take; i++) _pending.Add(report.Slots[i]);

        if (_pending.Count < _expected) return null;   // hybrid mode: more reports to come

        _assembling = false;
        var contacts = new List<TouchContact>(_pending);
        _pending.Clear();
        return Emit(contacts, _expected, _frameHasScanTime, _frameScanTime, padWidthMm, padHeightMm);
    }

    private TouchFrame Emit(List<TouchContact> contacts, int count,
                            bool hasScanTime, ushort scanTime, double padWidthMm, double padHeightMm)
    {
        double t = _clock.Advance(hasScanTime, scanTime, out var source);
        return new TouchFrame(contacts, count, t, source, scanTime, padWidthMm, padHeightMm);
    }

    public void Reset()
    {
        _pending.Clear();
        _assembling = false;
        _expected   = 0;
        _clock.Reset();
    }
}
