using System.ComponentModel;
using System.Windows;

namespace ScrollStrip;

public partial class App : System.Windows.Application
{
    private AppSettings? _settings;
    private TrayIcon? _trayIcon;
    private TouchpadListener? _listener;
    private EdgeScrollController? _controller;
    private FrameRecorder? _recorder;
    private ForegroundAppWatcher? _foreground;
    private NativeWheelSuppressor? _wheelSuppressor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = _settings = AppSettings.Load();
        var controller = _controller = new EdgeScrollController(settings);

        var recordPath = FrameRecorder.ResolvePath(e.Args);
        if (recordPath != null)
        {
            try
            {
                _recorder = new FrameRecorder(recordPath);
                controller.FrameProcessed += _recorder.Record;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not start recording to {recordPath}:\n{ex.Message}",
                    "ScrollStrip", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Everything below runs on this (UI) thread: the listener's window, the foreground hook and
        // the settings window all share its message loop, so the controller is single-threaded
        // apart from the inertia thread and the wheel hook, which use its lock-free members.
        _listener = new TouchpadListener();
        _listener.FrameReceived += controller.OnFrame;
        _listener.EventOccurred += ev =>
        {
            _recorder?.RecordEvent(ev);
            if (ev == ListenerEvent.ContactLost) controller.OnContactLost();
            else if (ev is ListenerEvent.Suspending or ListenerEvent.Resumed) controller.Reset();
        };
        _listener.Start();

        _foreground = new ForegroundAppWatcher();
        _foreground.ForegroundChanged += name => controller.Suspended = settings.IsExcluded(name);
        controller.Suspended = settings.IsExcluded(_foreground.Current);

        _wheelSuppressor = new NativeWheelSuppressor(() => controller.IsScrollActive);
        if (settings.SuppressNativeWheel) _wheelSuppressor.Start();

        settings.PropertyChanged += OnSettingChanged;

        _trayIcon = new TrayIcon(settings, _recorder?.Path);
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_settings == null || _controller == null) return;

        switch (e.PropertyName)
        {
            case nameof(AppSettings.ExcludedApps):
                _controller.Suspended = _settings.IsExcluded(_foreground?.Current);
                break;

            case nameof(AppSettings.SuppressNativeWheel):
                if (_settings.SuppressNativeWheel) _wheelSuppressor?.Start();
                else _wheelSuppressor?.Stop();
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _wheelSuppressor?.Dispose();
        _foreground?.Dispose();
        _listener?.Dispose();
        _controller?.Dispose();
        _recorder?.Dispose();
        base.OnExit(e);
    }
}
