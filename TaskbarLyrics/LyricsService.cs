using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskbarLyrics;

/// <summary>
/// Resolves a playing track to synced lyrics:
///   1. disk cache
///   2. SpicyLyrics API via the spicetify bridge (word-level when available)
///   3. LRCLIB (line-level, no auth) as fallback
/// </summary>
public sealed class LyricsService
{
    private readonly BridgeServer _bridge;
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private CancellationTokenSource? _cts;

    public event Action<Lyrics?>? LyricsResolved;

    public LyricsService(BridgeServer bridge)
    {
        _bridge = bridge;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TaskbarLyrics/0.1 (personal use)");
        _http.Timeout = TimeSpan.FromSeconds(15);
        _cacheDir = Path.Combine(Config.Dir, "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public void OnTrackChanged(TrackInfo? info)
    {
        _cts?.Cancel();
        if (info == null)
        {
            LyricsResolved?.Invoke(null);
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var lyrics = await ResolveAsync(info, cts.Token);
                if (!cts.IsCancellationRequested)
                    LyricsResolved?.Invoke(lyrics);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Write($"resolve: error: {ex.Message}");
                if (!cts.IsCancellationRequested) LyricsResolved?.Invoke(null);
            }
        }, cts.Token);
    }

    private async Task<Lyrics?> ResolveAsync(TrackInfo info, CancellationToken ct)
    {
        // Metadata-only key: stable across the trackId/duration trickling in.
        var cacheKey = $"{Matching.Norm(info.Title)}|{Matching.Norm(info.Artist)}";

        // Word-level (spicy) cache entries are final. lrclib/negative entries are
        // served but retried against SpicyLyrics whenever the bridge is up, so a
        // fallback taken during a bad moment can upgrade to word-level later.
        var cached = LoadCache(cacheKey);
        if (cached != null && (cached.Source == "spicy" || !_bridge.Connected))
        {
            Log.Write($"resolve: cache hit ({(cached.Lines.Count == 0 ? "negative" : cached.Source)})");
            return cached.Lines.Count == 0 ? null : cached;
        }

        // Rapid skipping shouldn't fire a search per skipped track; let the
        // selection settle before spending a query.
        if (info.SpotifyTrackId == null) await Task.Delay(600, ct);

        // Spotify is the source but the bridge hasn't pushed its state yet —
        // give it a moment so we get the exact track id instead of searching.
        if (info.IsSpotify && info.SpotifyTrackId == null)
        {
            for (var i = 0; i < 12 && info.SpotifyTrackId == null; i++)
            {
                await Task.Delay(250, ct);
                var sp = _bridge.SpotifyState;
                if (sp is { IsFresh: true, TrackId: not null } &&
                    (sp.Title.Length == 0 || sp.Title.Contains(info.Title, StringComparison.OrdinalIgnoreCase) ||
                     info.Title.Contains(sp.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    info.SpotifyTrackId = sp.TrackId;
                    if (info.DurationMs <= 0) info.DurationMs = sp.DurationMs;
                }
            }
        }

        Lyrics? lyrics = null;
        var bridgeWasUp = _bridge.Connected;

        // ---- 1) SpicyLyrics via the bridge ----
        if (bridgeWasUp)
        {
            var trackId = info.SpotifyTrackId ?? await FindTrackIdAsync(info, ct);
            if (trackId != null)
            {
                lyrics = await FetchSpicyAsync(trackId, ct);
                if (lyrics != null) Log.Write($"resolve: spicy lyrics ok ({lyrics.Type})");
            }
        }

        if (lyrics != null)
        {
            SaveCache(cacheKey, lyrics);
            return lyrics;
        }

        // Spicy came up empty — an existing fallback cache entry still stands.
        if (cached != null)
        {
            Log.Write($"resolve: keeping cached {(cached.Lines.Count == 0 ? "negative" : cached.Source)} (no spicy upgrade)");
            return cached.Lines.Count == 0 ? null : cached;
        }

        // ---- 2) LRCLIB fallback ----
        lyrics = await FetchLrclibAsync(info, ct);
        if (lyrics != null) Log.Write("resolve: lrclib ok");

        // Cache positives always; cache negatives only when the bridge was up,
        // so a temporarily-closed Spotify doesn't poison the cache.
        if (lyrics != null) SaveCache(cacheKey, lyrics);
        else if (bridgeWasUp) SaveCache(cacheKey, new Lyrics { Source = "none" });

        if (lyrics == null) Log.Write("resolve: no lyrics found");
        return lyrics;
    }

    /// <summary>Cheap in-memory memo so re-resolves and repeat plays don't re-query Spotify.</summary>
    private readonly Dictionary<string, List<TrackCandidate>> _searchMemo = new();

    private async Task<string?> FindTrackIdAsync(TrackInfo info, CancellationToken ct)
    {
        var cands = Matching.Candidates(info.Title, info.Artist);
        var queries = new List<string>();
        foreach (var (artist, title) in cands.Take(2))
            queries.Add(artist.Length > 0 ? $"{title} {artist}" : title);

        foreach (var q in queries.Distinct())
        {
            ct.ThrowIfCancellationRequested();

            if (!_searchMemo.TryGetValue(q, out var results))
            {
                results = await _bridge.SearchAsync(q) ?? new List<TrackCandidate>();
                if (results.Count > 0) _searchMemo[q] = results;
            }

            if (results is not { Count: > 0 })
            {
                Log.Write($"resolve: search \"{q}\" -> no usable results");
                continue;
            }
            var best = Matching.PickBest(results, cands, info.DurationMs, out var score);
            if (best != null)
            {
                Log.Write($"resolve: matched \"{best.Artists.FirstOrDefault()} - {best.Name}\" (score {score:0.00})");
                return best.Id;
            }
            var top = results[0];
            Log.Write($"resolve: search \"{q}\" -> {results.Count} results, best score {score:0.00} " +
                      $"(top: {top.Artists.FirstOrDefault()} - {top.Name}, {top.DurationMs / 1000:0}s vs {info.DurationMs / 1000:0}s)");
        }
        Log.Write("resolve: no confident spotify match");
        return null;
    }

    private async Task<Lyrics?> FetchSpicyAsync(string trackId, CancellationToken ct)
    {
        // 503 = the server queued the request and is generating the lyrics. Poll on
        // spicy-lyrics' own schedule (2s, x1.5 per try, capped at 10s — LyricsQueueRetry.ts)
        // for up to ~45s; the track-change CancellationToken ends it early. The old
        // 4 x 5s budget often gave up just before a queued track was ready and settled
        // for line-level LRCLIB lyrics.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (status, json) = await _bridge.LyricsAsync(trackId);
            if (status == 200 && json is { } j)
                return LyricsNormalizer.FromSpicy(j);
            if (status == 503)
            {
                var delay = (int)Math.Min(10_000, 2000 * Math.Pow(1.5, attempt));
                Log.Write($"resolve: spicy queued (503), retrying in {delay / 1000.0:0.#}s...");
                await Task.Delay(delay, ct);
                continue;
            }
            if (status != 0) Log.Write($"resolve: spicy status {status}");
            return null;
        }
        return null;
    }

    private async Task<Lyrics?> FetchLrclibAsync(TrackInfo info, CancellationToken ct)
    {
        foreach (var (artist, title) in Matching.Candidates(info.Title, info.Artist))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string? lrc = null;
                double dur = info.DurationMs;

                if (artist.Length > 0 && dur > 1000)
                {
                    var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}" +
                              $"&track_name={Uri.EscapeDataString(title)}&duration={(int)(dur / 1000)}";
                    var res = await _http.GetAsync(url, ct);
                    if (res.IsSuccessStatusCode)
                        lrc = ExtractSynced(await res.Content.ReadAsStringAsync(ct));
                }

                if (lrc == null)
                {
                    var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}" +
                              (artist.Length > 0 ? $"&artist_name={Uri.EscapeDataString(artist)}" : "");
                    var res = await _http.GetAsync(url, ct);
                    if (res.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var best = doc.RootElement.EnumerateArray()
                                .Where(e => e.TryGetProperty("syncedLyrics", out var s) &&
                                            s.ValueKind == JsonValueKind.String && s.GetString()!.Length > 0)
                                .OrderBy(e => dur > 1000 && e.TryGetProperty("duration", out var d) &&
                                              d.ValueKind == JsonValueKind.Number
                                    ? Math.Abs(d.GetDouble() * 1000 - dur) : double.MaxValue)
                                .Cast<JsonElement?>()
                                .FirstOrDefault();
                            if (best is { } b)
                            {
                                var bdur = b.TryGetProperty("duration", out var d2) && d2.ValueKind == JsonValueKind.Number
                                    ? d2.GetDouble() * 1000 : 0;
                                if (dur < 1000 || bdur <= 0 || Math.Abs(bdur - dur) < 10_000)
                                    lrc = b.GetProperty("syncedLyrics").GetString();
                            }
                        }
                    }
                }

                if (lrc != null)
                    return LyricsNormalizer.FromLrc(lrc, dur);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write($"resolve: lrclib error: {ex.Message}");
            }
        }
        return null;
    }

    private static string? ExtractSynced(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("syncedLyrics", out var s) &&
                   s.ValueKind == JsonValueKind.String && s.GetString()!.Length > 0
                ? s.GetString()
                : null;
        }
        catch { return null; }
    }

    // ---- disk cache ----

    private string CachePath(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        return Path.Combine(_cacheDir, hash + ".json");
    }

    private Lyrics? LoadCache(string key)
    {
        try
        {
            var p = CachePath(key);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<Lyrics>(File.ReadAllText(p), CacheJson);
        }
        catch { return null; }
    }

    private void SaveCache(string key, Lyrics lyrics)
    {
        try { File.WriteAllText(CachePath(key), JsonSerializer.Serialize(lyrics, CacheJson)); }
        catch (Exception ex) { Log.Write($"cache: save failed: {ex.Message}"); }
    }

    private static readonly JsonSerializerOptions CacheJson = new() { IncludeFields = true };
}
