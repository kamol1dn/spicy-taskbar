using System.IO;
using System.Text.Json;

namespace TaskbarLyrics;

/// <summary>
/// User-tweakable settings, loaded from config.json next to the exe
/// (created with defaults on first run). Edit + restart to apply.
/// </summary>
public sealed class Config
{
    public int Port { get; set; } = 9012;

    /// <summary>"taskbar" = render on the taskbar itself (left side).
    /// "above" = floating strip just above the taskbar.</summary>
    public string Placement { get; set; } = "taskbar";

    /// <summary>Which screen edge the lyric strip hugs: top | bottom.</summary>
    public string VPos { get; set; } = "bottom";

    /// <summary>left | center | right</summary>
    public string Align { get; set; } = "center";
    public int XOffset { get; set; } = 16;
    public int Width { get; set; } = 620;

    public double MainFontPx { get; set; } = 15;
    public double BgFontPx { get; set; } = 10.5;
    public string FontFamily { get; set; } = "Segoe UI Variable Display";

    /// <summary>Drop shadow behind all overlay text. Helps on light wallpapers,
    /// muddies things on dark ones — toggled from the tray.</summary>
    public bool TextShadow { get; set; } = true;

    public byte SungAlpha { get; set; } = 255;
    public byte UnsungAlpha { get; set; } = 100;
    public byte BgLineAlpha { get; set; } = 150;

    /// <summary>Minimum instrumental gap (ms) before the ● ● ● dots appear.</summary>
    public int InterludeGapMs { get; set; } = 2500;

    /// <summary>Extra ms of lead time applied to all lyrics (positive = lyrics earlier).</summary>
    public int GlobalOffsetMs { get; set; } = 0;

    // ---- active-app name module (macOS-menubar style) ----

    /// <summary>Master on/off for the focused-app name strip (toggled from the tray).</summary>
    public bool AppNameEnabled { get; set; } = false;

    /// <summary>"taskbar" = render on the taskbar itself, "above" = floating strip.</summary>
    public string AppNamePlacement { get; set; } = "taskbar";

    /// <summary>Which screen edge the app-name strip hugs: top | bottom.</summary>
    public string AppNameVPos { get; set; } = "top";

    /// <summary>left | center | right</summary>
    public string AppNameAlign { get; set; } = "left";
    public int AppNameXOffset { get; set; } = 16;
    public int AppNameWidth { get; set; } = 260;

    public double AppNameFontPx { get; set; } = 13;
    public byte AppNameAlpha { get; set; } = 230;

    // ---- audio visualizer ----

    /// <summary>Master on/off for the taskbar audio visualizer (toggled from the tray).</summary>
    public bool VizEnabled { get; set; } = true;

    /// <summary>Selected preset id — see <see cref="VisualizerPresets"/>.</summary>
    public string VizPreset { get; set; } = "equalizer-bleed";

    /// <summary>Pick a new random preset each time the song changes.</summary>
    public bool VizRandomizeOnTrack { get; set; } = false;

    /// <summary>Which screen edge the visualizer grows out of: top | bottom.
    /// "top" mirrors every preset vertically, so bars hang down from the edge.</summary>
    public string VizEdge { get; set; } = "bottom";

    /// <summary>How much of the screen height the visualizer surface covers (0.15–0.6);
    /// the "bleed" presets paint upward into this band, taskbar-only presets ignore it.</summary>
    public double VizHeightFraction { get; set; } = 0.33;

    /// <summary>Master brightness of the whole visualizer (0–1). Kept low so it stays a
    /// backdrop and doesn't wash out the taskbar icons / lyrics rendered in front of it.</summary>
    public double VizOpacity { get; set; } = 0.55;

    // ---- wallpaper (wallpaper/ folder, in Wallpaper Engine or Aura) ----

    /// <summary>Folder the wallpaper takes a random image from on every song change.</summary>
    public string WallpaperFolder { get; set; } = DefaultWallpaperFolder();

    /// <summary>Settings pushed to the desktop wallpaper (index.html) — set from the tray.</summary>
    public WallpaperSettings WallpaperDesktop { get; set; } = new();

    /// <summary>Settings pushed to the lock-screen wallpaper (lockscreen.html).</summary>
    public WallpaperSettings WallpaperLock { get; set; } = WallpaperSettings.LockDefaults();

    private static string DefaultWallpaperFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallpapers");
        return Directory.Exists(dir) ? dir : "";
    }

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarLyrics");

    public static Config Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
            var cfg = new Config();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            return cfg;
        }
        catch
        {
            return new Config();
        }
    }

    /// <summary>Persist current values back to config.json (used by tray toggles).</summary>
    public void Save()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write($"config: save failed: {ex.Message}");
        }
    }
}

/// <summary>
/// One wallpaper surface's look. Names mirror the page's `settings` keys (wallpaper/js/app.js);
/// sent to the page as-is and applied over its config.js defaults.
/// </summary>
public sealed class WallpaperSettings
{
    /// <summary>"wallpapers" (random image from the folder per song) or "dynamic" (album gradient).</summary>
    public string Background { get; set; } = "wallpapers";

    /// <summary>"all", or a subfolder / filename-prefix category of the folder.</summary>
    public string Collection { get; set; } = "all";

    /// <summary>"split" (cover + lyrics), "lyrics" (lyrics only) or "lock" (centred, under Windows' clock).</summary>
    public string Layout { get; set; } = "split";

    public int LyricsSize { get; set; } = 100;    // %
    public int Dim { get; set; } = 35;            // % darkening over the background
    public int WallpaperBlur { get; set; } = 0;   // px
    public bool KenBurns { get; set; } = true;    // slow zoom drift on images
    public bool AudioReactive { get; set; } = true;
    public bool LineBlur { get; set; } = true;    // blur lines away from the current one
    public bool SpicyFont { get; set; } = true;
    public bool Clock { get; set; } = true;       // clock when nothing is playing

    public static WallpaperSettings LockDefaults() => new() { Layout = "lock", Clock = false, Dim = 45 };

    public object ToMessage() => new
    {
        background = Background,
        collection = Collection,
        layout = Layout,
        lyricssize = LyricsSize,
        dim = Dim,
        wallpaperblur = WallpaperBlur,
        kenburns = KenBurns,
        audioreactive = AudioReactive,
        lineblur = LineBlur,
        spicyfont = SpicyFont,
        clock = Clock,
    };
}

public static class Log
{
    private static readonly object Lock = new();
    private static readonly string PathFile = Path.Combine(Config.Dir, "log.txt");

    public static void Write(string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Config.Dir);
                if (File.Exists(PathFile) && new FileInfo(PathFile).Length > 512 * 1024)
                    File.Delete(PathFile);
                File.AppendAllText(PathFile, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
        System.Diagnostics.Debug.WriteLine("[TaskbarLyrics] " + msg);
    }
}
