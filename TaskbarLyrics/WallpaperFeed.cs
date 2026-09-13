using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TaskbarLyrics;

/// <summary>
/// Mirrors what the overlay knows — track, lyrics, artwork, playback position — to the
/// wallpaper (and any other viewer) over the bridge's "/wallpaper" socket. Viewers get
/// a full snapshot when they connect, then live updates; position goes out a few times
/// a second and the wallpaper interpolates between pushes.
///
/// It also stands in for the Wallpaper Engine APIs other hosts (Aura) don't have:
///   {type:"list-wallpapers", dir}  -> {type:"wallpapers", dir, files[]}  (folder picker)
///   {type:"audio", on}             -> {type:"audio", bass} ~30x/s        (audio listener)
/// and carries the tray's wallpaper settings ({type:"settings"}) and "next wallpaper".
/// </summary>
public sealed class WallpaperFeed
{
    private readonly BridgeServer _bridge;
    private readonly Config _cfg;
    private readonly object _gate = new();
    private readonly Timer _timer;

    private (string Dir, DateTime At, List<string> Names)? _categories;

    private object? _track;
    private object _lyrics = new { type = "lyrics", state = "none", lyrics = (Lyrics?)null };
    private object? _art;
    private bool _lastPlaying;

    // Loopback capture runs only while some viewer has asked for audio.
    private readonly ConcurrentDictionary<BridgeServer.Viewer, byte> _audioViewers = new();
    private readonly object _audioGate = new();
    private AudioEngine? _audio;
    private Timer? _audioTimer;
    private long _audioLastTick;
    private int _audioBusy;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif" };

    public WallpaperFeed(BridgeServer bridge, Config cfg)
    {
        _bridge = bridge;
        _cfg = cfg;
        _bridge.ViewerConnected += SendSnapshot;
        _bridge.ViewerMessage += OnViewerMessage;
        _bridge.ViewerDisconnected += v =>
        {
            if (_audioViewers.TryRemove(v, out _)) UpdateAudio();
        };
        // SmtcWatcher subscribed first, so PositionEngine already holds this push.
        _bridge.StateUpdated += sp =>
        {
            if (sp.Playing != _lastPlaying) PushPosition(); // pause/resume lands at once
        };
        _timer = new Timer(_ => PushPosition(), null, 250, 250);
    }

    public void OnTrackChanged(TrackInfo? info)
    {
        lock (_gate)
        {
            _track = info == null ? new { type = "track", track = (object?)null } : new
            {
                type = "track",
                track = new
                {
                    title = info.Title,
                    artist = info.Artist,
                    album = info.Album,
                    cover = info.CoverUrl,
                    durationMs = info.DurationMs,
                    spotify = info.IsSpotify,
                },
            };
            _lyrics = new { type = "lyrics", state = info == null ? "none" : "loading", lyrics = (Lyrics?)null };
            _art = null; // the new track's SMTC art follows via OnArtwork
            _bridge.Broadcast(_track);
            _bridge.Broadcast(_lyrics);
        }
        PushPosition();
    }

    public void OnLyrics(Lyrics? lyrics)
    {
        lock (_gate)
        {
            _lyrics = new { type = "lyrics", state = lyrics == null ? "none" : "ok", lyrics };
            _bridge.Broadcast(_lyrics);
        }
    }

