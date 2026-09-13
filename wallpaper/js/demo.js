// Offline demo (open index.html?demo): fake track + made-up placeholder lyrics that
// exercise every renderer feature — letter emphasis, a background vocal, a duet
// line, wrapping, interlude dots — and a "song change" every loop. Cover and
// background are generated stand-ins (demo/). ?at=12000 starts 12 s into the song.
"use strict";

const Demo = (() => {
  function build() {
    const Lines = [], Bg = [];
    let t = 3600;
    const line = (words, o = {}) => {
      const Sylls = [];
      let s = o.at ?? t;
      for (const [text, dur, joined] of words) {
        Sylls.push({ Start: s, End: s + dur, Text: text + (joined ? "" : " "), PartOfWord: !!joined });
        s += dur;
      }
      const l = { Start: Sylls[0].Start, End: s, Text: Sylls.map((x) => x.Text).join("").trim(), Sylls, Opposite: !!o.opp };
      if (!o.at) t = s + (o.gap ?? 300);
      return l;
    };
    Lines.push(line([["Neon", 380], ["rivers", 420], ["running", 460], ["through", 300], ["the", 200], ["midnight", 620], ["glass", 700]]));
    Lines.push(line([["Every", 380], ["heartbeat", 520], ["echoes", 480], ["like", 260], ["a", 180], ["sli", 300, true], ["ding", 320], ["bass", 820]]));
    const hold = line([["Hold", 1300], ["on", 1500]], { gap: 500 });
    Lines.push(hold);
    Bg.push(line([["(hold", 500], ["on)", 700]], { at: hold.Start + 1400 }));
    Lines.push(line([["I", 220], ["can", 260], ["hear", 360], ["the", 200], ["city", 420], ["singing", 520], ["back", 360], ["to", 200], ["me", 900]], { opp: true, gap: 4200 }));
    Lines.push(line([["We", 280], ["are", 260], ["gold", 700], ["beneath", 520], ["the", 200], ["static", 520], ["and", 240], ["the", 200], ["rain", 900]]));
    Lines.push(line([["Oooh", 2200]], { gap: 400 }));
    Lines.push(line([["Carry", 420], ["me", 300], ["home", 1100]], { gap: 2500 }));
    return { Type: "Syllable", StartTime: Lines[0].Start, Lines, Bg, Source: "demo" };
  }

  const asset = (p) => new URL(p, location.href).href;

  function start() {
    const lyrics = build();
    const total = lyrics.Lines[lyrics.Lines.length - 1].End + 2500;
    const params = new URLSearchParams(location.search);
    const at = Number(params.get("at")) || 0;
    // ?still: no CSS transitions, so a headless screenshot catches settled states.
    if (params.has("still")) {
      const st = document.createElement("style");
      st.textContent = "*,*::before,*::after{transition:none!important}";
      document.head.appendChild(st);
    }
    let t0 = performance.now() - at, song = 0;
    // Stands in for the user's wallpaper folder in "My wallpapers" mode.
    Background.setFolderFiles([asset("demo/landscape.jpg")]);
    const next = () => {
      song++;
      setTrack({ title: "Neon Rivers", artist: "Demo Artist", album: "Placeholder Sessions", cover: asset("demo/cover.png"), durationMs: total });
      setLyrics("ok", lyrics);
    };
    next();
    setInterval(() => {
      let ms = performance.now() - t0;
      if (ms >= total) { t0 = performance.now(); ms = 0; next(); }
      Clock.update(ms, true, 1);
    }, 250);
  }

  return { start };
})();
