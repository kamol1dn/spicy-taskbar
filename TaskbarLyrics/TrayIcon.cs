using System.Drawing;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace TaskbarLyrics;

/// <summary>Minimal tray icon: placement toggle, open log, clear cache, exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayIcon(OverlayWindow overlay, VisualizerWindow viz, Application app)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Toggle placement (taskbar/above)", null, (_, _) => overlay.Dispatcher.BeginInvoke(overlay.TogglePlacement));
        menu.Items.Add(BuildVisualizerMenu(viz));
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

    /// <summary>"Visualizer" submenu: on/off, randomize-on-track, and a radio list of presets.</summary>
    private static System.Windows.Forms.ToolStripMenuItem BuildVisualizerMenu(VisualizerWindow viz)
    {
        var root = new System.Windows.Forms.ToolStripMenuItem("Visualizer");

        var enabled = new System.Windows.Forms.ToolStripMenuItem("Enabled", null,
            (_, _) => viz.Dispatcher.BeginInvoke(() => viz.SetEnabled(!viz.Enabled)));
        var randomize = new System.Windows.Forms.ToolStripMenuItem("Randomize preset on song change", null,
            (_, _) => viz.Dispatcher.BeginInvoke(() => viz.SetRandomize(!viz.Randomize)));

        root.DropDownItems.Add(enabled);
        root.DropDownItems.Add(randomize);
        root.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());

        var presetItems = new List<(string Id, System.Windows.Forms.ToolStripMenuItem Item)>();
        foreach (var preset in VisualizerPresets.All)
        {
            var id = preset.Id;
            var item = new System.Windows.Forms.ToolStripMenuItem(preset.Name, null,
                (_, _) => viz.Dispatcher.BeginInvoke(() => viz.SetPreset(id)));
            presetItems.Add((id, item));
            root.DropDownItems.Add(item);
        }

        // Reflect live state (incl. randomize-driven preset changes) each time it opens.
        root.DropDownOpening += (_, _) =>
        {
            enabled.Checked = viz.Enabled;
            randomize.Checked = viz.Randomize;
            foreach (var (id, item) in presetItems)
                item.Checked = viz.Preset.Id == id;
        };

        return root;
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
