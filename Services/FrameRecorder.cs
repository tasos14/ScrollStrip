using System.Globalization;
using System.IO;
using System.Text;

namespace ScrollStrip;

/// <summary>
/// --record mode: writes every touch frame to CSV for offline tuning.
/// One row per contact; frames with no contacts still get a row, with the contact columns empty.
/// </summary>
public sealed class FrameRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    internal static readonly string[] Columns =
    [
        "seq", "time_s", "dt_ms", "clock", "scan_time_raw", "reported_count", "finger_count",
        "contact_index", "contact_id", "norm_x", "norm_y", "tip", "confident",
        "pad_w_mm", "pad_h_mm", "zone", "tracked_zone", "state", "velocity", "emitted_delta", "inertia_launch",
    ];
    private const string EmptyContactColumns = ",,,,,";
    private long _seq;
    private double _lastTime = double.NaN;

    public string Path { get; }

    public FrameRecorder(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _writer = new StreamWriter(Path, append: false, Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine(string.Join(",", Columns));
    }

    public void Record(TouchFrame frame, EdgeScrollDiagnostics diag)
    {
        var inv = CultureInfo.InvariantCulture;

        lock (_lock)
        {
            long seq = _seq++;
            double dtMs = double.IsNaN(_lastTime) ? 0 : (frame.TimestampSec - _lastTime) * 1000.0;
            _lastTime = frame.TimestampSec;

            string head = string.Create(inv, $"{seq},{frame.TimestampSec:F4},{dtMs:F2}," +
                                             $"{frame.Clock},{frame.ScanTimeRaw},{frame.ReportedContactCount},{diag.FingerCount}");
            string tail = string.Create(inv, $"{frame.PadWidthMm:F1},{frame.PadHeightMm:F1}," +
                                             $"{diag.Zone},{diag.TrackedZone},{diag.State},{diag.Velocity:F4},{diag.EmittedDelta},{diag.InertiaLaunch:F1}");

            if (frame.Contacts.Count == 0)
            {
                // Six empty contact columns: index, id, x, y, tip, confident.
                _writer.WriteLine($"{head},{EmptyContactColumns},{tail}");
                return;
            }

            for (int i = 0; i < frame.Contacts.Count; i++)
            {
                var c = frame.Contacts[i];
                _writer.WriteLine(string.Create(inv,
                    $"{head},{i},{c.ContactId},{c.NormX:F5},{c.NormY:F5}," +
                    $"{(c.TipDown ? 1 : 0)},{(c.Confident ? 1 : 0)},{tail}"));
            }
        }
    }

    /// <summary>
    /// Writes a non-frame event — a lost contact, sleep, wake — as a row with only seq and the event
    /// name in <c>state</c>, so a recording shows when the watchdog and power handling fired.
    /// </summary>
    public void RecordEvent(ListenerEvent e)
    {
        lock (_lock)
        {
            var row = new string[Columns.Length];
            row[0] = (_seq++).ToString(CultureInfo.InvariantCulture);
            row[Array.IndexOf(Columns, "state")] = "!" + e;
            _writer.WriteLine(string.Join(",", row));
        }
    }

    /// <summary>Resolves --record / --record=PATH / --record PATH; null when the flag is absent.</summary>
    public static string? ResolvePath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--record=", StringComparison.OrdinalIgnoreCase))
                return a["--record=".Length..];

            if (!a.Equals("--record", StringComparison.OrdinalIgnoreCase)) continue;

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                return args[i + 1];

            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScrollStrip",
                $"record-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        }
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
