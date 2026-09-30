# Hold Routing and File References — Discussion

> Status: discussion only. Nothing here is built except the file-view paste
> pass-through that prompted it (557dd98). Decisions made so far and those
> still open are collected at the end.

## How this came up

On 2026-09-29 a held `Ctrl+X` in File Explorer let the physical cut through
before the quick note opened, so Explorer cut the selected *file*. The file
object on the clipboard carries a `FileContents` stream Windows will not hand
over, Zetl could not back the clipboard up, and every Zetl clipboard write and
every Replay paste refused until Replay was switched off. That lockup is fixed
(clipboard writes and Replay now fail open, 59e92d8), and a `Ctrl+V` into any
file list now bypasses Replay and Pop (557dd98).

The incident exposed a bigger idea: a held gesture on a *file* can mean
something useful — a note that references the file, or a file operation like
cut-and-rename. That raises the structural question of how several "programs"
can share the hold syntax and each know what to act on.

## Principles

- **One hook owner.** Chordl, inside Zetl, is the only thing that listens for
  taps and holds. Other programs never install their own keyboard hook; two
  hooks racing for the same `Ctrl+X` is how keyboard state gets corrupted.
- **Taps stay native and instant.** Routing happens only after a hold is
  detected (~350 ms in). The tap path gets nothing heavier than cheap window
  checks like the file-view test.
- **Fail open.** If a handler cannot decide, errors, or does not answer in
  time, Zetl falls back to its default behavior, never to a swallowed key.
- **Capture stays decision-free.** Routing chooses the handler from context;
  the user never picks one mid-gesture.

## Context snapshot

Zetl already records the target window and process at keydown (the
`keydown target` log lines). Extend that into one snapshot per gesture:

| Field | Source | Cost |
| --- | --- | --- |
| Target window and process | `GetForegroundWindow` at keydown (exists today) | trivial |
| Focus kind: file view, text field, other | focus chain; file view = inside `SHELLDLL_DefView` (exists today) | trivial |
| Target application | process image name, resolved off the hook thread | small, hold path only |
| Clipboard kinds after the pass-through | text, picture, file list (`CF_HDROP`), cut vs copy (`Preferred DropEffect`) | small, hold path only |
| Shell folder of the target window | `IShellWindows`, matched by window handle | COM call, hold or post-paste only |

## Handler model

A handler claims a gesture under a condition:

- "held `Ctrl+X` when focus is a file view"
- "held `Ctrl+C` when the target app is Excel"
- "held `Ctrl+V` when the clipboard holds a pending file cut"

Resolution picks the most specific matching claim, and Zetl's current
behavior is the final fallback. In code this replaces the fixed `switch` in
`ZetlShortcutCoordinator.HandleClaimedHoldAsync` with a small ordered table of
`(key, predicate, handler)`. Step one changes no behavior at all: the table
holds exactly today's handlers.

**External handlers.** A separate program (a file tool, say) registers its
claims over the named-pipe IPC Kastn already uses. At hold time Zetl sends the
snapshot and waits a bounded time for the handler to accept; no answer means
the default runs. The program never sees raw keystrokes, only resolved
gestures.

**Who writes the rules.** Open question: built-in claims first, then
user-editable rules in Settings ("in file views, held `Ctrl+X` goes to…").

## File references

### Getting the file for free

When Explorer cuts or copies a file it puts the full paths on the clipboard
as `CF_HDROP` (readable; only `FileContents` is not) plus `Preferred
DropEffect`, which says cut or copy. Because the physical press passes through
before a hold is known, "select a file, hold the key" hands Zetl the file list
with no extra UI.

### Gestures

Both keys can take a file, with different weight. Selecting several files
makes **one** slip listing all of them, with one note, never a prompt per
file:

- **Held `Ctrl+C` on a file** → a *reference slip*: the captured thing is the
  file. Copy never marks the file for moving, so nothing else changes.
- **Held `Ctrl+X` on a file** → a *quick note seeded with the reference*,
  plus movement tracking. The cut already marks the file for moving, so the
  note can record where it started and, if the user pastes it elsewhere, where
  it landed.

### Recording where a cut file lands

The paste goes straight through now (file-view pass-through), so Zetl observes
rather than intervenes:

1. At the held `Ctrl+X`, record the source path and the file's identity (below).
2. When a later tapped `Ctrl+V` lands in a file view, read the destination
   folder of that window (`IShellWindows`) after the pass-through, off the hook
   thread.
3. Confirm by identity: resolve the recorded file ID. A same-volume move keeps
   the ID, so the new path comes back directly. A cross-volume move is a copy
   plus delete (new ID), so fall back to destination folder + file name, then
   size and modified time.
4. Append `{from, to, when}` to the slip's move history.

