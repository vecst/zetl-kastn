# Zetl on Linux

Zetl's Linux port is in testing. It has been used on KDE Plasma 6 (Wayland)
with a USB keyboard; other desktops may work but have not been checked.

## What it needs

- **A 64-bit Linux desktop.** The bundle is self-contained: no .NET install.
- **Keyboard access.** Zetl works below the desktop, the way it hooks the
  keyboard on Windows: it takes each keyboard (`/dev/input/event*`), passes
  every key on through a virtual keyboard it creates (`/dev/uinput`), and keeps
  back only the shortcuts you hold. Your user needs read/write access to both.
- **A clipboard Zetl can watch.** Capture uses `ext-data-control-v1`, the
  Wayland protocol clipboard managers use. KDE Plasma 6 and wlroots compositors
  (Sway, Hyprland, and others) offer it. Without it (an X11 session, or a
  desktop that lacks it), shortcuts still work but captures find nothing.

## One-time keyboard setup

Most distributions let the `input` group read keyboards. Join it, and let that
group create Zetl's virtual keyboard:

```bash
sudo usermod -aG input "$USER"
sudo cp 70-zetl-uinput.rules /etc/udev/rules.d/
sudo udevadm control --reload
sudo udevadm trigger --name-match=uinput
```

Log out and back in so the new group applies. `70-zetl-uinput.rules` is in the
bundle and in `packaging/linux/`.

Know what this grants: any program you run can then read your keyboards, which
is what Zetl needs and also what a keylogger would need. That is the same
trust you give Zetl on Windows, extended to your other programs on Linux.

## Install

There are two ways to run Zetl. Both need the keyboard setup above.

### AppImage

`Zetl-<version>-x86_64.AppImage` is one file that runs on most distributions:

```bash
chmod +x Zetl-*-x86_64.AppImage
./Zetl-*-x86_64.AppImage            # Zetl
./Zetl-*-x86_64.AppImage --kastn    # Kastn
```

It needs FUSE, which most desktops have. Zetl and Kastn launch each other
through the AppImage file, so either can be closed without affecting the other.
To start Zetl when you log in, add the AppImage to your desktop's autostart
settings (KDE: System Settings, Autostart).

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

## If something goes wrong

- **Your keyboard stops responding:** hold both Shift keys and press Escape.
  Zetl lets every keyboard go at once and stays out of the way until restarted.
  Closing Zetl any other way, even killing it, also hands the keyboard back.
- **Shortcuts are off:** Zetl shows a notice, and `diagnostics.log` names the
  device and the missing permission.

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
controller and remapping tools do), and log out for it to take effect.
