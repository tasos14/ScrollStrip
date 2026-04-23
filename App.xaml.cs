using System.Windows;

namespace ScrollStrip;

public partial class App : System.Windows.Application
{
    private TrayIcon? _trayIcon;
    private TouchpadListener? _listener;
    private EdgeScrollController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();
        _controller = new EdgeScrollController(settings);

        _listener = new TouchpadListener();
        _listener.ContactsReceived += (contacts, count) =>
        {
            // ContactsReceived fires on the message-loop thread; controller is thread-safe for concurrent inertia timer
            _controller.OnContacts(contacts, count);
        };
        _listener.Start();

        _trayIcon = new TrayIcon(settings);

    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _listener?.Dispose();
        _controller?.Dispose();
        base.OnExit(e);
    }
}
