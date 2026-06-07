# Zetl Linux Port Roadmap

This is the working checklist for moving Zetl to one Avalonia application on
Windows and Linux. Design background and the evdev/uinput model live in
[`linux-port.md`](linux-port.md).

## Target

- One shared Avalonia UI and behavior model on Windows, X11, and Wayland.
- One portable workflow runtime above platform keyboard and clipboard backends.
- No required keyd/kmonad-style daemon.
- The current WinForms application remains usable until the Avalonia application
  passes parity testing on Windows.
- Linux runs without root after installation and permission setup.

## Current Baseline

Completed:

- [x] `Chordl` targets portable `net10.0`.
- [x] `Zetl.Core` contains portable state, storage, settings, and platform
      contracts.
- [x] Windows keyboard and clipboard implementations sit behind
      `IKeyboardBackend` and `IClipboard`.
- [x] `Zetl.App` is a buildable Avalonia head.
- [x] Note capture is ported to Avalonia.
- [x] Project setup is ported to Avalonia.
- [x] Settings are ported to Avalonia.
- [x] Both heads build side by side.
- [x] The portable 80-test suite and Windows artifact test pass.
- [ ] Re-verify the B1 evdev/uinput safety spike after correcting virtual
      modifier cleanup.

Retained WinForms fallback:

- `ZetlHotkeys.csproj` now builds `Zetl.Legacy.exe` only as an A7 fallback.
- Shared workflow orchestration lives in `Zetl.Runtime`; the canonical Windows
  application is the Avalonia `Zetl.exe`.

Still missing on Linux:

- Production keyboard backend extraction, hotplug, and multi-keyboard support.
- Linux clipboard implementation.
- Permission/install setup, packaging, and desktop integration.

## Target Architecture

The intended dependency direction is:

```text
Chordl -----------+
                  |
Zetl.Core --------+--> Zetl.Runtime --> Zetl.App (Avalonia)
                         |                 |
                         |                 +--> Windows UI behavior
                         |                 +--> Linux UI behavior
                         |
                         +--> IKeyboardBackend
                         +--> IClipboard

Platform implementations:
  WindowsKeyboardBackend / WindowsClipboard
  LinuxKeyboardBackend   / LinuxClipboard
```

`Zetl.Runtime` will be a portable `net10.0` project. It will own the behavior
currently embedded in `ZetlApplicationContext`, while the UI host owns:

- UI-thread dispatch.
- Opening windows and returning dialog results.
- Tray menu and application lifetime.
- Toast rendering and history display.
- Foreground-window capture, placement, restoration, and click-away policy.

The runtime should communicate with the host through small request/result
contracts. It must not reference Avalonia, WinForms, HWNDs, X11 handles, or
Wayland objects.

## Track A: Shared Application

### A1. Portable Test And Runtime Foundation

Goal: make behavior testable on Linux before moving orchestration.

- [x] Add a repository solution/build entry point covering all projects.
- [x] Create a portable test runner or test project targeting `net10.0`.
- [x] Move the portable Chordl, state, storage, settings, compile, Replay, and
      Pop tests out of the Windows executable.
- [x] Keep only genuinely Windows-specific tests in the WinForms head.
- [x] Add `Zetl.Runtime`, referencing `Chordl` and `Zetl.Core`.
- [x] Define UI dispatcher, notification, dialog, and foreground-target
      contracts.
- [x] Move settings application, logging queue, undo bookkeeping, and
      destination-label logic into the runtime first.

Done when:

- [x] Portable tests run on Windows without loading WinForms.
- [ ] Portable tests run on Linux without loading WinForms.
- [x] Both existing heads still build.
- [x] Runtime tests cover the first extracted behaviors.

### A2. Extract Shortcut Orchestration

Goal: remove platform UI and message-loop concerns from shortcut behavior.

