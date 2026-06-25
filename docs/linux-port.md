# Zetl Linux Architecture

This document explains the Linux keyboard and UI model. Current implementation
status is tracked in [`linux-roadmap.md`](linux-roadmap.md).

## Why Zetl Owns The Input Backend

The tap/hold state machine already lives in the portable `ChordlProcessor`.
Building a Linux backend means implementing the operating-system I/O ends—not a
second shortcut engine.

Keeping the backend inside Zetl provides:

- identical shortcut behavior and configuration across platforms;
- no separately managed keyd/kmonad-style daemon;
- input interception tied to Zetl's lifetime;
- one runtime and test model for Windows and Linux.

## Suppression Contract

Chordl's core contract is:

```text
HandleKeyEvent(key, isDown, isUp) -> suppress?
```

The operating systems apply it differently:

- **Windows:** a low-level hook sees each event and vetoes selected events.
- **Linux:** Zetl exclusively grabs the physical keyboard, then forwards every
  event Chordl does not suppress through uinput.

The Linux model is therefore grab-and-forward rather than selective veto, but
the decision engine remains the same.

## Dependency Direction

```text
Chordl -----------+
                  |
Zetl.Core --------+--> Zetl.Runtime --> Zetl.App (Avalonia)
                         |                 |
                         |                 +--> platform window policy
                         |
                         +--> IKeyboardBackend
                         +--> IClipboard

Platform implementations:
  WindowsKeyboardBackend / WindowsClipboard
  LinuxKeyboardBackend   / LinuxClipboard
```

`Zetl.Runtime` must not reference Avalonia, HWNDs, X11 handles, or Wayland
objects. The UI host owns:

- UI-thread dispatch;
- opening windows and returning typed results;
- tray and application lifetime;
- toast rendering and history;
- foreground capture, activation, placement, and click-away policy.

## Linux Keyboard Model

A production backend needs to:

1. discover eligible `/dev/input/event*` keyboards;
2. wait for a safe all-keys-up boundary;
3. acquire `EVIOCGRAB`;
4. create a uinput device with the required capabilities;
5. read physical events;
6. ask Chordl whether each key event should be suppressed;
7. forward unsuppressed events and required synchronization events;
8. emit replayed chords through uinput;
9. release virtual keys and grabs on every shutdown or failure path.

Exclusive grabs are released when file descriptors close, including after a
process crash. Logical virtual-key state still requires explicit cleanup:
closing the descriptor alone is not enough if the virtual device emitted a
modifier down without its matching release.

## Safety Rules

- Develop exclusive-grab behavior with SSH or a secondary input/recovery path.
- Do not grab while physical keys are already down.
- Keep a panic release/passthrough mechanism independent from the ordinary
  shortcut state machine.
- Never read events from Zetl's own uinput device.
- Preserve event order, repeat semantics, and `SYN_*` boundaries.
- Treat multiple keyboards as one logical input stream without duplicating
  output.
- Make disposal idempotent and release every virtual modifier explicitly.

## Clipboard Model

The initial Linux clipboard backend may use desktop tools:

- Wayland: `wl-copy` and `wl-paste`
- X11: `xclip` or `xsel`

`IClipboard` should expose the same behavior the runtime expects on Windows:
read, write, and a stable change token. On Linux, a content hash can act as the
change token.

Clipboard processes must have timeouts and must never block the input event
loop. Missing tools and unsupported content should produce actionable
notifications rather than terminate capture.

Text is the first integration target. Picture capture and restoration require a
separate MIME-aware clipboard path.

## Avalonia Window Policy

The UI is shared, but foreground behavior cannot be identical:

- Shortcut capture commits when focus moves away.
- Shortcut Compile cancels when focus moves away.
- Shortcut Boards auto-hide; tray-opened Boards remain open.
- Child dialogs temporarily protect an owning Board from auto-hide.
- Windows restores the captured target before synthetic paste.
- X11 can provide comparable activation and placement where permitted.
- Wayland may deny activation or exact placement; Zetl must continue cleanly
  with compositor-controlled behavior.

Platform-specific activation belongs in the UI host, not in the keyboard or
portable runtime layers.

## Project Layout

- `Chordl/` — portable tap/hold engine
- `Zetl.Core/` — state, persistence, settings, and platform contracts
- `Zetl.Runtime/` — portable shortcut/workflow orchestration
- `Zetl.App/` — shared Avalonia application
- `Zetl.Tests/` — portable behavior tests
- `Zetl.Linux.Spike/` — disposable evdev/uinput safety work

The production Linux backend should graduate from the spike only after the
hardware safety gate in [`linux-roadmap.md`](linux-roadmap.md) passes.
