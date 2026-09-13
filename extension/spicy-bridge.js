// Spicy Bridge — companion Spicetify extension for the Taskbar Lyrics overlay.
// Runs inside the Spotify desktop client. Connects OUT to the overlay app's
// local WebSocket server and services two request types:
//   {type:"search", reqId, query}   -> Spotify track search (internal token)
//   {type:"lyrics", reqId, trackId} -> SpicyLyrics API fetch + objpack unpack
// It also pushes exact Spotify playback state every 250ms ({type:"sp_state"}), plus
// immediately on songchange / play-pause so mix-mode transitions land at once,
// which the overlay uses for tight sync + direct track IDs when Spotify itself
// is the player.
(function SpicyBridge() {
  const BRIDGE_URL = "ws://localhost:9012";
  // Mirror our logs to the overlay app so diagnostics land in one file
  // instead of Spotify's devtools console.
  const LOG = (...a) => {
    console.log("[SpicyBridge]", ...a);
    try {
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({
          type: "log",
          msg: a.map((x) => (typeof x === "string" ? x : String(x && x.message ? x.message : x))).join(" "),
        }));
      }
    } catch (e) { /* never let logging break anything */ }
  };

  // ---------- objpack unpack (ported from spicy-lyrics src/utils/objpack.ts) ----------
  function slUnpack(packed) {
    if (!Array.isArray(packed) || packed.length !== 2) throw new Error("bad payload");
    const values = packed[0];
    const stream = packed[1];
    if (!Array.isArray(values) || !Array.isArray(stream)) throw new Error("bad payload");
    const FORBID = new Set(["__proto__", "constructor", "prototype"]);
    let cursor = 0;
    const read = () => {
      if (cursor >= stream.length) throw new Error("unexpected end of stream");
      return stream[cursor++];
    };
    const ptr = (p) => {
      if (typeof p !== "number" || !Number.isInteger(p) || p < 0 || p >= values.length)
        throw new Error("invalid pointer " + p);
      return values[p];
    };
    const readKey = () => {
      const k = ptr(read());
      if (typeof k !== "string" || FORBID.has(k)) throw new Error("bad key");
      return k;
    };
    const safeSet = (o, k, v) =>
      Object.defineProperty(o, k, { value: v, writable: true, enumerable: true, configurable: true });
    function decode(depth) {
      if (depth > 512) throw new Error("max depth");
      const op = read();
      if (typeof op !== "number" || !Number.isInteger(op)) throw new Error("bad opcode");
      if (op >= 0) return ptr(op);
      switch (op) {
        case -1: {
          const n = read();
          const keys = new Array(n);
          for (let i = 0; i < n; i++) keys[i] = readKey();
          const o = {};
          for (let i = 0; i < n; i++) safeSet(o, keys[i], decode(depth + 1));
          return o;
        }
        case -2: {
          const n = read();
          const a = new Array(n);
          for (let i = 0; i < n; i++) a[i] = decode(depth + 1);
          return a;
        }
        case -3: {
          const n = read();
          const nk = read();
          const keys = new Array(nk);
          for (let i = 0; i < nk; i++) keys[i] = readKey();
          const a = new Array(n);
          for (let i = 0; i < n; i++) {
            const o = {};
            for (let k = 0; k < nk; k++) safeSet(o, keys[k], decode(depth + 1));
            a[i] = o;
          }
          return a;
        }
        case -4: return [];
        case -5: return [decode(depth + 1)];
        case -6: return {};
        default: throw new Error("unknown opcode " + op);
      }
    }
    const result = decode(0);
    if (cursor !== stream.length) throw new Error("extra data");
    return result;
  }

  // ---------- Spotify internal access token ----------
  // The token surface moves between Spotify client versions: `sp://oauth/v2/token`
  // and `Platform.Session.accessToken` both disappeared in the 1.2.9x line, which is
  // why every SpicyLyrics fetch started failing with "no spotify token available"
  // and the overlay silently fell back to line-level LRCLIB lyrics. Probe the known
  // sources newest-first instead of depending on any single one.
  let tokenCache = null;   // { accessToken, expiresAtTime }
  let tokenSource = null;  // name of the probe that last worked
  let tokenDiagShown = false;

  function normalizeToken(r) {
    if (!r) return null;
    const t = typeof r === "string" ? r : (r.accessToken || r.access_token);
    if (typeof t !== "string" || t.length === 0) return null;
    const exp = (typeof r === "object" &&
                 (r.expiresAtTime || r.accessTokenExpirationTimestampMs || r.expires_at)) || 0;
    // Unknown expiry: re-probe in 15 minutes rather than pinning a stale token forever.
    return { accessToken: t, expiresAtTime: exp > Date.now() ? exp : Date.now() + 15 * 60_000 };
  }

  // Each probe resolves to something normalizeToken understands, or null/throws.
  const TOKEN_PROBES = [
    // What spicy-lyrics itself reads first on current clients: a synchronous
    // getter over the platform's own (always fresh) authorization store.
    ["AuthorizationAPI.getState", async () => {
      const api = Spicetify.Platform && Spicetify.Platform.AuthorizationAPI;
      if (!api || typeof api.getState !== "function") return null;
      const state = api.getState();
      if (!state || state.isAuthorized === false || !state.token) return null;
      return state.token; // { accessToken, accessTokenExpirationTimestampMs }
    }],
    ["AuthorizationAPI.getToken", async () => {
      const api = Spicetify.Platform && Spicetify.Platform.AuthorizationAPI;
      return api && typeof api.getToken === "function" ? await api.getToken() : null;
    }],
    ["AuthorizationAPI._tokenProvider", async () => {
      const api = Spicetify.Platform && Spicetify.Platform.AuthorizationAPI;
      const tp = api && api._tokenProvider;
      if (typeof tp === "function") return await tp({ preferCached: true });
      if (tp && typeof tp.getToken === "function") return await tp.getToken();
      return null;
    }],
    ["Platform.Session", async () => {
      const s = Spicetify.Platform && Spicetify.Platform.Session;
      return s && s.accessToken ? s : null;
    }],
    ["cosmos sp://oauth/v2/token", async () =>
      await Spicetify.CosmosAsync.get("sp://oauth/v2/token")],
  ];

  // One-time dump of what the client actually exposes, so the next time Spotify
  // moves this it is diagnosable from the overlay log instead of devtools.
  function logTokenDiag() {
    if (tokenDiagShown) return;
    tokenDiagShown = true;
    try {
      const p = Spicetify.Platform || {};
      const api = p.AuthorizationAPI;
      LOG("token: no source worked. Platform keys: " +
          Object.keys(p).filter((k) => /auth|session|token/i.test(k)).join(",") +
          " | AuthorizationAPI: " + (api ? Object.keys(api).join(",") : "absent"));
    } catch (e) { /* diagnostics must never throw */ }
  }

  // A token the API refused with 401. The platform keeps handing back the same
  // string until it rotates, so remember it rather than just dropping the cache.
  let rejectedToken = null;

  function invalidateToken(token) {
    rejectedToken = token;
    if (tokenCache && tokenCache.accessToken === token) tokenCache = null;
  }

  async function getToken() {
    if (tokenCache && tokenCache.expiresAtTime - Date.now() > 60_000) return tokenCache.accessToken;

    // Try the source that worked last time first, so refreshes don't re-walk dead APIs.
    const probes = tokenSource
      ? TOKEN_PROBES.slice().sort((a, b) => (b[0] === tokenSource) - (a[0] === tokenSource))
      : TOKEN_PROBES;

    const failures = [];
    let lastResort = null;
    for (const [name, probe] of probes) {
      try {
        const tok = normalizeToken(await probe());
        if (tok && tok.accessToken === rejectedToken) {
          lastResort = lastResort || tok;
          failures.push(name + "=rejected");
          continue;
        }
        if (tok) {
          if (tokenSource !== name) LOG("token: using " + name);
          tokenSource = name;
          tokenCache = tok;
          return tok.accessToken;
        }
        failures.push(name + "=empty");
      } catch (e) {
        failures.push(name + "=" + ((e && e.message) || e));
      }
    }
    // Every source still offers the refused token. A doubtful token beats none —
    // the next 401 asks again — so hand it back rather than failing outright.
    if (lastResort) {
      rejectedToken = null;
      tokenCache = lastResort;
      return lastResort.accessToken;
    }
    logTokenDiag();
    throw new Error("no spotify token available (" + failures.join("; ") + ")");
  }

  // ---------- SpicyLyrics API ----------
  const API_HOST = "https://api.spicylyrics.org";
  // Known-good version used only to bootstrap the ext_version query; the real
  // latest version returned by the API replaces it immediately after.
  const BOOTSTRAP_VERSION = "6.1.1";
  // spicy-lyrics sends only the bare "x.y.z" (Session.ParseVersion); a raw
  // LoadedVersion can carry suffixes the API doesn't recognise.
  const parseVersion = (v) => {
    const m = typeof v === "string" ? v.match(/(\d+)\.(\d+)\.(\d+)/) : null;
    return m ? m[0] : null;
  };
  let extVersion =
    parseVersion(window._spicy_lyrics_metadata && window._spicy_lyrics_metadata.LoadedVersion);

  // Same deadline spicy-lyrics uses: fetch has none, and a stalled request would
  // otherwise just sit until the overlay's own 20s bridge timeout.
  const REQUEST_TIMEOUT_MS = 15_000;

  async function apiQuery(queries, headers) {
    const version = extVersion || BOOTSTRAP_VERSION;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    try {
      const res = await fetch(API_HOST + "/query", {
        method: "POST",
        signal: controller.signal,
        headers: {
          "Content-Type": "application/json",
          "SpicyLyrics-Version": version,
          // Sent by every spicy-lyrics client request (utils/API/Query.ts).
          "X-mode": "2",
          ...headers,
        },
        body: JSON.stringify({ queries, client: { version } }),
      });
      if (!res.ok) throw new Error("api http " + res.status);
      return await res.json();
    } finally {
      clearTimeout(timer);
    }
  }

  const jobResult = (data) => {
    const qs = (data && data.queries) || [];
    const job = qs.find((q) => q.operationId === "0") || qs[0];
    return job && job.result ? job.result : null;
  };

  async function ensureVersion() {
    if (extVersion) return extVersion;
    // spicy-lyrics may have loaded after us — pick up its version if present now.
    const meta = window._spicy_lyrics_metadata;
    const loaded = parseVersion(meta && meta.LoadedVersion);
    if (loaded) {
      extVersion = loaded;
      return extVersion;
    }
    try {
      const r = jobResult(await apiQuery([{ operation: "ext_version" }], {}));
      if (r && r.httpStatus === 200) extVersion = parseVersion(r.data);
    } catch (e) {
      LOG("ext_version fetch failed", e);
    }
    if (!extVersion) extVersion = BOOTSTRAP_VERSION;
    return extVersion;
  }

  async function lyricsQuery(trackId, token) {
    return jobResult(await apiQuery(
      [{ operation: "lyrics", variables: { id: trackId, auth: "SpicyLyrics-WebAuth" } }],
      { "SpicyLyrics-WebAuth": "Bearer " + token }
    ));
  }

  async function fetchLyrics(trackId) {
    await ensureVersion();
    const token = await getToken();
    let r = await lyricsQuery(trackId, token);

    // The token looked valid to us but the API refused it — the client rotated it
    // early. Retire it and retry exactly once with a fresh one (as spicy-lyrics does);
    // previously a 401 dropped straight through to line-level LRCLIB lyrics.
    if (r && r.httpStatus === 401) {
      LOG("lyrics: 401, refreshing token and retrying once");
      invalidateToken(token);
      let fresh = null;
      try { fresh = await getToken(); } catch (e) { /* keep the 401 */ }
      if (fresh && fresh !== token) r = await lyricsQuery(trackId, fresh);
    }

    const status = r ? r.httpStatus : 0;
    if (status === 200) {
      const lyrics = slUnpack(r.data);
      if (lyrics == null || lyrics === "") return { status: 404 };
      return { status: 200, lyrics };
    }
    return { status };
  }

  // ---------- Spotify search ----------
  // Preferred path: the client's own GraphQL search (Spicetify keeps the
  // persisted-query hashes current). It runs on Spotify's internal quota, so it
  // doesn't hit the public Web API's aggressive per-app rate limit (HTTP 429).
  let gqlDiagShown = false;

  // Spotify's GraphQL response shapes differ per query definition and change
  // between client versions, so instead of hardcoding a path we walk the whole
  // response and collect every track node we find (in encounter order, which
  // preserves Spotify's own relevance ranking).
  function collectTracks(node, out, seen, depth) {
    if (!node || typeof node !== "object" || depth > 12 || out.length >= 20) return;
    if (Array.isArray(node)) {
      for (const child of node) collectTracks(child, out, seen, depth + 1);
      return;
    }
    const uri = node.uri;
    if (typeof uri === "string" && uri.startsWith("spotify:track:") && node.name) {
      const id = uri.split(":")[2];
      if (!seen.has(id)) {
        seen.add(id);
        out.push({
          id,
          name: node.name,
          artists: ((node.artists && node.artists.items) || [])
            .map((a) => (a && a.profile && a.profile.name) || "")
            .filter(Boolean),
          durationMs: (node.duration && node.duration.totalMilliseconds) ||
                      node.duration_ms || node.durationMs || 0,
          album: (node.albumOfTrack && node.albumOfTrack.name) || null,
        });
      }
    }
    for (const k in node) {
      if (Object.prototype.hasOwnProperty.call(node, k)) {
        collectTracks(node[k], out, seen, depth + 1);
      }
    }
  }

  async function searchViaGraphQL(query) {
    const G = window.Spicetify && Spicetify.GraphQL;
    const defs = G && G.Definitions;
    // searchModalResults is what the client's own search modal uses, but it only
    // returns *top* results — when those are all playlists/artists it yields no
    // tracks and we fell through to the rate-limited Web API (429). So try each
    // search definition the client has, track-focused ones first, until one
    // produces tracks.
    const candidates = defs
      ? ["searchTracks", "searchDesktop", "searchModalResults"].filter((n) => defs[n])
      : [];
    if (!G || candidates.length === 0 || typeof G.Request !== "function") {
      if (!gqlDiagShown) {
        gqlDiagShown = true;
        const names = defs ? Object.keys(defs) : [];
        LOG("graphql unavailable — GraphQL:" + !!G + " Request:" + (G && typeof G.Request) +
            " searchDefs:[" + names.filter((n) => /search/i.test(n)).join(",") + "]");
      }
      return null;
    }

    // Spotify's search queries declare a pile of non-null feature-flag
    // variables; omitting any one of them fails the whole request, so send the
    // full known set. Extra unused variables are harmless.
    const vars = {
      searchTerm: query,
      offset: 0,
      limit: 10,
      numberOfTopResults: 5,
      includeAudiobooks: true,
      includeArtistHasConcertsField: false,
      includePreReleases: true,
      includeLocalConcertsField: false,
      includeAuthors: false,
      includeUsers: false,
      includeGenres: false,
    };

    let lastErr = null;
    for (const name of candidates) {
      let res;
      try {
        res = await G.Request(defs[name], vars);
      } catch (e) {
        lastErr = e;
        continue;
      }
      const out = [];
      collectTracks(res, out, new Set(), 0);
      if (out.length > 0) return out;
      if (!gqlDiagShown) {
        gqlDiagShown = true;
        LOG("graphql " + name + " returned no tracks; shape: " + JSON.stringify(res).slice(0, 300));
      }
    }
    if (lastErr) throw lastErr;
    return [];
  }

  async function searchViaWebApi(query) {
    const url =
      "https://api.spotify.com/v1/search?type=track&limit=10&q=" + encodeURIComponent(query);
    let j = null;
    try {
      // CosmosAsync attaches the client's full auth (incl. client-token) —
      // a raw fetch with just the bearer token gets rejected by the Web API.
      j = await Spicetify.CosmosAsync.get(url);
      if (!j || !j.tracks) throw new Error("cosmos bad shape: " + JSON.stringify(j).slice(0, 120));
    } catch (e) {
      const token = await getToken();
      const res = await fetch(url, { headers: { Authorization: "Bearer " + token } });
      if (!res.ok) throw new Error("search http " + res.status + " " + (await res.text()).slice(0, 100));
      j = await res.json();
    }
    return ((j && j.tracks && j.tracks.items) || []).map((t) => ({
      id: t.id,
      name: t.name,
      artists: (t.artists || []).map((a) => a.name),
      durationMs: t.duration_ms,
      album: t.album ? t.album.name : null,
    }));
  }

  function describeError(e) {
    if (!e) return "?";
    const parts = [e.name || "Error"];
    if (e.message) parts.push(e.message);
    if (e.status) parts.push("status=" + e.status);
    if (e.stack) parts.push(String(e.stack).split("\n")[0]);
    try {
      const j = JSON.stringify(e);
      if (j && j !== "{}") parts.push(j.slice(0, 250));
    } catch (x) { /* circular */ }
    return parts.join(" | ");
  }

  async function searchTracks(query) {
    try {
      const viaGql = await searchViaGraphQL(query);
      if (viaGql && viaGql.length > 0) return viaGql;
      LOG("graphql search unavailable/empty, using web api");
    } catch (e) {
      LOG("graphql search failed, using web api: " + describeError(e));
    }
    return await searchViaWebApi(query);
  }

  // ---------- Player state push ----------
  function playerState() {
    try {
      const d = Spicetify.Player.data;
      if (!d || !d.item) return null;
      return {
        type: "sp_state",
        playing: Spicetify.Player.isPlaying(),
        positionMs: Spicetify.Player.getProgress(),
        durationMs: Spicetify.Player.getDuration(),
        trackId: (d.item.uri || "").split(":")[2] || null,
        title: d.item.name || "",
        artist: (d.item.artists || []).map((a) => a.name).join(", "),
        ts: Date.now(),
      };
    } catch (e) {
      return null;
    }
  }

  // ---------- WebSocket client (dials out to the overlay app) ----------
  let ws = null;
  let pushTimer = null;
  let eventsBound = false;
  const PUSH_MS = 250;

  function pushState() {
    const st = playerState();
    if (st && ws && ws.readyState === WebSocket.OPEN) {
      try { ws.send(JSON.stringify(st)); } catch (e) {}
    }
  }

  // Push the moment Spotify itself changes state, rather than up to PUSH_MS later.
  // Mix mode seeks the incoming track to a non-zero start offset as it blends, so the
  // overlay has to learn the new baseline immediately or it stays behind for the song.
  function bindPlayerEvents() {
    if (eventsBound) return;
    eventsBound = true;
    try {
      Spicetify.Player.addEventListener("songchange", () => {
        pushState();
        // getProgress() can still report the outgoing track for a tick after the event.
        setTimeout(pushState, 80);
        setTimeout(pushState, 300);
      });
      Spicetify.Player.addEventListener("onplaypause", pushState);
    } catch (e) {
      LOG("could not bind player events: " + describeError(e));
    }
  }

  async function handleMessage(msg) {
    let req;
    try {
      req = JSON.parse(msg);
    } catch (e) {
      return;
    }
    const reply = { type: "resp", reqId: req.reqId };
    try {
      if (req.type === "search") {
        reply.ok = true;
        reply.results = await searchTracks(req.query);
      } else if (req.type === "lyrics") {
        const r = await fetchLyrics(req.trackId);
        reply.ok = true;
        reply.status = r.status;
        if (r.status === 200) reply.lyrics = r.lyrics;
      } else if (req.type === "ping") {
        reply.ok = true;
        reply.pong = true;
      } else {
        reply.ok = false;
        reply.error = "unknown request type";
      }
    } catch (e) {
      reply.ok = false;
      reply.error = String((e && e.message) || e);
    }
    try {
      if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(reply));
    } catch (e) { /* ignore */ }
  }

  function connect() {
    try {
      ws = new WebSocket(BRIDGE_URL);
    } catch (e) {
      setTimeout(connect, 5000);
      return;
    }
    ws.onopen = () => {
      LOG("connected to overlay app");
      try { ws.send(JSON.stringify({ type: "hello", role: "spicetify", version: extVersion })); } catch (e) {}
      if (pushTimer) clearInterval(pushTimer);
      pushTimer = setInterval(pushState, PUSH_MS);
      bindPlayerEvents();
    };
    ws.onmessage = (ev) => handleMessage(ev.data);
    ws.onclose = () => {
      if (pushTimer) { clearInterval(pushTimer); pushTimer = null; }
      setTimeout(connect, 3000);
    };
    ws.onerror = () => { /* onclose fires next */ };
  }

  // Wait for Spicetify to be ready, then start.
  (function waitReady() {
    if (
      window.Spicetify &&
      Spicetify.CosmosAsync &&
      Spicetify.Player &&
      Spicetify.Platform
    ) {
      LOG("starting");
      ensureVersion();
      connect();
    } else {
      setTimeout(waitReady, 300);
    }
  })();
})();
