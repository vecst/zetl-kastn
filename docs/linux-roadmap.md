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

Implement `LinuxKeyboardBackend` behind `IKeyboardBackend`:

- [ ] Move Linux constants, structs, ioctl wrappers, and safe handles out of the
      spike.
- [ ] Map Linux `KEY_*` values to Chordl key values.
- [ ] Preserve key-down, key-up, and repeat semantics.
- [ ] Forward required non-key events and synchronization boundaries.
- [ ] Avoid reading Zetl's own uinput device.
- [ ] Support multiple physical keyboards without duplicate events.
- [ ] Handle device add/remove, backend restart, suspend, and resume.
- [ ] Implement `SendChord` and `SendPaste`.
- [ ] Make disposal and failure paths release virtual modifiers and device
      grabs.
- [ ] Report actionable device and permission errors.
- [ ] Test recorded Linux event sequences through the portable Chordl suite.

Done when two keyboards, hotplug, restart, and forced termination recover
without duplicate, lost, inverted, or stuck input.

## Priority 3: Linux Clipboard

Implement `IClipboard` for common desktop sessions:

- [ ] Detect Wayland versus X11.
- [ ] Support `wl-copy`/`wl-paste` on Wayland.
- [ ] Support `xclip` or `xsel` on X11.
- [ ] Use a content-derived change token.
- [ ] Apply process timeouts away from keyboard handling.
- [ ] Preserve multiline Unicode text.
- [ ] Handle missing tools, empty clipboard, unsupported content, and command
      failures without crashing.
- [ ] Document the initial external-tool dependencies.
- [ ] Define the image clipboard path separately from the text-first backend.

Done when copy capture, slip editing, compile copy, Pop, Replay, and clipboard
restoration pass integration tests under supported X11 and Wayland sessions.

## Priority 4: Permissions And Installation

- [ ] Choose the supported `/dev/input/event*` and `/dev/uinput` permission
      model.
- [ ] Provide udev rules and group/setup instructions.
- [ ] Detect insufficient permissions at startup.
- [ ] Add a diagnostic report listing candidate and usable devices.
- [ ] Package the application, desktop file, icon, and optional autostart.
- [ ] Document rollback and uninstall of permission changes.

The installed app must run as the desktop user. Permission failures should name
the exact missing device or group and the corrective action.

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

- [ ] Select Linux backends at startup.
- [ ] Run tray, capture, Board, Compile, settings, templates, and first-run
      flows.
- [ ] Verify X11 focus and placement.
- [ ] Verify Wayland activation and placement fallback.
- [ ] Test normal and Shift lanes.
- [ ] Test logout/login, suspend/resume, device hotplug, and compositor restart.

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