If the user saves the note and never pastes, the cut simply expires; the note
still holds the original reference.

**Dismissing the note undoes the cut**, mirroring text: a discarded held text
cut pastes the text back, so a discarded held file cut cancels the pending
move. Explorer's cut removes nothing until the paste, so undoing it means
replacing the clipboard with the same file list marked as a *copy*
(`Preferred DropEffect` = copy). Explorer then stops showing the files as cut,
and the files stay on the clipboard the same way cut text does after a
paste-back. Guard it like text paste-back: only when the clipboard still holds
exactly that cut (unchanged change token), so a newer copy is never clobbered.
To confirm during implementation: that Explorer clears the greyed-out state on
that clipboard change.

### Why a symbolic link won't track moves

A symbolic link stores a path. When the target moves, the link dangles; it
does not follow. Creating one also needs admin rights or Developer Mode on
Windows. The options that do survive moves:

| Mechanism | Survives rename/move | Limits |
| --- | --- | --- |
| Symbolic link | No, it dangles | needs elevation or Developer Mode |
| Hard link | Yes, it is the same file under two names | same volume only; files only; editors that save by write-new-and-replace leave the link on the old content |
| NTFS file ID (`GetFileInformationByHandle`, `OpenFileById`) | Yes, within one volume | lost on cross-volume moves and on replace-style saves |
| Shell shortcut (`.lnk`) resolve | Yes, often across volumes | uses Windows' Distributed Link Tracking service, which Explorer shortcuts already rely on; NTFS only, best effort |
| Content hash | Finds copies anywhere | needs a search to find the file again; changes whenever the file is edited |

**Proposal:** store a layered identity and resolve in order —

1. the recorded path (fast path);
2. the NTFS file ID on the recorded volume (same-volume moves and renames);
3. a serialized shell link, resolved without UI (Windows' own move tracking);
4. name + size + modified time near the last known folder (last resort);

— and mark the reference **stale** in Kastn when none of these finds it,
instead of guessing. A stale file shows an **Update link** button that opens a
file picker; choosing the file re-captures its identity and records the relink
in the move history. Stale and relink are per file, so one missing file in a
multi-file slip does not mark the others.

### Link or copy

A reference points at the user's file; it does not copy it into the project
the way pictures are stored. Copying would survive anything but duplicates
data and silently diverges from the real file. Open question: an explicit
"snapshot this file into the project" action in Kastn for when a copy is
wanted.

### Slip shape (sketch)

A file reference joins text, picture, and dual slips as a representation:

```
Files[]             one entry per selected file, in selection order
  Path              last resolved full path
  Name              file name at capture
  Size, ModifiedUtc at capture
  VolumeSerial      volume the file ID belongs to
  FileId            NTFS 128-bit file ID
  ShellLink         serialized shell link (optional)
  Moves[]           { From, To, AtUtc, Kind: moved | relinked }
  Stale             true when resolution failed
```

Compile outputs the path by default (so a TSV row can reference files).
Replay pastes the path as text into text targets; in a file view it could
paste the file itself (a later handler).

## The file tool (later)

The other idea — cut a file, paste it with a new name — is a different job:
a file-manager action, not note-taking. It becomes the first handler that is
not Zetl's default:

- claim: held `Ctrl+V` in a file view while the clipboard holds a pending cut;
- behavior: a small dialog prefilled with the file name; on confirm, move the
  file to the window's folder under the new name, then record the move on any
  reference slip that tracks that file.

Whether it lives inside Zetl or as a separate program registered over IPC is
open. It is a good test of the external-handler path either way.

## Decisions

Made 2026-09-30:

- **Both keys take files.** Held `Ctrl+C` makes a reference slip; held `Ctrl+X`
  makes a note seeded with the reference and tracks where the cut files land.
- **Dismissing a file note undoes the cut**, like a discarded text cut
  (re-mark the files as a copy; guarded by the change token).
- **Several files make one slip** listing them all, with one note.
- **Stale references get an Update link button** (file picker, per file);
  layered identity resolves first, stale is shown instead of guessing.

## Open decisions

1. Identity layers: all four from the start, or path + file ID first and the
   shell link later.
2. Reference-only, or also an explicit snapshot-into-project action.
3. Routing rules: built-in only at first, or user-editable in Settings.
4. File tool: part of Zetl, or a separate program over IPC.

## Suggested order

1. Context snapshot plus handler table with today's behavior (no visible
   change).
2. File reference slip: capture from `CF_HDROP` (one slip per selection), path
   + file ID identity, Kastn display with per-file stale marking and Update
   link.
3. Held `Ctrl+X` on files: note seeded with the reference, cut undone on
   dismiss, movement tracking (destination folder + file ID resolve).
4. The file tool as the first non-default handler, then external handlers
   over IPC.
