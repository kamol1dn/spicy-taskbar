# Taskbar Lyrics
(Why do i even do this to myself...)

![Taskbar Lyrics demo](readme-assets/demo.gif)

![Taskbar Lyrics in context](readme-assets/screenshot.jpg)

Synced (word-by-word, karaoke-style) lyrics rendered directly on the Windows taskbar,
for whatever is playing — Spotify, YouTube in a browser, anything that shows up in the
Windows media flyout.

## Download

Grab the latest build from [Releases](https://github.com/kamol1dn/spicy-taskbar/releases):

- **`TaskbarLyrics.exe`** — self-contained, just run it. No .NET install needed.
- **`TaskbarLyrics-framework-dependent.zip`** — tiny, needs the
  [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

It runs from the tray (♪). Lyrics for Spotify are word-level once the spicetify
extension is installed (below); everything else falls back to line-level sync.

## How it works

```
┌───────────────┐  ws://localhost:9012   ┌──────────────────────────────┐
│ Spotify       │ ◄────────────────────► │ TaskbarLyrics.exe (WPF)      │
│ + spicetify   │   search / lyrics /    │  • SMTC watcher (any player) │
│ spicy-bridge  │   exact position push  │  • position interpolation    │
└───────────────┘                        │  • karaoke overlay renderer  │
        │                                └──────────────────────────────┘
        ▼                                          │ fallback
  api.spicylyrics.org                              ▼
  (word-level lyrics,                         lrclib.net
   uses Spotify's own token)                  (line-level, no auth)
```

- **`extension/spicy-bridge.js`** — spicetify extension running inside Spotify. Servs
  Spotify track search + SpicyLyrics API fetches (using the client's own session token,
  same as the real Spicy Lyrics extension) and pushes exact playback position every 500ms.
- **`TaskbarLyrics/`** — .NET 8 WPF app. Listens to the Windows media session (SMTC),
  resolves lyrics (cache → SpicyLyrics via bridge → LRCLIB), renders a click-through
  always-on-top strip over the taskbar: main line with per-syllable sweep, smaller
  filler/background vocals row ("yeah", "come on"), 3-dot interlude animation. Also hosts
  the audio visualizer and the active-app name strip — see [Modules](#modules).
- YouTube/browser tracks: video title + channel get cleaned (`(Official Video)`, `ft.`,
  `Artist - Title` splitting) and matched against Spotify search by title/artist/duration.

## Build & run

```powershell
cd TaskbarLyrics
dotnet build -c Release
start bin\Release\net8.0-windows10.0.19041.0\TaskbarLyrics.exe
```

Extension install (already done once):

```powershell
Copy-Item extension\spicy-bridge.js "$env:APPDATA\spicetify\Extensions\"
spicetify config extensions spicy-bridge.js
spicetify apply
```

## Modules

Three independent overlays, each toggled and positioned on its own from the tray:

| module | what it draws |
|---|---|
| **Lyrics** | the karaoke line, filler/background vocals row, ● ● ● interludes |
| **Active app** | name of the app that currently has focus, macOS-menubar style |
| **Visualizer** | album-colored spectrum bleeding out of the taskbar |

Lyrics and Active app can each sit on the top or bottom edge, left/middle/right, on the
taskbar itself or floating just inside the work area, with a pixel offset from the edge.
The visualizer picks an edge too — at the top it mirrors, so bars hang downward.

## Config

`config.json` next to the exe (created on first run):

| key | meaning |
|---|---|
| `Placement` | `taskbar` (on the bar) or `above` (floating strip beside it) |
| `VPos` | screen edge for the lyrics: `bottom` or `top` |
| `Align` | `left` / `center` / `right` — moves the strip *and* the text inside it, so left-aligned lyrics start flush at the edge |
| `XOffset` | inset in px from the aligned edge (no effect while centered) |
| `Width` | max width of the strip |
| `VizEdge` | screen edge for the visualizer: `bottom` or `top` (presets mirror) |
| `AppNameEnabled` | show the focused app's name, macOS-menubar style (off by default) |
| `AppNameVPos` / `AppNameAlign` / `AppNameXOffset` | same position options as the lyrics, independently |
| `AppNameFontPx` / `AppNameAlpha` / `AppNameWidth` | appearance of the app-name strip |
| `MainFontPx` / `BgFontPx` | font sizes for main + filler rows |
| `TextShadow` | drop shadow behind all overlay text — turn off on dark wallpapers |
| `GlobalOffsetMs` | sync nudge, positive = lyrics earlier |
| `InterludeGapMs` | min instrumental gap before the ● ● ● dots |

Tray icon (♪), one submenu per module:

- **Lyrics position** — top/bottom, left/middle/right, edge offset, on-taskbar
- **Active app** — on/off, same position options, font size
- **Visualizer** — on/off, top/bottom edge, randomize, preset

…plus **Text shadow** (applies to every module), open log, clear lyrics cache, exit. Everything picked from the tray is saved to `config.json`.

## Autostart

Create a shortcut to `TaskbarLyrics.exe` in `shell:startup`
(Win+R → `shell:startup` → paste shortcut).

## Notes

- Data: `%LOCALAPPDATA%\TaskbarLyrics\` (log.txt + lyrics cache).
- Word-level lyrics need Spotify running (the bridge lives inside it). Without it,
  LRCLIB still provides line-level sync for anything playing.
- The SpicyLyrics API is unofficial; its response notice permits personal individual
  use via official clients/forks — this is a personal single-user companion. If it
  ever blocks/changes, the LRCLIB path keeps working.
- Browser position (SMTC) can drift ~0.5s; Spotify position is exact via the bridge.
