#!/bin/sh
set -eu

prefix="${PREFIX:-$HOME/.local}"
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
app_dir="$prefix/lib/rpgmodder"
bin_dir="$prefix/bin"
desktop_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icon_dir="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
config_dir="${XDG_CONFIG_HOME:-$HOME/.config}"

mkdir -p "$app_dir" "$bin_dir" "$desktop_dir" "$icon_dir" "$config_dir"
cp -a "$source_dir"/. "$app_dir"/
chmod u+x "$app_dir/RPGModder"
ln -sfn "$app_dir/RPGModder" "$bin_dir/RPGModder"
cp "$source_dir/packaging/linux/rpgmodder.desktop" "$desktop_dir/rpgmodder.desktop"
cp "$source_dir/packaging/linux/rpgmodder.png" "$icon_dir/rpgmodder.png"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$desktop_dir" || true
fi
if command -v xdg-mime >/dev/null 2>&1; then
    xdg-mime default rpgmodder.desktop x-scheme-handler/nxm || true
fi

printf 'RPGModder installed to %s\n' "$app_dir"
