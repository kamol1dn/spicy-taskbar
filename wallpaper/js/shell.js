// Page markup shared by index.html (desktop) and lockscreen.html, so the two entry
// points differ only in which surface they declare. Runs before the other scripts,
// which look these elements up when they load.
"use strict";

window.WALLPAPER_SURFACE = window.WALLPAPER_SURFACE ||
  new URLSearchParams(location.search).get("surface") || "desktop";

document.body.dataset.surface = window.WALLPAPER_SURFACE;
document.body.insertAdjacentHTML("afterbegin", `
  <div id="bgRoot">
    <canvas id="dyn"></canvas>
    <div id="dynCss"></div>
    <div id="wp"></div>
    <div id="scrim"></div>
  </div>

  <div id="clock"><div id="clockTime"></div><div id="clockDate"></div></div>

  <main id="stage">
    <section id="card">
      <div class="coverWrap"><img id="cover" alt=""></div>
      <div class="info">
        <div class="meta">
          <div id="title"></div>
          <div id="artist"></div>
          <div id="album"></div>
        </div>
        <div class="progress">
          <div class="bar"><div id="progressFill"></div></div>
          <div class="times"><span id="elapsed"></span><span id="remaining"></span></div>
        </div>
        <div id="controls">
          <button data-act="prev" aria-label="Previous"><svg viewBox="0 0 24 24"><path d="M6 6h2v12H6zm3.5 6 8.5 6V6z"/></svg></button>
          <button data-act="toggle" aria-label="Play/pause"><svg class="i-pause" viewBox="0 0 24 24"><path d="M6 5h4v14H6zm8 0h4v14h-4z"/></svg><svg class="i-play" viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg></button>
          <button data-act="next" aria-label="Next"><svg viewBox="0 0 24 24"><path d="M16 6h2v12h-2zM6 18l8.5-6L6 6z"/></svg></button>
        </div>
      </div>
    </section>
    <section id="lyrics">
      <div id="lyricsContent"></div>
      <div id="loader"><span></span><span></span><span></span></div>
    </section>
  </main>
`);
