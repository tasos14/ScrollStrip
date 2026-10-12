using System.Diagnostics;

namespace ScrollStrip;

public partial class SettingsWindow : System.Windows.Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DataContext = settings;
    }

    private void OnZonePresetDefault(object sender, System.Windows.RoutedEventArgs e) =>
        _settings.EdgeZoneWidthMm = 12.0;

    private void OnZonePresetDeviceEdge(object sender, System.Windows.RoutedEventArgs e) =>
        _settings.EdgeZoneWidthMm = 1.8;

    /// <summary>Offers the apps that currently have a window, refreshed each time the list opens.</summary>
    private void OnAppPickerOpened(object? sender, EventArgs e)
    {
        string typed = AppPicker.Text;
        AppPicker.ItemsSource = RunningWindowedApps()
            .Where(n => !_settings.IsExcluded(n))
            .ToList();
        AppPicker.Text = typed;
    }

    private void OnAddExcluded(object sender, System.Windows.RoutedEventArgs e)
    {
        string name = AppSettings.NormalizeAppName(AppPicker.Text ?? "");
        if (name.Length == 0 || _settings.IsExcluded(name)) return;

        _settings.ExcludedApps = [.. _settings.ExcludedApps, name];
        AppPicker.Text = "";
    }

    private void OnRemoveExcluded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (ExcludedList.SelectedItem is not string selected) return;
        _settings.ExcludedApps = _settings.ExcludedApps
            .Where(n => !string.Equals(n, selected, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static IEnumerable<string> RunningWindowedApps()
    {
        int self = Environment.ProcessId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id != self && p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName);
                }
                catch
                {
                    // exited or inaccessible: skip
                }
            }
        }
        return names;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
