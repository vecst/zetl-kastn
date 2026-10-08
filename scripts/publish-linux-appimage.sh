#!/usr/bin/env bash
# Builds Zetl-v<version>-linux-x86_64.AppImage (Zetl and Kastn in one image) into
# artifacts/publish/linux-x64-appimage. Run on Linux from a clean checkout.
# appimagetool and the AppImage runtime are pinned and checked by SHA256.
#
#   scripts/publish-linux-appimage.sh               release AppImage
#   scripts/publish-linux-appimage.sh --allow-dirty only while changing this script
set -euo pipefail

appimagetool_url=https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
appimagetool_sha256=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_url=https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
runtime_sha256=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d

allow_dirty=0
case "${1:-}" in
    --allow-dirty) allow_dirty=1 ;;
    "") ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
esac

if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then
    echo "Build the AppImage on x86_64 Linux, so the staged executables can be checked." >&2
    exit 1
fi

root=$(cd "$(dirname "$0")/.." && pwd)
tools=$root/artifacts/tools/appimage
out_dir=$root/artifacts/publish/linux-x64-appimage
work=$root/artifacts/publish/.appimage-$$
appdir=$work/Zetl.AppDir
cd "$root"

commit=$(git rev-parse HEAD)
if [ "$allow_dirty" = 0 ] && [ -n "$(git status --porcelain --untracked-files=all)" ]; then
    echo "The release worktree is not clean. Commit or remove these changes before publishing:" >&2
    git status --short --untracked-files=all >&2
    exit 1
fi

# fetch URL SHA256 FILE: download once into the tool cache, always verify.
fetch() {
    if [ ! -f "$3" ] || ! echo "$2  $3" | sha256sum -c --quiet - 2>/dev/null; then
        curl -sSfL -o "$3.part" "$1"
        echo "$2  $3.part" | sha256sum -c --quiet - || { echo "Checksum mismatch for $1" >&2; exit 1; }
        mv "$3.part" "$3"
    fi
}
mkdir -p "$tools"
fetch "$appimagetool_url" "$appimagetool_sha256" "$tools/appimagetool-x86_64.AppImage"
fetch "$runtime_url" "$runtime_sha256" "$tools/runtime-x86_64"
chmod 755 "$tools/appimagetool-x86_64.AppImage"

trap 'rm -rf "$work"' EXIT
rm -rf "$work"
mkdir -p "$appdir/usr/lib/zetl"

echo "Publishing Zetl and Kastn from commit $commit..."
# The AppImage is already one file, so publish the ordinary multi-file layout:
# nothing is unpacked to ~/.net at start.
for project in Zetl.App/Zetl.App.csproj Kastn.App/Kastn.App.csproj; do
    dotnet publish "$project" \
        -p:PublishProfile=linux-x64 \
        -p:PublishSingleFile=false \
        -p:PublishDir="$appdir/usr/lib/zetl/" \
        -p:DebugType=None \
        -p:DebugSymbols=false \
        --nologo
done

version=$("$appdir/usr/lib/zetl/Zetl" --version)
case "$version" in
    *"+$commit") ;;
    *) echo "Zetl identifies '$version', not commit '$commit'." >&2; exit 1 ;;
esac
[ "$("$appdir/usr/lib/zetl/Kastn" --version)" = "$version" ] || { echo "Kastn's version differs from Zetl's." >&2; exit 1; }

install -m 755 packaging/linux/AppRun "$appdir/AppRun"
cp LICENSE "$appdir/usr/lib/zetl/LICENSE"
sed 's#^Exec=.*#Exec=zetl#' packaging/linux/zetl.desktop > "$appdir/zetl.desktop"
cp packaging/linux/zetl.svg "$appdir/zetl.svg"
ln -s zetl.svg "$appdir/.DirIcon"
install -D -m 644 packaging/linux/zetl.svg "$appdir/usr/share/icons/hicolor/scalable/apps/zetl.svg"
install -D -m 644 packaging/linux/kastn.png "$appdir/usr/share/icons/hicolor/256x256/apps/kastn.png"
install -D -m 644 packaging/linux/70-zetl-uinput.rules "$appdir/usr/share/zetl/70-zetl-uinput.rules"

file_version=${version%%+*}
mkdir -p "$out_dir"
image=$out_dir/Zetl-v$file_version-linux-x86_64.AppImage
# Extract-and-run lets appimagetool work where FUSE is unavailable (CI).
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tools/appimagetool-x86_64.AppImage" \
    --no-appstream --runtime-file "$tools/runtime-x86_64" "$appdir" "$image.part" >/dev/null
mv "$image.part" "$image"
(cd "$out_dir" && sha256sum "$(basename "$image")" > "$(basename "$image").sha256")

echo
echo "AppImage ready: $image"
echo "Version: $version"
cat "$image.sha256"
