namespace ScrollStrip.Tests;

/// <summary>A wall clock the test advances by hand.</summary>
internal sealed class FakeWall
{
    public double Seconds { get; set; } = 100.0;
    public double Now() => Seconds;
}

public class ScanTimeClockTests
{
    private const double FrameDt = 0.0112;
    private const ushort FrameTicks = 112;   // 11.2 ms in 100 µs scan-time units

    [Fact]
    public void Scan_time_drives_frame_time_once_trusted()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        ushort raw = 1000;
        double t0 = clock.Advance(true, raw, out _);
        for (int i = 0; i < 10; i++)
        {
            // The wall clock jitters; the device counter does not — scan time must win.
            wall.Seconds += FrameDt + (i % 2 == 0 ? 0.004 : -0.004);
            raw += FrameTicks;
            double t = clock.Advance(true, raw, out var source);
            Assert.Equal(FrameClock.ScanTime, source);
            Assert.Equal(t0 + (i + 1) * FrameDt, t, precision: 9);
        }
    }

    [Fact]
    public void Scan_time_unwraps_across_the_16_bit_rollover()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        ushort raw = 65_400;
        double prev = clock.Advance(true, raw, out _);
        for (int i = 0; i < 5; i++)
        {
            wall.Seconds += FrameDt;
            raw = unchecked((ushort)(raw + FrameTicks));   // 65 512 → 88 → 200 …
            double t = clock.Advance(true, raw, out _);
            Assert.Equal(FrameDt, t - prev, precision: 9);
            prev = t;
        }
    }

    [Fact]
    public void Frozen_scan_time_never_freezes_frame_time()
    {
        // The development pad declares Scan Time and reports 0 in every frame. Trusting it froze
        // frame time and with it the 50 ms phantom grace, which then rejected every touch.
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        double prev = clock.Advance(true, 0, out _);
        for (int i = 0; i < 40; i++)
        {
            wall.Seconds += FrameDt;
            double t = clock.Advance(true, 0, out var source);
            Assert.Equal(FrameDt, t - prev, precision: 9);
            Assert.Equal(FrameClock.WallClock, source);
            prev = t;
        }
    }

    [Fact]
    public void A_counter_that_stuck_stays_distrusted_even_if_it_starts_moving()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        clock.Advance(true, 0, out _);
        for (int i = 0; i < 20; i++) { wall.Seconds += FrameDt; clock.Advance(true, 0, out _); }

        ushort raw = 0;
        for (int i = 0; i < 5; i++)
        {
            wall.Seconds += FrameDt;
            raw += FrameTicks;
            clock.Advance(true, raw, out var source);
            Assert.Equal(FrameClock.WallClock, source);
        }
    }

    [Fact]
    public void A_briefly_stalled_counter_is_forgiven()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        ushort raw = 500;
        clock.Advance(true, raw, out _);
        for (int i = 0; i < 5; i++) { wall.Seconds += FrameDt; clock.Advance(true, raw, out _); }   // < 16 stuck frames

        wall.Seconds += FrameDt;
        raw += FrameTicks;
        clock.Advance(true, raw, out var source);
        Assert.Equal(FrameClock.ScanTime, source);
    }

    [Fact]
    public void An_idle_gap_longer_than_the_wrap_uses_the_wall_clock()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        double t0 = clock.Advance(true, 1000, out _);
        wall.Seconds += 10.0;   // longer than the 6.55 s wrap: the counter delta is meaningless
        double t1 = clock.Advance(true, 1200, out var source);

        Assert.Equal(10.0, t1 - t0, precision: 9);
        Assert.Equal(FrameClock.WallClock, source);
    }

    [Fact]
    public void Without_scan_time_the_wall_clock_is_used()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        double t0 = clock.Advance(false, 0, out _);
        wall.Seconds += 0.02;
        double t1 = clock.Advance(false, 0, out var source);

        Assert.Equal(0.02, t1 - t0, precision: 9);
        Assert.Equal(FrameClock.WallClock, source);
    }

    [Fact]
    public void Time_is_monotonic_through_every_path()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);
        var rng = new Random(42);

        double prev = double.NegativeInfinity;
        ushort raw = 0;
        for (int i = 0; i < 500; i++)
        {
            wall.Seconds += rng.NextDouble() < 0.02 ? 8.0 : rng.NextDouble() * 0.02;
            raw = unchecked((ushort)(raw + rng.Next(0, 300)));
            double t = clock.Advance(rng.NextDouble() < 0.9, raw, out _);
            Assert.True(t >= prev);
            prev = t;
        }
    }

    [Fact]
    public void Reset_restores_trust_in_the_counter()
    {
        var wall = new FakeWall();
        var clock = new ScanTimeClock(wall.Now);

        clock.Advance(true, 0, out _);
        for (int i = 0; i < 20; i++) { wall.Seconds += FrameDt; clock.Advance(true, 0, out _); }

        clock.Reset();
        ushort raw = 100;
        clock.Advance(true, raw, out _);
        wall.Seconds += FrameDt;
        raw += FrameTicks;
        clock.Advance(true, raw, out var source);

        Assert.Equal(FrameClock.ScanTime, source);
    }
}

