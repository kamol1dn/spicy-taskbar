// Wires everything together: Wallpaper Engine properties + audio, the TaskbarLyrics
// bridge (ws://localhost:PORT/wallpaper) for track/lyrics/position, Wallpaper
// Engine's own media integration as a fallback when the app isn't running, the
// now-playing card, the idle clock and the render loop.
"use strict";

// ---------------- settings ----------------
const settings = {
  background: "wallpapers",
  collection: "all",
  wallpaperblur: 0,
  dim: 35,
  kenburns: true,
  dynspeed: 100,
  audioreactive: true,
  layout: "split",
  lyricssize: 100,
  linepos: 45,
  lineblur: true,
  spicyfont: true,
  clock: true,
  offset: 0,
  port: "9012",
  folder: "",
  fps: 60,
};

// Wallpaper Engine injects its APIs before page scripts run; anything else (Aura,
// a browser) gets folder listing and audio from TaskbarLyrics over the bridge.
const IN_WE = typeof window.wallpaperRegisterAudioListener === "function";

// config.js, then URL parameters (?background=dynamic&lyricssize=120). Values are
// coerced to the type of the default so "false"/"120" come out as bool/number.
(function loadConfig() {
  const apply = (key, raw) => {
    if (!(key in settings)) return;
    const def = settings[key];
    if (typeof def === "boolean") settings[key] = raw === true || raw === "true" || raw === "1";
    else if (typeof def === "number") { const n = Number(raw); if (!Number.isNaN(n)) settings[key] = n; }
    else settings[key] = String(raw);
  };
  const lock = window.WALLPAPER_SURFACE === "lockscreen";
  if (lock) { settings.layout = "lock"; settings.clock = false; }
  const cfg = window.WALLPAPER_CONFIG || {};
  for (const k of Object.keys(cfg)) apply(k, cfg[k]);
  if (lock && cfg.lockscreen) for (const k of Object.keys(cfg.lockscreen)) apply(k, cfg.lockscreen[k]);
  for (const [k, v] of new URLSearchParams(location.search)) apply(k, v);
})();

function applySettings() {
  const root = document.documentElement.style;
  root.setProperty("--dim", settings.dim / 100);
  root.setProperty("--lyrics-scale", settings.lyricssize / 100);
  document.body.dataset.layout = settings.layout;
  document.body.classList.toggle("spicyfont", settings.spicyfont);
  document.body.classList.toggle("noclock", !settings.clock);
  Background.setOptions({
    mode: settings.background,
    collection: settings.collection,
    blur: settings.wallpaperblur,
    kenBurns: settings.kenburns,
    speed: settings.dynspeed / 100,
    audioReactive: settings.audioreactive,
  });
  LyricsView.setOptions({ blur: settings.lineblur, linePos: settings.linepos / 100 });
}

// Must exist globally before Wallpaper Engine starts calling in.
window.wallpaperPropertyListener = {
  applyUserProperties(p) {
    let portChanged = false;
    for (const key of Object.keys(p)) {
      if (!(key in settings) || !p[key] || p[key].value === undefined) continue;
      if (key === "port" && String(p[key].value) !== settings.port) portChanged = true;
      settings[key] = p[key].value;
    }
    applySettings();
    if (portChanged) Bridge.reconnect();
  },
  applyGeneralProperties(p) {
    if (p.fps) maxFps = p.fps;
  },
  setPaused(paused) {
    wePaused = paused;
    if (!paused) requestAnimationFrame(loop);
  },
  userDirectoryFilesAddedOrChanged(prop, files) {
    if (prop === "wallpaperfolder") Background.addFiles(files);
  },
  userDirectoryFilesRemoved(prop, files) {
    if (prop === "wallpaperfolder") Background.removeFiles(files);
  },
};

// ---------------- playback clock ----------------
// Position arrives a few times a second; interpolate in between, and slew small
// corrections instead of snapping so the karaoke fill never visibly jitters.
const Clock = {
  base: 0, at: performance.now(), playing: false, rate: 1, has: false,
  now() {
    const el = this.playing ? (performance.now() - this.at) * this.rate : 0;
    return this.base + el;
  },
  update(ms, playing, rate) {
    const predicted = this.now();
    const err = ms - predicted;
    if (!this.has || playing !== this.playing || Math.abs(err) > 300) {
      this.base = ms;
    } else {
      this.base = predicted + err * 0.25;
    }
    this.at = performance.now();
    this.playing = playing;
    this.rate = rate || 1;
    this.has = true;
  },
};

