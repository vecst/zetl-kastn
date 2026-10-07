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
      and take virtual keyboards only for tests.
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

`LinuxClipboard` implements `IClipboard` over Wayland's `ext-data-control-v1`
(see the clipboard model in [`linux-port.md`](linux-port.md)).

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
- [ ] An X11-session clipboard (none yet: capture is off without
      `ext-data-control-v1`).
- [ ] Check GNOME, which may not offer `ext-data-control-v1`.

## Priority 4: Permissions And Installation

User setup, install, and rollback are in [`linux-setup.md`](linux-setup.md).

- [x] Permission model: the desktop user in the `input` group, plus a udev rule
      (`packaging/linux/70-zetl-uinput.rules`) giving that group `/dev/uinput`.
- [x] Detect insufficient permissions at startup; the log names the device and
      the missing access, and the installer reports what is missing.
- [x] Self-contained `linux-x64` bundle (`scripts/publish-linux-x64.sh`) with a
      per-user installer: desktop entries, icons, optional autostart, uninstall.
- [x] Document rollback of the permission changes.
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
- [ ] Verify placement on multiple monitors and scaled displays. Avalonia
      renders unscaled under Xwayland at fractional scaling (the Board is
      1040 physical px on a 1.2x display).
- [ ] Fixed layouts assume Segoe UI widths; Linux sans fonts are 2-8% wider,
      which clips the Board's toolbar. Decide the UI font or loosen layouts.
- [ ] Test logout/login, suspend/resume, and compositor restart.

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
