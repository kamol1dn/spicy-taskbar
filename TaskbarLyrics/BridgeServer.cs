using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TaskbarLyrics;

public sealed class TrackCandidate
{
    public string Id = "";
    public string Name = "";
    public List<string> Artists = new();
    public double DurationMs;
}

public sealed class SpState
{
    public bool Playing;
    public double PositionMs;
    public double DurationMs;
    public string? TrackId;
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string? Cover; // https cover URL (i.scdn.co), when the extension sends one
    public long ReceivedTick;

    public bool IsFresh => (Stopwatch.GetTimestamp() - ReceivedTick) / (double)Stopwatch.Frequency < 2.5;

    /// <summary>Position now, extrapolated from when the push arrived.</summary>
    public double PositionNowMs
    {
        get
        {
            if (!Playing) return PositionMs;
            return PositionMs + (Stopwatch.GetTimestamp() - ReceivedTick) / (double)Stopwatch.Frequency * 1000.0;
        }
    }
}

/// <summary>
/// Local WebSocket server with two kinds of client:
///   * the spicetify extension (path "/") — request/response (search, lyrics) plus
///     pushed Spotify state; one at a time, a reconnect replaces the old socket.
///   * viewers (path "/wallpaper") — any number of displays (the wallpaper, one per
///     monitor, in Wallpaper Engine or Aura) that receive <see cref="Broadcast"/>s and
///     can ask for a few things hosts other than Wallpaper Engine lack (see WallpaperFeed).
/// </summary>
public sealed class BridgeServer
{
    /// <summary>A socket plus its send lock: WebSocket allows only one outstanding
    /// SendAsync, and search/lyrics requests (or broadcasts) can overlap.</summary>
    private sealed class Conn(WebSocket ws)
    {
        public readonly WebSocket Ws = ws;
        private readonly SemaphoreSlim _send = new(1, 1);

        public async Task SendAsync(byte[] bytes)
        {
            await _send.WaitAsync();
            try { await Ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _send.Release(); }
        }
    }

    /// <summary>A connected viewer (one wallpaper instance), as seen by subscribers.</summary>
    public sealed class Viewer
    {
        private readonly Action<object> _send;
        internal Viewer(Action<object> send) => _send = send;
        public void Send(object message) => _send(message);
    }

    private readonly int _port;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<Conn, Viewer> _viewers = new();
    private Conn? _ext;
    private int _reqCounter;

    public volatile SpState? SpotifyState;

    /// <summary>Raised the moment a state push lands, so position/track changes
    /// apply immediately instead of waiting for the next SMTC poll.</summary>
    public event Action<SpState>? StateUpdated;
    public bool Connected => _ext is { Ws.State: WebSocketState.Open };

    /// <summary>Fires when the spicetify extension (re)connects — lets the app retry fallback lyrics.</summary>
    public event Action? ClientConnected;

    /// <summary>Fires when a viewer connects, so it can be brought up to date with a snapshot.</summary>
    public event Action<Viewer>? ViewerConnected;

    /// <summary>A viewer asked for something (wallpaper folder listing, audio level).</summary>
    public event Action<Viewer, JsonElement>? ViewerMessage;

    public event Action<Viewer>? ViewerDisconnected;

    public bool HasViewers => !_viewers.IsEmpty;

    public BridgeServer(int port) => _port = port;

    public void Start() => _ = Task.Run(AcceptLoop);

    /// <summary>Browser pages can open ws://localhost too. Only Spotify may take the
    /// extension slot (any page could otherwise kick the real extension off and feed
    /// the overlay its own lyrics); viewers must be local files (Wallpaper Engine) or localhost.</summary>
    private static bool OriginAllowed(string? origin, bool viewer)
    {
        if (string.IsNullOrEmpty(origin) || origin == "null" || origin.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        if (u.IsLoopback) return true;
        // The threat is ordinary web pages. Spotify's own client origin (xpui) is
        // allowed whatever scheme a given build serves it from.
        if (u.Scheme is "http" or "https")
            return !viewer && u.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase);
        return !viewer;
    }