// ---------------- state ----------------
const state = {
  track: null,        // { title, artist, album, cover, durationMs }
  trackKey: null,
  art: null,          // SMTC art (data URL) from the bridge
  weArt: null,        // Wallpaper Engine media thumbnail (fallback)
  lyricsState: "none",
};

const $ = (id) => document.getElementById(id);

function coverSource() {
  return (state.track && state.track.cover) || state.art || state.weArt || null;
}

function refreshCover() {
  const src = coverSource();
  const img = $("cover");
  if (src && img.dataset.src !== src) {
    img.dataset.src = src;
    const pre = new Image();
    pre.onload = () => { if (img.dataset.src === src) { img.src = src; img.classList.add("ready"); } };
    pre.src = src;
  } else if (!src) {
    img.classList.remove("ready");
    delete img.dataset.src;
  }
  if (src) Background.setCover(src);
}

function setTrack(track) {
  // First artist only: Spotify lists every artist ("ROSÉ, Bruno Mars") where
  // Windows' media info may carry just the first, and that isn't a new song.
  const key = track ? `${track.title}|${(track.artist || "").split(",")[0].trim()}`.toLowerCase() : null;
  const changed = key !== state.trackKey;
  state.track = track;
  state.trackKey = key;
  if (changed) {
    state.art = null;
    if (track) Background.songChanged();
  }
  document.body.classList.toggle("idle", !track);
  $("title").textContent = track ? track.title : "";
  $("artist").textContent = track ? track.artist : "";
  $("album").textContent = track && track.album ? track.album : "";
  refreshCover();
}

let lyricsSig = null, lyricsKey = null;
function setLyrics(st, lyrics) {
  // The app re-announces a track when its identity firms up (SMTC -> Spotify id)
  // and re-resolves it. Keep the lines on screen through that instead of
  // rebuilding them mid-song: ignore "loading" for the song already shown, and
  // identical payloads.
  if (st === "loading" && lyricsKey === state.trackKey && LyricsView.hasLyrics) return;
  const sig = st === "ok" ? JSON.stringify(lyrics) : st;
  if (sig === lyricsSig && lyricsKey === state.trackKey) return;
  lyricsSig = sig;
  lyricsKey = state.trackKey;
  state.lyricsState = st;
  LyricsView.set(st === "ok" ? lyrics : null);
  // While loading keep the split layout, so the card doesn't jump when lyrics land.
  document.body.classList.toggle("nolyrics", st === "none");
  document.body.classList.toggle("loading", st === "loading");
}

// ---------------- bridge (TaskbarLyrics) ----------------
const Bridge = (() => {
  let ws = null, timer = null, connected = false;

  function connect() {
    clearTimeout(timer);
    try {
      ws = new WebSocket(`ws://localhost:${settings.port}/wallpaper`);
    } catch (e) {
      timer = setTimeout(connect, 3000);
      return;
    }
    ws.onopen = () => {
      connected = true;
      document.body.classList.add("bridged");
      requestExtras();
    };
    ws.onclose = () => {
      if (connected) {
        connected = false;
        document.body.classList.remove("bridged");
        setLyrics("none", null); // no lyrics source without the app
        WeMedia.replay();
      }
      timer = setTimeout(connect, 3000);
    };
    ws.onerror = () => {};
    ws.onmessage = (ev) => {
      let m;
      try { m = JSON.parse(ev.data); } catch (e) { return; }
      switch (m.type) {
        case "track": setTrack(m.track); break;
        case "lyrics": setLyrics(m.state, m.lyrics); break;
        case "art": state.art = m.dataUrl; refreshCover(); break;
        case "pos": if (m.has) Clock.update(m.ms, m.playing, m.rate); break;
        case "wallpapers": gotWallpaperList(m); break;
        case "audio": Background.setAudioLevel(Math.min(1, (m.bass || 0) * 1.1)); break;
      }
    };
  }

  function send(obj) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
  }

  // What Wallpaper Engine would otherwise provide: the image folder (when no folder
  // was picked in its panel) and the audio level.
  function requestExtras() {
    if (settings.folder && !Background.hasFolderFiles) send({ type: "list-wallpapers", dir: settings.folder });
    if (!IN_WE) send({ type: "audio", on: !!settings.audioreactive });
  }

  return {
    start: connect,
    reconnect() { if (ws) { try { ws.onclose = null; ws.close(); } catch (e) {} } connected = false; connect(); },
    requestExtras,
    get connected() { return connected; },
  };
})();

