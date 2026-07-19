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

    /// <summary>left | center | right</summary>
    public string Align { get; set; } = "center";
    public int XOffset { get; set; } = 16;
    public int Width { get; set; } = 620;

    public double MainFontPx { get; set; } = 15;
    public double BgFontPx { get; set; } = 10.5;
    public string FontFamily { get; set; } = "Segoe UI Variable Display";

    public byte SungAlpha { get; set; } = 255;
    public byte UnsungAlpha { get; set; } = 100;
    public byte BgLineAlpha { get; set; } = 150;

    /// <summary>Minimum instrumental gap (ms) before the ● ● ● dots appear.</summary>
    public int InterludeGapMs { get; set; } = 2500;

    /// <summary>Extra ms of lead time applied to all lyrics (positive = lyrics earlier).</summary>
    public int GlobalOffsetMs { get; set; } = 0;

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