    private async Task AcceptLoop()
    {
        var listener = new HttpListener();
        // "localhost" prefix is bindable without elevation; the extension connects to ws://localhost:PORT
        listener.Prefixes.Add($"http://localhost:{_port}/");
        try { listener.Start(); }
        catch (Exception ex) { Log.Write($"bridge: listener failed: {ex.Message}"); return; }
        Log.Write($"bridge: listening on localhost:{_port}");

        while (true)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                if (!ctx.Request.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = 426;
                    ctx.Response.Close();
                    continue;
                }

                var viewer = ctx.Request.Url?.AbsolutePath.TrimEnd('/')
                    .Equals("/wallpaper", StringComparison.OrdinalIgnoreCase) == true;
                var origin = ctx.Request.Headers["Origin"];
                if (!OriginAllowed(origin, viewer))
                {
                    Log.Write($"bridge: refused {(viewer ? "viewer" : "extension")} connection from origin {origin}");
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    continue;
                }

                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var conn = new Conn(wsCtx.WebSocket);
                if (viewer)
                {
                    var v = new Viewer(msg => _ = SendSafeAsync(conn, msg));
                    _viewers[conn] = v;
                    Log.Write($"bridge: viewer connected ({_viewers.Count} total, origin {origin ?? "-"})");
                    _ = Task.Run(() => ReceiveLoop(conn, viewer: true));
                    _ = Task.Run(() => ViewerConnected?.Invoke(v));
                }
                else
                {
                    Log.Write($"bridge: spicetify extension connected (origin {origin ?? "-"})");
                    var old = Interlocked.Exchange(ref _ext, conn);
                    try { old?.Ws.Abort(); } catch { }
                    _ = Task.Run(() => ReceiveLoop(conn, viewer: false));
                    _ = Task.Run(() => ClientConnected?.Invoke());
                }
            }
            catch (Exception ex)
            {
                Log.Write($"bridge: accept error: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }

    private async Task ReceiveLoop(Conn conn, bool viewer)
    {
        var ws = conn.Ws;
        var buf = new byte[1 << 16];
        // Collect raw bytes and decode once per message: decoding each frame on its
        // own split multi-byte UTF-8 characters that straddled a frame boundary into
        // U+FFFD — which garbled lyrics payloads (they easily exceed 64 KB).
        using var msg = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                msg.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    msg.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                if (viewer) HandleViewerMessage(conn, text);
                else HandleMessage(text);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"bridge: {(viewer ? "viewer" : "extension")} receive loop ended: {ex.Message}");
        }
        finally
        {
            if (viewer)
            {
                if (_viewers.TryRemove(conn, out var v))
                {
                    try { ViewerDisconnected?.Invoke(v); }
                    catch (Exception ex) { Log.Write($"bridge: viewer-disconnect handler threw: {ex.Message}"); }
                }
                Log.Write($"bridge: viewer disconnected ({_viewers.Count} left)");
            }
            else
            {
                Interlocked.CompareExchange(ref _ext, null, conn);
            }
        }
    }