// ---------------- wallpaper folder via the bridge (non-WE hosts) ----------------
// Remember the last listing so wallpapers still rotate while TaskbarLyrics is closed.
const LIST_CACHE = "spicywp.folderList";
function gotWallpaperList(m) {
  if (!m.files || !m.files.length) {
    if (m.error) console.warn("wallpaper folder:", m.dir, m.error);
    return;
  }
  Background.setFolderFiles(m.files);
  try { localStorage.setItem(LIST_CACHE, JSON.stringify({ dir: m.dir, files: m.files })); } catch (e) {}
}
(function restoreListing() {
  if (IN_WE || !settings.folder) return;
  try {
    const c = JSON.parse(localStorage.getItem(LIST_CACHE) || "null");
    if (c && c.dir === settings.folder && c.files && c.files.length) Background.setFolderFiles(c.files);
  } catch (e) {}
})();

// ---------------- Wallpaper Engine media integration (fallback) ----------------
// Works for any player Windows knows about, without TaskbarLyrics running:
// no lyrics, but the card, the cover gradient and per-song wallpapers still work.
const WeMedia = (() => {
  let props = null, playing = false, timeline = null;
  const push = () => {
    if (Bridge.connected) return;
    setTrack(props && props.title ? { title: props.title, artist: props.artist || "", album: props.albumTitle || "", durationMs: timeline ? timeline.duration * 1000 : 0 } : null);
    if (timeline) Clock.update(timeline.position * 1000, playing, 1);
  };
  if (window.wallpaperRegisterMediaPropertiesListener) {
    window.wallpaperRegisterMediaPropertiesListener((e) => { props = e; push(); });
    window.wallpaperRegisterMediaThumbnailListener((e) => { state.weArt = e.thumbnail || null; refreshCover(); });
    window.wallpaperRegisterMediaPlaybackListener((e) => { playing = e.state === 1; push(); });
    window.wallpaperRegisterMediaTimelineListener((e) => { timeline = e; push(); });
  }
  return { replay: push };
})();

// ---------------- audio ----------------
if (window.wallpaperRegisterAudioListener) {
  window.wallpaperRegisterAudioListener((a) => {
    // 64 bins per channel, low frequencies first.
    let s = 0;
    for (let i = 0; i < 6; i++) s += a[i] + a[64 + i];
    Background.setAudioLevel(Math.min(1, (s / 12) * 1.3));
  });
}

// ---------------- card + clock ----------------
const fmt = (ms) => {
  const s = Math.max(0, Math.floor(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};

let lastClockText = "";
function updateChrome(t) {
  const dur = state.track ? state.track.durationMs : 0;
  if (dur > 0) {
    $("progressFill").style.transform = `scaleX(${clamp(t / dur, 0, 1)})`;
    const e = fmt(t), r = "-" + fmt(dur - t);
    if ($("elapsed").textContent !== e) $("elapsed").textContent = e;
    if ($("remaining").textContent !== r) $("remaining").textContent = r;
  }
  document.body.classList.toggle("nodur", !(dur > 0));
  document.body.classList.toggle("paused", !Clock.playing);

  const d = new Date();
  const txt = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  if (txt !== lastClockText) {
    lastClockText = txt;
    $("clockTime").textContent = txt;
    $("clockDate").textContent = d.toLocaleDateString([], { weekday: "long", month: "long", day: "numeric" });
  }
}

// ---------------- loop ----------------
let maxFps = settings.fps || 60, wePaused = false, lastFrame = 0, lastDraw = 0;
function loop(now) {
  if (wePaused) return;
  requestAnimationFrame(loop);
  // Respect Wallpaper Engine's FPS limit.
  if (now - lastDraw < 1000 / maxFps - 1) return;
  const dt = Math.min(0.1, (now - (lastFrame || now)) / 1000);
  lastFrame = now;
  lastDraw = now;
  const t = Clock.now() + Number(settings.offset || 0);
  Background.frame(dt);
  LyricsView.frame(t, dt);
  updateChrome(t);
}

// ---------------- boot ----------------
applySettings();
setTrack(null);
setLyrics("none", null);
if (new URLSearchParams(location.search).has("demo")) Demo.start();
else Bridge.start();
requestAnimationFrame(loop);
