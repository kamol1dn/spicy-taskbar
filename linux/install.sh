#!/usr/bin/env bash
# Build and install spicy-wallpaper for the current user:
#   ~/.local/bin/spicy-wallpaper
#   ~/.local/share/spicy-wallpaper/wallpaper -> this checkout's wallpaper/ (edits show up live)
#   ~/.local/share/spicy-wallpaper/SpicyLockInput.qml -> lock-screen tap relay
#   ~/.config/systemd/user/spicy-wallpaper.service (enabled with --enable)
#   ~/.config/spicetify/Extensions/spicy-bridge.js (with --spicetify)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
enable=0
spicetify=0
for arg in "$@"; do
  case "$arg" in
    --enable) enable=1 ;;
    --spicetify) spicetify=1 ;;
    *) echo "usage: $0 [--enable] [--spicetify]" >&2; exit 2 ;;
  esac
done

cmake -S "$here" -B "$here/build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$HOME/.local"
cmake --build "$here/build"
cmake --install "$here/build"

mkdir -p "$HOME/.local/share/spicy-wallpaper"
ln -sfn "$repo/wallpaper" "$HOME/.local/share/spicy-wallpaper/wallpaper"
# Lock-screen tap relay, loaded by the illogical-impulse hook (ii-lock.patch).
ln -sfn "$here/ii/SpicyLockInput.qml" "$HOME/.local/share/spicy-wallpaper/SpicyLockInput.qml"

mkdir -p "$HOME/.config/systemd/user"
install -m 644 "$here/spicy-wallpaper.service" "$HOME/.config/systemd/user/spicy-wallpaper.service"
systemctl --user daemon-reload
if [[ $enable == 1 ]]; then
  systemctl --user enable --now spicy-wallpaper.service
  systemctl --user restart spicy-wallpaper.service
fi

if [[ $spicetify == 1 ]]; then
  ext_dir="$HOME/.config/spicetify/Extensions"
  mkdir -p "$ext_dir"
  cp "$repo/extension/spicy-bridge.js" "$ext_dir/"
  spicetify config extensions spicy-bridge.js
  spicetify apply
fi

echo "installed. log: ~/.local/state/spicy-wallpaper/log.txt  config: ~/.config/spicy-wallpaper/config.json"
