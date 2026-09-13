// Lyrics renderer modelled on spicy-lyrics' own (src/utils/Lyrics/Animator): the
// same curves, springs, gradient sweep, glow, distance blur, per-letter emphasis on
// long syllables and the "• • •" interlude dots. Input is the overlay app's
// normalised model (times in ms):
//   { Type: "Syllable"|"Line", Lines: [{Start, End, Text, Sylls?: [{Start, End, Text, PartOfWord}], Opposite}],
//     Bg: [{Start, End, Text, Sylls?, Opposite}] }
"use strict";

const LyricsView = (() => {
  const viewport = document.getElementById("lyrics");
  const content = document.getElementById("lyricsContent");

  // ---- curves & springs (spicy-lyrics LyricsAnimator.ts) ----
  const C = {
    scale: spline([[0, 0.95], [0.7, 1.0505], [1, 1]]),
    y: spline([[0, 1 / 100], [0.9, -1 / 60], [1, 0]]),
    glow: spline([[0, 0], [0.15, 1], [0.6, 1], [1, 0]]),
    lScale: spline([[0, 0.95], [0.7, 1.175], [1, 1]]),
    lY: spline([[0, 1 / 100], [0.9, -1 / 56], [1, 0]]),
    dScale: spline([[0, 0.75], [0.7, 1.05], [1, 1]]),
    dY: spline([[0, 0], [0.9, -0.12], [1, 0]]),
    dGlow: spline([[0, 0], [0.6, 1], [1, 1]]),
    dOp: spline([[0, 0.35], [0.6, 1], [1, 1]]),
    lineGlow: spline([[0, 0], [0.5, 1], [1, 0]]),
  };
  const sScale = (v) => new Spring(v, 0.88, 0.64);
  const sY = (v) => new Spring(v, 1.45, 0.4);
  const sGlow = (v) => new Spring(v, 1.18, 0.56);

  const GAP_DOTS_MS = 3000;      // getLyricsBetweenShow()
  const PRE_HIDE_DOTS_MS = 500;  // preHiddenDotLineMs
  const LETTER_MIN_MS = 1000;    // IsLetterCapable (non-simple mode)
  const LETTER_END_TRIM = 250;   // Emphasize: letters finish 250ms before the syllable
  const BLUR_PER_LINE = 1.25, BLUR_MAX = 1.25 * 5 + 1.25 * 0.465;

  let rows = [];
  let focus = -1, blurFocus = -2;
  let lastT = -1;
  let blurEnabled = true;
  let linePos = 0.45;
  const scroll = new Spring(0, 0.85, 0.78);
  const moving = new Set();       // rows whose springs are still settling
  const RTL = /[֐-ࣿיִ-﷿ﹰ-﻿]/;

  // ---------- build ----------
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }

  function makeSyl(seg, parent, isBg) {
    let text = seg.Text;
    if (!seg.PartOfWord && text.endsWith(" ")) text = text.slice(0, -1);
    const syl = {
      start: seg.Start, end: seg.End, bg: isBg, el: el("span", "syl"),
      sc: sScale(C.scale(0)), y: sY(C.y(0)), g: sGlow(0), letters: null, w: {},
    };
    const chars = Array.from(text);
    if (seg.End - seg.Start >= LETTER_MIN_MS && text.trim().length > 0 && !RTL.test(text)) {
      // Long held syllable: animate letter by letter (spicy's "Emphasize").
      syl.el.classList.add("lg");
      const end = Math.max(seg.Start + 1, seg.End - LETTER_END_TRIM);
      const per = (end - seg.Start) / chars.length;
      syl.letters = chars.map((ch, i) => {
        const le = el("span", "let" + (ch.trim() ? "" : " sp"), ch);
        syl.el.appendChild(le);
        return {
          el: le, start: seg.Start + i * per, end: seg.Start + (i + 1) * per,
          sc: sScale(C.lScale(0)), y: sY(C.lY(0)), g: sGlow(0), w: {},
        };
      });
    } else {
      syl.el.textContent = text;
    }
    parent.appendChild(syl.el);
    return syl;
  }

  function buildLine(line, cls, isBg, syls) {
    const le = el("div", "line " + cls);
    if (line.Sylls && line.Sylls.length) {
      let group = null;
      line.Sylls.forEach((seg, i) => {
        if (!group) group = el("span", "wg");
        syls.push(makeSyl(seg, group, isBg));
        // A word ends at a syllable that isn't joined to the next one.
        if (!seg.PartOfWord || i === line.Sylls.length - 1) { le.appendChild(group); group = null; }
      });
    } else {
      le.classList.add("whole");
      le.textContent = line.Text;
    }
    return le;
  }

  function dotRow(start, end, opposite) {
    const r = { kind: "dots", start, end, el: el("div", "row dots" + (opposite ? " opp" : "")), dots: [], state: -1 };
    const g = el("div", "dotGroup");
    const third = (end - start) / 3;
    for (let i = 0; i < 3; i++) {
      const d = el("span", "dot", "•");
      g.appendChild(d);
      r.dots.push({
        el: d, start: start + i * third, end: start + (i + 1) * third,
        sc: new Spring(C.dScale(0), 0.7, 0.6), y: new Spring(0, 1.25, 0.4),
        g: new Spring(0, 1, 0.5), op: new Spring(C.dOp(0), 1, 0.5), w: {},
      });
    }
    r.el.appendChild(g);
    return r;
  }

  function set(lyrics) {
    content.textContent = "";
    rows = [];
    moving.clear();
    focus = -1; blurFocus = -2; lastT = -1;
    if (!lyrics || !lyrics.Lines || !lyrics.Lines.length) return;

    const lines = lyrics.Lines;
    const lineType = lyrics.Type !== "Syllable";

    // Background vocals belong to the lead line they overlap most.
    const bgFor = lines.map(() => []);
    for (const bg of lyrics.Bg || []) {
      let best = -1, bestOv = -Infinity;
      lines.forEach((l, i) => {
        const ov = Math.min(bg.End, l.End + 400) - Math.max(bg.Start, l.Start - 400);
        if (ov > bestOv) { bestOv = ov; best = i; }
      });
      if (best >= 0) bgFor[best].push(bg);
    }

    const first = lines[0];
    if (first.Start >= GAP_DOTS_MS) rows.push(dotRow(0, first.Start, first.Opposite));

    lines.forEach((line, i) => {
      const r = {
        kind: lineType ? "line" : "syl", el: el("div", "row lyric" + (line.Opposite ? " opp" : "")),
        start: line.Start, end: line.End, syls: [], state: -1, lead: null,
      };
      r.lead = buildLine(line, "lead", false, r.syls);
      r.el.appendChild(r.lead);
      if (lineType) r.lg = sGlow(0);
      for (const bg of bgFor[i]) {
        r.el.appendChild(buildLine(bg, "bgv", true, r.syls));
        r.start = Math.min(r.start, bg.Start);
        r.end = Math.max(r.end, bg.End);
      }
      if (RTL.test(line.Text)) r.el.classList.add("rtl");
      rows.push(r);

      const next = lines[i + 1];
      if (next && next.Start - r.end >= GAP_DOTS_MS) rows.push(dotRow(r.end, next.Start, next.Opposite));
    });

    for (const r of rows) content.appendChild(r.el);
    snapAll(0);
  }

  // ---------- per-frame ----------
  const stateOf = (t, a, b) => (t < a ? 0 : t >= b ? 2 : 1); // NotSung / Active / Sung
  const pct = (t, a, b) => clamp((t - a) / Math.max(1, b - a), 0, 1);

  function write(o, key, css, val, eps) {
    const prev = o.w[key];
    if (prev !== undefined && Math.abs(prev - val) < eps) return;
    o.w[key] = val;
    o.el.style.setProperty(css, key === "gp" ? val.toFixed(2) + "%" : key === "gr" ? val.toFixed(2) + "px" : String(+val.toFixed(4)));
  }
  function writeTf(o, y, sc) {
    if (o.w.ty !== undefined && Math.abs(o.w.ty - y) < 0.0004 && Math.abs(o.w.ts - sc) < 0.0005) return;
    o.w.ty = y; o.w.ts = sc;
    o.el.style.transform = `translate3d(0,${y.toFixed(4)}em,0) scale(${sc.toFixed(4)})`;
  }

  // Word/syllable (spicy: word branch of the Syllable animator)
  function animSyl(s, t, dt, instant) {
    const st = stateOf(t, s.start, s.end);
    const k = st === 1 ? pct(t, s.start, s.end) : st === 0 ? 0 : 1;
    s.sc.set(C.scale(k), instant); s.y.set(C.y(k), instant); s.g.set(C.glow(k), instant);
    const sc = s.sc.step(dt), y = s.y.step(dt), g = s.g.step(dt);
    writeTf(s, y, sc);
    write(s, "gp", "--gp", st === 1 ? -20 + 120 * k : st === 0 ? -20 : 100, 0.05);
    write(s, "gr", "--gr", 4 + 2 * g, 0.05);
    write(s, "ga", "--ga", Math.min(g * 0.35, 1), 0.005);
    return !(s.sc.resting && s.y.resting && s.g.resting);
  }

  // Long syllable split into letters: the active letter leads, neighbours follow
  // with a steep falloff (spicy: proximity-based letter animation).
  function animLetters(s, t, dt, instant) {
    const ws = stateOf(t, s.start, s.end);
    const L = s.letters;
    let ai = -1, ap = 0;
    for (let k = 0; k < L.length; k++) {
      if (stateOf(t, L[k].start, L[k].end) === 1) { ai = k; ap = pct(t, L[k].start, L[k].end); break; }
    }
    let busy = false;
    for (let k = 0; k < L.length; k++) {
      const l = L[k];
      const ls = stateOf(t, l.start, l.end);
      let ts, ty, tg, gp;
      if (ws === 0 || ls === 0) {
        ts = C.lScale(0); ty = C.lY(0); tg = 0; gp = -20;
      } else if (ws === 2) {
        ts = C.lScale(1); ty = C.lY(1); tg = 0; gp = 100;
      } else {
        ts = C.lScale(0); ty = C.lY(0); tg = 0;
        if (ai !== -1) {
          const d = Math.abs(k - ai);
          const fall = 1 / (1 + Math.pow(d, 2.8)), gfall = 1 / (1 + d * 0.9);
          ts += (C.lScale(ap) - ts) * fall;
          ty += (C.lY(ap) - ty) * fall;
          tg = C.glow(ap) * gfall;
        } else if (ls === 2) {
          tg = C.glow(0.2); // SungLetterGlow: the tail after the last letter finishes
        }
        gp = ls === 2 ? 100 : k === ai ? -20 + 120 * easeSinOut(ap) : -20;
      }
      l.sc.set(ts, instant); l.y.set(ty, instant); l.g.set(tg, instant);
      const sc = l.sc.step(dt), y = l.y.step(dt), g = l.g.step(dt);
      writeTf(l, y * 2, sc);
      write(l, "gp", "--gp", gp, 0.05);
      write(l, "gr", "--gr", 4 + 12 * g, 0.05);
      write(l, "ga", "--ga", Math.min(g * 1.85, 1), 0.005);
      if (!(l.sc.resting && l.y.resting && l.g.resting)) busy = true;
    }
    return busy;
  }

  function animDots(r, t, dt, instant) {
    let busy = false;
    for (const d of r.dots) {
      const st = stateOf(t, d.start, d.end);
      const k = st === 1 ? pct(t, d.start, d.end) : st === 0 ? 0 : 1;
      d.sc.set(C.dScale(k), instant); d.y.set(C.dY(k), instant);
      d.g.set(C.dGlow(k), instant); d.op.set(C.dOp(k), instant);
      const sc = d.sc.step(dt), y = d.y.step(dt), g = d.g.step(dt), op = d.op.step(dt);
      writeTf(d, y, sc);
      write(d, "op", "opacity", op, 0.004);
      write(d, "gr", "--gr", 4 + 6 * g, 0.05);
      write(d, "ga", "--ga", Math.min(g * 0.9, 1), 0.005);
      if (!(d.sc.resting && d.y.resting && d.g.resting && d.op.resting)) busy = true;
    }
    return busy;
  }

  function animLine(r, t, dt, instant) {
    const st = r.state;
    const k = st === 1 ? pct(t, r.start, r.end) : st === 0 ? 0 : 1;
    r.lg.set(st === 1 ? C.lineGlow(k) : 0, instant);
    const g = r.lg.step(dt);
    const o = { el: r.lead, w: r.w || (r.w = {}) };
    write(o, "gp", "--gp", st === 1 ? k * 100 : st === 0 ? -20 : 100, 0.05);
    write(o, "gr", "--gr", 4 + 8 * g, 0.05);
    write(o, "ga", "--ga", Math.min(g * 0.5, 1), 0.005);
    return !r.lg.resting;
  }

  function animRow(r, t, dt, instant) {
    if (r.kind === "dots") return animDots(r, t, dt, instant);
    if (r.kind === "line") return animLine(r, t, dt, instant);
    let busy = false;
    for (const s of r.syls) if (s.letters ? animLetters(s, t, dt, instant) : animSyl(s, t, dt, instant)) busy = true;
    return busy;
  }

  function setRowState(r, st, t) {
    r.state = st;
    r.el.classList.toggle("ns", st === 0);
    r.el.classList.toggle("act", st === 1);
    r.el.classList.toggle("sung", st === 2);
  }

  function snapAll(t) {
    for (const r of rows) {
      setRowState(r, stateOf(t, r.start, r.end), t);
      animRow(r, t, 0, true);
    }
    moving.clear();
  }

  function applyBlur() {
    rows.forEach((r, i) => {
      const d = Math.abs(i - focus);
      const b = blurEnabled && d > 0 ? Math.min(BLUR_PER_LINE * d, BLUR_MAX) : 0;
      if (r.blur !== b) { r.blur = b; r.el.style.setProperty("--blur", b + "px"); }
    });
  }

  function frame(t, dt) {
    if (!rows.length) return;
    // A jump (seek, first frame, resume after a long pause) settles everything in
    // place instead of animating every line through the song at once.
    const jumped = lastT < 0 || Math.abs(t - lastT) > 1500;
    lastT = t;
    if (jumped) snapAll(t);

    let firstActive = -1;
    for (let i = 0; i < rows.length; i++) {
      const r = rows[i];
      const st = stateOf(t, r.start, r.end);
      if (st !== r.state) { setRowState(r, st, t); moving.add(r); }
      if (st === 1 && firstActive < 0) firstActive = i;
      if (r.kind === "dots") {
        r.el.classList.toggle("pre", st === 1 && t > r.end - PRE_HIDE_DOTS_MS);
      }
    }
    if (firstActive >= 0) focus = firstActive;
    else if (focus < 0 || jumped) {
      // Between lines (or before the first): aim at the upcoming one.
      focus = rows.findIndex((r) => r.start > t);
      if (focus < 0) focus = rows.length - 1;
    }
    if (focus !== blurFocus) { blurFocus = focus; applyBlur(); }

    // Scroll: put the focused line at `linePos` of the viewport height.
    const fr = rows[focus];
    const target = fr.el.offsetTop + fr.el.offsetHeight / 2 - viewport.clientHeight * linePos;
    scroll.set(target, jumped);
    content.style.transform = `translate3d(0,${(-scroll.step(dt)).toFixed(2)}px,0)`;

    for (let i = 0; i < rows.length; i++) {
      const r = rows[i];
      if (r.state === 1 || moving.has(r)) {
        const busy = animRow(r, t, dt, false);
        if (!busy && r.state !== 1) moving.delete(r);
      }
    }
  }

  return {
    set,
    frame,
    setOptions(o) {
      if (o.blur !== undefined) { blurEnabled = o.blur; blurFocus = -2; }
      if (o.linePos !== undefined) linePos = o.linePos;
    },
    get hasLyrics() { return rows.length > 0; },
  };
})();