- [x] Move pending shortcut and clipboard observation logic.
- [x] Move tap handling for Replay and Pop.
- [x] Move hold routing for copy, cut, paste, Pop, Replay, Board, and undo.
- [x] Move replay clipboard save/restore behavior.
- [x] Represent note capture and compile flows as request/result records.
- [x] Replace direct window calls with host requests.
- [x] Keep `OnTapDispatched` synchronous where Chordl requires an immediate
      suppress decision.
- [x] Add deterministic tests using fake keyboard, clipboard, delay, dispatcher,
      notification sink, and typed host request/result records.

Done when:

- [x] `ZetlApplicationContext` is primarily a WinForms adapter around
  `Zetl.Runtime`.
- [x] Shortcut behavior tests do not require a desktop session.
- [x] Current Windows builds and platform tests remain green.

### A3. Finish Standalone Avalonia Windows

Goal: complete the smaller UI pieces before the two large workspaces.

- [x] First-run guide.
- [x] Bucket settings.
- [x] Text prompt.
- [x] Notification history.
- [x] Add preview routes and representative throwaway data for each window.
- [x] Add keyboard parity: Escape, default action, and `Ctrl+Enter`.
- [x] Verify scaling, tab order, validation, and light/dark theme behavior.

Done when:

- [x] Each window can be launched independently from the preview harness.
- [x] Result contracts match what the runtime needs.
- [x] No window reads or writes real user data in preview mode.

### A4. Port The Board

Goal: make the main project/bucket/note workspace usable in Avalonia.

- [x] Project selection, creation, deletion, rename, and activation.
- [x] Bucket tree/list with nesting and active-bucket state.
- [x] Bucket creation, deletion, rename, and settings.
- [x] Note list, editing, creation, deletion, and save-on-selection behavior.
- [x] Standard/Replay kind and Pop state indicators.
- [x] Normal and Shift lane support.
- [x] Existing Board keyboard shortcuts.
- [x] Store-change refresh without losing the useful selection.
- [x] Host-controlled auto-hide/click-away behavior.

Done when:

- [x] The Board can perform every operation documented in the README.
- [x] The WinForms and Avalonia Boards use the same persisted-state operations.
- [x] Board workflows have runtime/state tests for destructive operations.

### A5. Port Compile

Goal: reproduce the complete compile workflow.

- [x] Source project selector and session-only filtering.
- [x] Bucket/note selection tree with select-all/clear-all.
- [x] Formatted, Plain, TSV, and unformatted modes.
- [x] TSV row length and bucket defaults.
- [x] Live preview.
- [x] Copy, paste now, save flattened, and save preserving note structure.
- [x] Independent destination project and bucket selectors.
- [x] Last-item completion path.

Done when:

- [x] Golden tests cover compiled output for every mode.
- [x] Both UIs compile through the same portable state operations.
- [x] Clipboard and paste actions flow through runtime/platform contracts.

### A5.5. Theme Tokens And Editor

Goal: make the Avalonia appearance configurable without hand-editing XAML.

- [x] Move colors, typography, spacing, and control styling to named theme
      resources.
- [x] Define versioned theme settings with light and dark variants.
- [x] Add an Avalonia theme editor with live preview and validation.
- [x] Save, load, duplicate, reset, import, and export themes.
- [x] Apply theme changes across open windows without restarting.
- [x] Keep a built-in fallback theme so invalid user files cannot break startup.

Done when:

- [x] A user can configure and persist a complete theme from the GUI.
- [x] Theme files round-trip without losing unknown forward-compatible values.
- [x] Built-in and custom themes use shared resources across standalone windows
      and both workspaces.

### A6. Avalonia Tray, Toasts, And Window Policy

Goal: make `Zetl.App` a complete Windows daily driver.

- [x] Tray icon, menu, Board action, and quit lifecycle.
- [x] Toast presentation and notification history.
- [x] First-run launch and settings persistence.
- [x] Single-instance handling.
- [x] Periodic activity-log flushing.
- [x] Abstract window activation/restore behavior.
- [x] Implement Windows foreground behavior for Avalonia.
- [x] Define Linux behavior that degrades cleanly when Wayland denies activation
      or exact placement.
