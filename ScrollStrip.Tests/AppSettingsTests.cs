using System.IO;
using System.Text.Json;

namespace ScrollStrip.Tests;

public class AppSettingsTests
{
    private static readonly string RealSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollStrip", "settings.json");

    [Fact]
    public void Instances_not_from_Load_never_touch_the_users_settings_file()
    {
        var before = Snapshot();

        var s = new AppSettings
        {
            EdgeScrollEnabled = false,
            EdgeZoneWidthMm   = 3,
            InertiaStrength   = 9,
            LeftEdgeEnabled   = true,
        };
        s.Save();   // the guard lives in Save itself, so even an explicit call must not write
        var after = Snapshot();

        Assert.Equal(before.Exists, after.Exists);
        if (before.Exists) Assert.Equal(before.Written, after.Written);
    }

    [Fact]
    public void Zone_width_is_clamped_and_rounded()
    {
        var s = new AppSettings();

        s.EdgeZoneWidthMm = 100;   Assert.Equal(30.0, s.EdgeZoneWidthMm);
        s.EdgeZoneWidthMm = 0.2;   Assert.Equal(1.5, s.EdgeZoneWidthMm);
        s.EdgeZoneWidthMm = 12.34; Assert.Equal(12.3, s.EdgeZoneWidthMm);
    }

    [Fact]
    public void Inertia_strength_is_clamped()
    {
        var s = new AppSettings();

        s.InertiaStrength = 0;  Assert.Equal(1, s.InertiaStrength);
        s.InertiaStrength = 99; Assert.Equal(10, s.InertiaStrength);
    }

    [Fact]
    public void Pre_phase_2_settings_load_with_new_defaults_and_keep_their_percentages()
    {
        const string legacy = """
            { "EdgeScrollEnabled": true, "RightEdgeEnabled": true, "RightEdgeWidthPct": 15,
              "BottomEdgeEnabled": true, "BottomEdgeHeightPct": 20, "InertiaStrength": 7 }
            """;

        var s = JsonSerializer.Deserialize<AppSettings>(legacy)!;

        Assert.Equal(15, s.RightEdgeWidthPct);     // still the fallback for unitless pads
        Assert.Equal(20, s.BottomEdgeHeightPct);
        Assert.Equal(7, s.InertiaStrength);
        Assert.Equal(12.0, s.EdgeZoneWidthMm);     // new settings take their defaults
        Assert.True(s.RequireBeginInZone);
        Assert.False(s.StopWhenLeavingZone);
        Assert.False(s.LeftEdgeEnabled);
    }

    private static (bool Exists, DateTime Written) Snapshot() =>
        File.Exists(RealSettingsPath)
            ? (true, File.GetLastWriteTimeUtc(RealSettingsPath))
            : (false, default);
}
