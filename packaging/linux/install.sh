#!/bin/sh
# Installs Zetl and Kastn for the current user from an unpacked linux-x64
# bundle, or removes them. Nothing here needs root: keyboard and /dev/uinput
# access are reported, and their one-time setup is described in docs/linux-setup.md.
#
#   ./install.sh               install (or update) from this bundle
#   ./install.sh --autostart   also start Zetl when you log in
#   ./install.sh --uninstall   remove the installed files (your notes stay)
set -eu

bundle=$(cd "$(dirname "$0")" && pwd)
data_home=${XDG_DATA_HOME:-$HOME/.local/share}
config_home=${XDG_CONFIG_HOME:-$HOME/.config}
app_dir=$data_home/zetl
bin_dir=$HOME/.local/bin
applications=$data_home/applications
icons=$data_home/icons/hicolor
autostart=$config_home/autostart

autostart_wanted=0
action=install
for arg in "$@"; do
    case $arg in
        --autostart) autostart_wanted=1 ;;
        --uninstall) action=uninstall ;;
        -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
        *) echo "Unknown option: $arg" >&2; exit 2 ;;
    esac
done

refresh_desktop() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$applications" >/dev/null 2>&1 || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$icons" >/dev/null 2>&1 || true
}

if [ "$action" = uninstall ]; then
    rm -f "$bin_dir/zetl" "$bin_dir/kastn" \
        "$applications/zetl.desktop" "$applications/kastn.desktop" \
        "$autostart/zetl.desktop" \
        "$icons/scalable/apps/zetl.svg" "$icons/256x256/apps/kastn.png"
    rm -rf "$app_dir"
    # Self-contained single-file apps unpack their native libraries here.
    rm -rf "$HOME/.net/Zetl" "$HOME/.net/Kastn"
    refresh_desktop
    echo "Removed Zetl and Kastn. Your notes and settings in $config_home/Zetl are untouched."
    echo "To undo the keyboard permission setup as well, see docs/linux-setup.md."
    exit 0
fi

for required in Zetl Kastn hotkeys.json LICENSE zetl.desktop kastn.desktop zetl.svg kastn.png; do
    if [ ! -f "$bundle/$required" ]; then
        echo "This bundle is incomplete: $required is missing." >&2
        exit 1
    fi
done

# Stage beside the install, then swap, so a failed copy never leaves half an app.
mkdir -p "$data_home"
staging=$(mktemp -d "$data_home/.zetl-install.XXXXXX")
trap 'rm -rf "$staging"' EXIT
cp "$bundle/Zetl" "$bundle/Kastn" "$bundle/hotkeys.json" "$bundle/LICENSE" "$staging/"
# Keep the installer beside the apps so --uninstall needs no bundle.
cp "$0" "$staging/install.sh"
chmod 755 "$staging/Zetl" "$staging/Kastn" "$staging/install.sh"
rm -rf "$app_dir.previous"
[ -d "$app_dir" ] && mv "$app_dir" "$app_dir.previous"
mv "$staging" "$app_dir"
rm -rf "$app_dir.previous"
trap - EXIT

mkdir -p "$bin_dir" "$applications" "$icons/scalable/apps" "$icons/256x256/apps"
ln -sf "$app_dir/Zetl" "$bin_dir/zetl"
ln -sf "$app_dir/Kastn" "$bin_dir/kastn"
sed "s#@BINDIR@#$app_dir#g" "$bundle/zetl.desktop" > "$applications/zetl.desktop"
sed "s#@BINDIR@#$app_dir#g" "$bundle/kastn.desktop" > "$applications/kastn.desktop"
cp "$bundle/zetl.svg" "$icons/scalable/apps/zetl.svg"
cp "$bundle/kastn.png" "$icons/256x256/apps/kastn.png"
if [ "$autostart_wanted" = 1 ]; then
    mkdir -p "$autostart"
    cp "$applications/zetl.desktop" "$autostart/zetl.desktop"
fi
refresh_desktop

echo "Installed $("$app_dir/Zetl" --version) to $app_dir."
[ "$autostart_wanted" = 1 ] && echo "Zetl will start when you log in."

# Zetl takes the keyboard below the desktop: it must read keyboards and
# create a virtual one. Report what is missing instead of failing later.
keyboards_ok=1
for device in /dev/input/event*; do
    [ -e "$device" ] || continue
    if [ ! -r "$device" ] || [ ! -w "$device" ]; then keyboards_ok=0; break; fi
done
uinput_ok=1
[ -w /dev/uinput ] || uinput_ok=0
if [ "$keyboards_ok" = 1 ] && [ "$uinput_ok" = 1 ]; then
    echo "Keyboard access: ready."
else
    echo
    echo "Keyboard access is not set up yet, so global shortcuts will be off:"
    [ "$keyboards_ok" = 1 ] || echo "  - keyboards in /dev/input are not readable and writable (join the 'input' group)"
    [ "$uinput_ok" = 1 ] || echo "  - /dev/uinput is not writable (install 70-zetl-uinput.rules)"
    echo "The one-time setup is in docs/linux-setup.md."
fi