    public void OnArtwork(byte[]? bytes)
    {
        lock (_gate)
        {
            if (bytes is not { Length: > 0 }) { _art = null; return; }
            var mime = bytes[0] == 0x89 ? "image/png" : "image/jpeg";
            _art = new { type = "art", dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" };
            _bridge.Broadcast(_art);
        }
    }

    private object PositionMessage()
    {
        var now = PositionEngine.NowMs;
        return new
        {
            type = "pos",
            has = now.HasValue,
            // Same sync nudge the taskbar overlay applies, so both show the same line.
            ms = (now ?? 0) + _cfg.GlobalOffsetMs,
            playing = PositionEngine.Playing,
            rate = PositionEngine.Rate,
        };
    }

    private void PushPosition()
    {
        _lastPlaying = PositionEngine.Playing;
        if (_bridge.HasViewers) _bridge.Broadcast(PositionMessage());
    }

    // ---- tray settings ----

    private object SettingsMessage() => new
    {
        type = "settings",
        folder = _cfg.WallpaperFolder,
        desktop = _cfg.WallpaperDesktop.ToMessage(),
        lockscreen = _cfg.WallpaperLock.ToMessage(),
    };

    /// <summary>Send the current wallpaper settings to every open wallpaper (after a tray change).</summary>
    public void PushSettings() => _bridge.Broadcast(SettingsMessage());

    /// <summary>Tray "Next wallpaper": every wallpaper in image mode moves to its next image.</summary>
    public void NextWallpaper() => _bridge.Broadcast(new { type = "next-wallpaper" });

    /// <summary>
    /// Collections offered in the tray for the configured folder: subfolders and filename
    /// prefixes ("nord_a_forest.jpg" -> "nord") that hold at least 3 images, largest first.
    /// </summary>
    public List<string> Categories()
    {
        var dir = _cfg.WallpaperFolder;
        if (_categories is { } c && c.Dir == dir && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(1))
            return c.Names;
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ListImageFiles(dir))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var cut = name.IndexOf('_');
            if (cut > 0) Bump(counts, name[..cut]);
            var parent = Path.GetDirectoryName(f);
            if (parent != null && !string.Equals(Path.TrimEndingDirectorySeparator(parent),
                    Path.TrimEndingDirectorySeparator(dir), StringComparison.OrdinalIgnoreCase))
                Bump(counts, Path.GetFileName(parent));
        }
        var names = counts.Where(kv => kv.Value >= 3)
            .OrderByDescending(kv => kv.Value)
            .Take(12)
            .Select(kv => kv.Key.ToLowerInvariant())
            .Distinct()
            .ToList();
        _categories = (dir, DateTime.UtcNow, names);
        return names;

        static void Bump(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;
    }

    private void SendSnapshot(BridgeServer.Viewer v)
    {
        lock (_gate)
        {
            v.Send(new { type = "hello", app = "TaskbarLyrics" });
            v.Send(SettingsMessage()); // first, so the page lays out right from the start
            if (_track != null) v.Send(_track);
            v.Send(_lyrics);
            if (_art != null) v.Send(_art);
        }
        v.Send(PositionMessage());
    }

    private void OnViewerMessage(BridgeServer.Viewer v, JsonElement m)
    {
        var type = m.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "list-wallpapers")
        {
            var dir = m.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            v.Send(ListWallpapers(dir));
        }
        else if (type == "audio")
        {
            var on = m.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
            if (on) _audioViewers[v] = 0;
            else _audioViewers.TryRemove(v, out _);
            UpdateAudio();
        }
    }

    /// <summary>Image files in the folder the wallpaper asks for (subfolders too).</summary>
    private static object ListWallpapers(string? dir)
    {
        var files = ListImageFiles(dir);
        Log.Write($"feed: listed {files.Length} wallpapers in {dir}");
        return files.Length > 0
            ? new { type = "wallpapers", dir, files, error = (string?)null }
            : new { type = "wallpapers", dir, files, error = (string?)"no images found" };
    }

    private static string[] ListImageFiles(string? dir)
    {
        dir = Environment.ExpandEnvironmentVariables(dir ?? "").Trim();
        if (dir.Length == 0 || !Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            return Directory
                .EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(f => ImageExt.Contains(Path.GetExtension(f)))
                .Take(5000)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Write($"feed: listing {dir} failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private void UpdateAudio()
    {
        lock (_audioGate)
        {
            if (!_audioViewers.IsEmpty && _audio == null)
            {
                _audio = new AudioEngine();
                _audio.Start();
                _audioLastTick = Stopwatch.GetTimestamp();
                _audioTimer = new Timer(_ => AudioTick(), null, 0, 33);
                Log.Write("feed: audio level stream on");
            }
            else if (_audioViewers.IsEmpty && _audio != null)
            {
                _audioTimer?.Dispose();
                _audioTimer = null;
                _audio.Stop();
                _audio = null;
                Log.Write("feed: audio level stream off");
            }
        }
    }

    private void AudioTick()
    {
        // Timer callbacks can overlap under load; the analyser is single-threaded.
        if (Interlocked.Exchange(ref _audioBusy, 1) == 1) return;
        try
        {
            var a = _audio;
            if (a == null) return;
            var now = Stopwatch.GetTimestamp();
            a.Update((now - _audioLastTick) * 1000.0 / Stopwatch.Frequency);
            _audioLastTick = now;
            var msg = new { type = "audio", bass = a.IsActive ? Math.Round(a.Bass, 3) : 0 };
            foreach (var v in _audioViewers.Keys) v.Send(msg);
        }
        finally { Volatile.Write(ref _audioBusy, 0); }
    }
}
