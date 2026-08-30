using System.Drawing;
using System.IO;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace TaskbarLyrics;

/// <summary>Tray icon: per-module position/appearance menus, open log, clear cache, exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Config cfg, OverlayWindow overlay, VisualizerWindow viz, AppNameWindow appName,
        Application app)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(BuildLyricsMenu(overlay));
        menu.Items.Add(BuildAppNameMenu(appName));
        menu.Items.Add(BuildVisualizerMenu(viz));
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Applies to every module's text — handy to flip when the wallpaper changes.
        var shadow = new Forms.ToolStripMenuItem("Text shadow", null, (_, _) =>
        {
            cfg.TextShadow = !cfg.TextShadow;
            cfg.Save();
            overlay.Dispatcher.BeginInvoke(overlay.RefreshAppearance);
            appName.Dispatcher.BeginInvoke(appName.RefreshAppearance);
        });
        menu.Items.Add(shadow);
        menu.Opening += (_, _) => shadow.Checked = cfg.TextShadow;

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
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Dispatcher.BeginInvoke(() => app.Shutdown()));

        _icon = new Forms.NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Taskbar Lyrics",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    /// <summary>
    /// The position controls a strip-shaped module exposes. Every getter/setter is a
    /// delegate so the lyrics and app-name modules can share one menu builder.
    /// </summary>
    private sealed record PositionModule(
        Dispatcher Dispatcher,
        Func<string> GetVPos, Action<string> SetVPos,
        Func<string> GetAlign, Action<string> SetAlign,
        Func<int> GetXOffset, Action<int> SetXOffset,
        Func<string> GetPlacement, Action TogglePlacement);

    /// <summary>
    /// Adds the shared position block (edge, alignment, offset, on-taskbar) to a module
    /// submenu. Returns an action that refreshes its check marks and labels.
    /// </summary>
    private static Action AddPositionItems(Forms.ToolStripMenuItem root, PositionModule m)
    {
        var edges = new List<(string Value, Forms.ToolStripMenuItem Item)>();
        foreach (var (value, label) in new[] { ("top", "Top"), ("bottom", "Bottom") })
        {
            var v = value;
            var item = new Forms.ToolStripMenuItem(label, null,
                (_, _) => m.Dispatcher.BeginInvoke(() => m.SetVPos(v)));
            edges.Add((v, item));
            root.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        var aligns = new List<(string Value, Forms.ToolStripMenuItem Item)>();
        foreach (var (value, label) in new[] { ("left", "Left"), ("center", "Middle"), ("right", "Right") })
        {
            var v = value;
            var item = new Forms.ToolStripMenuItem(label, null,
                (_, _) => m.Dispatcher.BeginInvoke(() => m.SetAlign(v)));
            aligns.Add((v, item));
            root.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        var offset = new Forms.ToolStripMenuItem("Edge offset...", null, (_, _) =>
        {
            if (NumberPrompt.Show("Edge offset",
                    "Distance in pixels from the left/right screen edge.\n(No effect while middle-aligned.)",
                    m.GetXOffset(), 0, 4000) is { } px)
                m.Dispatcher.BeginInvoke(() => m.SetXOffset(px));
        });
        root.DropDownItems.Add(offset);

        var onTaskbar = new Forms.ToolStripMenuItem("On the taskbar", null,
            (_, _) => m.Dispatcher.BeginInvoke(m.TogglePlacement));
        root.DropDownItems.Add(onTaskbar);

        return () =>
        {
            foreach (var (value, item) in edges) item.Checked = m.GetVPos() == value;
            foreach (var (value, item) in aligns) item.Checked = m.GetAlign() == value;
            offset.Text = $"Edge offset... ({m.GetXOffset()} px)";
            onTaskbar.Checked = m.GetPlacement() == "taskbar";
        };
    }

    /// <summary>"Lyrics position" submenu: screen edge, alignment, offset, taskbar/floating.</summary>
    private static Forms.ToolStripMenuItem BuildLyricsMenu(OverlayWindow overlay)
    {
        var root = new Forms.ToolStripMenuItem("Lyrics position");
        var refresh = AddPositionItems(root, new PositionModule(
            overlay.Dispatcher,
            () => overlay.VPos, overlay.SetVPos,
            () => overlay.Align, overlay.SetAlign,
            () => overlay.XOffset, overlay.SetXOffset,
            () => overlay.Placement, overlay.TogglePlacement));

        root.DropDownOpening += (_, _) => refresh();
        return root;
    }

    /// <summary>"Active app" submenu: on/off, the shared position block, and a font size.</summary>
    private static Forms.ToolStripMenuItem BuildAppNameMenu(AppNameWindow appName)
    {
        var root = new Forms.ToolStripMenuItem("Active app");

        var enabled = new Forms.ToolStripMenuItem("Enabled", null,
            (_, _) => appName.Dispatcher.BeginInvoke(() => appName.SetEnabled(!appName.Enabled)));
        root.DropDownItems.Add(enabled);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        var refresh = AddPositionItems(root, new PositionModule(
            appName.Dispatcher,
            () => appName.VPos, appName.SetVPos,
            () => appName.Align, appName.SetAlign,
            () => appName.XOffset, appName.SetXOffset,
            () => appName.Placement, appName.TogglePlacement));

        var fontSize = new Forms.ToolStripMenuItem("Font size...", null, (_, _) =>
        {
            if (NumberPrompt.Show("Font size", "Text size in pixels.",
                    (int)Math.Round(appName.FontPx), 8, 48) is { } px)
                appName.Dispatcher.BeginInvoke(() => appName.SetFontSize(px));
        });
        root.DropDownItems.Add(fontSize);

        root.DropDownOpening += (_, _) =>
        {
            enabled.Checked = appName.Enabled;
            fontSize.Text = $"Font size... ({(int)Math.Round(appName.FontPx)} px)";
            refresh();
        };
        return root;
    }

    /// <summary>"Visualizer" submenu: on/off, randomize-on-track, screen edge, preset list.</summary>
    private static Forms.ToolStripMenuItem BuildVisualizerMenu(VisualizerWindow viz)
    {
        var root = new Forms.ToolStripMenuItem("Visualizer");

        var enabled = new Forms.ToolStripMenuItem("Enabled", null,
            (_, _) => viz.Dispatcher.BeginInvoke(() => viz.SetEnabled(!viz.Enabled)));
        var randomize = new Forms.ToolStripMenuItem("Randomize preset on song change", null,
            (_, _) => viz.Dispatcher.BeginInvoke(() => viz.SetRandomize(!viz.Randomize)));

        root.DropDownItems.Add(enabled);
        root.DropDownItems.Add(randomize);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        var edgeItems = new List<(string Value, Forms.ToolStripMenuItem Item)>();
        foreach (var (value, label) in new[] { ("top", "Edge: top"), ("bottom", "Edge: bottom") })
        {
            var v = value;
            var item = new Forms.ToolStripMenuItem(label, null,
                (_, _) => viz.Dispatcher.BeginInvoke(() => viz.SetEdge(v)));
            edgeItems.Add((v, item));
            root.DropDownItems.Add(item);
        }
        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        var presetItems = new List<(string Id, Forms.ToolStripMenuItem Item)>();
        foreach (var preset in VisualizerPresets.All)
        {
            var id = preset.Id;
            var item = new Forms.ToolStripMenuItem(preset.Name, null,
                (_, _) => viz.Dispatcher.BeginInvoke(() => viz.SetPreset(id)));
            presetItems.Add((id, item));
            root.DropDownItems.Add(item);
        }

        // Reflect live state (incl. randomize-driven preset changes) each time it opens.
        root.DropDownOpening += (_, _) =>
        {
            enabled.Checked = viz.Enabled;
            randomize.Checked = viz.Randomize;
            foreach (var (value, item) in edgeItems)
                item.Checked = viz.Edge == value;
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

/// <summary>Tiny modal "type a number" dialog - WinForms has no built-in input box.</summary>
internal static class NumberPrompt
{
    /// <summary>Returns the entered value, or null if cancelled.</summary>
    public static int? Show(string title, string message, int current, int min, int max)
    {
        using var form = new Forms.Form
        {
            Text = title,
            FormBorderStyle = Forms.FormBorderStyle.FixedDialog,
            StartPosition = Forms.FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new System.Drawing.Size(300, 132),
            TopMost = true,
        };

        var label = new Forms.Label
        {
            Text = message,
            AutoSize = false,
            Bounds = new Rectangle(12, 12, 276, 44),
        };
        var input = new Forms.NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(current, min, max),
            Bounds = new Rectangle(12, 62, 100, 24),
        };
        var ok = new Forms.Button
        {
            Text = "OK",
            DialogResult = Forms.DialogResult.OK,
            Bounds = new Rectangle(132, 96, 75, 26),
        };
        var cancel = new Forms.Button
        {
            Text = "Cancel",
            DialogResult = Forms.DialogResult.Cancel,
            Bounds = new Rectangle(213, 96, 75, 26),
        };

        form.Controls.AddRange(new Forms.Control[] { label, input, ok, cancel });
        form.AcceptButton = ok;      // Enter commits
        form.CancelButton = cancel;  // Esc dismisses
        input.Select(0, input.Text.Length);

        return form.ShowDialog() == Forms.DialogResult.OK ? (int)input.Value : null;
    }
}
