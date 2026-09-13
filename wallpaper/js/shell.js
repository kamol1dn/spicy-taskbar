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
      </div>
    </section>
    <section id="lyrics">
      <div id="lyricsContent"></div>
      <div id="loader"><span></span><span></span><span></span></div>
    </section>
  </main>
`);
