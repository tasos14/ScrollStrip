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

    [Theory]
    [InlineData("mstsc", "mstsc")]
    [InlineData("MSTSC.EXE", "mstsc")]
    [InlineData("  vmware.exe ", "vmware")]
    [InlineData("Code", "code")]
    public void Exclusion_matching_ignores_case_and_the_exe_suffix(string listed, string foreground)
    {
        var s = new AppSettings { ExcludedApps = [listed] };

        Assert.True(s.IsExcluded(foreground));
        Assert.True(s.IsExcluded(foreground + ".exe"));
    }

    [Fact]
    public void Unlisted_and_unknown_apps_are_not_excluded()
    {
        var s = new AppSettings { ExcludedApps = ["mstsc"] };

        Assert.False(s.IsExcluded("notepad"));
        Assert.False(s.IsExcluded(null));
    }

    [Fact]
    public void Excluded_apps_are_normalized_and_deduplicated()
    {
        var s = new AppSettings { ExcludedApps = ["mstsc.exe", "MSTSC", " ", "", "code"] };

        Assert.Equal(new[] { "mstsc", "code" }, s.ExcludedApps);
    }

    [Fact]
    public void Excluded_apps_survive_a_save_and_load_round_trip()
    {
        var s = new AppSettings { ExcludedApps = ["mstsc", "vmware"], InvertLeftEdgeScroll = true, SuppressNativeWheel = true };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        Assert.Equal(new[] { "mstsc", "vmware" }, back.ExcludedApps);
        Assert.True(back.InvertLeftEdgeScroll);
        Assert.True(back.SuppressNativeWheel);
    }

    [Fact]
    public void Replacing_the_exclusion_list_notifies()
    {
        var s = new AppSettings();
        var changed = new List<string?>();
        s.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        s.ExcludedApps = ["mstsc"];

        Assert.Contains(nameof(AppSettings.ExcludedApps), changed);
    }

    private static (bool Exists, DateTime Written) Snapshot() =>
        File.Exists(RealSettingsPath)
            ? (true, File.GetLastWriteTimeUtc(RealSettingsPath))
            : (false, default);
}
