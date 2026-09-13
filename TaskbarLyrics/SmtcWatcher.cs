using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TaskbarLyrics;

public sealed class TrackInfo
{
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string? CoverUrl; // https cover from the Spotify bridge; SMTC art arrives via ArtworkChanged
    public double DurationMs;
    public bool IsSpotify;
    public string? SpotifyTrackId;

    /// <summary>Built from a bridge push (Spotify's own state) rather than SMTC metadata.</summary>
    public bool FromBridge;

    // For SMTC-sourced info the identity deliberately excludes SpotifyTrackId/duration —
    // those trickle in asynchronously and must not look like a track change. Bridge-sourced
    // info has the id up front, so it keys on that: during a mix transition Spotify swaps
    // tracks well before SMTC metadata catches up.
    public string Key => FromBridge && SpotifyTrackId != null
        ? $"sp:{SpotifyTrackId}"
        : $"{Title}|{Artist}|{IsSpotify}";
}

/// <summary>
/// Polls the Windows media session (SMTC — the API behind the volume-flyout media
/// card) for the current track + timeline, feeding PositionEngine and raising
/// TrackChanged when the playing song changes. Covers Spotify, browsers, VLC, etc.
/// When Spotify is the active source and the bridge is pushing exact state, that
/// wins over SMTC's coarser timeline.
/// </summary>
public sealed class SmtcWatcher
{
    private readonly BridgeServer _bridge;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private string? _lastKey;
    private string? _pendingKey;
    private DateTime _pendingSince;
    private string? _artworkKey;
    private readonly object _gate = new();

    /// <summary>True while Spotify owns the current SMTC session (set by the poll loop).</summary>
    private volatile bool _spotifyIsCurrent;

    public event Action<TrackInfo?>? TrackChanged;

    /// <summary>Raised on a real track change with the album-art bytes (or null when none).</summary>
    public event Action<byte[]?>? ArtworkChanged;

    public SmtcWatcher(BridgeServer bridge)
    {
        _bridge = bridge;
        // Apply Spotify's pushes the instant they land. Waiting for the next 500ms poll
        // leaves a stale baseline on screen right after a mix transition seeks the
        // incoming track to a non-zero start offset.
        _bridge.StateUpdated += OnBridgeState;
    }

    private void OnBridgeState(SpState sp)
    {
        if (!_spotifyIsCurrent) return;
        PositionEngine.Set(sp.PositionNowMs, sp.Playing);

        if (sp.TrackId == null) return;
        var info = FromBridge(sp);
        // Spotify changing track is authoritative and never flickers, so skip the
        // debounce that exists for browsers rewriting their metadata mid-load.
        lock (_gate)
        {
            if (info.Key != _lastKey) EmitIfChanged(info, immediate: true);
        }
    }

    private static TrackInfo FromBridge(SpState sp) => new()
    {
        Title = sp.Title,
        Artist = sp.Artist,
        Album = sp.Album,
        CoverUrl = sp.Cover,
        DurationMs = sp.DurationMs,
        IsSpotify = true,
        SpotifyTrackId = sp.TrackId,
        FromBridge = true,
    };

    public void Start() => _ = Task.Run(PollLoop);

    private async Task PollLoop()
    {
        try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception ex) { Log.Write($"smtc: manager init failed: {ex}"); return; }

