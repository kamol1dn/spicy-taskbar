// Clicks and taps on the wallpaper: tap a lyric line to jump to it (as in Spicy Lyrics),
// tap the cover to play/pause, the progress bar to seek, and the prev/play/next buttons.
//
// Only hosts that can act on it turn this on — the Linux app announces it in its hello
// ({caps: {controls: true}}). The page tells the host where its clickable parts are
// ({type:"hitboxes"}), so the host makes just those regions take input (the rest of the
// desktop stays click-through) and the lock screen can relay taps from its own surface
// ({type:"tap", x, y}), since a locked session only gives input to the locker.
"use strict";

const Interact = (() => {
  let supported = false; // the host can act on controls
  let wanted = true;     // tray: "Clickable lyrics & controls"
  let lastSig = "";
  const on = () => supported && wanted;

  function rectOf(el, pad) {
    if (!el) return null;
    const cs = getComputedStyle(el);
    if (cs.display === "none" || cs.visibility === "hidden") return null;
    const r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return null;
    return {
      x: Math.round(r.left - pad), y: Math.round(r.top - pad),
      w: Math.round(r.width + 2 * pad), h: Math.round(r.height + 2 * pad),
    };
  }

  function hitboxes() {
    const b = document.body.classList;
    if (!on() || b.contains("idle")) return [];
    const out = [];
    const add = (el, pad, kind) => { const r = rectOf(el, pad); if (r) { r.kind = kind; out.push(r); } };
    add(document.querySelector(".coverWrap"), 0, "cover");
    add(document.getElementById("controls"), 4, "controls");
    if (!b.contains("nodur")) add(document.querySelector(".progress"), 8, "progress");
    if (!b.contains("nolyrics")) add(document.getElementById("lyrics"), 0, "lyrics");
    return out;
  }

  function publish(force) {
    const rects = hitboxes();
    const sig = JSON.stringify(rects);
    if (!force && sig === lastSig) return;
    lastSig = sig;
    Bridge.send({ type: "hitboxes", rects });
  }

  function control(action, extra) {
    Bridge.send(Object.assign({ type: "control", action }, extra || {}));
  }

  // `ms` is lyric time (what the rows are stamped with). The playback clock runs
  // `settings.offset` behind it; the host takes off its own sync nudge.
  function seek(ms) {
    const clockMs = Math.max(0, ms - Number(settings.offset || 0));
    Clock.update(clockMs, Clock.playing, Clock.rate); // move the lines now, not on the next push
    control("seek", { ms: clockMs });
  }

  function flash(el) {
    el.classList.remove("tapped");
    void el.offsetWidth;
    el.classList.add("tapped");
  }

  function act(el, x) {
    if (!on() || !el || !el.closest) return;
    const btn = el.closest("[data-act]");
    if (btn) {
      flash(btn);
      if (btn.dataset.act === "toggle") {
        control("playpause");
        Clock.update(Clock.now(), !Clock.playing, Clock.rate);
      } else {
        control(btn.dataset.act);
      }
      return;
    }
    if (el.closest(".coverWrap")) {
      control("playpause");
      Clock.update(Clock.now(), !Clock.playing, Clock.rate);
      return;
    }
    const prog = el.closest(".progress");
    if (prog) {
      const dur = state.track ? state.track.durationMs : 0;
      const r = prog.querySelector(".bar").getBoundingClientRect();
      if (dur > 0 && r.width > 0) seek(clamp((x - r.left) / r.width, 0, 1) * dur + Number(settings.offset || 0));
      return;
    }
    const row = el.closest(".row.lyric");
    if (row && row.dataset.start) {
      flash(row);
      seek(Number(row.dataset.start));
    }
  }

  document.addEventListener("click", (e) => act(e.target, e.clientX));
  window.addEventListener("resize", () => publish(false));
  setInterval(() => publish(false), 500); // layout moves with track/lyrics/surface changes

  return {
    setSupported(v) {
      supported = !!v;
      document.body.classList.toggle("interactive", on());
      publish(true);
    },
    setWanted(v) {
      wanted = v !== false;
      document.body.classList.toggle("interactive", on());
      publish(true);
    },
    // A tap relayed from the lock screen, in this page's CSS pixels.
    tap(x, y) { act(document.elementFromPoint(x, y), x); },
    republish() { publish(true); },
  };
})();
