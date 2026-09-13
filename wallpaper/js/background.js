// Background: either a random image from the user's wallpaper folder (a new one on
// every song change, crossfaded) or spicy-lyrics' "dynamic background" — the album
// cover warped, rotated and heavily blurred on the GPU, pulsing with the bass.
"use strict";

const Background = (() => {
  const wpRoot = document.getElementById("wp");
  const dynCanvas = document.getElementById("dyn");
  const dynCss = document.getElementById("dynCss"); // no-WebGL stand-in for the gradient

  const IMAGE_EXT = /\.(jpe?g|png|webp|gif|bmp|avif)$/i;

  let mode = "wallpapers";      // user choice: "wallpapers" | "dynamic"
  let active = null;            // what is actually showing (wallpapers falls back to dynamic when the folder is empty)
  let blurPx = 0;
  let kenBurns = true;
  let collection = "all";
  let speedMul = 1;
  let audioReactive = true;

  // ---------------- wallpaper pack ----------------
  const files = new Set();
  let bag = [];
  let currentPath = null;
  let currentBitmap = null;
  let loadSeq = 0;
  let failures = 0;      // consecutive images that wouldn't load
  let broken = false;    // gave up on the folder until a new listing arrives

  // Category of an image: its folder name, or the filename prefix before "_"
  // (the pack names files like "nord_a_snowy_peak.jpg").
  function category(path) {
    const parts = path.replace(/\\/g, "/").split("/");
    const name = parts[parts.length - 1].toLowerCase();
    const dir = (parts[parts.length - 2] || "").toLowerCase();
    const prefix = name.includes("_") ? name.slice(0, name.indexOf("_")) : "";
    return [dir, prefix];
  }

  function pool() {
    const all = [...files];
    if (collection === "all") return all;
    const hit = all.filter((p) => category(p).includes(collection));
    return hit.length > 0 ? hit : all;
  }

  // Shuffle-bag: every image shows once before any repeats, never twice in a row.
  function nextPath() {
    const p = pool();
    if (p.length === 0) return null;
    bag = bag.filter((x) => files.has(x) && p.includes(x));
    if (bag.length === 0) {
      bag = p.slice();
      for (let i = bag.length - 1; i > 0; i--) {
        const j = (Math.random() * (i + 1)) | 0;
        [bag[i], bag[j]] = [bag[j], bag[i]];
      }
      if (bag.length > 1 && bag[bag.length - 1] === currentPath) bag.unshift(bag.pop());
    }
    return bag.pop();
  }

  function screenSize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    // Guard a not-yet-laid-out window (0x0 would make zero-size framebuffers).
    return [Math.max(16, Math.round(innerWidth * dpr)), Math.max(16, Math.round(innerHeight * dpr)), dpr];
  }

  async function decodeToBitmap(path) {
    const img = await loadImage(fileUrl(path), false);
    const [w, h] = screenSize();
    // Crop to the screen's aspect ("cover") and resample once, off the main
    // thread where supported — camera photos are 24 MP and would otherwise be
    // rescaled by the compositor every frame of the Ken Burns drift.
    const s = Math.max(w / img.naturalWidth, h / img.naturalHeight);
    const sw = w / s, sh = h / s;
    const sx = (img.naturalWidth - sw) / 2, sy = (img.naturalHeight - sh) / 2;
    try {
      return await createImageBitmap(img, sx, sy, sw, sh, { resizeWidth: w, resizeHeight: h, resizeQuality: "high" });
    } catch (e) {
      return { img, sx, sy, sw, sh, width: w, height: h, close() {} };
    }
  }

  function paintLayer(canvas, bmp) {
    const [w, h, dpr] = screenSize();
    canvas.width = w; canvas.height = h;
    const ctx = canvas.getContext("2d");
    const b = blurPx * dpr;
    // Overdraw by the blur radius so blurred edges don't fade to transparent.
    const m = b * 2;
    if (b > 0) ctx.filter = `blur(${b}px)`;
    if (bmp.img) ctx.drawImage(bmp.img, bmp.sx, bmp.sy, bmp.sw, bmp.sh, -m, -m, w + 2 * m, h + 2 * m);
    else ctx.drawImage(bmp, -m, -m, w + 2 * m, h + 2 * m);
  }

  function showBitmap(bmp) {
    const layer = document.createElement("canvas");
    layer.className = "wpl";
    // Each image drifts toward a different corner.
    layer.style.setProperty("--kx", (Math.random() < 0.5 ? -1 : 1) * (0.6 + Math.random() * 0.8) + "%");
    layer.style.setProperty("--ky", (Math.random() < 0.5 ? -1 : 1) * (0.4 + Math.random() * 0.6) + "%");
    layer.classList.toggle("kb", kenBurns);
    paintLayer(layer, bmp);
    const old = [...wpRoot.children];
    wpRoot.appendChild(layer);
    void layer.offsetWidth; // commit opacity:0 first so the fade-in transition runs
    layer.classList.add("in");
    setTimeout(() => old.forEach((el) => el.remove()), 1600);
  }

  async function showWallpaper(path) {
    if (!path) return;
    const seq = ++loadSeq;
    try {
      const bmp = await decodeToBitmap(path);
      if (seq !== loadSeq) { bmp.close && bmp.close(); return; } // a newer song won
      if (currentBitmap && currentBitmap.close) currentBitmap.close();
      currentBitmap = bmp;
      currentPath = path;
      failures = 0;
      showBitmap(bmp);
    } catch (e) {
      console.warn("wallpaper failed to load", path, e);
      files.delete(path);
      if (seq !== loadSeq) return;
      // A few bad files are skipped; a run of failures means the folder isn't
      // reachable from this page at all — show the gradient rather than nothing.
      if (++failures >= 5 || files.size === 0) {
        broken = true;
        failures = 0;
        apply();
        return;
      }
      showWallpaper(nextPath());
    }
  }

  // ---------------- dynamic (WebGL) ----------------
  let gl = null, prog = {}, fbo = [], quad = null;
  let texA = null, texB = null, mix = 1, coverUrl = null, coverSeq = 0;
  let t = Math.random() * 100, pulse = 0, audioLevel = 0;
  let lowW = 256, lowH = 144;

  const VS = "attribute vec2 a;varying vec2 v;void main(){v=a*.5+.5;gl_Position=vec4(a,0.,1.);}";
  // Three rotating, domain-warped copies of the cover, rendered at low resolution.
  const WARP = `precision mediump float;varying vec2 v;
    uniform sampler2D uA,uB;uniform float uMix,uT,uAsp,uZoom;
    vec2 rot(vec2 p,float a){float c=cos(a),s=sin(a);return vec2(c*p.x-s*p.y,s*p.x+c*p.y);}
    vec3 cover(sampler2D t,vec2 p){
      vec3 c=texture2D(t,rot(p,uT*.05)*.85+.5).rgb*.5;
      c+=texture2D(t,rot(p*1.35+vec2(.25,-.1),-uT*.07+1.7)*.9+.5).rgb*.3;
      c+=texture2D(t,rot(p*.75-vec2(.2,.3),uT*.09+3.1)+.5).rgb*.2;
      return c;}
    void main(){
      vec2 p=(v-.5)*vec2(uAsp,1.)*uZoom;
      p+=.16*vec2(sin(p.y*3.+uT*.8),cos(p.x*2.6-uT*.65));
      p+=.08*vec2(sin(p.y*5.2-uT*1.2+2.),cos(p.x*4.4+uT*1.05));
      gl_FragColor=vec4(mix(cover(uB,p),cover(uA,p),uMix),1.);}`;
  // Dual-Kawase style tap pattern; run several passes with growing offsets.
  const BLUR = `precision mediump float;varying vec2 v;uniform sampler2D uS;uniform vec2 uPx;uniform float uO;
    void main(){vec2 o=uPx*(uO+.5);
      vec3 c=texture2D(uS,v+vec2(-o.x,o.y)).rgb+texture2D(uS,v+o).rgb+texture2D(uS,v-o).rgb+texture2D(uS,v+vec2(o.x,-o.y)).rgb;
      gl_FragColor=vec4(c*.25,1.);}`;
  // Upscale + saturation (spicy uses 1.5) + dithering so the smooth gradient doesn't band.
  const FINAL = `precision mediump float;varying vec2 v;uniform sampler2D uS;uniform float uSat,uSeed;
    float r(vec2 c){return fract(sin(dot(c,vec2(12.9898,78.233))+uSeed)*43758.5453);}
    void main(){vec3 c=texture2D(uS,v).rgb;float l=dot(c,vec3(.299,.587,.114));c=mix(vec3(l),c,uSat);
      c+=(r(gl_FragCoord.xy)-.5)*(2./255.);gl_FragColor=vec4(c,1.);}`;

  function compile(fs) {
    const p = gl.createProgram();
    for (const [type, src] of [[gl.VERTEX_SHADER, VS], [gl.FRAGMENT_SHADER, fs]]) {
      const s = gl.createShader(type);
      gl.shaderSource(s, src);
      gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      gl.attachShader(p, s);
    }
    gl.bindAttribLocation(p, 0, "a");
    gl.linkProgram(p);
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) {
      const info = gl.getActiveUniform(p, i);
      u[info.name] = gl.getUniformLocation(p, info.name);
    }
    return { p, u };
  }

  function makeTex(w, h, wrap) {
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, wrap);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, wrap);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    return tex;
  }

  function initGL() {
    if (gl) return true;
    gl = dynCanvas.getContext("webgl", { alpha: false, antialias: false, depth: false, premultipliedAlpha: false });
    if (!gl) return false;
    try {
      prog.warp = compile(WARP);
      prog.blur = compile(BLUR);
      prog.final = compile(FINAL);
    } catch (e) {
      console.error("dynamic background shader failed", e);
      gl = null;
      return false;
    }
    quad = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    // 256x256 = power of two, so the cover can use MIRRORED_REPEAT in WebGL1.
    texA = makeTex(256, 256, gl.MIRRORED_REPEAT);
    texB = makeTex(256, 256, gl.MIRRORED_REPEAT);
    // Until a cover arrives (or for media with no art), flow a quiet default palette.
    const d = defaultCover();
    for (const tex of [texA, texB]) {
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, d);
    }
    resizeGL();
    return true;
  }

  function defaultCover() {
    const c = document.createElement("canvas");
    c.width = c.height = 256;
    const ctx = c.getContext("2d");
    ctx.fillStyle = "#15122b";
    ctx.fillRect(0, 0, 256, 256);
    for (const [x, y, r, col] of [[60, 70, 150, "#4b2a7a"], [200, 180, 140, "#123f5c"], [190, 50, 110, "#6a2350"], [70, 210, 120, "#1d2f6b"]]) {
      const g = ctx.createRadialGradient(x, y, 0, x, y, r);
      g.addColorStop(0, col);
      g.addColorStop(1, "rgba(0,0,0,0)");
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, 256, 256);
    }
    return c;
  }

  function resizeGL() {
    if (!gl) return;
    const [w, h] = screenSize();
    // The picture is a blur — half resolution is indistinguishable and half the fill cost.
    dynCanvas.width = Math.max(2, w >> 1);
    dynCanvas.height = Math.max(2, h >> 1);
    lowW = 256;
    lowH = Math.max(64, Math.round(256 * (h / w)));
    for (const f of fbo) { gl.deleteFramebuffer(f.fb); gl.deleteTexture(f.tex); }
    fbo = [0, 1].map(() => {
      const tex = makeTex(lowW, lowH, gl.CLAMP_TO_EDGE);
      const fb = gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
      return { fb, tex };
    });
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  }

  async function setCover(url) {
    if (!url || url === coverUrl) return;
    coverUrl = url;
    dynCss.style.backgroundImage = `url("${url}")`;
    const seq = ++coverSeq;
    let img;
    try { img = await loadImage(url, true); } catch (e) { return; }
    if (seq !== coverSeq || !initGL()) return;
    const c = document.createElement("canvas");
    c.width = c.height = 256;
    const ctx = c.getContext("2d");
    const s = Math.min(img.naturalWidth, img.naturalHeight);
    ctx.drawImage(img, (img.naturalWidth - s) / 2, (img.naturalHeight - s) / 2, s, s, 0, 0, 256, 256);
    // Swap: the old cover becomes B and the new one fades in over it.
    [texA, texB] = [texB, texA];
    gl.bindTexture(gl.TEXTURE_2D, texA);
    try {
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, c);
    } catch (e) {
      console.warn("cover not usable in WebGL (no CORS?)", e);
      [texA, texB] = [texB, texA];
      return;
    }
    mix = 0;
  }

  function draw(program, target, w, h) {
    gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fb : null);
    gl.viewport(0, 0, w, h);
    gl.useProgram(program.p);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }

  function renderDynamic(dt) {
    // Bass kicks speed the flow up and let it "breathe", like spicy's
    // BackgroundAnimationController does from Spotify's audio analysis.
    const decay = Math.exp(-5 * dt);
    pulse = Math.max(pulse * decay, audioReactive ? audioLevel : 0);
    t += dt * 0.55 * speedMul * (1 + 1.5 * pulse);
    mix = Math.min(1, mix + dt / 1.0);

    const w = prog.warp;
    gl.useProgram(w.p);
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, texA);
    gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, texB);
    gl.uniform1i(w.u.uA, 0); gl.uniform1i(w.u.uB, 1);
    gl.uniform1f(w.u.uMix, mix);
    gl.uniform1f(w.u.uT, t);
    gl.uniform1f(w.u.uAsp, lowW / lowH);
    gl.uniform1f(w.u.uZoom, 1.0 - 0.05 * pulse);
    draw(w, fbo[0], lowW, lowH);

    const b = prog.blur;
    gl.useProgram(b.p);
    gl.activeTexture(gl.TEXTURE0);
    gl.uniform1i(b.u.uS, 0);
    gl.uniform2f(b.u.uPx, 1 / lowW, 1 / lowH);
    let src = 0;
    for (let i = 0; i < 7; i++) {
      gl.bindTexture(gl.TEXTURE_2D, fbo[src].tex);
      gl.uniform1f(b.u.uO, i);
      draw(b, fbo[1 - src], lowW, lowH);
      src = 1 - src;
    }

    const f = prog.final;
    gl.useProgram(f.p);
    gl.bindTexture(gl.TEXTURE_2D, fbo[src].tex);
    gl.uniform1i(f.u.uS, 0);
    gl.uniform1f(f.u.uSat, 1.5);
    gl.uniform1f(f.u.uSeed, (t * 7.3) % 10);
    draw(f, null, dynCanvas.width, dynCanvas.height);
  }

  // ---------------- mode switching ----------------
  function effectiveMode() {
    return mode === "wallpapers" && files.size > 0 && !broken ? "wallpapers" : "dynamic";
  }

  function apply() {
    const m = effectiveMode();
    if (m === active) return;
    active = m;
    document.body.dataset.bg = m;
    // Without WebGL (a host with GPU acceleration off) fall back to a CSS-blurred cover.
    if (m === "dynamic" && !initGL()) document.body.classList.add("nogl");
    if (m === "wallpapers" && !currentPath) showWallpaper(nextPath());
  }

  addEventListener("resize", () => {
    resizeGL();
    if (!currentBitmap) return;
    // Grown past the resolution we decoded at (monitor/DPI change): decode again.
    const [w] = screenSize();
    if (currentBitmap.width < w * 0.9 && currentPath) showWallpaper(currentPath);
    else [...wpRoot.children].forEach((c) => paintLayer(c, currentBitmap));
  });

  return {
    setOptions(o) {
      if (o.mode !== undefined) mode = o.mode;
      if (o.collection !== undefined && o.collection !== collection) { collection = o.collection; bag = []; }
      if (o.kenBurns !== undefined) {
        kenBurns = o.kenBurns;
        [...wpRoot.children].forEach((c) => c.classList.toggle("kb", kenBurns));
      }
      if (o.blur !== undefined && o.blur !== blurPx) {
        blurPx = o.blur;
        if (currentBitmap) [...wpRoot.children].forEach((c) => paintLayer(c, currentBitmap));
      }
      if (o.speed !== undefined) speedMul = o.speed;
      if (o.audioReactive !== undefined) audioReactive = o.audioReactive;
      apply();
    },
    addFiles(list) {
      for (const p of list) if (IMAGE_EXT.test(p)) files.add(p);
      broken = false;
      apply();
    },
    removeFiles(list) {
      for (const p of list) files.delete(p);
      if (files.size === 0) currentPath = null;
      apply();
    },
    /** Replace the whole list (folder listed by TaskbarLyrics for non-WE hosts). */
    setFolderFiles(list) {
      files.clear();
      bag = [];
      this.addFiles(list);
    },
    get hasFolderFiles() { return files.size > 0; },
    /** New song: next wallpaper from the pack (wallpaper mode only). */
    songChanged() {
      if (effectiveMode() === "wallpapers") showWallpaper(nextPath());
    },
    setCover,
    setAudioLevel(v) { audioLevel = v; },
    frame(dt) {
      if (active === "dynamic" && gl) renderDynamic(Math.min(dt, 0.1));
    },
    get mode() { return active; },
  };
})();
