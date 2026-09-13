// Starting defaults. Normally you change the look from TaskbarLyrics' tray icon
// (right-click -> Wallpaper), which overrides these and is remembered by the page.
// Any key can also be given in the URL: index.html?background=dynamic&lyricssize=120
// (Wallpaper Engine's own property panel works too.)
window.WALLPAPER_CONFIG = {
  // "wallpapers" = random image from `folder` on every song change, "dynamic" = album gradient
  background: "wallpapers",
  // Listed by TaskbarLyrics (which must be running). Wallpaper Engine uses its folder picker instead.
  folder: "C:\\Users\\User\\Pictures\\Wallpapers",
  // Filter by subfolder or filename prefix (nord_..., gruvbox_...): all, calm, digital, gruvbox, monochrome, nord, others
  collection: "all",
  wallpaperblur: 0,      // px
  kenburns: true,        // slow zoom drift on wallpapers
  dim: 35,               // darken background, %
  dynspeed: 100,         // gradient speed, %
  audioreactive: true,   // gradient pulses with the bass

  layout: "split",       // "split" (cover + lyrics) or "lyrics"
  lyricssize: 100,       // %
  linepos: 45,           // active line height, % of the screen
  lineblur: true,        // blur lines away from the current one
  spicyfont: true,
  offset: 0,             // ms, positive = lyrics earlier
  clock: true,           // clock when nothing is playing
  fps: 60,
  port: "9012",          // TaskbarLyrics bridge

  // lockscreen.html applies these on top of everything above.
  lockscreen: {
    layout: "lock",      // lyrics centred under Windows' own clock + a small now-playing strip
    clock: false,        // Windows draws the time on the lock screen already
    dim: 45,
  },
};
