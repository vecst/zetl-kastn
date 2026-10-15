# Zetl Linux Roadmap

This is the authoritative checklist for Linux platform work. The keyboard model
and architecture rationale live in [`linux-port.md`](linux-port.md).

## Target

- One Avalonia application and behavior model on Windows, X11, and Wayland
- One portable runtime above platform keyboard and clipboard backends
- No required keyd/kmonad-style daemon
- User-session operation without running Zetl as root
- Clear degradation where Wayland denies activation or exact placement
- Windows behavior remains regression-free

## Shared Foundation

The platform-neutral work is complete:

- [x] `Chordl` targets portable `net10.0`.
- [x] `Zetl.Core` owns state, persistence, settings, templates, views, themes,
      and platform contracts.
- [x] `Zetl.Runtime` owns shortcut and workflow orchestration.
- [x] Keyboard and clipboard behavior sit behind platform seams.
- [x] Runtime tests use fake keyboard, clipboard, delay, dispatcher,
      notification, and host implementations.
- [x] `Zetl.App` is the canonical Avalonia Windows application.
- [x] Capture, project setup, Board, Compile, settings, first-run, bucket
      settings, notifications, toasts, tray, themes, and window policy are
      implemented in Avalonia.
- [x] The Avalonia app is the only Windows UI and platform host.
- [x] The whole solution builds and the full test suite passes on Linux
      (AerynOS 2026.08, October 6, 2026). Kastn's PDF fonts resolve through
      fontconfig, and Kastn's control pipe keeps a listener open between
      connections (on Unix a gap resets queued clients).

## Current Linux Baseline

The `Zetl.Linux.Spike` project has demonstrated the core model on real hardware:

- [x] Enumerate keyboard event devices.
- [x] Acquire `EVIOCGRAB`.
- [x] Create a uinput keyboard.
- [x] Forward untouched key and synchronization events.
- [x] Route Ctrl+C through `ChordlProcessor`.
- [x] Suppress held actions and replay taps.
- [x] Exercise autorepeat and modifier changes.
- [x] Release grabs after normal exit and `SIGKILL`.
- [x] Provide a panic release path.

The spike was exercised on June 7, 2026 on AerynOS 2026.05 under Wayland with a
Keychron C2 Pro. It passed discovery, forwarding, uinput creation, tap/hold,
autorepeat, panic release, and immediate re-grab after forced termination.

A later desktop run exposed a logically stuck Ctrl state after beginning with an
orphaned Ctrl repeat. The spike now has an all-keys-up pre-grab gate and explicit
virtual-key cleanup, but those changes still need a fresh hardware verification.

## Priority 1: Re-verify Input Safety

Re-run on October 6, 2026: AerynOS 2026.08, KDE Plasma 6 Wayland, Keychron C2
Pro. `tools/KeyStateProbe` logged the keys and modifiers the compositor
delivered to a focused window, so a logically stuck key would have appeared as
a modifier on later keys; none did. Voice cues (`scripts/linux-say.ps1`) paced
each step and the probe or spike log confirmed it.

- [x] Re-run the spike on the Wayland target after the all-keys-up and virtual
      release changes.
- [x] Typing, Ctrl+C tap (suppressed and replayed), Ctrl+C hold (suppressed),
      Shift, and Caps Lock were correct after safety timeout, panic release,
      `SIGTERM`, and `SIGKILL` with Ctrl or Shift held through the grab.
- [ ] Verify the exception exit path (inject it with a synthetic keyboard).
- [x] Starting while Ctrl or a letter was held waited for release, then
      grabbed cleanly.
- [x] Hardware autorepeat before and during grab.
- [x] Re-grab immediately after panic, timeout, and `SIGKILL`.
- [ ] Repeat under X11 when an X11 target is available.

Findings for the production backend:

- KWin drives keyboard LEDs through the device even while Zetl holds the grab,
  so Caps Lock's light followed on both Keychron nodes without forwarding.
- `SIGTERM` ended the spike without its cleanup (exit 143). The keyboard was
  still correct because closing the descriptors released the grab and KWin
  dropped the destroyed virtual device's keys, but the backend should handle
  `SIGTERM` (logout, `systemctl stop`) with an orderly release.