- [x] Decide and document click-away commit behavior per window.

Done when:

- [x] `Zetl.App` starts as a tray application and drives the real runtime on Windows.
- [x] No preview harness is used during normal startup.
- [x] All documented shortcuts and tray actions are usable.

### A7. Windows Parity And Cutover

Goal: establish the Avalonia head as the canonical application.

- [x] Run both heads against disposable equivalent state and compare workflows.
- [ ] Dogfood Avalonia on Windows through normal daily use.
- [ ] Verify elevated-app limitations and failure messages. Automated rejection
      coverage is complete; an elevated foreground target still needs a manual pass.
- [ ] Verify startup, shutdown, crash recovery, and single-instance behavior.
      Automated first-run, close-to-tray, forced-restart, persistence, and
      second-instance checks pass; tray Quit still needs the manual pass.
- [x] Update README and build/publish instructions.
- [x] Switch the primary Windows artifact to `Zetl.App`.
- [ ] Retire WinForms only after a stable fallback tag or branch exists.

Done when:

- [x] No known README workflow requires the WinForms head.
- Avalonia has passed an agreed dogfood period without state corruption or
  shortcut regressions.

## Track B: Linux Platform

Track B can proceed in parallel with Track A. The first spike should be run on
the Linux machine over SSH so keyboard recovery does not depend on the grabbed
device.

### B1. evdev/uinput Safety Spike

Goal: prove the input model on real hardware before building abstractions.

- [x] Enumerate candidate keyboard event devices.
- [x] Open one device and acquire `EVIOCGRAB`.
- [x] Create a uinput keyboard with the required key capabilities.
- [x] Forward untouched key events and synchronization events.
- [x] Route Ctrl+C events through `ChordlProcessor`.
- [x] Suppress held action events and replay tapped Ctrl+C.
- [x] Add a hard-coded panic passthrough/release mechanism.
- [x] Confirm file descriptors release the grab on process exit and crash.
- [x] Test autorepeat and modifier changes during hold detection.

Done when:

- Typing remains correct through the forwarder.
- Tap and hold Ctrl+C behave correctly in at least one X11 and one Wayland
  desktop session, where available.
- The keyboard can be recovered after exceptions and forced termination.

Validated June 7, 2026 on AerynOS 2026.05 under Wayland using a Keychron C2
Pro. The spike passed read-only discovery, timed grab/forward, uinput creation,
tap/hold replay and suppression, Linux key autorepeat, panic release, and
immediate re-grab after `SIGKILL`. X11 was not available on the target machine.
The hardware run also exposed and fixed Shift autorepeat restarting hold
detection; portable regression coverage now protects that behavior. A later
desktop check found Ctrl logically stuck after a run that began with an orphaned
Ctrl repeat. B1 remains open until the new all-keys-up pre-grab gate and explicit
virtual key release sequence pass another desktop test.

### B2. Production Linux Keyboard Backend

Goal: implement `IKeyboardBackend` robustly.

- [ ] Add Linux input constants, structs, ioctl wrappers, and safe handles.
- [ ] Map Linux `KEY_*` codes to Chordl virtual-key values.
- [ ] Preserve key-down, key-up, and repeat semantics.
- [ ] Forward non-key events required by physical keyboards.
- [ ] Avoid reading Zetl's own uinput device.
- [ ] Support multiple physical keyboards.
- [ ] Handle device add/remove and suspend/resume.
- [ ] Implement `SendChord` and `SendPaste`.
- [ ] Ensure disposal cannot leave an intentional grab active.
- [ ] Log actionable device and permission errors.

Done when:

- The portable Chordl suite passes through recorded Linux event sequences.
- Two keyboards can be used without duplicate or lost events.
- Hotplug and backend restart recover without restarting Zetl.

