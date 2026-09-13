// Small math helpers shared by the background and the lyrics renderer.
"use strict";

// Analytic damped spring (same closed form as Fraktality's spr, which spicy-lyrics
// ports): frequency in Hz, damping ratio d (<1 bouncy, 1 critical, >1 sluggish).
class Spring {
  constructor(pos, freq, damp) {
    this.p = pos; this.v = 0; this.g = pos; this.f = freq; this.d = damp;
  }
  set(goal, instant) {
    this.g = goal;
    if (instant) { this.p = goal; this.v = 0; }
  }
  get resting() {
    return Math.abs(this.p - this.g) < 1e-4 && Math.abs(this.v) < 1e-3;
  }
  step(dt) {
    if (this.resting) { this.p = this.g; this.v = 0; return this.p; }
    const d = this.d, f = this.f * 2 * Math.PI, g = this.g;
    const o = this.p - g, v = this.v;
    let p, nv;
    if (d === 1) {
      const q = Math.exp(-f * dt), w = dt * q;
      p = o * (q + w * f) + v * w + g;
      nv = v * (q - w * f) - o * (w * f * f);
    } else if (d < 1) {
      const q = Math.exp(-d * f * dt), c = Math.sqrt(1 - d * d);
      const i = Math.cos(dt * f * c), j = Math.sin(dt * f * c);
      const z = j / c, y = j / (f * c);
      p = (o * (i + z * d) + v * y) * q + g;
      nv = (v * (i - z * d) - o * (z * f)) * q;
    } else {
      const c = Math.sqrt(d * d - 1);
      const r1 = -f * (d + c), r2 = -f * (d - c);
      const e1 = Math.exp(r1 * dt), e2 = Math.exp(r2 * dt);
      const co2 = (v - o * r1) / (2 * f * c), co1 = o - co2;
      p = co1 * e1 + co2 * e2 + g;
      nv = co1 * r1 * e1 + co2 * r2 * e2;
    }
    this.p = p; this.v = nv;
    return p;
  }
}

// Natural cubic spline through [time, value] points, clamped to the end points —
// spicy-lyrics shapes every word/letter/dot animation curve with one of these.
function spline(points) {
  const n = points.length;
  const xs = points.map((p) => p[0]), ys = points.map((p) => p[1]);
  const h = [], a = [], l = [1], mu = [0], z = [0], c = new Array(n).fill(0), b = [], d = [];
  for (let i = 0; i < n - 1; i++) h[i] = xs[i + 1] - xs[i];
  for (let i = 1; i < n - 1; i++) {
    a[i] = (3 / h[i]) * (ys[i + 1] - ys[i]) - (3 / h[i - 1]) * (ys[i] - ys[i - 1]);
    l[i] = 2 * (xs[i + 1] - xs[i - 1]) - h[i - 1] * mu[i - 1];
    mu[i] = h[i] / l[i];
    z[i] = (a[i] - h[i - 1] * z[i - 1]) / l[i];
  }
  for (let j = n - 2; j >= 0; j--) {
    c[j] = z[j] - mu[j] * c[j + 1];
    b[j] = (ys[j + 1] - ys[j]) / h[j] - (h[j] * (c[j + 1] + 2 * c[j])) / 3;
    d[j] = (c[j + 1] - c[j]) / (3 * h[j]);
  }
  return (x) => {
    if (x <= xs[0]) return ys[0];
    if (x >= xs[n - 1]) return ys[n - 1];
    let j = 0;
    while (j < n - 2 && x > xs[j + 1]) j++;
    const t = x - xs[j];
    return ys[j] + b[j] * t + c[j] * t * t + d[j] * t * t * t;
  };
}

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
const easeSinOut = (t) => Math.sin((t * Math.PI) / 2);

// Load an image. onload rather than img.decode(): decode() stalls while the page is
// hidden, and onload fires either way.
function loadImage(url, cors) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    if (cors && /^https?:/i.test(url)) img.crossOrigin = "anonymous";
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error("image failed: " + url));
    img.src = url;
  });
}

// file:///-URL for a path Wallpaper Engine hands us (C:\dir\a b#1.jpg).
function fileUrl(path) {
  if (/^(https?|file|data):/i.test(path)) return path;
  const parts = path.replace(/\\/g, "/").split("/");
  return "file:///" + parts.map((s, i) => (i === 0 && /^[a-z]:$/i.test(s) ? s : encodeURIComponent(s))).join("/");
}