        while (true)
        {
            try { await PollOnce(); }
            catch (Exception ex) { Log.Write($"smtc: poll error: {ex.Message}"); }
            await Task.Delay(500);
        }
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession? s)
    {
        try { return s?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    /// <summary>
    /// Windows' "current" session is just the one that last changed state — a paused
    /// browser tab (an Instagram reel) regularly takes it while Spotify keeps playing.
    /// Prefer whatever is actually playing, Spotify first; fall back to "current".
    /// </summary>
    private static GlobalSystemMediaTransportControlsSession? PickSession(GlobalSystemMediaTransportControlsSessionManager mgr)
    {
        var current = mgr.GetCurrentSession();
        if (IsPlaying(current)) return current;
        try
        {
            GlobalSystemMediaTransportControlsSession? playing = null;
            foreach (var s in mgr.GetSessions())
            {
                if (!IsPlaying(s)) continue;
                if (s.SourceAppUserModelId?.Contains("spotify", StringComparison.OrdinalIgnoreCase) == true) return s;
                playing ??= s;
            }
            if (playing != null) return playing;
        }
        catch { /* session list unavailable; use current */ }
        return current;
    }

    private async Task PollOnce()
    {
        var session = PickSession(_mgr!);
        if (session == null)
        {
            _spotifyIsCurrent = false;
            PositionEngine.Clear();
            bool cleared;
            lock (_gate) cleared = EmitIfChanged(null);
            if (cleared) { _artworkKey = null; ArtworkChanged?.Invoke(null); }
            return;
        }

        var aumid = session.SourceAppUserModelId ?? "";
        var isSpotify = aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase);
        _spotifyIsCurrent = isSpotify;

        var props = await session.TryGetMediaPropertiesAsync();
        var title = props?.Title ?? "";
        var artist = props?.Artist ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            _spotifyIsCurrent = false;
            PositionEngine.Clear();
            bool cleared;
            lock (_gate) cleared = EmitIfChanged(null);
            if (cleared) { _artworkKey = null; ArtworkChanged?.Invoke(null); }
            return;
        }

        var info = new TrackInfo { Title = title, Artist = artist, Album = props?.AlbumTitle ?? "", IsSpotify = isSpotify };

        var playback = session.GetPlaybackInfo();
        var playing = playback?.PlaybackStatus ==
                      GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var rate = playback?.PlaybackRate ?? 1.0;

        var sp = _bridge.SpotifyState;
        // Chromium throttles the extension's push timer while Spotify's window is in the
        // background, so pushes can stop between song-change/play-pause events and the state
        // goes stale. While SMTC still names the song the bridge last reported, keep the
        // bridge's identity (track id, full artist list): otherwise the key flipped from
        // "sp:<id>" to "Title|Artist" a few seconds into every song, re-resolving the lyrics
        // and looking like a brand-new track to the wallpaper.
        var bridgeSameSong = isSpotify && sp is { TrackId: not null } && TitlesRoughlyMatch(sp.Title, title);
        if (isSpotify && sp is { IsFresh: true, TrackId: not null })
        {
            // Spotify's own clock, pushed from inside the app — always authoritative.
            //
            // This deliberately does NOT check that the SMTC title matches: during a mix
            // transition Spotify has already moved to the next track while SMTC still
            // reports the previous one, and the old title gate dropped us onto SMTC's
            // timeline for exactly that window. That timeline is only republished on
            // play/pause/seek, so if the mix started the incoming track at an offset, the
            // wrong baseline stuck for the rest of the song and merely got extrapolated
            // forward — which is why pausing or seeking made it snap back into sync.
            if (title.Length > 0 && !TitlesRoughlyMatch(sp.Title, title))
                Log.Write($"smtc: bridge/SMTC disagree (bridge \"{sp.Title}\" vs smtc \"{title}\") — trusting bridge");

            info = FromBridge(sp);
            PositionEngine.Set(sp.PositionNowMs, sp.Playing);
        }
        else
        {
            var tl = session.GetTimelineProperties();
            var posMs = tl.Position.TotalMilliseconds;
            if (playing && tl.LastUpdatedTime.Year > 2000)
            {
                var stale = (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalMilliseconds;
                if (stale > 0 && stale < 30 * 60_000) posMs += stale * rate;
            }
            info.DurationMs = tl.EndTime.TotalMilliseconds;
            PositionEngine.Set(posMs, playing, rate);
            if (bridgeSameSong) info = FromBridge(sp!); // identity only; position stays SMTC's
        }

        lock (_gate) EmitIfChanged(info);

        // Artwork is tracked separately from the emit: a bridge-driven change (mix
        // transition) already emitted without ever reaching this poll's thumbnail.
        // SMTC's metadata lags Spotify there, so wait until it names the same track
        // before pulling art, or the visualizer would take the previous cover.
        var artInSync = !isSpotify || sp is not { IsFresh: true } || TitlesRoughlyMatch(sp.Title, title);
        string? showing;
        lock (_gate) showing = _lastKey;
        if (showing != _artworkKey && artInSync)
        {
            _artworkKey = showing;
            _ = LoadArtworkAsync(props?.Thumbnail);
        }
    }

    /// <summary>Read the current track's thumbnail bytes and hand them to the visualizer.</summary>
    private async Task LoadArtworkAsync(IRandomAccessStreamReference? thumb)
    {
        try
        {
            if (thumb == null) { ArtworkChanged?.Invoke(null); return; }
            using var ras = await thumb.OpenReadAsync();
            var size = (uint)ras.Size;
            if (size == 0) { ArtworkChanged?.Invoke(null); return; }
            var reader = new DataReader(ras);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            ArtworkChanged?.Invoke(bytes);
        }
        catch (Exception ex)
        {
            Log.Write($"smtc: artwork load failed: {ex.Message}");
            ArtworkChanged?.Invoke(null);
        }
    }

    private static bool TitlesRoughlyMatch(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return true;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when a real (debounced) track change was emitted this call.
    /// <paramref name="immediate"/> skips the debounce for sources that never flicker.
    /// </summary>
    private bool EmitIfChanged(TrackInfo? info, bool immediate = false)
    {
        var key = info?.Key;
        if (key == _lastKey) { _pendingKey = null; return false; }

        // Debounce: browsers flicker metadata while loading — require the new
        // identity to hold for 700ms before treating it as a real track change.
        if (!immediate)
        {
            if (key != _pendingKey)
            {
                _pendingKey = key;
                _pendingSince = DateTime.UtcNow;
                return false;
            }
            if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds < 700) return false;
        }

        _lastKey = key;
        _pendingKey = null;
        Log.Write($"smtc: track -> {(info == null ? "(none)" : $"{info.Artist} - {info.Title}" + (info.IsSpotify ? " [spotify]" : ""))}");
        TrackChanged?.Invoke(info);
        return true;
    }
}
