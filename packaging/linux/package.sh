#!/bin/sh
# Builds the Linux release packages: RPGModder-<version>-linux-x64.tar.gz and .zip (CI-10, CI-11).
# Usage: package.sh <version>
#   PREBUILT_DIR=<folder>  package an existing publish output instead of publishing
#   RUNTIME=<rid>          the runtime identifier, linux-x64 by default
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
version="${1:?usage: package.sh <version>}"
case "$version" in
    *[!0-9A-Za-z.+-]*)
        printf 'Invalid version: %s\n' "$version" >&2
        exit 2
        ;;
esac

runtime="${RUNTIME:-linux-x64}"
package_name="RPGModder-$version-$runtime"
publish_dir="$repo_root/artifacts/$runtime/$package_name"
archive="$repo_root/artifacts/$package_name.tar.gz"
zip_archive="$repo_root/artifacts/$package_name.zip"
prebuilt_dir="${PREBUILT_DIR:-}"

if [ -n "$prebuilt_dir" ]; then
    prebuilt_dir=$(CDPATH= cd -- "$prebuilt_dir" && pwd)
    if [ "$prebuilt_dir" != "$publish_dir" ]; then
        rm -rf "$publish_dir"
        mkdir -p "$publish_dir"
        cp -a "$prebuilt_dir"/. "$publish_dir"/
    fi
else
    rm -rf "$publish_dir"
    mkdir -p "$publish_dir"
    dotnet publish "$repo_root/RPGModder.UI/RPGModder.UI.csproj" \
        --configuration Release \
        --runtime "$runtime" \
        --self-contained true \
        -p:RestoreLockedMode=true \
        --output "$publish_dir"
fi

mkdir -p "$publish_dir/packaging/linux"

cp "$repo_root/packaging/linux/rpgmodder.desktop" "$publish_dir/packaging/linux/"
cp "$repo_root/packaging/linux/rpgmodder.png" "$publish_dir/packaging/linux/"
cp "$repo_root/packaging/linux/install.sh" "$publish_dir/packaging/linux/"
cp "$repo_root/packaging/linux/uninstall.sh" "$publish_dir/packaging/linux/"
cp "$repo_root/LICENSE" "$publish_dir/"
chmod u+x "$publish_dir/RPGModder" "$publish_dir/packaging/linux/install.sh" "$publish_dir/packaging/linux/uninstall.sh"

rm -f "$archive" "$zip_archive"
tar -C "$(dirname "$publish_dir")" -czf "$archive" "$package_name"
printf 'Created %s\n' "$archive"

# The zip keeps the Unix modes, so the 2.0.0 updater can apply it too (CI-17).
if ! command -v zip >/dev/null 2>&1; then
    printf 'zip is not installed, so %s was not created.\n' "$zip_archive" >&2
    exit 1
fi
(cd "$(dirname "$publish_dir")" && zip -q -r -X "$zip_archive" "$package_name")
printf 'Created %s\n' "$zip_archive"
