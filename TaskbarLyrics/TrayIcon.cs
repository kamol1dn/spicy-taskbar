using System.Drawing;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace TaskbarLyrics;

/// <summary>Minimal tray icon: placement toggle, open log, clear cache, exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayIcon(OverlayWindow overlay, Application app)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Toggle placement (taskbar/above)", null, (_, _) => overlay.Dispatcher.BeginInvoke(overlay.TogglePlacement));
        menu.Items.Add("Open log", null, (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start("notepad.exe", Path.Combine(Config.Dir, "log.txt"));
            }
            catch { }
        });
        menu.Items.Add("Clear lyrics cache", null, (_, _) =>
        {
            try
            {
                var dir = Path.Combine(Config.Dir, "cache");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
            catch { }
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Dispatcher.BeginInvoke(() => app.Shutdown()));

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Taskbar Lyrics",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var f = new Font("Segoe UI Symbol", 11, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("♪", f, Brushes.White, -1, 0);
        }
        var h = bmp.GetHicon();
        return Icon.FromHandle(h);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
