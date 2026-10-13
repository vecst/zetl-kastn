# Zetl on Linux

Zetl's Linux port is in testing. It has been used on KDE Plasma 6 (Wayland),
GNOME 50 (Ubuntu 26.04), and Cinnamon on X11 (Linux Mint 22.3) with a USB
keyboard; other desktops may work but have not been checked. With an NVIDIA
card, use NVIDIA's driver: the open-source `nouveau` driver froze Cinnamon.

## What it needs

- **A 64-bit Linux desktop.** The bundle is self-contained: no .NET install.
- **Keyboard access.** Zetl works below the desktop, the way it hooks the
  keyboard on Windows: it takes each keyboard (`/dev/input/event*`), passes
  every key on through a virtual keyboard it creates (`/dev/uinput`), and keeps
  back only the shortcuts you hold. Your user needs read/write access to both.
- **A clipboard Zetl can watch.** On KDE Plasma 6 and wlroots compositors
  (Sway, labwc, Wayfire, and desktops built on them) Zetl uses the Wayland
  protocol clipboard managers use: `ext-data-control-v1`, or wlroots' older
  `zwlr-data-control`. Elsewhere it uses X11's clipboard: in an X11 session, and
  on GNOME through Xwayland, which GNOME keeps in step with its own clipboard.
  Zetl's diagnostics log says which one it connected through.

## One-time keyboard setup

Most distributions let the `input` group read keyboards. Join it, and let that
group create Zetl's virtual keyboard:

```bash
sudo usermod -aG input "$USER"
echo 'KERNEL=="uinput", SUBSYSTEM=="misc", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"' | sudo tee /etc/udev/rules.d/70-zetl-uinput.rules
sudo udevadm control --reload
sudo udevadm trigger --name-match=uinput
```

This works from any folder. The same rule ships as `70-zetl-uinput.rules` in
the bundle and in `packaging/linux/`.

Restart your computer so the new group applies. Logging out and back in is not
always enough: on Ubuntu, a per-user session manager kept running (by an SSH
login, for example) passes the old groups to your next session. If Zetl still
says shortcuts are off, its dialog says which step is missing.

Know what this grants: any program you run can then read your keyboards, which
is what Zetl needs and also what a keylogger would need. That is the same
trust you give Zetl on Windows, extended to your other programs on Linux.

## Install

There are two ways to run Zetl. Both need the keyboard setup above.

### AppImage

`Zetl-v<version>-linux-x86_64.AppImage` is one file that runs on most distributions:

```bash
chmod +x Zetl-*-linux-x86_64.AppImage
./Zetl-*-linux-x86_64.AppImage            # Zetl
./Zetl-*-linux-x86_64.AppImage --kastn    # Kastn
```

It needs FUSE, which most desktops have. Zetl and Kastn launch each other
through the AppImage file, so either can be closed without affecting the other.
To start Zetl when you log in, turn on **Start at login** in Zetl's Settings.
It adds an autostart entry for the AppImage file, and repairs that entry if
you move the AppImage and run it from its new place.

### Installed bundle

Unpack the `linux-x64` bundle and run its installer as yourself (not root):

```bash
./install.sh --autostart
```

It installs to `~/.local/share/zetl`, adds `zetl` and `kastn` to
`~/.local/bin`, menu entries, and icons, and with `--autostart` starts Zetl when
you log in. It reports whether keyboard access is ready. Run it again to update;
quit Zetl first.

Your notes and settings live in `~/.config/Zetl` (`$XDG_CONFIG_HOME/Zetl`), with
the diagnostics log beside them.

## No tray icon?

Some desktops show no tray icons; stock GNOME is one, and so is Ubuntu's GNOME
without its tray extension. Open Zetl again from your app menu (or run it
again) and it shows its menu in a small window: Settings, the tour, Open Kastn,
Quit, and the rest.

## niri

niri tiles windows and places floating ones itself, and Zetl's windows reach
it through `xwayland-satellite`, which passes on no positions. Add these rules
to the end of `~/.config/niri/config.kdl` (niri reloads it on save) so Zetl's
popups float, the toast and hold indicator sit in corners without taking the
keyboard, and see-through popups show what is behind them:

```kdl
window-rule {
    match app-id="^Zetl$"
    open-floating true
    draw-border-with-background false
}
window-rule {
    match app-id="^Zetl$" title="^Zetl (hold indicator|Notification)$"
    open-focused false
}
window-rule {
    match app-id="^Zetl$" title="^Zetl hold indicator$"
    default-floating-position x=16 y=16 relative-to="top-right"
}
window-rule {
    match app-id="^Zetl$" title="^Zetl Notification$"
    default-floating-position x=16 y=16 relative-to="bottom-right"
}
window-rule {
    match app-id="^Zetl$" title="^(Save Zetl Note|Save Zetl Image|Zetl Board|Compose|Start a template)$"
    default-floating-position x=0 y=200 relative-to="top"
}
```

With `focus-follows-mouse` on, moving the mouse off a capture popup counts as
clicking away from it, which saves and closes it. Turn it off, or keep the
mouse still while a popup is open. Zetl's popup position setting has no effect
on niri; the rules above decide where popups go.

## If something goes wrong

- **Your keyboard stops responding:** hold both Shift keys and press Escape.
  Zetl lets every keyboard go at once and stays out of the way until restarted.
  Closing Zetl any other way, even killing it, also hands the keyboard back.
- **Shortcuts are off:** Zetl says which setup step is missing (with a link to
  this guide) and closes; `diagnostics.log` names the device and the missing
  permission. Start Zetl again once the step is done.

## Uninstall and undo the setup

```bash
~/.local/share/zetl/install.sh --uninstall   # or ./install.sh --uninstall from the bundle
```

This removes the program, menu entries, icons, and autostart, and leaves your
notes. To undo the keyboard setup as well:

```bash
sudo rm /etc/udev/rules.d/70-zetl-uinput.rules
sudo udevadm control --reload
sudo udevadm trigger --name-match=uinput
sudo gpasswd -d "$USER" input
```

Leave the `input` group only if nothing else you use needs it (some game
controller and remapping tools do), and restart for it to take effect.
