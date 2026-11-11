#!/bin/sh
set -eu

prefix="${PREFIX:-$HOME/.local}"
desktop_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icon_dir="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
config_dir="${XDG_CONFIG_HOME:-$HOME/.config}"

rm -f "$prefix/bin/RPGModder"
rm -f "$desktop_dir/rpgmodder.desktop" "$desktop_dir/rpgmodder-nxm.desktop"
rm -f "$icon_dir/rpgmodder.png"
rm -rf "$prefix/lib/rpgmodder"

remove_mime_handler() {
    file=$1
    [ -f "$file" ] || return 0
    temp="$file.rpgmodder.tmp"
    awk -F= '
        $1 == "x-scheme-handler/nxm" {
            count = split($2, handlers, ";")
            value = ""
            for (i = 1; i <= count; i++) {
                if (handlers[i] != "" && handlers[i] != "rpgmodder.desktop" && handlers[i] != "rpgmodder-nxm.desktop")
                    value = value handlers[i] ";"
            }
            if (value != "") print $1 "=" value
            next
        }
        { print }
    ' "$file" > "$temp"
    mv "$temp" "$file"
}

remove_mime_handler "$config_dir/mimeapps.list"
remove_mime_handler "$desktop_dir/mimeapps.list"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$desktop_dir" || true
fi

printf 'RPGModder removed. User settings and game workspaces were preserved.\n'
