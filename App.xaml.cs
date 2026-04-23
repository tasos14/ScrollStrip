namespace ScrollStrip;

public partial class App : Microsoft.UI.Xaml.Application
{
    private TrayIcon? _trayIcon;
    private TouchpadListener? _listener;
    private EdgeScrollController? _controller;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var settings = AppSettings.Load();
        _controller = new EdgeScrollController(settings);

        _listener = new TouchpadListener();
        _listener.ContactsReceived += (contacts, count) => _controller.OnContacts(contacts, count);
        _listener.Start();

        _trayIcon = new TrayIcon(settings);
    }

    public void Shutdown()
    {
        _trayIcon?.Dispose();
        _listener?.Dispose();
        _controller?.Dispose();
        Exit();
    }
}
