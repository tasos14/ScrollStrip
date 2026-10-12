namespace ScrollStrip.Tests;

public class FrameWatchdogTests
{
    private const double Frame = 0.0112;

    [Fact]
    public void A_resting_finger_that_keeps_reporting_never_stalls()
    {
        var dog = new FrameWatchdog();
        double t = 10;
        for (int i = 0; i < 500; i++)
        {
            dog.OnFrame(t, anyContactDown: true);
            t += Frame;
            Assert.False(dog.CheckStalled(t));
        }
    }

    [Theory]
    [InlineData(0.131)]   // same-ID pauses seen on the development pad, finger still down
    [InlineData(0.203)]
    [InlineData(0.235)]
    public void Observed_same_contact_pauses_are_tolerated(double pause)
    {
        var dog = new FrameWatchdog();
        dog.OnFrame(10, anyContactDown: true);

        Assert.False(dog.CheckStalled(10 + pause));
    }

    [Theory]
    [InlineData(0.359)]   // lost-lift gaps seen on the development pad
    [InlineData(0.619)]
    [InlineData(1.812)]
    public void Observed_lost_lifts_are_caught(double gap)
    {
        var dog = new FrameWatchdog();
        dog.OnFrame(10, anyContactDown: true);

        Assert.True(dog.CheckStalled(10 + gap));
    }

    [Fact]
    public void A_stall_is_reported_once()
    {
        var dog = new FrameWatchdog();
        dog.OnFrame(10, anyContactDown: true);

        Assert.True(dog.CheckStalled(11));
        Assert.False(dog.CheckStalled(12));
        Assert.False(dog.ContactDown);
    }

    [Fact]
    public void Silence_after_a_proper_lift_is_not_a_stall()
    {
        var dog = new FrameWatchdog();
        dog.OnFrame(10, anyContactDown: true);
        dog.OnFrame(10 + Frame, anyContactDown: false);   // the lift report arrived

        Assert.False(dog.CheckStalled(100));
    }

    [Fact]
    public void Reset_forgets_a_down_contact()
    {
        var dog = new FrameWatchdog();
        dog.OnFrame(10, anyContactDown: true);
        dog.Reset();

        Assert.False(dog.CheckStalled(100));
    }
}

public class NativeWheelSuppressorTests
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const uint Injected = 0x1;

    [Theory]
    [InlineData(WM_MOUSEWHEEL)]
    [InlineData(WM_MOUSEHWHEEL)]
    public void Drops_a_native_wheel_event_while_scrolling(int message)
    {
        Assert.True(NativeWheelSuppressor.ShouldSuppress(message, 0, () => true));
    }

    [Fact]
    public void Passes_wheel_events_when_not_scrolling()
    {
        Assert.False(NativeWheelSuppressor.ShouldSuppress(WM_MOUSEWHEEL, 0, () => false));
    }

    [Fact]
    public void Passes_injected_wheel_events_without_asking_whether_scrolling()
    {
        // Our own SendInput output is injected; it must pass, and it must not reach shared state.
        int asked = 0;
        bool suppressed = NativeWheelSuppressor.ShouldSuppress(WM_MOUSEWHEEL, Injected, () => { asked++; return true; });

        Assert.False(suppressed);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Never_touches_other_mouse_messages()
    {
        int asked = 0;
        Assert.False(NativeWheelSuppressor.ShouldSuppress(WM_MOUSEMOVE, 0, () => { asked++; return true; }));
        Assert.Equal(0, asked);
    }
}
