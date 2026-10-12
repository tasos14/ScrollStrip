using System.Windows;

namespace ScrollStrip;

public partial class App : System.Windows.Application
{
    private TrayIcon? _trayIcon;
    private TouchpadListener? _listener;
    private EdgeScrollController? _controller;
    private FrameRecorder? _recorder;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();
        _controller = new EdgeScrollController(settings);

        var recordPath = FrameRecorder.ResolvePath(e.Args);
        if (recordPath != null)
        {
            try
            {
                _recorder = new FrameRecorder(recordPath);
                _controller.FrameProcessed += _recorder.Record;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Could not start recording to {recordPath}:\n{ex.Message}",
                    "ScrollStrip", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        _listener = new TouchpadListener();
        _listener.FrameReceived += frame =>
        {
            // FrameReceived fires on the message-loop thread; controller is thread-safe for concurrent inertia timer
            _controller.OnFrame(frame);
        };
        _listener.Start();

        _trayIcon = new TrayIcon(settings, _recorder?.Path);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _listener?.Dispose();
        _controller?.Dispose();
        _recorder?.Dispose();
        base.OnExit(e);
    }
}