- The Keychron exposes a second keyboard node (`-if02-event-kbd`); the backend
  must grab every keyboard node of a device, not only the first.

Do not move exclusive-grab code into the production app until the keyboard
returns to a correct logical state after every tested termination path.

## Priority 2: Production Keyboard Backend

`Zetl.Linux` implements `LinuxKeyboardBackend` behind `IKeyboardBackend`, and
Zetl selects it on Linux. The device-free decisions live in
`LinuxKeyboardRouter`; `LinuxEvdev` holds the system calls.

- [x] Move Linux constants, structs, and ioctl wrappers out of the spike.
- [x] Map Linux `KEY_*` values to Chordl key values (the standard keyboard;
      media and vendor keys bypass Chordl).
- [x] Preserve key-down, key-up, and repeat semantics. A repeat Chordl
      swallows for a key the desktop holds releases that key, because a
      Wayland compositor repeats held keys itself.
- [x] Forward synchronization boundaries; frames emptied by suppression are
      dropped. Only key and SYN events are forwarded: only pure keyboard
      nodes are taken, never combined keyboard/pointer nodes.
- [x] Avoid reading Zetl's own uinput device (by kernel name and by name),
      and take virtual keyboards only for tests. (Fixed October 8, 2026: the
      virtual check resolved sysfs links lexically and never matched, so Zetl
      tried to grab every uinput keyboard; it now uses `realpath`.)
- [x] One Zetl and one Kastn per user. (Fixed October 8, 2026: an unprefixed
      .NET mutex is per Unix session on Linux, so launches from the menu,
      autostart, or Kastn's relaunch could run a second Zetl.)
- [x] Support multiple physical keyboards (both Keychron nodes) through one
      virtual keyboard.
- [x] Handle device add/remove (inotify) and backend restart.
- [ ] Verify suspend and resume.
- [x] Implement `SendChord` and `SendPaste` from the virtual keyboard's own
      modifier state.
- [x] Make disposal and failure paths release virtual keys before ending
      grabs, including SIGTERM/SIGHUP/SIGINT and a failed reader.
- [x] Report device and permission errors naming the device.
- [x] Test recorded Linux event sequences (`LinuxKeyboardRouterTests`) and
      real kernel devices (`LinuxKeyboardBackendTests`, synthetic uinput
      keyboards whose output the test grabs so nothing reaches the desktop).

Verified October 6, 2026 in the real app on the Keychron under KDE Wayland:
both nodes taken in about 120 ms; typing, native browser copy, a replayed
Ctrl+V tap, and a Ctrl+C hold; SIGTERM exit left no stuck key and no virtual
device.

## Priority 3: Linux Clipboard

`LinuxClipboard` implements `IClipboard` over Wayland's `ext-data-control-v1`,
or wlroots' older `zwlr-data-control-unstable-v1` where that is all the
compositor offers (see the clipboard model in [`linux-port.md`](linux-port.md)).

- [x] The `zwlr-data-control` fallback (October 9, 2026): every real-clipboard
      test passes over both protocols under headless sway 1.12, and a held
      Ctrl+C capture in the real app worked over `zwlr` alone.

- [x] Read and write through the compositor's clipboard-manager protocol, with
      a change token advanced by selection events.
- [x] Capture text, HTML (the copied fragment), images, and LibreOffice Calc's
      native replay bundle from one selection generation.
- [x] Never read copies marked with the password-manager hint.
- [x] Exact backup and restore of every MIME type.
- [x] Staged pastes that report the read that pastes, despite KDE clipboard
      monitors reading every new selection.
- [x] Transfers time out and never block the Wayland thread or key handling.
- [x] Unit tests over a scripted selection; integration tests against the real
      compositor with `wl-copy`/`wl-paste` and a second data-control client.
- [x] Replay end to end: items pasted in order through the virtual keyboard,
      each read confirmed and archived, and the user's clipboard restored when
      the queue ran out (October 7, 2026).
- [ ] Verify slip editing and Compose copy/paste end to end.
- [x] An X11 selection clipboard backend (`X11Selection`, October 10, 2026):
      the fallback when the compositor has no clipboard-manager protocol. On
      GNOME 50 (Ubuntu 26.04) the `--linux-clipboard-probe` checks all pass
      (reads, writes with HTML, 1 MB both ways via INCR, staged-paste reads,
      backup and restore), and a held Ctrl+C capture worked in the real app.
      `LinuxX11ClipboardTests` need a Linux run with `xclip`.
- [x] Rich text through GNOME's Xwayland bridge: a Firefox copy kept its
      HTML and Replayed formatted into LibreOffice Writer (October 10, 2026).
- [x] Compose and Replay paste into native GNOME apps (GNOME Text Editor,
      October 10, 2026).
- [x] Image links and base64 `data:` images capture as pictures on GNOME.
- [x] A copied picture through GNOME's bridge (a held Ctrl+C on a picture in
      LibreOffice Writer). GNOME's Image Viewer copies the file, so its path.