### B3. Linux Clipboard

Goal: implement `IClipboard` for common Linux sessions.

- [ ] Detect Wayland versus X11 session.
- [ ] Support `wl-copy`/`wl-paste` on Wayland.
- [ ] Support `xclip` or `xsel` on X11.
- [ ] Use a stable content-derived change token.
- [ ] Apply timeouts so a clipboard command cannot stall keyboard handling.
- [ ] Handle missing tools, non-text content, empty clipboard, and command
      failures.
- [ ] Preserve multiline Unicode text.
- [ ] Document the initial external-tool dependency.

Done when:

- Copy capture, note editing, compile copy, Pop, and Replay clipboard restoration
  pass integration tests on X11 and Wayland.
- Missing clipboard tools produce a clear notification without crashing.

### B4. Permissions And Installation

Goal: run the backend as the desktop user, not as root.

- [ ] Choose the supported permission model for `/dev/input/event*` and
      `/dev/uinput`.
- [ ] Add udev rules and group/setup instructions.
- [ ] Detect insufficient permissions at startup.
- [ ] Provide a diagnostic command or startup report listing usable devices.
- [ ] Package required desktop file, icon, and autostart option.
- [ ] Document rollback/uninstall of permission changes.

Done when:

- A fresh supported Linux install can configure Zetl without running the app as
  root.
- Permission failures explain the exact corrective action.

## Integration Milestones

### I1. Headless Linux Workflow

Depends on A2, B2, and B3.

- [ ] Start the runtime without Avalonia.
- [ ] Capture a copied value into disposable state.
- [ ] Save a quick note.
- [ ] Replay a queued value into a terminal/editor.
- [ ] Compile notes to stdout and clipboard.

Done when the complete keyboard -> runtime -> state/clipboard path works before
UI variables are introduced.

### I2. Avalonia Linux Alpha

Depends on A4, A5, A6, and I1.

- [ ] Select Linux backends at startup.
- [ ] Run tray, note capture, Board, compile, settings, and first-run flows.
- [ ] Verify X11 focus/placement behavior.
- [ ] Verify Wayland fallback behavior.
- [ ] Test normal and Shift lanes.
- [ ] Test logout/login, suspend/resume, and device hotplug.

Done when the README's core workflows are usable on the target Linux machine.

### I3. Release Candidate

- [ ] Build and smoke-test Windows and Linux artifacts from a clean checkout.
- [ ] Run portable unit tests and platform integration tests.
- [ ] Test state migration and cross-version rollback.
- [ ] Test Unicode paths, multiline clipboard content, scaling, and dark mode.
- [ ] Document supported distributions, desktop sessions, dependencies, and
      known Wayland limitations.
- [ ] Add release packaging and checksums.

Done when Windows remains regression-free and Linux installation is repeatable
from the documentation.

## Recommended Execution Order

The next main-code milestone is **A1**, followed by **A2**. This unlocks real
Avalonia integration and gives Linux a portable test suite.

In parallel on the Linux machine, start **B1** as soon as an SSH recovery path is
available. Do not wait for the UI to finish: the input spike is the highest-risk
unknown in the port.

After A2:

1. Continue A3, A4, and A5 in that order.
2. Turn the Avalonia head into the real Windows application in A6.
3. Complete B2 and B3 behind the same runtime contracts.
4. Join the tracks at I1 before attempting the full Linux desktop alpha.

## Checks Kept Green

Run these after each milestone:

```powershell
dotnet build Zetl.slnx --no-restore -p:UseAppHost=false
dotnet .\Zetl.Tests\bin\Debug\net10.0\Zetl.Tests.dll
dotnet .\bin\Debug\net10.0-windows\Zetl.dll --self-test
```

The portable test command is the primary behavior gate and must also run on
Linux when that environment is available. The Windows self-test now checks only
the platform artifact's embedded configuration.
