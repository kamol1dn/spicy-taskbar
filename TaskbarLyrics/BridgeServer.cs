using System.Collections.Concurrent;
using System.Diagnostics;
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
/// Local WebSocket server the spicetify extension dials into.
/// Provides request/response (search, lyrics) plus the latest pushed Spotify state.
/// </summary>
public sealed class BridgeServer
{
    private readonly int _port;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private WebSocket? _socket;
    private int _reqCounter;

    public volatile SpState? SpotifyState;
    public bool Connected => _socket is { State: WebSocketState.Open };

    /// <summary>Fires when the spicetify extension (re)connects — lets the app retry fallback lyrics.</summary>
    public event Action? ClientConnected;

    public BridgeServer(int port) => _port = port;

    public void Start() => _ = Task.Run(AcceptLoop);

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
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                Log.Write("bridge: spicetify extension connected");
                var old = Interlocked.Exchange(ref _socket, wsCtx.WebSocket);
                try { old?.Abort(); } catch { }
                _ = Task.Run(() => ReceiveLoop(wsCtx.WebSocket));
                _ = Task.Run(() => ClientConnected?.Invoke());
            }
            catch (Exception ex)
            {
                Log.Write($"bridge: accept error: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }

    private async Task ReceiveLoop(WebSocket ws)
    {
        var buf = new byte[1 << 16];
        var sb = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                HandleMessage(sb.ToString());
            }
        }
        catch (Exception ex)
        {
            Log.Write($"bridge: receive loop ended: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_socket, ws)) _socket = null;
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
                    ReceivedTick = Stopwatch.GetTimestamp(),
                };
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
        var ws = _socket;
        if (ws is not { State: WebSocketState.Open }) return null;

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
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
        var resp = await RequestAsync(new { type = "lyrics", reqId, trackId }, reqId, 20_000);
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