- [x] An X11 session: Cinnamon 6.6 on Linux Mint 22.3 (October 11, 2026).
      From a bare install: the clipboard connected through X11 with nothing
      added, the shortcuts-off dialog named the missing step, the tray icon
      showed, and after setup the whole tour (holds, popups, Compose paste)
      passed. The open-source `nouveau` driver on an RTX 4070 Ti killed
      Cinnamon's GPU channel as a popup opened (a driver fault, frozen screen);
      NVIDIA's driver fixed it. Fixed on the way: the toast and hold indicator
      fell behind the app after their first show (the window manager drops
      "keep above" on hide; 3fa8b32), and a held copy of a LibreOffice picture
      timed out because two reads asked LibreOffice at once (2eb6b5f). Mint's
      image viewer hands over a picture and its file path together; a held
      Ctrl+X on a file in Nemo, discarded, leaves the file alone.
- [x] niri 26.04 (PikaOS 4, October 12, 2026): `ext-data-control-v1`, X11
      apps through `xwayland-satellite`. Satellite keeps no `_NET_CLIENT_LIST`,
      so Zetl threw away every active window; with no list it now trusts
      `_NET_ACTIVE_WINDOW`, which satellite clears to zero while a Wayland
      window has focus (6ba9ab9). Satellite passes on no positions and niri
      tiles resizable windows, so placement comes from niri window rules
      (in linux-setup.md), and focus-follows-mouse closes popups.
- [x] Hyprland 0.56 (PikaOS 4, October 12, 2026): `ext-data-control-v1`, its
      own Xwayland window manager (client list and all). Hyprland tiles
      resizable windows (a float rule fixes it), sets no X11 primary monitor,
      and shifts an X11 window that opens while the pointer is on another
      monitor by that monitor's offset; Zetl now undoes a window manager's
      move as a window appears (081cd3f) and asks Hyprland where the mouse
      is (5a50fac). `float_switch_override_focus` moves focus as the mouse
      crosses between tiled and floating windows; setup in linux-setup.md.
- [x] COSMIC 1.0 (Pop!_OS 24.04, October 13, 2026): `ext-data-control-v1`
      out of the box, a full Xwayland window manager, and `/dev/uinput`
      already writable by the user (only the `input` group needed; the
      advice no longer asks for the udev rule there, 7fda6f3). COSMIC places
      every managed X11 window itself and ignores moves (test windows of
      every type), so popups open near the middle; only override-redirect
      windows go where asked. On COSMIC the toast and hold ring are overlays
      (986f26a), nudged after they appear because COSMIC draws them at 0,0
      otherwise (2a2421b); checked with screenshots through the portal.
      Driven over SSH with injected keys: text capture from COSMIC Edit, a
      picture from LibreOffice Draw (about 2 s for a large wallpaper while
      LibreOffice renders its PNG), Kastn from Zetl's menu (connected, lists
      the captures), and start at login (Zetl came back after a reboot). Seen
      on the way: Settings' "Start Zetl when you log in" label is clipped by
      its focus box. (Enter on a checkbox toggles it and Ctrl+Enter accepts,
      as intended.)
