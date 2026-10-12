using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ScrollStrip;

public class AppSettings : INotifyPropertyChanged
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScrollStrip", "settings.json");

    private bool _edgeScrollEnabled = true;
    private bool _rightEdgeEnabled = true;
    private bool _leftEdgeEnabled = false;
    private bool _bottomEdgeEnabled = false;
    private double _edgeZoneWidthMm = 12.0;
    private bool _requireBeginInZone = true;
    private bool _stopWhenLeavingZone = false;
    private double _minStartVelocity = 0.0;
    // Percentage fallbacks, used only when the touchpad reports no physical dimensions.
    private int _rightEdgeWidthPct = 10;
    private int _leftEdgeWidthPct = 10;
    private int _bottomEdgeHeightPct = 10;
    private double _verticalScrollSpeedMultiplier = 1.5;
    private double _horizontalScrollSpeedMultiplier = 1.5;
    private bool _lockCursorDuringScroll = true;
    private bool _runAtStartup = false;
    private bool _invertVerticalScroll = false;
    private bool _invertHorizontalScroll = false;
    private bool _invertLeftEdgeScroll = false;
    private bool _suppressNativeWheel = false;
    private List<string> _excludedApps = new();
    private bool _inertiaEnabled = true;
    private int _inertiaStrength = 5;

    public bool EdgeScrollEnabled                  { get => _edgeScrollEnabled;                set => Set(ref _edgeScrollEnabled, value); }
    public bool RightEdgeEnabled                   { get => _rightEdgeEnabled;                 set => Set(ref _rightEdgeEnabled, value); }
    public bool LeftEdgeEnabled                    { get => _leftEdgeEnabled;                  set => Set(ref _leftEdgeEnabled, value); }
    public bool BottomEdgeEnabled                  { get => _bottomEdgeEnabled;                set => Set(ref _bottomEdgeEnabled, value); }
    /// <summary>Physical thickness of every edge zone. Scroll.app's default is 12.3 mm; 1.8 mm is its "device edge" mode.</summary>
    public double EdgeZoneWidthMm                  { get => _edgeZoneWidthMm;                  set => Set(ref _edgeZoneWidthMm, Math.Clamp(Math.Round(value, 1), 1.5, 30.0)); }
    /// <summary>Only a contact whose first frame lands in a zone may scroll; a finger sliding in from the centre may not.</summary>
    public bool RequireBeginInZone                 { get => _requireBeginInZone;               set => Set(ref _requireBeginInZone, value); }
    /// <summary>End the scroll, with no inertia, once the finger leaves the zone (plus hysteresis).</summary>
    public bool StopWhenLeavingZone                { get => _stopWhenLeavingZone;              set => Set(ref _stopWhenLeavingZone, value); }
    /// <summary>Speed a drag must reach to start scrolling, in pad lengths per second along the scroll axis. 0 = off.</summary>
    public double MinStartVelocity                 { get => _minStartVelocity;                 set => Set(ref _minStartVelocity, Math.Clamp(value, 0.0, 5.0)); }
    public int  RightEdgeWidthPct                  { get => _rightEdgeWidthPct;                set => Set(ref _rightEdgeWidthPct, Math.Clamp(value, 5, 30)); }
    public int  LeftEdgeWidthPct                   { get => _leftEdgeWidthPct;                 set => Set(ref _leftEdgeWidthPct, Math.Clamp(value, 5, 30)); }
    public int  BottomEdgeHeightPct                { get => _bottomEdgeHeightPct;              set => Set(ref _bottomEdgeHeightPct, Math.Clamp(value, 5, 30)); }
    public double VerticalScrollSpeedMultiplier    { get => _verticalScrollSpeedMultiplier;    set => Set(ref _verticalScrollSpeedMultiplier, Math.Clamp(value, 0.5, 5.0)); }
    public double HorizontalScrollSpeedMultiplier  { get => _horizontalScrollSpeedMultiplier;  set => Set(ref _horizontalScrollSpeedMultiplier, Math.Clamp(value, 0.5, 5.0)); }
    public bool LockCursorDuringScroll             { get => _lockCursorDuringScroll;           set => Set(ref _lockCursorDuringScroll, value); }
    public bool RunAtStartup                       { get => _runAtStartup;                     set { Set(ref _runAtStartup, value); if (_persist) ApplyStartup(value); } }
    public bool InvertVerticalScroll               { get => _invertVerticalScroll;             set => Set(ref _invertVerticalScroll, value); }
    public bool InvertHorizontalScroll             { get => _invertHorizontalScroll;           set => Set(ref _invertHorizontalScroll, value); }
    /// <summary>Direction of the left edge. The right edge uses <see cref="InvertVerticalScroll"/>.</summary>
    public bool InvertLeftEdgeScroll               { get => _invertLeftEdgeScroll;             set => Set(ref _invertLeftEdgeScroll, value); }
    /// <summary>Drop the touchpad driver's own wheel events while ScrollStrip is scrolling (low-level mouse hook).</summary>
    public bool SuppressNativeWheel                { get => _suppressNativeWheel;              set => Set(ref _suppressNativeWheel, value); }

    /// <summary>
    /// Process names (no ".exe", any case) in whose foreground windows edge scrolling is off.
    /// Replace the list to change it — mutating it in place would neither notify nor save.
    /// </summary>
    public List<string> ExcludedApps
    {
        get => _excludedApps;
        set => Set(ref _excludedApps, (value ?? new()).Select(NormalizeAppName).Where(n => n.Length > 0)
                                                     .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    public bool IsExcluded(string? processName) =>
        processName != null && _excludedApps.Contains(NormalizeAppName(processName), StringComparer.OrdinalIgnoreCase);

    public static string NormalizeAppName(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name;
    }
    public bool InertiaEnabled                     { get => _inertiaEnabled;                   set => Set(ref _inertiaEnabled, value); }
    public int  InertiaStrength                    { get => _inertiaStrength;                  set => Set(ref _inertiaStrength, Math.Clamp(value, 1, 10)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Only the instance Load() returns writes to disk and the registry. Deserialization and
    // instances built in tests stay in memory, so neither can overwrite the user's settings.
    private bool _persist;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        Save();
    }

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(SettingsPath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
        }
        catch { }

        settings ??= new AppSettings();
        settings._persist = true;
        // Re-register on every launch so the Run key follows the exe if it has moved.
        if (settings.RunAtStartup) ApplyStartup(true);
        return settings;
    }

    public void Save()
    {
        if (!_persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void ApplyStartup(bool enable)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "ScrollStrip";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key == null) return;
        if (enable)
            key.SetValue(valueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
