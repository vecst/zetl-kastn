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