    private void HandleViewerMessage(Conn conn, string json)
    {
        if (!_viewers.TryGetValue(conn, out var v)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            ViewerMessage?.Invoke(v, doc.RootElement.Clone());
        }
        catch (Exception ex)
        {
            Log.Write($"bridge: bad viewer message: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions BroadcastJson = new() { IncludeFields = true };

    /// <summary>Send a message to every connected viewer (no-op when there are none).</summary>
    public void Broadcast(object message)
    {
        if (_viewers.IsEmpty) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BroadcastJson);
        foreach (var conn in _viewers.Keys) _ = SendRawAsync(conn, bytes);
    }

    private Task SendSafeAsync(Conn conn, object message) =>
        SendRawAsync(conn, JsonSerializer.SerializeToUtf8Bytes(message, BroadcastJson));

    private async Task SendRawAsync(Conn conn, byte[] bytes)
    {
        try
        {
            if (conn.Ws.State == WebSocketState.Open) await conn.SendAsync(bytes);
        }
        catch
        {
            // A dead viewer; its receive loop ends and removes it.
            try { conn.Ws.Abort(); } catch { }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "sp_state")
            {
                SpotifyState = new SpState
                {
                    Playing = root.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True,
                    PositionMs = root.TryGetProperty("positionMs", out var pos) ? pos.GetDouble() : 0,
                    DurationMs = root.TryGetProperty("durationMs", out var d) ? d.GetDouble() : 0,
                    TrackId = root.TryGetProperty("trackId", out var tid) ? tid.GetString() : null,
                    Title = root.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "",
                    Artist = root.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : "",
                    Album = root.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.String ? al.GetString() ?? "" : "",
                    Cover = root.TryGetProperty("cover", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() : null,
                    ReceivedTick = Stopwatch.GetTimestamp(),
                };
                try { StateUpdated?.Invoke(SpotifyState); }
                catch (Exception ex) { Log.Write($"bridge: state handler threw: {ex.Message}"); }
            }
            else if (type == "resp")
            {
                var reqId = root.TryGetProperty("reqId", out var rid) ? rid.GetString() : null;
                if (reqId != null && _pending.TryRemove(reqId, out var tcs))
                    tcs.TrySetResult(root.Clone());
            }
            else if (type == "log")
            {
                Log.Write($"ext: {(root.TryGetProperty("msg", out var m) ? m.GetString() : "")}");
            }
            else if (type == "hello")
            {
                Log.Write($"bridge: hello from extension (spicy-lyrics v{(root.TryGetProperty("version", out var v) ? v.GetString() : "?")})");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"bridge: bad message: {ex.Message}");
        }
    }

    private async Task<JsonElement?> RequestAsync(object payload, string reqId, int timeoutMs)
    {
        var conn = _ext;
        if (conn is not { Ws.State: WebSocketState.Open }) return null;

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;
        try
        {
            await conn.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload));
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        catch (Exception ex)
        {
            Log.Write($"bridge: request failed: {ex.Message}");
            return null;
        }
        finally
        {
            _pending.TryRemove(reqId, out _);
        }
    }

    public async Task<List<TrackCandidate>?> SearchAsync(string query)
    {
        var reqId = $"s{Interlocked.Increment(ref _reqCounter)}";
        var resp = await RequestAsync(new { type = "search", reqId, query }, reqId, 10_000);
        if (resp is not { } r) return null;
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
        {
            Log.Write($"bridge: search error: {(r.TryGetProperty("error", out var e) ? e.GetString() : "?")}");
            return null;
        }
        var list = new List<TrackCandidate>();
        if (r.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                var c = new TrackCandidate
                {
                    Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    DurationMs = item.TryGetProperty("durationMs", out var dm) ? dm.GetDouble() : 0,
                };
                if (item.TryGetProperty("artists", out var arts) && arts.ValueKind == JsonValueKind.Array)
                    foreach (var a in arts.EnumerateArray())
                        if (a.GetString() is { } s) c.Artists.Add(s);
                if (c.Id.Length > 0) list.Add(c);
            }
        }
        return list;
    }

    /// <summary>Returns (httpStatus, unpacked lyrics JSON or null). Status 0 = bridge unavailable/error.</summary>
    public async Task<(int Status, JsonElement? Lyrics)> LyricsAsync(string trackId)
    {
        var reqId = $"l{Interlocked.Increment(ref _reqCounter)}";
        // The extension may spend two 15s API attempts (401 -> refresh token -> retry).
        var resp = await RequestAsync(new { type = "lyrics", reqId, trackId }, reqId, 35_000);
        if (resp is not { } r) return (0, null);
        if (!(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
        {
            Log.Write($"bridge: lyrics error: {(r.TryGetProperty("error", out var e) ? e.GetString() : "?")}");
            return (0, null);
        }
        var status = r.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
        if (status == 200 && r.TryGetProperty("lyrics", out var ly))
            return (200, ly.Clone());
        return (status, null);
    }
}
