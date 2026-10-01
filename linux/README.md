# Spicy Wallpaper for Linux (Wayland)

The wallpaper half of Taskbar Lyrics, ported to a Wayland desktop (built and tested on
Hyprland with the Quickshell *illogical-impulse* shell). Same lyrics wallpaper, same
spicetify bridge, same tray-driven settings — no taskbar strip, visualizer or active-app
name.

`spicy-wallpaper` is one Qt 6 process:

| Windows app | Linux port |
|---|---|
| SMTC (media flyout) | MPRIS over D-Bus — Spotify, browsers, mpv, anything |
| Wallpaper Engine / Aura | a WebEngine `wlr-layer-shell` surface per monitor (Bottom layer, click-through) showing `../wallpaper/index.html` |
| WASAPI loopback | `cava` (PipeWire) for the bass pulse |
| Aura lock screen | the same surface shows through a transparent lock (`misc:session_lock_xray`); the page flips to its lock layout on logind's `LockedHint` |
| WinForms tray | StatusNotifier tray icon (Qt) |
| — | **controls**: tap a lyric line to jump to it, the cover to play/pause, the bar to seek, prev/play/next buttons — on the desktop *and* on the lock screen |

The bridge protocol, lyrics resolution (cache → SpicyLyrics via the bridge → LRCLIB),
title matching and the page itself are unchanged.

## Requirements

Qt 6 (base, declarative, websockets, webengine), `layer-shell-qt`, `cmake`, `ninja`, a
compiler; `cava` for the bass pulse; a compositor with layer-shell (Hyprland, Sway,
niri, KDE…). On Arch/CachyOS:

```bash
sudo pacman -S --needed qt6-base qt6-declarative qt6-websockets qt6-webengine layer-shell-qt cmake ninja cava
```

## Install

```bash
linux/install.sh --enable
```

Builds into `linux/build`, installs `~/.local/bin/spicy-wallpaper`, links
`~/.local/share/spicy-wallpaper/wallpaper` to this checkout's `wallpaper/` (so edits show
up after *Reload wallpaper*), and installs + starts a systemd user service bound to
`graphical-session.target`. Run the binary directly instead if you prefer
(`exec-once = spicy-wallpaper`).

## Word-level Spotify lyrics (spicetify)

Install spicetify without root:

```bash
mkdir -p ~/.spicetify
gh release download -R spicetify/cli -p '*linux-amd64.tar.gz' -D /tmp
tar xzf /tmp/spicetify-*-linux-amd64.tar.gz -C ~/.spicetify   # then add ~/.spicetify to PATH
```

Then point it at Spotify and enable the bridge (`--spicetify` on `install.sh` does the
last three steps). For spotify-launcher the install lives in your home, so no root needed:

```bash
spicetify config spotify_path ~/.local/share/spotify-launcher/install/usr/share/spotify prefs_path ~/.config/spotify/prefs
cp extension/spicy-bridge.js ~/.config/spicetify/Extensions/
spicetify config extensions spicy-bridge.js
spicetify backup apply
```

spotify-launcher updates Spotify in place, which wipes the patch — run
`spicetify backup apply` again after an update. The log then shows
`resolve: spicy lyrics ok (Syllable)`.

## Controls

The page reports where its clickable parts are (cover, buttons, progress bar, lyrics
column); only those become the layer's input region, so the rest of the desktop stays
click-through. Controls go to the current player over MPRIS (`PlayPause`, `Next`,
`Previous`, `SetPosition`). Toggle with tray → *Clickable lyrics & controls*.

## Lock screen

Hyprland has no lock/unlock IPC event, and while locked only the locker gets to draw
on top and receive input, so the lock screen works like this:

1. The locker leaves its surface transparent and Hyprland has
   `misc:session_lock_xray = true` (illogical-impulse does both), so the wallpaper layer
   shows underneath.
2. The locker sets logind's `LockedHint`, and the page flips to its lock layout (centred
   lyrics, its own clock, a compact now-playing strip with controls).
3. A tiny relay component inside the locker's surface (`ii/SpicyLockInput.qml`) puts
   invisible touch targets over the page's regions and forwards taps to it over
   `ws://127.0.0.1:9012/input` — so play/pause/skip and tap-to-seek work while locked.
   Clicks anywhere else still reach the lock UI.

illogical-impulse does neither 2 nor 3 out of the box; `ii-lock.patch` adds both to its
`LockScreen.qml` (a `Connections` block and a `Loader` that's a no-op when the component
isn't installed):

```bash
patch -d ~/.config/quickshell/ii -p1 < linux/ii-lock.patch
```

You can preview the lock layout without locking:

```bash
busctl call org.freedesktop.login1 /org/freedesktop/login1/session/auto org.freedesktop.login1.Session SetLockedHint b true
```

(`b false` to go back.)

## Tray menu

```
♪
├─ Next wallpaper · Folder…
├─ Desktop       background · collection · layout · lyrics size · darken · blur ·
├─ Lock screen   slow zoom · bass pulse · line blur · font · clock
├─ Lyrics timing… sync nudge in ms, positive = earlier
├─ Clickable lyrics & controls
├─ Reload wallpaper · Open log · Open config · Clear lyrics cache
└─ Exit
```

## Config

`~/.config/spicy-wallpaper/config.json` (created on first run; the tray writes it):

| key | meaning |
|---|---|
| `Port` | bridge port (default 9012 — the extension dials this) |
| `GlobalOffsetMs` | sync nudge, positive = lyrics earlier |
| `Interactive` | clickable lyrics, cover, progress and buttons (desktop + lock relay) |
| `Layer` | `bottom` (default: above the shell's own wallpaper, below windows) or `background` |
| `WallpaperDir` | folder with `index.html`; empty = installed link, then this checkout |
| `WallpaperFolder` | images, one picked per song (default `~/Pictures/Wallpapers` or `~/Pictures/wallpaper`) |
| `WallpaperDesktop` / `WallpaperLock` | per-surface look, same keys as on Windows |

Data: log in `~/.local/state/spicy-wallpaper/log.txt`, lyrics cache in
`~/.cache/spicy-wallpaper/lyrics`, page storage in `~/.local/share/spicy-wallpaper/web`.

## Notes

- The Spicy Lyrics font is only served to Spotify (CORS), so the page falls back to
  Google Sans Flex / Inter / Noto Sans.
- With the default Bottom layer the wallpaper covers the shell's desktop widgets
  (illogical-impulse's `backgroundWidgets` share that layer). Set `Layer` to `background`
  to sit under them — then stacking against the shell's own wallpaper depends on map
  order.
- Spotify's MPRIS already carries the exact track id, so word-level lyrics work even
  before the bridge has pushed its state; position still comes from the bridge when it's
  connected (MPRIS otherwise).
