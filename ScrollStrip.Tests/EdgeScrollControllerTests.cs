using static ScrollStrip.Tests.Pad;

namespace ScrollStrip.Tests;

public class EdgeScrollControllerTests
{
    // Drag gain at the default speed multiplier of 1.5: 2000 × 1.5 wheel units per pad length.
    private const double Gain = 3000.0;

    // ── Verification list ────────────────────────────────────────────────

    [Fact]
    public void Tap_in_zone_emits_nothing_and_neither_pins_the_cursor_nor_coasts()
    {
        using var pad = new Pad();

        pad.Frame(Finger(1, InRightZone, 0.500));
        pad.Frame(Finger(1, InRightZone, 0.501));
        pad.Frame(Finger(1, InRightZone, 0.499));
        pad.Frame(Finger(1, InRightZone, 0.500));
        pad.Lift(1, InRightZone, 0.500);

        Assert.Empty(pad.Output.Scrolls);
        Assert.Equal(0, pad.Output.Locks);
        Assert.Empty(pad.Inertia.Starts);
    }

    [Fact]
    public void Slow_drag_emits_distance_proportional_to_travel()
    {
        using var pad = new Pad();

        // 0.4 pad lengths upward over 60 frames (~0.6 pad/s), then rest so nothing coasts.
        pad.Drag(1, InRightZone, 0.7, InRightZone, 0.3, frames: 60);
        pad.Hold(1, InRightZone, 0.3, frames: 9);
        pad.Lift(1, InRightZone, 0.3);

        // The tap guard swallows up to 2% of the pad before scrolling starts.
        int total = pad.Output.Total(vertical: true);
        Assert.InRange(total, (0.4 - 0.02) * Gain, 0.4 * Gain);
        Assert.All(pad.Output.Scrolls, s => Assert.True(s.Amount > 0 && s.Vertical));
        Assert.Empty(pad.Inertia.Starts);
    }

    [Fact]
    public void Flick_launches_inertia_with_the_flick_boost()
    {
        using var pad = new Pad();

        // 0.06 pad per 11.2 ms frame = 5.36 pad/s, above the 3.75 flick threshold.
        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);