- [x] Bazzite 44 (KDE Wayland, Fedora Kinoite base, read-only `/usr`,
      October 14, 2026): `ext-data-control-v1`; `/dev/uinput` already
      writable by the user; the `input` group lives only in `/usr/lib/group`,
      so Zetl now reads it there and says to copy it into `/etc/group` before
      `usermod` (3f37439). Captures, placement, Kastn, start at login, and
      the toast-focus fix (typing after a save reached KWrite) all checked
      over SSH. The RC 3 AppImage runs: `--kastn` started Kastn, which started
      Zetl through the same AppImage (two FUSE mounts), and they connected.
- [x] Xfce 4.20 on Xorg (Manjaro, October 13, 2026): X11 clipboard, tray icon,
      first-run dialog, captures (popup top-center, keyboard back in the app
      after saving), Kastn from the menu, start at login (held across a
      restart). Manjaro marks no
      RandR primary monitor, so `Screens.Primary` was null: the toast landed
      in Zetl's 1200x800 fallback area and other popups went unplaced (xfwm4
      centered them); placement now falls back to the monitor at the origin
      (c7c2850). systemd 261 rejects `udevadm trigger --name-match=uinput`
      (57e0c2f). `xfce4-clipman` runs by default; nothing seen yet.
- [x] The toast took the keyboard on KDE after a quick note from Konsole:
      Zetl can't hand focus back to a native Wayland window, and KWin gave it
      to the newly mapped toast (with a taskbar icon). The toast and ring now
      set WM_HINTS input=false and _NET_WM_USER_TIME 0 before every show
      (a5718c0); confirmed by Lukas on AerynOS, October 13, 2026.
- [x] Kastn takes the keyboard on GNOME, both when it opens and when Zetl
      brings it forward again (checked October 10, 2026 by typing into a slip
      and saving with Ctrl+S, no click). Zetl's own popups needed a fix
      (c021d9e); Kastn's main window did not.
- [ ] Check GNOME, which may not offer `ext-data-control-v1`. Planned route:
      skip Wayland and reach GNOME's own clipboard (Mutter's `Meta.Selection`)
      over D-Bus, most likely through a small GNOME Shell extension that Zetl
      installs, behind the same `ILinuxSelection` seam. The remote-desktop
      portal clipboard is the no-install alternative, but it asks for consent
      and shows a "being controlled" indicator while active. Design and prior
      art (Clipman): [`linux-gnome-clipboard.md`](linux-gnome-clipboard.md).

## Priority 4: Permissions And Installation

User setup, install, and rollback are in [`linux-setup.md`](linux-setup.md).

- [x] Permission model: the desktop user in the `input` group, plus a udev rule
      (`packaging/linux/70-zetl-uinput.rules`) giving that group `/dev/uinput`.
- [x] Detect insufficient permissions at startup; the log names the device and
      the missing access, and the installer reports what is missing.
- [x] Self-contained `linux-x64` bundle (`scripts/publish-linux-x64.sh`) with a
      per-user installer: desktop entries, icons, optional autostart, uninstall.
- [x] Document rollback of the permission changes.
- [x] AppImage (`scripts/publish-linux-appimage.sh`): both apps in one image;
      each launches the other through the AppImage so either can close first.
- [x] Start at login from Zetl's Settings: writes the XDG autostart entry
      (the AppImage file when running from one) and repairs it after a move.
      Checked on GNOME 50 (October 10, 2026).
- [ ] A distribution package (an AerynOS `stone.yaml` recipe for boulder, then
      others) that installs the udev rule system-wide.
- [ ] Decide whether a `--diagnose` report of candidate and usable devices is
      still needed beyond the log and installer check.

## Integration Milestones

### Headless Workflow

- [ ] Start the portable runtime with Linux backends and no Avalonia UI.
- [ ] Capture copied text into disposable state.
- [ ] Save a quick note.
- [ ] Replay a queue into a terminal or editor.
- [ ] Compile to stdout and the clipboard.

