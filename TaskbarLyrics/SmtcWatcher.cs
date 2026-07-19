using Windows.Media.Control;

namespace TaskbarLyrics;

public sealed class TrackInfo
{
    public string Title = "";
    public string Artist = "";
    public double DurationMs;
    public bool IsSpotify;
    public string? SpotifyTrackId;

    // Identity deliberately excludes SpotifyTrackId/duration — those trickle in
    // asynchronously and must not look like a track change.
    public string Key => $"{Title}|{Artist}|{IsSpotify}";
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

    public event Action<TrackInfo?>? TrackChanged;

    public SmtcWatcher(BridgeServer bridge) => _bridge = bridge;

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

    private async Task PollOnce()
    {
        var session = _mgr!.GetCurrentSession();
        if (session == null)
        {
            PositionEngine.Clear();
            EmitIfChanged(null);
            return;
        }

        var aumid = session.SourceAppUserModelId ?? "";
        var isSpotify = aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase);

        var props = await session.TryGetMediaPropertiesAsync();
        var title = props?.Title ?? "";
        var artist = props?.Artist ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            PositionEngine.Clear();
            EmitIfChanged(null);
            return;
        }

        var info = new TrackInfo { Title = title, Artist = artist, IsSpotify = isSpotify };

        var playback = session.GetPlaybackInfo();
        var playing = playback?.PlaybackStatus ==
                      GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var rate = playback?.PlaybackRate ?? 1.0;

        var sp = _bridge.SpotifyState;
        if (isSpotify && sp is { IsFresh: true } && TitlesRoughlyMatch(sp.Title, title))
        {
            // Exact position pushed from inside Spotify — use it.
            info.SpotifyTrackId = sp.TrackId;
            info.DurationMs = sp.DurationMs;
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
        }

        EmitIfChanged(info);
    }

    private static bool TitlesRoughlyMatch(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return true;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private void EmitIfChanged(TrackInfo? info)
    {
        var key = info?.Key;
        if (key == _lastKey) { _pendingKey = null; return; }

        // Debounce: browsers flicker metadata while loading — require the new
        // identity to hold for 700ms before treating it as a real track change.
        if (key != _pendingKey)
        {
            _pendingKey = key;
            _pendingSince = DateTime.UtcNow;
            return;
        }
        if ((DateTime.UtcNow - _pendingSince).TotalMilliseconds < 700) return;

        _lastKey = key;
        _pendingKey = null;
        Log.Write($"smtc: track -> {(info == null ? "(none)" : $"{info.Artist} - {info.Title}" + (info.IsSpotify ? " [spotify]" : ""))}");
        TrackChanged?.Invoke(info);
    }
}