        var start = Assert.Single(pad.Inertia.Starts);
        double v = 0.06 / DefaultFrameDt;
        Assert.Equal(InertiaPhysics.LaunchVelocity(v, Gain, 3.75), start.Launch, precision: 6);
        Assert.Equal(v * Gain / 60 * InertiaPhysics.FlickBoost, start.Launch, precision: 6);
        Assert.True(start.Vertical);
        Assert.Equal(0.08, start.Decel, precision: 10);
    }

    [Fact]
    public void Ordinary_release_launches_inertia_with_the_normal_boost()
    {
        using var pad = new Pad();

        // 0.02 pad per frame = 1.79 pad/s, near the development pad's median drag speed.
        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.4, frames: 20);
        pad.Lift(1, InRightZone, 0.4);

        var start = Assert.Single(pad.Inertia.Starts);
        Assert.Equal(0.02 / DefaultFrameDt * Gain / 60 * InertiaPhysics.NormalBoost, start.Launch, precision: 6);
    }

    [Fact]
    public void Slide_in_from_the_centre_does_not_scroll()
    {
        using var pad = new Pad();

        pad.Drag(1, Centre, 0.5, InRightZone, 0.5, frames: 20);              // slide into the strip
        pad.Drag(1, InRightZone, 0.5, InRightZone, 0.1, frames: 20, touchDown: false);
        pad.Lift(1, InRightZone, 0.1);

        Assert.Empty(pad.Output.Scrolls);
        Assert.Equal(EdgeZone.Right, pad.Diagnostics[^2].Zone);   // it was in the zone…
        Assert.Equal("Idle", pad.Diagnostics[^2].State);          // …and correctly not entered
    }

    [Fact]
    public void Slide_in_from_the_centre_scrolls_when_the_began_in_zone_rule_is_off()
    {
        using var pad = new Pad(s => s.RequireBeginInZone = false);

        pad.Drag(1, Centre, 0.5, InRightZone, 0.5, frames: 20);
        pad.Drag(1, InRightZone, 0.5, InRightZone, 0.1, frames: 20, touchDown: false);

        Assert.True(pad.Output.Total(vertical: true) > 0);
    }

    [Fact]
    public void Leaving_the_zone_mid_scroll_keeps_scrolling_by_default()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.6, frames: 10);
        int inZone = pad.Output.Total(vertical: true);
        pad.Drag(1, InRightZone, 0.6, Centre, 0.3, frames: 20, touchDown: false);

        Assert.Equal(EdgeZone.None, pad.Last.Zone);
        Assert.Equal("Scrolling", pad.Last.State);
        Assert.True(pad.Output.Total(vertical: true) > inZone);
    }

    [Fact]
    public void Leaving_the_zone_stops_without_inertia_when_configured()
    {
        using var pad = new Pad(s => s.StopWhenLeavingZone = true);

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.5, frames: 10);
        Assert.Equal("Scrolling", pad.Last.State);

        pad.Drag(1, InRightZone, 0.5, Centre, 0.2, frames: 10, touchDown: false);
        Assert.Equal("Idle", pad.Last.State);

        int afterLeaving = pad.Output.Count;
        pad.Drag(1, Centre, 0.2, Centre, 0.1, frames: 5, touchDown: false);
        pad.Lift(1, Centre, 0.1);

        Assert.Equal(afterLeaving, pad.Output.Count);
        Assert.Empty(pad.Inertia.Starts);
    }

    [Fact]
    public void Leaving_the_zone_within_the_hysteresis_band_keeps_scrolling()
    {
        using var pad = new Pad(s => s.StopWhenLeavingZone = true);

        // Zone edge is 0.925 (12 mm); the exit line is 14 mm in, at 1 - 14/160 = 0.9125.
        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.6, frames: 10);
        pad.Drag(1, InRightZone, 0.6, 0.918, 0.4, frames: 10, touchDown: false);

        Assert.Equal(EdgeZone.None, pad.Last.Zone);
        Assert.Equal("Scrolling", pad.Last.State);
    }

    [Fact]
    public void Second_finger_mid_scroll_ends_the_scroll_and_cancels_the_coast_after_the_grace()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.5, frames: 10);
        int beforeSecondFinger = pad.Output.Count;

        // Two fingers for ~100 ms: the first frame exits (launching a coast), and once the
        // 50 ms phantom grace has passed the persisting second finger cancels it.
        for (int i = 0; i < 9; i++)
            pad.Frame(Finger(1, InRightZone, 0.5 - i * 0.01), Finger(2, Centre, 0.5));

        Assert.Equal(beforeSecondFinger, pad.Output.Count);
        Assert.Single(pad.Inertia.Starts);
        Assert.False(pad.Inertia.Active);
        Assert.False(pad.Output.Locked);
    }

    [Fact]
    public void Phantom_contacts_right_after_lift_do_not_cancel_the_coast()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);
        Assert.True(pad.Inertia.Active);
        int stopsAtLaunch = pad.Inertia.Stops;

        // Within ~20 ms of a lift, touchpads emit spurious multi-touch and single-contact frames.
        pad.FrameAfter(0.008, Finger(5, InRightZone, 0.2), Finger(6, 0.9, 0.25));
        pad.FrameAfter(0.008, Finger(7, InRightZone, 0.21));
        Assert.True(pad.Inertia.Active);
        Assert.Equal(stopsAtLaunch, pad.Inertia.Stops);

        // A real touch after the grace window does stop it.
        pad.FrameAfter(0.060, Finger(8, Centre, 0.5));
        Assert.False(pad.Inertia.Active);
    }

    // ── Regressions from the hardware recordings ─────────────────────────

    [Fact]
    public void Cursor_is_pinned_only_once_scrolling_starts()
    {
        using var pad = new Pad();

        // Resting and drifting inside the tap guard must leave the cursor free.
        pad.Frame(Finger(1, InRightZone, 0.500));
        pad.Hold(1, InRightZone, 0.505, frames: 20);
        Assert.Equal("EdgeContact", pad.Last.State);
        Assert.Equal(0, pad.Output.Locks);

        pad.Drag(1, InRightZone, 0.505, InRightZone, 0.3, frames: 10, touchDown: false);
        Assert.Equal(1, pad.Output.Locks);
        Assert.True(pad.Output.Locked);

        pad.Lift(1, InRightZone, 0.3);
        Assert.False(pad.Output.Locked);
    }

    [Fact]
    public void Cursor_is_never_pinned_when_locking_is_off()
    {
        using var pad = new Pad(s => s.LockCursorDuringScroll = false);

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.3, frames: 20);

        Assert.True(pad.Output.Count > 0);
        Assert.Equal(0, pad.Output.Locks);
    }

    [Fact]
    public void Batched_sub_millisecond_frames_do_not_spike_velocity()
    {
        using var pad = new Pad();

        // 1.8 pad/s, with scans delivered in pairs: each pair is two consecutive scans, so each
        // carries a full scan's movement, but they arrive 20 µs apart instead of 11.2 ms. About
        // 4.7% of frames on the development pad arrive this way. Δ/dt across a pair reads ~560×
        // too fast; the 8 ms window has to hold the reference across it.
        const double speed = 1.8;
        const double batchGap = 0.00002;
        double y = 0.8;
        pad.Frame(Finger(1, InRightZone, y));
        for (int i = 0; i < 10; i++)   // 20 scans: y 0.8 → 0.4
        {
            y -= speed * DefaultFrameDt;
            pad.FrameAfter(2 * DefaultFrameDt - batchGap, Finger(1, InRightZone, y));
            y -= speed * DefaultFrameDt;
            pad.FrameAfter(batchGap, Finger(1, InRightZone, y));
        }

        double peak = pad.Diagnostics.Max(d => Math.Abs(d.Velocity));
        Assert.InRange(peak, speed * 0.9, speed * 1.1);
    }

    [Fact]
    public void Movement_below_the_dead_zone_accumulates_instead_of_being_lost()
    {
        using var pad = new Pad();

        // 0.0003 per frame is under the 0.0005 dead zone; 300 frames is 0.09 of the pad.
        pad.Drag(1, InRightZone, 0.6, InRightZone, 0.6 - 0.0003 * 300, frames: 300);

        Assert.InRange(pad.Output.Total(vertical: true), (0.09 - 0.02) * Gain * 0.9, 0.09 * Gain);
    }

    [Fact]
    public void Resting_before_lift_does_not_coast()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);   // flick speed…
        pad.Hold(1, InRightZone, 0.2, frames: 6);                       // …then ~67 ms still
        pad.Lift(1, InRightZone, 0.2);

        Assert.Empty(pad.Inertia.Starts);
    }

    [Fact]
    public void Non_confident_contact_is_ignored_mid_scroll()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.6, frames: 10);
        int before = pad.Output.Count;

        // A palm the firmware flags as non-confident lands mid-pad while the finger keeps going.
        for (int i = 1; i <= 10; i++)
            pad.Frame(Finger(1, InRightZone, 0.6 - i * 0.02), Finger(2, 0.3, 0.7, confident: false));

        Assert.Equal("Scrolling", pad.Last.State);
        Assert.Equal(1, pad.Last.FingerCount);
        Assert.True(pad.Output.Count > before);
    }

    // ── Zones and settings ───────────────────────────────────────────────

    [Fact]
    public void Zone_width_is_physical_when_the_pad_reports_its_size()
    {
        using var pad = new Pad();   // 160 mm wide: 12 mm is the outer 7.5%

        Assert.Equal(EdgeZone.Right, pad.Frame(Finger(1, 0.926, 0.5)).Zone);
        Assert.Equal(EdgeZone.None,  pad.Frame(Finger(2, 0.920, 0.5)).Zone);
    }

    [Fact]
    public void Zone_width_falls_back_to_the_percentage_without_physical_size()
    {
        using var pad = new Pad(padWidthMm: 0, padHeightMm: 0);   // 10% fallback

        Assert.Equal(EdgeZone.Right, pad.Frame(Finger(1, 0.920, 0.5)).Zone);
        Assert.Equal(EdgeZone.None,  pad.Frame(Finger(2, 0.890, 0.5)).Zone);
    }

    [Fact]
    public void Zone_width_setting_resizes_the_strip()
    {
        using var pad = new Pad(s => s.EdgeZoneWidthMm = 20);   // 1 - 20/160 = 0.875

        Assert.Equal(EdgeZone.Right, pad.Frame(Finger(1, 0.880, 0.5)).Zone);
    }

    [Fact]
    public void Left_edge_scrolls_vertically_when_enabled()
    {
        using var pad = new Pad(s => s.LeftEdgeEnabled = true);

        pad.Drag(1, 0.03, 0.8, 0.03, 0.4, frames: 20);

        Assert.Equal(EdgeZone.Left, pad.Last.TrackedZone);
        Assert.True(pad.Output.Total(vertical: true) > 0);
        Assert.Equal(0, pad.Output.Total(vertical: false));
    }

    [Fact]
    public void Left_edge_is_off_by_default()
    {
        using var pad = new Pad();

        pad.Drag(1, 0.03, 0.8, 0.03, 0.4, frames: 20);

        Assert.Empty(pad.Output.Scrolls);
    }

    [Fact]
    public void Bottom_edge_scrolls_horizontally()
    {
        using var pad = new Pad(s => s.BottomEdgeEnabled = true);

        // 12 mm of a 114.9 mm-tall pad: the bottom strip starts at y = 0.8956.
        pad.Drag(1, 0.6, 0.97, 0.3, 0.97, frames: 20);

        Assert.Equal(EdgeZone.Bottom, pad.Last.TrackedZone);
        Assert.True(pad.Output.Total(vertical: false) > 0);
        Assert.Equal(0, pad.Output.Total(vertical: true));
    }

    [Fact]
    public void Right_edge_wins_in_the_bottom_right_corner()
    {
        using var pad = new Pad(s => s.BottomEdgeEnabled = true);

        Assert.Equal(EdgeZone.Right, pad.Frame(Finger(1, InRightZone, 0.97)).Zone);
    }

    [Fact]
    public void Invert_flips_both_the_drag_and_the_coast()
    {
        using var pad = new Pad(s => s.InvertVerticalScroll = true);

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);

        Assert.All(pad.Output.Scrolls, s => Assert.True(s.Amount < 0));
        Assert.True(Assert.Single(pad.Inertia.Starts).Launch < 0);
    }

    [Fact]
    public void Inertia_disabled_never_coasts()
    {
        using var pad = new Pad(s => s.InertiaEnabled = false);

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);

        Assert.True(pad.Output.Count > 0);
        Assert.Empty(pad.Inertia.Starts);
    }

    [Fact]
    public void Inertia_strength_sets_the_deceleration()
    {
        using var pad = new Pad(s => s.InertiaStrength = 1);

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);

        Assert.Equal(0.16, Assert.Single(pad.Inertia.Starts).Decel, precision: 10);
    }

    [Fact]
    public void Disabled_edge_scrolling_emits_nothing()
    {
        using var pad = new Pad(s => s.EdgeScrollEnabled = false);

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);

        Assert.Empty(pad.Output.Scrolls);
        Assert.Empty(pad.Inertia.Starts);
    }

    [Fact]
    public void Min_start_velocity_gates_slow_drags_only()
    {
        using var slow = new Pad(s => s.MinStartVelocity = 3.0);
        slow.Drag(1, InRightZone, 0.8, InRightZone, 0.4, frames: 20);   // 1.8 pad/s
        Assert.Empty(slow.Output.Scrolls);

        using var fast = new Pad(s => s.MinStartVelocity = 3.0);
        fast.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);   // 5.4 pad/s
        Assert.True(fast.Output.Count > 0);
    }

    [Fact]
    public void Any_touch_after_the_grace_stops_a_coast_even_outside_a_zone()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);
        Assert.True(pad.Inertia.Active);

        pad.FrameAfter(0.2, Finger(2, Centre, Centre));

        Assert.False(pad.Inertia.Active);
    }

    // ── Phase 5: outside events ──────────────────────────────────────────

    [Fact]
    public void Lost_contact_ends_the_scroll_unpins_and_does_not_coast()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);   // flick speed, then silence
        Assert.True(pad.Output.Locked);

        pad.Controller.OnContactLost();

        Assert.False(pad.Output.Locked);
        Assert.Empty(pad.Inertia.Starts);
        Assert.False(pad.Controller.IsScrollActive);
    }

    [Fact]
    public void After_a_lost_contact_the_next_touch_in_the_zone_scrolls()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.6, frames: 10);
        pad.Controller.OnContactLost();
        int before = pad.Output.Count;

        // The lift really was lost: a new contact ID appears, as in the recordings.
        pad.FrameAfter(0.6, Finger(9, InRightZone, 0.7));
        pad.Drag(9, InRightZone, 0.7, InRightZone, 0.4, frames: 10, touchDown: false);

        Assert.True(pad.Output.Count > before);
    }

    [Fact]
    public void After_a_lost_contact_a_paused_finger_resuming_in_the_zone_scrolls_again()
    {
        using var pad = new Pad();

        // The watchdog can't tell a long pause from a lost lift; if the same contact resumes,
        // it must be able to scroll again rather than be stuck as ineligible.
        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.6, frames: 10);
        pad.Controller.OnContactLost();
        int before = pad.Output.Count;

        pad.FrameAfter(0.3, Finger(1, InRightZone, 0.6));
        pad.Drag(1, InRightZone, 0.6, InRightZone, 0.3, frames: 10, touchDown: false);

        Assert.True(pad.Output.Count > before);
    }

    [Fact]
    public void Suspending_ends_a_scroll_and_a_coast_at_once()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        pad.Lift(1, InRightZone, 0.2);
        Assert.True(pad.Inertia.Active);

        pad.Controller.Suspended = true;   // e.g. an excluded app came to the front

        Assert.False(pad.Inertia.Active);
        Assert.False(pad.Output.Locked);
    }

    [Fact]
    public void While_suspended_frames_are_ignored_and_scrolling_resumes_after()
    {
        using var pad = new Pad();
        pad.Controller.Suspended = true;

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.4, frames: 20);
        pad.Lift(1, InRightZone, 0.4);
        Assert.Empty(pad.Output.Scrolls);

        pad.Controller.Suspended = false;
        pad.Drag(2, InRightZone, 0.8, InRightZone, 0.4, frames: 20);
        Assert.True(pad.Output.Count > 0);
    }

    [Fact]
    public void Reset_drops_the_scroll_and_the_coast()
    {
        using var pad = new Pad();

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.5, frames: 10);
        pad.Controller.Reset();

        Assert.False(pad.Output.Locked);
        Assert.False(pad.Inertia.Active);
        Assert.Equal("Idle", pad.Frame(Finger(1, Centre, 0.5)).State);
    }

    [Fact]
    public void IsScrollActive_covers_the_drag_and_the_coast()
    {
        using var pad = new Pad();
        Assert.False(pad.Controller.IsScrollActive);

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.2, frames: 10);
        Assert.True(pad.Controller.IsScrollActive);   // finger scrolling

        pad.Lift(1, InRightZone, 0.2);
        Assert.True(pad.Controller.IsScrollActive);   // coasting

        pad.Inertia.Stop();
        Assert.False(pad.Controller.IsScrollActive);
    }

    [Fact]
    public void Resting_in_the_zone_is_not_an_active_scroll()
    {
        using var pad = new Pad();

        pad.Frame(Finger(1, InRightZone, 0.5));
        pad.Hold(1, InRightZone, 0.5, frames: 10);

        Assert.Equal("EdgeContact", pad.Last.State);
        Assert.False(pad.Controller.IsScrollActive);   // a wheel hook must not drop input yet
    }

    [Fact]
    public void Left_edge_direction_is_independent_of_the_right_edge()
    {
        using var pad = new Pad(s => { s.LeftEdgeEnabled = true; s.InvertLeftEdgeScroll = true; });

        pad.Drag(1, InRightZone, 0.8, InRightZone, 0.4, frames: 20);
        pad.Lift(1, InRightZone, 0.4);
        int right = pad.Output.Total(vertical: true);

        pad.FrameAfter(0.5);
        pad.Drag(2, 0.03, 0.8, 0.03, 0.4, frames: 20);
        int left = pad.Output.Total(vertical: true) - right;

        Assert.True(right > 0);
        Assert.True(left < 0);
    }
}
