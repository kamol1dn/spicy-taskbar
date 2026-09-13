using System.Drawing;
using System.IO;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace TaskbarLyrics;

/// <summary>Tray icon: per-module position/appearance menus, wallpaper settings, open log, clear cache, exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Config cfg, OverlayWindow overlay, VisualizerWindow viz, AppNameWindow appName,
        WallpaperFeed feed, Application app)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(BuildLyricsMenu(overlay));
        menu.Items.Add(BuildAppNameMenu(appName));
        menu.Items.Add(BuildVisualizerMenu(viz));
        menu.Items.Add(BuildWallpaperMenu(cfg, feed));
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

        // Read at render time by the taskbar strip and the wallpaper feed, so a change lands at once.
        var timing = new Forms.ToolStripMenuItem("Lyrics timing...", null, (_, _) =>
        {
            if (NumberPrompt.Show("Lyrics timing",
                    "Shift in ms. Positive = lyrics earlier (if they lag the song), negative = later. Taskbar and wallpaper.",
                    cfg.GlobalOffsetMs, -5000, 5000) is not { } ms) return;
            cfg.GlobalOffsetMs = ms;
            cfg.Save();
        });
        menu.Items.Add(timing);
        menu.Opening += (_, _) =>
        {
            shadow.Checked = cfg.TextShadow;
            timing.Text = $"Lyrics timing... ({cfg.GlobalOffsetMs:+0;-0;0} ms)";
        };

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

    /// <summary>"Lyrics" submenu: on/off, hide-on-desktop, then edge, alignment, offset, taskbar/floating.</summary>
    private static Forms.ToolStripMenuItem BuildLyricsMenu(OverlayWindow overlay)
    {
        var root = new Forms.ToolStripMenuItem("Lyrics");

        var enabled = new Forms.ToolStripMenuItem("Enabled", null,
            (_, _) => overlay.Dispatcher.BeginInvoke(() => overlay.SetEnabled(!overlay.Enabled)));
        // With the lyrics wallpaper up, the desktop already shows the lyrics full-screen.
        var hideOnDesktop = new Forms.ToolStripMenuItem("Hide while the desktop is showing", null,
            (_, _) => overlay.Dispatcher.BeginInvoke(() => overlay.SetHideOnDesktop(!overlay.HideOnDesktop)));
        root.DropDownItems.Add(enabled);
        root.DropDownItems.Add(hideOnDesktop);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        var refresh = AddPositionItems(root, new PositionModule(
            overlay.Dispatcher,
            () => overlay.VPos, overlay.SetVPos,
            () => overlay.Align, overlay.SetAlign,
            () => overlay.XOffset, overlay.SetXOffset,
            () => overlay.Placement, overlay.TogglePlacement));

        root.DropDownOpening += (_, _) =>
        {
            enabled.Checked = overlay.Enabled;
            hideOnDesktop.Checked = overlay.HideOnDesktop;
            hideOnDesktop.Enabled = overlay.Enabled;
            refresh();
        };
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

    /// <summary>
    /// "Wallpaper" submenu: next image, image folder, and one look submenu per surface
    /// (desktop / lock screen). Changes are saved to config.json and pushed straight to
    /// every open wallpaper, which also remembers them for when the app isn't running.
    /// </summary>
    private static Forms.ToolStripMenuItem BuildWallpaperMenu(Config cfg, WallpaperFeed feed)
    {
        var root = new Forms.ToolStripMenuItem("Wallpaper");
        void Changed()
        {
            cfg.Save();
            feed.PushSettings();
        }

        root.DropDownItems.Add("Next wallpaper", null, (_, _) => feed.NextWallpaper());
        var folder = new Forms.ToolStripMenuItem("Folder...", null, (_, _) =>
        {
            using var dlg = new Forms.FolderBrowserDialog
            {
                Description = "Folder the wallpaper picks a random image from on every song change",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(cfg.WallpaperFolder) ? cfg.WallpaperFolder : "",
            };
            if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
            cfg.WallpaperFolder = dlg.SelectedPath;
            // Collections belong to the old folder.
            cfg.WallpaperDesktop.Collection = cfg.WallpaperLock.Collection = "all";
            Changed();
        });
        root.DropDownItems.Add(folder);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());
        root.DropDownItems.Add(BuildWallpaperSurfaceMenu("Desktop", cfg.WallpaperDesktop, feed, Changed));
        root.DropDownItems.Add(BuildWallpaperSurfaceMenu("Lock screen", cfg.WallpaperLock, feed, Changed));

        root.DropDownOpening += (_, _) => folder.Text = cfg.WallpaperFolder.Length > 0
            ? $"Folder... ({Path.GetFileName(Path.TrimEndingDirectorySeparator(cfg.WallpaperFolder))})"
            : "Folder... (none)";
        return root;
    }

    /// <summary>The look of one wallpaper surface: background, collection, layout, sizes, toggles.</summary>
    private static Forms.ToolStripMenuItem BuildWallpaperSurfaceMenu(string label, WallpaperSettings s,
        WallpaperFeed feed, Action changed)
    {
        var root = new Forms.ToolStripMenuItem(label);
        var refresh = new List<Action>();

        // A submenu of mutually exclusive values, rebuilt on open so its options
        // (the folder's collections) and check mark are always current.
        void Choice<T>(string text, Func<(T Value, string Label)[]> options, Func<T> get, Action<T> set)
        {
            var sub = new Forms.ToolStripMenuItem(text);
            void Rebuild()
            {
                sub.DropDownItems.Clear();
                foreach (var (value, optLabel) in options())
                {
                    var v = value;
                    sub.DropDownItems.Add(new Forms.ToolStripMenuItem(optLabel, null, (_, _) => { set(v); changed(); })
                    {
                        Checked = EqualityComparer<T>.Default.Equals(get(), v),
                    });
                }
            }
            Rebuild();
            sub.DropDownOpening += (_, _) => Rebuild();
            root.DropDownItems.Add(sub);
        }

        void Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var item = new Forms.ToolStripMenuItem(text, null, (_, _) => { set(!get()); changed(); });
            refresh.Add(() => item.Checked = get());
            root.DropDownItems.Add(item);
        }

        static string Title(string c) => c.Length == 0 ? c : char.ToUpperInvariant(c[0]) + c[1..];

        Choice("Background",
            () => new[] { ("wallpapers", "My wallpapers (new one each song)"), ("dynamic", "Album gradient") },
            () => s.Background, v => s.Background = v);
        Choice("Collection",
            () => new[] { ("all", "All") }.Concat(feed.Categories().Select(c => (c, Title(c)))).ToArray(),
            () => s.Collection, v => s.Collection = v);
        Choice("Layout",
            () => new[] { ("split", "Cover + lyrics"), ("lyrics", "Lyrics only"), ("lock", "Centred (lock screen)") },
            () => s.Layout, v => s.Layout = v);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        Choice("Lyrics size",
            () => new[] { 80, 90, 100, 115, 130, 150 }.Select(p => (p, $"{p}%")).ToArray(),
            () => s.LyricsSize, v => s.LyricsSize = v);
        Choice("Darken background",
            () => new[] { 0, 20, 35, 50, 65 }.Select(p => (p, p == 0 ? "Off" : $"{p}%")).ToArray(),
            () => s.Dim, v => s.Dim = v);
        Choice("Wallpaper blur",
            () => new[] { 0, 6, 12, 24 }.Select(px => (px, px == 0 ? "Off" : $"{px} px")).ToArray(),
            () => s.WallpaperBlur, v => s.WallpaperBlur = v);
        root.DropDownItems.Add(new Forms.ToolStripSeparator());

        Toggle("Slow zoom drift", () => s.KenBurns, v => s.KenBurns = v);
        Toggle("Gradient reacts to bass", () => s.AudioReactive, v => s.AudioReactive = v);
        Toggle("Blur distant lines", () => s.LineBlur, v => s.LineBlur = v);
        Toggle("Spicy Lyrics font", () => s.SpicyFont, v => s.SpicyFont = v);
        Toggle("Clock when nothing plays", () => s.Clock, v => s.Clock = v);

        root.DropDownOpening += (_, _) => refresh.ForEach(r => r());
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
