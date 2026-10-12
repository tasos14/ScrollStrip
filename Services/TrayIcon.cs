using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScrollStrip;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private readonly Icon _generatedIcon;

    public TrayIcon(AppSettings settings, string? recordPath = null)
    {
        _settings = settings;

        var menu = new ContextMenuStrip();
        if (recordPath != null)
        {
            var recordItem = new ToolStripMenuItem("Show recording…");
            recordItem.Click += (_, _) => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{recordPath}\""));
            menu.Items.Add(recordItem);
            menu.Items.Add(new ToolStripSeparator());
        }
        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => OpenSettings();
        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        _generatedIcon = CreateTrayIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = recordPath != null ? "ScrollStrip (recording)" : "ScrollStrip",
            Icon = _generatedIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();
    }

    private static Icon CreateTrayIcon()
    {
        const int sz = 32;
        using var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var rect = new RectangleF(1.5f, 2f, sz - 3f, sz - 4.5f);
        using var path = RoundedPath(rect, 4f);
        using var fill = new SolidBrush(Color.FromArgb(248, 248, 248));
        using var border = new Pen(Color.FromArgb(160, 160, 160), 2f);
        g.FillPath(fill, path);
        g.DrawPath(border, path);

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        NativeMethods.DestroyIcon(hIcon);
        return icon;
    }

    private static GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void OpenSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_settings);
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _generatedIcon.Dispose();
    }
}
