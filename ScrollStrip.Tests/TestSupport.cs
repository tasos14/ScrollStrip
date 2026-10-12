namespace ScrollStrip.Tests;

/// <summary>Records wheel events and cursor pinning instead of posting real input.</summary>
internal sealed class RecordingOutput : IScrollOutput
{
    private readonly object _lock = new();
    private readonly List<(int Amount, bool Vertical)> _scrolls = new();

    public int Locks { get; private set; }
    public int Unlocks { get; private set; }
    public bool Locked { get; private set; }

    public void Scroll(int wheelAmount, bool vertical)
    {
        lock (_lock) _scrolls.Add((wheelAmount, vertical));
    }

    public bool LockCursor() { Locks++; Locked = true; return true; }
    public void UnlockCursor() { Unlocks++; Locked = false; }

    public List<(int Amount, bool Vertical)> Scrolls
    {
        get { lock (_lock) return new(_scrolls); }
    }

    public int Count => Scrolls.Count;
    public int Total(bool vertical) => Scrolls.Where(s => s.Vertical == vertical).Sum(s => s.Amount);
}

/// <summary>Records coast starts and stops instead of running a timer thread.</summary>
internal sealed class RecordingInertia : IInertia
{
    public List<(double Launch, bool Vertical, double Decel, double Terminus)> Starts { get; } = new();
    public int Stops { get; private set; }
    public bool Active { get; private set; }

    public void Start(double launchPerTick, bool vertical, double decel, double terminus)
    {
        Starts.Add((launchPerTick, vertical, decel, terminus));
        Active = true;
    }

    public void Stop() { Stops++; Active = false; }
    public void Dispose() { }
}

/// <summary>
/// Drives an <see cref="EdgeScrollController"/> with synthetic frames. Defaults mirror the
/// development pad: 160 × 114.9 mm at ~89 Hz.
/// </summary>
internal sealed class Pad : IDisposable
{
    public const double DefaultFrameDt = 0.0112;

    public AppSettings Settings { get; } = new();   // in-memory: never writes the user's settings
    public RecordingOutput Output { get; } = new();
    public RecordingInertia Inertia { get; } = new();
    public EdgeScrollController Controller { get; }
    public List<EdgeScrollDiagnostics> Diagnostics { get; } = new();

    public double Time { get; private set; } = 1.0;
    public double PadWidthMm { get; }
    public double PadHeightMm { get; }

    /// <summary>Right-edge zone starts at 1 - 12/160 = 0.925 with the default 12 mm width.</summary>
    public const double InRightZone = 0.97;
    public const double Centre = 0.5;

    public Pad(Action<AppSettings>? configure = null, double padWidthMm = 160.0, double padHeightMm = 114.9)
    {
        configure?.Invoke(Settings);
        PadWidthMm  = padWidthMm;
        PadHeightMm = padHeightMm;
        Controller  = new EdgeScrollController(Settings, Output, Inertia);
        Controller.FrameProcessed += (_, d) => Diagnostics.Add(d);
    }

    public EdgeScrollDiagnostics Last => Diagnostics[^1];

    public static TouchContact Finger(int id, double x, double y, bool confident = true) =>
        new(id, x, y, TipDown: true, Confident: confident);

    public static TouchContact Up(int id, double x, double y) =>
        new(id, x, y, TipDown: false, Confident: true);

    /// <summary>Delivers one frame <paramref name="dt"/> seconds after the previous one.</summary>
    public EdgeScrollDiagnostics FrameAfter(double dt, params TouchContact[] contacts)
    {
        Time += dt;
        Controller.OnFrame(new TouchFrame(contacts, contacts.Length, Time,
            FrameClock.WallClock, 0, PadWidthMm, PadHeightMm));
        return Last;
    }

    public EdgeScrollDiagnostics Frame(params TouchContact[] contacts) => FrameAfter(DefaultFrameDt, contacts);

    /// <summary>The lift frame a PTP sends: the contact reported once more with Tip Switch up.</summary>
    public EdgeScrollDiagnostics Lift(int id, double x, double y) => Frame(Up(id, x, y));

    /// <summary>Holds a contact still for <paramref name="frames"/> frames.</summary>
    public void Hold(int id, double x, double y, int frames)
    {
        for (int i = 0; i < frames; i++) Frame(Finger(id, x, y));
    }

    /// <summary>Moves a contact in a straight line over <paramref name="frames"/> frames, after a touch-down frame at the start.</summary>
    public void Drag(int id, double x0, double y0, double x1, double y1, int frames, bool touchDown = true)
    {
        if (touchDown) Frame(Finger(id, x0, y0));
        for (int i = 1; i <= frames; i++)
        {
            double f = (double)i / frames;
            Frame(Finger(id, x0 + (x1 - x0) * f, y0 + (y1 - y0) * f));
        }
    }

    public void Dispose() => Controller.Dispose();
}
