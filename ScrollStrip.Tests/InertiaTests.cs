namespace ScrollStrip.Tests;

public class InertiaPhysicsTests
{
    [Theory]
    [InlineData(5, 0.08)]     // Scroll.app's default
    [InlineData(1, 0.16)]
    [InlineData(9, 0.04)]
    [InlineData(10, 0.0336)]
    public void Strength_maps_onto_deceleration(int strength, double decel)
    {
        Assert.Equal(decel, InertiaPhysics.DecelForStrength(strength), precision: 4);
    }

    [Fact]
    public void Launch_boosts_an_ordinary_release_three_times()
    {
        // 1.8 pad/s at gain 3000 is 90 wheel units per 1/60 s tick.
        Assert.Equal(270.0, InertiaPhysics.LaunchVelocity(1.8, 3000, 3.75), precision: 9);
    }

    [Fact]
    public void Launch_boosts_a_flick_eight_times()
    {
        Assert.Equal(1600.0, InertiaPhysics.LaunchVelocity(4.0, 3000, 3.75), precision: 9);
    }

    [Fact]
    public void Flick_threshold_is_exclusive()
    {
        double atThreshold = InertiaPhysics.LaunchVelocity(3.75, 3000, 3.75);
        Assert.Equal(3.75 * 50 * InertiaPhysics.NormalBoost, atThreshold, precision: 9);
    }

    [Fact]
    public void Launch_keeps_the_sign_of_the_finger_velocity()
    {
        Assert.Equal(-InertiaPhysics.LaunchVelocity(4.0, 3000, 3.75),
                     InertiaPhysics.LaunchVelocity(-4.0, 3000, 3.75), precision: 9);
    }

    [Fact]
    public void Coast_distance_matches_the_geometric_sum()
    {
        // Σ v(1-d)^k for k ≥ 1 is v(1-d)/d; the terminus clips a tail of about 2(1-d)/d.
        var physics = new InertiaPhysics(1000, 0.08, 2.0);
        long total = Run(physics, _ => 1.0 / 60);

        Assert.InRange(total, 1000 * 0.92 / 0.08 - 30, 1000 * 0.92 / 0.08);
    }

    [Fact]
    public void Coast_distance_does_not_depend_on_tick_jitter()
    {
        long steady = Run(new InertiaPhysics(1000, 0.08, 2.0), _ => 1.0 / 60);
        // Same average rate, alternating early and late ticks as an imprecise timer would.
        long jittery = Run(new InertiaPhysics(1000, 0.08, 2.0), i => i % 2 == 0 ? 1.0 / 120 : 1.0 / 40);

        Assert.InRange(jittery, steady * 0.97, steady * 1.03);
    }

    [Fact]
    public void Coast_stops_at_the_terminus()
    {
        var physics = new InertiaPhysics(100, 0.5, 2.0);
        Run(physics, _ => 1.0 / 60);

        Assert.True(physics.IsDone);
        Assert.True(Math.Abs(physics.Velocity) <= 2.0);
        Assert.Equal(0, physics.Step(1.0 / 60));
    }

    [Fact]
    public void Non_positive_dt_emits_nothing_and_does_not_decay()
    {
        var physics = new InertiaPhysics(100, 0.08, 2.0);

        Assert.Equal(0, physics.Step(0));
        Assert.Equal(0, physics.Step(-0.01));
        Assert.Equal(100, physics.Velocity);
    }

    [Fact]
    public void Fractions_carry_over_between_steps()
    {
        // 3 → 2.97 → … at 1% decel: every step is fractional, but none of it is lost.
        var physics = new InertiaPhysics(3.0, 0.01, 2.0);
        double continuous = 0, v = 3.0;
        long emitted = 0;
        for (int i = 0; i < 20; i++)
        {
            v *= 0.99;
            continuous += v;
            emitted += physics.Step(1.0 / 60);
        }

        Assert.InRange(emitted, Math.Floor(continuous) - 1, Math.Floor(continuous));
    }

    [Fact]
    public void Negative_launch_emits_negative_amounts()
    {
        var physics = new InertiaPhysics(-500, 0.08, 2.0);
        var amounts = new List<int>();
        while (!physics.IsDone) amounts.Add(physics.Step(1.0 / 60));

        Assert.All(amounts, a => Assert.True(a <= 0));
        Assert.True(amounts.Sum() < 0);
    }

    private static long Run(InertiaPhysics physics, Func<int, double> dt)
    {
        long total = 0;
        for (int i = 0; !physics.IsDone && i < 100_000; i++) total += physics.Step(dt(i));
        return total;
    }
}

/// <summary>These run the real timer thread, so they wait in wall-clock time.</summary>
public class InertiaScrollerTests
{
    private sealed class Sink
    {
        private readonly object _lock = new();
        private readonly List<(int Amount, bool Vertical)> _posts = new();

        public void Post(int amount, bool vertical) { lock (_lock) _posts.Add((amount, vertical)); }
        public List<(int Amount, bool Vertical)> Posts { get { lock (_lock) return new(_posts); } }
    }

    [Fact]
    public void Coasts_on_its_own_thread_and_stops_by_itself()
    {
        var sink = new Sink();
        using var scroller = new InertiaScroller(sink.Post);

        // Fast decay so the coast ends in ~0.4 s: 100 → 2 at 0.3 per tick is ~11 ticks.
        scroller.Start(100, vertical: false, decel: 0.3, terminus: 2.0);
        WaitUntilQuiet(sink, TimeSpan.FromSeconds(3));

        var posts = sink.Posts;
        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.False(p.Vertical));
        // Geometric sum 100 × 0.7 / 0.3 ≈ 233; real ticks jitter, so allow slack.
        Assert.InRange(posts.Sum(p => p.Amount), 233 * 0.8, 233 * 1.1);
    }

    [Fact]
    public void Posts_nothing_after_Stop_returns()
    {
        var sink = new Sink();
        using var scroller = new InertiaScroller(sink.Post);

        scroller.Start(50, vertical: true, decel: 0.001, terminus: 2.0);   // effectively endless
        Thread.Sleep(100);
        scroller.Stop();
        int atStop = sink.Posts.Count;
        Thread.Sleep(150);

        Assert.True(atStop > 0);
        Assert.Equal(atStop, sink.Posts.Count);
    }

    private static void WaitUntilQuiet(Sink sink, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int last = -1;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            int now = sink.Posts.Count;
            if (now > 0 && now == last) return;
            last = now;
        }
    }
}
