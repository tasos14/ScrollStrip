using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ScrollStrip;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        Root.DataContext = settings;

        // Hide to tray on close instead of destroying the window
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };

        // Window size and position
        AppWindow.Resize(new SizeInt32(400, 760));
        CenterOnScreen();

        // Make non-resizable
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
        }

        // Mica backdrop on Win11+; WinUI 3 silently ignores it on unsupported systems
        SystemBackdrop = new MicaBackdrop();
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var x = (area.WorkArea.Width - 400) / 2;
        var y = (area.WorkArea.Height - 760) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }
}
