# GNOME Clipboard Plan

GNOME (Mutter) does not offer `ext-data-control-v1`, the Wayland protocol Zetl's
KDE and wlroots clipboard uses, so a capture on GNOME finds nothing today. GNOME
Shell itself has the whole clipboard through Mutter's `Meta.Selection`. The plan
is to skip Wayland on GNOME: a small GNOME Shell extension that ships with Zetl
exposes that clipboard to Zetl over the session D-Bus, and a GNOME implementation
of `ILinuxSelection` talks to it. Nothing above that seam changes.

The remote-desktop portal's clipboard needs no install, but it asks for consent
and shows a "being controlled" indicator while active, which rules it out for an
always-running capture tool.

## Revised plan (October 8, 2026): an X11 clipboard backend first

`wl-copy`/`wl-paste` are no lite path: without `ext-data-control-v1`, Wayland
lets a client read the clipboard only while it has focus, so `wl-paste` flashes
a window to take focus, which on GNOME can flicker, be refused, and pull focus
from the app being captured, and `wl-paste --watch` cannot work at all.

X11 has no focus rule for the clipboard, and Zetl's windows already run as an
X11 client under Xwayland on every Wayland desktop. Compositors bridge the
clipboard between Wayland apps and Xwayland. So the order is:

1. **Full:** `ext-data-control-v1` (KDE, wlroots), as now.
2. **Lite:** an X11 selection backend (XFixes change notification,
   `XConvertSelection` reads, owning `CLIPBOARD` for writes) for X11 sessions,
   GNOME, and any desktop with Xwayland. X11 offers every target the source
   provides, and as owner Zetl sees each request, so fidelity and staged-paste
   confirmation may survive; where the bridge drops formats, text and pictures
   are the floor.
3. **GNOME extension:** only if Lite falls short on GNOME (popup focus,
   capture origin).

Gate: confirm GNOME bridges a copy from a native Wayland app to Xwayland
immediately, with no X11 window focused (`xclip -selection clipboard -o` and
`-t TARGETS -o` after copying in GNOME Text Editor; then the reverse with
`echo hello | xclip -selection clipboard`). KWin once synced only when an X11
window gained focus.

## First tier: the interaction works

Lukas's call (October 8, 2026): on GNOME the core interaction must work even
if fidelity and metadata are lower. The first tier is:

- **Must work:** held Ctrl+C capture of text and pictures, quick notes, Replay
  and Compose pasting text, and popups that take focus.
- **May degrade:** exact multi-format backup and restore (restore the text
  only), rich formats such as HTML and Calc's native bundle, read-confirmed
  staged pastes (fall back to a timed paste), and capture origin.

Capture origin may not be lost: the extension can report the focused window's
app ID and title (`global.display.get_focus_window()`), which Zetl cannot learn
on GNOME Wayland any other way. Popup focus may also need the extension,
since Mutter may refuse Zetl's X11 activation request; Clipman activates its
popup from the extension (`MetaWindow.activate`).

## Prior art: Clipman

[Clipman](https://github.com/MohammedEl-sayedAhmed/clipman) by Mohammed
El-sayed Ahmed (Apache License 2.0) is a GNOME clipboard manager built the same
way: its extension (`extension/extension.js`, GNOME Shell 45 to 51) watches
`Meta.Selection` and forwards clips to its daemon over D-Bus. Useful findings:

- `global.display.get_selection()` and its `owner-changed` signal report every
  new clipboard owner with no polling; filter on
  `Meta.SelectionType.SELECTION_CLIPBOARD`.
- `source.get_mimetypes()` lists what the owner offers, and
  `source.read_async(mime, cancellable)` returns a stream. Clipman waits 150 ms
  after the owner change before reading, reads in 64 KiB chunks with a size cap
  and a 5 s timeout (an owner that never sends would hold the pipe), closes the
  stream immediately, and drops one trailing NUL that some X11 apps send.
- Text types in order of preference: `text/plain;charset=utf-8`,
  `UTF8_STRING`, `text/plain`, `STRING`.
- Caller authentication: the extension answers only the D-Bus connection that
  owns the daemon's well-known name (tracked with `Gio.bus_watch_name`), and
  learns its PID with `GetConnectionUnixProcessID`. Zetl's extension needs at
  least this, since it would otherwise hand the clipboard to any session process.
- `Clutter` virtual keyboards can type into the focused window; Zetl does not
  need this, because it already pastes through its uinput keyboard.
- Before GNOME 49, hiding a window from Alt+Tab needs patches to the window
  lists; GNOME 49 adds `Meta.Window.hide_from_window_list()`.

Clipman writes the clipboard with `wl-copy`. Zetl needs more: exact backup and
restore of every MIME type, and staged pastes that report which read pasted.
Both point to Zetl's extension owning the clipboard with its own
`Meta.SelectionSource` subclass. It would offer every stored type and see each
read request, which is how a staged paste knows it was pasted. This is
unverified, and is the first thing to prototype.

If Zetl adapts Clipman code, Apache 2.0 requires keeping its copyright and
NOTICE, carrying the license, and marking the files we changed.

## Open questions

- Install and update of the extension from Zetl's bundle and AppImage
  (`gnome-extensions install`), and what Zetl does while it is disabled.
- Which GNOME Shell versions to support (Ubuntu and Solus testing will set
  the floor).
- Whether the keyboard side needs anything on GNOME (it should not: it works
  below the compositor).
