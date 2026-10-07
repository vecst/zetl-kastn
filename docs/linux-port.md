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

Zetl talks to the compositor's clipboard directly through `ext-data-control-v1`,
the Wayland protocol KWin (and wlroots compositors) give clipboard managers.
`WaylandDataControl` owns its own Wayland connection on a dedicated thread;
`LinuxClipboard` implements `IClipboard` over it.

Why not `wl-copy`/`wl-paste` or Klipper's D-Bus interface:

- a data-control client sees every selection change without focus, so the
  change token is a generation counter advanced by compositor events, not a
  polled content hash;
- every MIME type of one copy is read from a single offer, so a capture is
  never torn between two copies;
- text and HTML (and LibreOffice's native bundle) go on the clipboard together,
  which `wl-copy` cannot do and Zetl's rich copy and clipboard restore need;
- Klipper's D-Bus interface carries text only.

Transfers never block the Wayland thread: incoming data is read on the caller's
thread, and Zetl's own data is written from the thread pool, so Zetl can read a
selection it owns. Copies marked `x-kde-passwordManagerHint` are never read for
capture. Staged pastes carry that hint too; because some KDE clipboard monitors
read every new selection regardless, a staged paste waits for them to go quiet
before the paste is sent, and only later reads count as the paste landing.

Without `ext-data-control-v1` (X11 sessions, compositors that lack it) the
clipboard falls back to the unsupported stub and capture is off.

## Avalonia Window Policy

The UI is shared, but foreground behavior cannot be identical:

- Shortcut capture commits when focus moves away.
- Shortcut Compile cancels when focus moves away.
- Shortcut Boards auto-hide; tray-opened Boards remain open.
- Child dialogs temporarily protect an owning Board from auto-hide.
- Windows restores the captured target before synthetic paste.

Avalonia runs on X11, under Xwayland on Wayland desktops. Zetl reads keys below
the desktop, so the window manager never sees a shortcut as input to Zetl and
refuses ordinary activation. `ZetlX11Activation` asks the way task switchers do
(`_NET_ACTIVE_WINDOW` from a pager/tool source), keeping the popup above until
the window manager's active window is the popup. Avalonia's own activation
tracking misses activations it did not request, so the click-away watcher
follows X11 keyboard focus, which leaves Zetl's windows whenever another app or
the desktop is clicked. A shortcut's target is the active X11 window, or "a
Wayland window" when a native app had focus; the compositor's focus chain hands
focus back to it when a popup closes.

Platform-specific activation belongs in the UI host, not in the keyboard or
portable runtime layers.

## Project Layout

- `Chordl/` — portable tap/hold engine
- `Zetl.Core/` — state, persistence, settings, and platform contracts
- `Zetl.Runtime/` — portable shortcut/workflow orchestration
- `Zetl.Linux/` — evdev/uinput keyboard backend and Wayland data-control clipboard
- `Zetl.App/` — shared Avalonia application, including X11 activation
- `Zetl.Tests/` — portable behavior tests, plus Linux kernel and compositor
  integration tests that skip elsewhere
- `Zetl.Linux.Spike/` — the original evdev/uinput safety spike, superseded by
  `Zetl.Linux`
