# Zetl Linux port

Goal: run Zetl on Linux (X11 and Wayland) while keeping a single C# codebase and
one behavior model shared with Windows. No external daemon (keyd/kmonad); Zetl
owns its own keyboard interception, the same way it does on Windows.

## Why roll our own (vs keyd / kmonad)

The hard part — the tap/hold state machine — is already written and tested in
`ChordlProcessor`, and it is platform-neutral. "Rolling our own" therefore means
writing only the OS I/O ends (read keys, send keys), not a new engine. Doing so
buys: one identical behavior/config across platforms, one shippable artifact, no
second daemon to install/configure, and it ties interception to Zetl's lifetime
(app not running -> keyboard behaves normally), which dodges the always-on-daemon
tradeoffs an external tool would force.

## The one contract that makes this clean

`ChordlProcessor.HandleKeyEvent(vk, isDown, isUp)` already returns `bool` =
"suppress this event". That single contract fits both platforms:

- Windows: the low-level hook returns 1 when it says suppress.
- Linux: the grab-and-forward reader re-injects the event via uinput *unless* it
  says suppress.

So the brain needs no changes; only the plumbing on either side differs.

## Platform model difference

- Windows hook = selective veto: see each event, swallow just the ones you want.
- Linux evdev = grab-and-forward: `EVIOCGRAB` the keyboard (suppresses
  everything), then re-emit via uinput everything you do NOT want to suppress.
  Same logic, inverted.

## Build order (each step independently valuable)

Status: the Windows-side foundation (steps 2-3) is done — they need no Linux box.
The spike and the Linux backend (steps 1, 4) are next and need the CachyOS
machine.

1. **Spike (de-risk first).** Throwaway: open `/dev/input/eventX`, `EVIOCGRAB`,
   create a uinput device, forward all keys, wire only Ctrl+C through
   `ChordlProcessor`. Prove tap/hold-while-Ctrl-held on real hardware + the
   compositor. Develop over SSH (a livelock could otherwise lock the keyboard).
2. **Extract the platform seam on Windows (no-regret).** *(done)* `IKeyboardBackend`
   + `WindowsKeyboardBackend` (hook + SendInput), `IClipboard` +
   `WindowsClipboard`. Behavior unchanged.
3. **Extract `Zetl.Core`.** *(done)* Portable `net10.0` library: `Chordl` (now
   neutral) + `Zetl.Core` (`ZetlState`/storage/settings + the platform
   interfaces). Windows head stays `net10.0-windows`/WinForms and references both.
4. **Linux backend.** evdev reader + uinput sender + clipboard (wl-clipboard /
   xclip), implementing the seam, reusing `ChordlProcessor`. Start headless
   (drive a quick note from a terminal) to validate capture -> action first.
5. **Linux UI.** Avalonia rewrite of the forms / tray / toasts. Biggest volume,
   lowest uncertainty, last.

## Project layout (after steps 2-3)

- `Chordl/` (`net10.0`) — portable tap/hold engine: processor, models, keys,
  config loader.
- `Zetl.Core/` (`net10.0`) — portable workflow logic: state, storage, settings,
  and the `IKeyboardBackend` / `IClipboard` seams. `InternalsVisibleTo("Zetl")`.
- `ZetlHotkeys.csproj` (`net10.0-windows`, WinForms) — the Windows head: forms,
  tray, `WindowsKeyboardBackend`, `WindowsClipboard`, `ChordlInput`, `Program`.
- A future Linux head references `Chordl` + `Zetl.Core` and supplies
  `LinuxKeyboardBackend` / `LinuxClipboard` + an Avalonia UI.

## The seam (step 2)

- `IKeyboardBackend` — global interception (`Start` + suppress contract) plus
  synthetic replay (`SendChord` / `SendPaste`). Windows: low-level hook +
  `ChordlInput`/SendInput. Linux: evdev grab/read + uinput.
- `IClipboard` (next) — `TryGetText` / `SetText` / change token. Windows:
  WinForms clipboard + `GetClipboardSequenceNumber`. Linux: wl-clipboard/xclip,
  with a content hash standing in for the change token.

Window/foreground P/Invokes (`SetForegroundWindow`, `AttachThreadInput`, etc.)
are UI-layer and handled by the Avalonia work (step 5), not the input seam.

## Free vs new

- Free (already cross-platform): `ChordlProcessor`, all `ZetlState` logic,
  storage, compile, settings.
- New: evdev/uinput interop (a few hundred lines of P/Invoke), clipboard
  shell-out, the Avalonia UI.

## Risks / things to budget

- Modifier-while-tap-hold behavior on real hardware (the spike validates this).
- Robustness tail: device hotplug, multiple keyboards, `KEY_*`->`VK_*` map,
  autorepeat, permissions (udev rule / `input`+`uinput` group).
- Crash-safety: an exclusive grab is released when the fd closes, so a *crash*
  restores the keyboard; only a *livelock* is dangerous — keep a hard-coded
  panic passthrough.