This isolates keyboard, clipboard, runtime, and storage behavior before desktop
window policy is involved.

### Avalonia Linux Alpha

- [x] Select Linux backends at startup.
- [x] Shortcut popups come to the front with focus on KDE Wayland (X11
      activation as a tool request), dismiss on click-away to another app or
      the desktop, and hand focus back when they close.
- [x] Capture, quick note, and Board verified by hand on KDE Plasma Wayland
      (October 7, 2026).
- [x] First-run tour, Compose, toasts, the hold indicator, the tray icon, and
      Kastn (connected to Zetl over IPC) render and come forward on KDE.
- [x] Shift lane capture and Compose; pass-through flip.
- [ ] Settings, templates, and the tray menu.
- [x] A way into the menu without a tray: GNOME on Ubuntu 26.04 has no
      StatusNotifierWatcher, so Zetl's tray icon never shows. Opening Zetl
      again now shows its menu in a window (2c82a73, checked there).
- [ ] Verify placement on multiple monitors and scaled displays. Avalonia
      renders unscaled under Xwayland at fractional scaling (the Board is
      1040 physical px on a 1.2x display). Cause found October 8, 2026:
      Avalonia scales from `Xft.dpi`, which Plasma sets at login when Xwayland
      apps scale themselves, but the test session (up since September 5) had
      no `RESOURCE_MANAGER` at all; `AVALONIA_GLOBAL_SCALE_FACTOR=1.2` gives
      the designed sizes. Next: check a fresh login; if `Xft.dpi` is still
      missing, report it to Plasma/AerynOS and fall back to XSETTINGS
      (`Gdk/UnscaledDPI`). Xwayland has one scale, so a second monitor at a
      different scale cannot match from an X11 app. X11's work area spans
      every monitor, so placement now clips it to each screen (3f694b7); the
      toast lands right on a 6K-at-200% + 1080p GNOME desk.
- [x] Fixed layouts assumed Segoe UI widths; Linux sans fonts are 2-8% wider.
      The Board and Settings were given room so nothing clips (every Zetl
      window checked under Noto Sans).
- [ ] Test logout/login, suspend/resume, and compositor restart.

### Popups follow the mouse

Agreed October 12, 2026: a popup opens on the monitor the mouse is on, on
every platform. Hyprland does it (5a50fac); X11 can't see the mouse while it
is over a native Wayland window, so each desktop needs its own source:

- [x] Hyprland: the cursor from its IPC socket (`j/cursorpos`).
- [ ] Windows: the cursor's monitor instead of the working window's (Zetl
      already reads `GetCursorPos`; only the screen choice changes).
- [ ] X11 sessions (Cinnamon, Xfce): `XQueryPointer` is accurate there; Zetl
      reads no pointer on Linux yet.
- [ ] KDE Plasma (Wayland): a KWin script or D-Bus call for the cursor.
- [ ] Sway and wlroots: the focused output over IPC (no cursor query).
- [ ] niri: places floating windows itself; the focused output, if a rule can
      target it.
- [ ] GNOME: no cursor for other apps without an extension; stays on the
      primary monitor unless the GNOME Shell extension route happens.
- [x] The toast stays in the notification area on the primary monitor
      (Lukas, October 12, 2026); only popups follow the mouse.

### Release Candidate

- [ ] Build Windows and Linux artifacts from a clean checkout.
- [ ] Run portable and platform integration suites.
- [ ] Test migration and cross-version rollback.
- [ ] Test Unicode paths, multiline clipboard content, scaling, and themes.
- [ ] Document supported distributions, sessions, dependencies, and known
      Wayland limitations.
- [ ] Add release packaging and checksums.

## Development Gates

```powershell
dotnet build Zetl.slnx --no-restore -p:UseAppHost=false
dotnet test Zetl.Tests\Zetl.Tests.csproj --no-build
dotnet .\artifacts\bin\Debug\Zetl.dll --self-test
```

The portable test assembly must also run on Linux. Linux backend integration
tests should use disposable state and must never require a developer to recover
the only active keyboard without an SSH or secondary-input path.