public class TouchFrameAssemblerTests
{
    private static TouchContact Down(int id, double x = 0.5, double y = 0.5) => new(id, x, y, true, true);
    private static TouchContact Stale(int id) => new(id, 0.1, 0.1, TipDown: true, Confident: true);

    private static ReportParse Report(int? contactCount, params TouchContact[] slots)
    {
        var r = new ReportParse
        {
            AnySlotValid    = true,
            HasContactCount = contactCount.HasValue,
            ContactCount    = contactCount ?? 0,
            HasScanTime     = true,
            ScanTime        = 1234,
        };
        r.Slots.AddRange(slots);
        return r;
    }

    private static TouchFrameAssembler NewAssembler() => new(new ScanTimeClock(new FakeWall().Now));

    [Fact]
    public void Only_the_first_contact_count_slots_are_live()
    {
        // A 5-slot report with one live contact: the other slots hold stale data, tip bits and all.
        var frame = NewAssembler().Add(Report(1, Down(10), Stale(3), Stale(4), Stale(5), Stale(6)), 160, 114.9);

        Assert.NotNull(frame);
        Assert.Equal(10, Assert.Single(frame!.Contacts).ContactId);
        Assert.Equal(1, frame.ReportedContactCount);
    }

    [Fact]
    public void Hybrid_mode_frame_spans_several_reports()
    {
        var asm = NewAssembler();

        // Three contacts, two slots per report: count 3 in the first report, 0 in the rest.
        Assert.Null(asm.Add(Report(3, Down(1), Down(2)), 160, 114.9));
        var frame = asm.Add(Report(0, Down(3), Stale(9)), 160, 114.9);

        Assert.NotNull(frame);
        Assert.Equal(new[] { 1, 2, 3 }, frame!.Contacts.Select(c => c.ContactId));
        Assert.Equal(3, frame.ReportedContactCount);
    }

    [Fact]
    public void A_new_count_mid_frame_drops_the_incomplete_frame()
    {
        var asm = NewAssembler();

        Assert.Null(asm.Add(Report(3, Down(1), Down(2)), 160, 114.9));
        var frame = asm.Add(Report(1, Down(7), Stale(8)), 160, 114.9);

        Assert.Equal(7, Assert.Single(frame!.Contacts).ContactId);
    }

    [Fact]
    public void Zero_count_with_nothing_pending_is_an_all_up_frame()
    {
        var frame = NewAssembler().Add(Report(0, Stale(1), Stale(2)), 160, 114.9);

        Assert.NotNull(frame);
        Assert.Empty(frame!.Contacts);
    }

    [Fact]
    public void Without_contact_count_each_report_is_a_frame_of_its_tip_down_slots()
    {
        var up = new TouchContact(2, 0.5, 0.5, TipDown: false, Confident: true);
        var frame = NewAssembler().Add(Report(null, Down(1), up, Down(3)), 160, 114.9);

        Assert.Equal(new[] { 1, 3 }, frame!.Contacts.Select(c => c.ContactId));
    }

    [Fact]
    public void Reports_with_no_parsable_slot_are_dropped()
    {
        var bad = Report(1, Down(1));
        bad.AnySlotValid = false;

        Assert.Null(NewAssembler().Add(bad, 160, 114.9));
    }

    [Fact]
    public void Frame_carries_pad_size_and_the_raw_scan_time()
    {
        var frame = NewAssembler().Add(Report(1, Down(1)), 160, 114.9);

        Assert.Equal(160, frame!.PadWidthMm);
        Assert.Equal(114.9, frame.PadHeightMm);
        Assert.Equal(1234, frame.ScanTimeRaw);
    }

    [Fact]
    public void Confident_down_excludes_palms_and_lifted_contacts()
    {
        var frame = new TouchFrame(
            new[]
            {
                new TouchContact(1, 0.9, 0.5, TipDown: true,  Confident: true),
                new TouchContact(2, 0.3, 0.7, TipDown: true,  Confident: false),   // palm
                new TouchContact(3, 0.5, 0.5, TipDown: false, Confident: true),    // lifting
            },
            3, 0, FrameClock.WallClock, 0, 160, 114.9);

        Assert.Equal(1, Assert.Single(frame.ConfidentDown).ContactId);
    }
}
