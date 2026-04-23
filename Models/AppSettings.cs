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
    private int _rightEdgeWidthPct = 10;
    private bool _bottomEdgeEnabled = false;
    private int _bottomEdgeHeightPct = 10;
    private double _verticalScrollSpeedMultiplier = 1.5;
    private double _horizontalScrollSpeedMultiplier = 1.5;
    private bool _lockCursorDuringScroll = true;
    private bool _runAtStartup = false;
    private bool _invertVerticalScroll = false;
    private bool _invertHorizontalScroll = false;
    private bool _inertiaEnabled = true;
    private int _inertiaStrength = 5;

    public bool EdgeScrollEnabled                  { get => _edgeScrollEnabled;                set => Set(ref _edgeScrollEnabled, value); }
    public bool RightEdgeEnabled                   { get => _rightEdgeEnabled;                 set => Set(ref _rightEdgeEnabled, value); }
    public int  RightEdgeWidthPct                  { get => _rightEdgeWidthPct;                set => Set(ref _rightEdgeWidthPct, Math.Clamp(value, 5, 30)); }
    public bool BottomEdgeEnabled                  { get => _bottomEdgeEnabled;                set => Set(ref _bottomEdgeEnabled, value); }
    public int  BottomEdgeHeightPct                { get => _bottomEdgeHeightPct;              set => Set(ref _bottomEdgeHeightPct, Math.Clamp(value, 5, 30)); }
    public double VerticalScrollSpeedMultiplier    { get => _verticalScrollSpeedMultiplier;    set => Set(ref _verticalScrollSpeedMultiplier, Math.Clamp(value, 0.5, 5.0)); }
    public double HorizontalScrollSpeedMultiplier  { get => _horizontalScrollSpeedMultiplier;  set => Set(ref _horizontalScrollSpeedMultiplier, Math.Clamp(value, 0.5, 5.0)); }
    public bool LockCursorDuringScroll             { get => _lockCursorDuringScroll;           set => Set(ref _lockCursorDuringScroll, value); }
    public bool RunAtStartup                       { get => _runAtStartup;                     set { Set(ref _runAtStartup, value); ApplyStartup(value); } }
    public bool InvertVerticalScroll               { get => _invertVerticalScroll;             set => Set(ref _invertVerticalScroll, value); }
    public bool InvertHorizontalScroll             { get => _invertHorizontalScroll;           set => Set(ref _invertHorizontalScroll, value); }
    public bool InertiaEnabled                     { get => _inertiaEnabled;                   set => Set(ref _inertiaEnabled, value); }
    public int  InertiaStrength                    { get => _inertiaStrength;                  set => Set(ref _inertiaStrength, Math.Clamp(value, 1, 10)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        Save();
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
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
