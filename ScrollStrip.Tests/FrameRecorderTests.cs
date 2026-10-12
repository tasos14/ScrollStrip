using System.Globalization;
using System.IO;

namespace ScrollStrip.Tests;

public class FrameRecorderTests
{
    [Fact]
    public void No_flag_means_no_recording()
    {
        Assert.Null(FrameRecorder.ResolvePath(["--other"]));
        Assert.Null(FrameRecorder.ResolvePath([]));
    }

    [Fact]
    public void Equals_form_gives_the_path()
    {
        Assert.Equal(@"C:\tmp\a.csv", FrameRecorder.ResolvePath([@"--record=C:\tmp\a.csv"]));
    }

    [Fact]
    public void Separate_argument_gives_the_path()
    {
        Assert.Equal(@"C:\tmp\b.csv", FrameRecorder.ResolvePath(["--record", @"C:\tmp\b.csv"]));
    }

    [Theory]
    [InlineData("--record")]
    [InlineData("--RECORD")]
    public void Bare_flag_records_to_app_data(string flag)
    {
        string? path = FrameRecorder.ResolvePath([flag]);

        Assert.NotNull(path);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), path);
        Assert.Contains(@"ScrollStrip\record-", path);
        Assert.EndsWith(".csv", path);
    }

    [Fact]
    public void A_following_flag_is_not_taken_as_the_path()
    {
        string? path = FrameRecorder.ResolvePath(["--record", "--verbose"]);

        Assert.NotNull(path);
        Assert.EndsWith(".csv", path);
    }

    [Fact]
    public void Every_row_has_the_header_column_count_even_in_a_comma_decimal_culture()
    {
        // A short empty-contact row was a real bug; a German culture would also turn every
        // decimal point into a comma if formatting were not invariant.
        var saved = CultureInfo.CurrentCulture;
        string path = Path.Combine(Path.GetTempPath(), $"scrollstrip-test-{Guid.NewGuid():N}.csv");
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var diag = new EdgeScrollDiagnostics(1, EdgeZone.Right, EdgeZone.Right, "Scrolling", 1.2345, 42, 270.5);
            using (var rec = new FrameRecorder(path))
            {
                rec.Record(Frame(), diag);
                rec.Record(Frame(new TouchContact(1, 0.97, 0.5, true, true)), diag);
                rec.Record(Frame(new TouchContact(1, 0.97, 0.5, true, true),
                                 new TouchContact(2, 0.30, 0.7, true, false)), diag);
            }

            var lines = File.ReadAllLines(path);
            int columns = lines[0].Split(',').Length;
            Assert.Equal(1 + 1 + 1 + 2, lines.Length);   // header + 0-, 1- and 2-contact frames
            Assert.All(lines, l => Assert.Equal(columns, l.Split(',').Length));
            Assert.Contains("1.2345", lines[1]);
            Assert.Contains("270.5", lines[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            File.Delete(path);
        }
    }

    [Fact]
    public void Event_rows_have_the_header_column_count_and_name_the_event()
    {
        string path = Path.Combine(Path.GetTempPath(), $"scrollstrip-test-{Guid.NewGuid():N}.csv");
        try
        {
            var diag = new EdgeScrollDiagnostics(1, EdgeZone.Right, EdgeZone.Right, "Scrolling", 1.0, 1, 0);
            using (var rec = new FrameRecorder(path))
            {
                rec.Record(Frame(new TouchContact(1, 0.97, 0.5, true, true)), diag);
                rec.RecordEvent(ListenerEvent.ContactLost);
                rec.RecordEvent(ListenerEvent.Resumed);
            }

            var lines = File.ReadAllLines(path);
            int columns = lines[0].Split(',').Length;
            int state = Array.IndexOf(lines[0].Split(','), "state");

            Assert.All(lines, l => Assert.Equal(columns, l.Split(',').Length));
            Assert.Equal("!ContactLost", lines[2].Split(',')[state]);
            Assert.Equal("!Resumed", lines[3].Split(',')[state]);
            Assert.Equal(new[] { "0", "1", "2" }, lines.Skip(1).Select(l => l.Split(',')[0]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TouchFrame Frame(params TouchContact[] contacts) =>
        new(contacts, contacts.Length, 12.3456, FrameClock.WallClock, 0, 160.0, 114.9);
}
