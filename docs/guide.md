# Zetl User Guide

Everything Zetl and Kastn can do, in detail. For a quick introduction, start
with the [README](../README.md).

- [The basics](#the-basics)
- [Coldkeys: tap and hold](#coldkeys-tap-and-hold)
- [Projects, buckets, and slips](#projects-buckets-and-slips)
- [Capturing](#capturing)
- [Replay and Pop](#replay-and-pop)
- [Compile](#compile)
- [Templates and creation types](#templates-and-creation-types)
- [The Board](#the-board)
- [Kastn](#kastn)
- [Undo](#undo)
- [Settings and themes](#settings-and-themes)
- [Storage and privacy](#storage-and-privacy)

## The Basics

Zetl turns familiar keyboard shortcuts into two gestures:

- **Tap** the shortcut and the application in front of you behaves normally.
- **Hold** it (about a third of a second) and Zetl opens a capture, project,
  Replay, or compile action.

While you hold, a small **hold indicator** ring fills and completes the moment
the hold fires. It only appears once a press has lasted longer than a tap, so
ordinary copies and pastes never show it, and it can't take focus from the app
you're in. Keys whose hold does nothing never show it.

The system has three parts:

- **Chordl** is the tap/hold keyboard layer underneath.
- **Zetl** is the resident tray app for fast capture, replay, and output.
- **Kastn** is the companion workbench for browsing, organizing, editing, and
  publishing what you captured.

The guiding split: **capture and move on in Zetl; sit down and work in Kastn.**

Zetl lives in the Windows notification area. Its tray menu provides:

- `Open Board` and `Open Shift Board`
- `New Project`
- `How Zetl Works`
- `Notification History` and `Clear Notification History`
- `Toggle Active Bucket Pop Mode`
- `Settings`
- `Open Kastn`
- `Quit`

Only one Zetl runs per Windows sign-in; launching it again finds the running
one and exits.

> Windows keeps keyboard shortcuts and synthetic input from crossing between
> normal and elevated apps. If shortcuts don't work in an app running as
> administrator, run Zetl as administrator too. Windows' secure desktop (the
> sign-in and UAC screens) can't be reached at all.

## Coldkeys: Tap And Hold

Zetl's shortcuts are called **Coldkeys**: tap for the ordinary shortcut, hold
for the Zetl action.

| Coldkey | Tap | Hold |
| --- | --- | --- |
| `Ctrl+A` | Normal select-all | Select all and capture it |
| `Ctrl+B` | Normal `Ctrl+B` | Open the Board |
| `Ctrl+C` | Copy; captured too while a project is active | Review and save what you copied |
| `Ctrl+J` | Normal `Ctrl+J` | Toggle between the Journal and your last project |
| `Ctrl+P` | Normal `Ctrl+P` | Toggle Pop mode |
| `Ctrl+R` | Normal `Ctrl+R` | Toggle Replay mode |
| `Ctrl+T` | Normal `Ctrl+T` | Start a project from a template |
| `Ctrl+X` | Normal cut | Open a quick note |
| `Ctrl+V` | Normal paste, or the next Replay item | Open Compile |
| `Ctrl+Z` | Normal undo | Undo the latest Zetl action |

**Lanes.** Add `Shift` to use the independent Shift lane: `Ctrl+Shift+C`,
`Ctrl+Shift+X`, `Ctrl+Shift+V`, and so on. Each lane has its own active
project, so you can keep a Replay or data-entry project on one lane while the
other captures ordinary notes, or stays inactive. Lane names are configurable
in Settings.

**Changing what a hold does.** Settings → **Hold Actions** lists every tap and
hold rule with a menu of the actions it can run, including *Normal key
behavior*. A rule for file lists (File Explorer, the desktop, Open/Save
dialogs) wins there over the rule for anywhere. **Reset to defaults** puts
everything back.

**Hold indicator settings** are at the bottom of the Hold Actions page:

- **Show hold progress** (on by default).
- **Indicator position:** where popups open, at the mouse pointer, centered, or
  a corner. The pointer position is read once per press, so this works over a
  full-screen video too.
- **Demo overlay:** adds the key name, a millisecond timer that stops when the
  action fires, and the action's name, and shows taps as well. Made for
  recording demos.

The hold threshold defaults to `353 ms`; it and the repeat suppression delay
are in Settings → Chordl Settings. The keys Chordl watches are defined in
[`hotkeys.json`](../hotkeys.json).

## Projects, Buckets, And Slips

A **project** contains buckets. A **bucket** contains ordered **slips**, Zetl's
name for a single note.

A slip can hold text, a picture, or both, plus optional details:

- title and body text, or a caption
- source, such as `copy`, `cut`, `compile`, or `replay`
- when it was created and in which capture session
- which application and window it came from
- picture dimensions, content hash, and asset information

New projects start with `Capture` and `Quick Note` buckets (changeable in
Settings) plus a protected `Scratch` bucket.

### The Journal

Each lane has its own active project, or none. Held captures made with no
active project go to the rolling **Journal**, the always-present default home
(`Journal` and `Journal Shift`). The Journal sorts slips into day buckets named
for the weekday and date, such as `Mon 07-06`.

The whole period's days are created up front (Mon–Sun for a weekly Journal,
every day of the month for a monthly one), so you can drop a note into a
*future* day as a reminder that's waiting when the day arrives. Each day holds
a `Capture` child for copies and a `Quick Note` child for quick notes, created
the first time you use them that day. `DayStartHour` sets when a new day
begins, for late-night work.

Selecting a bucket of your own in the Journal (anything that isn't a day or a
day's `Capture` / `Quick Note`) keeps copies going there until you select a
day again.

### Activating a project

Filing to the Journal doesn't make it active. A project becomes active only
when you choose it:

- saving a capture dialog with **Activate** on (on by default for a held copy;
  on for a quick note only when its project is already active),
- activating it from the Board, or
- `Ctrl+J`.

Dismissing a dialog with `Esc` never changes the active project. The tray icon
is green while a project is active and grey when none is.

Finishing a project sets it aside and clears it from its lane. It isn't
deleted or locked and can be reactivated later.

**Deactivate a quiet project after** (hours, off by default) switches a project
off once it has gone that long without a capture, so a forgotten project never
traps your notes. Every capture, including tapped copies, counts as activity.

## Capturing

### Copy capture

Tap `Ctrl+C` to copy normally. While a project is active (and auto-capture is
on), what you copied is also added to the active bucket.

With no project active, **When no project is active** in Settings decides what
a tapped copy does:

- **Don't capture copies** (default): copy and paste behave exactly as without
  Zetl.
- **Capture copies to the Journal**: every copy is saved to today's Journal
  `Capture` bucket, without activating the Journal.

The grey tray icon's tooltip names the current choice.

**Private copies are never captured.** Password managers and some other apps
mark a copy as private using the same signals Windows' own clipboard history
respects (`ExcludeClipboardContentFromMonitorProcessing`, a zero
`CanIncludeInClipboardHistory`, or `Clipboard Viewer Ignore`). Zetl checks for
them before reading anything: a tapped copy is skipped silently, and holding
`Ctrl+C` on one shows a short notice instead of a capture.

Hold `Ctrl+C` to review and route the copy before saving it. If nothing new
was copied, the hold opens the Board instead.

Zetl captures:

- plain text
- rich text, keeping the HTML alongside a plain-text fallback
- pictures on the clipboard
- links that point straight at an image, which Zetl downloads and saves as the
  picture

Pictures are stored as PNG project assets; copying the same picture again
reuses the stored file.

Some apps put text and a picture on the clipboard together; copying
spreadsheet cells is the usual case. Zetl keeps both on one slip, which acts as
text so compile, Replay, and views see the table, with the picture alongside.
Kastn can switch which one the slip prefers.

For text, Zetl privately keeps the clipboard's HTML when there is any.
LibreOffice Calc copies also keep Calc's own cell formats, so Replay can paste
cells back with their formatting intact. Editing the slip text drops these
stored formats so Replay never pastes stale formatting. Clean exports leave
them out; archive exports keep them.

Downloading a copied image link contacts that link's server. Downloads time out
after 10 seconds, are limited to 25 MB, and fall back to an ordinary text slip
if the image can't be fetched.

### Quick notes

Hold `Ctrl+X` to open a quick note. It starts with the cut text when there is
any, and empty otherwise.

Filed to the Journal, a quick note goes to today's `Quick Note` child. In a
project, it goes to the bucket you last used for quick notes there, starting
with `Scratch`. `Alt+B` creates a bucket inline, and `Ctrl+Enter` saves.

Dismissing a quick note made from a cut pastes the cut text back, so nothing is
lost. Quick notes leave the clipboard alone by default; Settings can change
that.

### Capture origin

Zetl can record where a capture began:

```text
Microsoft Edge · Zetl roadmap — Google Docs · 10:42 AM
```

The levels are `Off`, `Application only`, and `Application and window title`.
Executable paths are never stored. Capture origin is private: it isn't part of
the slip's text, and clean exports remove it.

## Replay And Pop

### Replay mode

Replay pastes a bucket back in the order you captured it, for ordered data
entry:

1. Turn on Replay with a held `Ctrl+R`.
2. Copy the values into the bucket in the order you need them.
3. Tap `Ctrl+V` in the destination app, once per value.

For each paste, Zetl places the next slip on the clipboard, pastes it, moves
it to a review bucket, and then puts your own clipboard back, every format of
it (rich text, spreadsheet data, file lists, pictures). A few clipboards can't
be backed up, such as a file copied in File Explorer; Replay still pastes and
tells you the old clipboard can't be restored. It never blocks your paste. If
you copy something new while Replay is restoring, your new copy wins.

Pasting into a file list (File Explorer, the desktop, an Open/Save dialog)
always pastes your files normally; Replay and Pop stay out of it.

Text slips paste with the richest formatting the target accepts: Calc's own
formats, then HTML, then plain text. Picture slips paste as pictures.

If Windows refuses a paste, the slip stays in the queue. When the queue is
empty, the bucket goes back to normal. Fast taps are handled one at a time per
lane, so no slip is pasted twice, and the two lanes' queues run independently.
A Replay queue picks up where it left off after Zetl restarts.

### Pop mode

Pop is for throwaway copy and paste:

1. Turn on Pop with a held `Ctrl+P`.
2. Tap `Ctrl+V` normally.
3. If what you pasted matches the latest slip from this session in the active
   bucket, Zetl removes that slip.

Replay and Pop exclude each other; turning one on turns the other off.

## Compile

Hold `Ctrl+V` to open Compile. It starts on the active project, or, with none
active, on the project you last added to, skipping consumable projects (those
with a Replay bucket). The project list puts recent
projects first, and picking another changes neither lane.

Compile can:

- select whole buckets or individual slips
- use the whole project or just the current session
- output `Formatted`, `Plain`, or `TSV` (spreadsheet rows)
- copy the result, or paste it straight away
- paste the last copied item
- paste selected slips without headings
- save the result into a bucket in any project
- keep slips separate or flatten them into one compiled slip
- finish the source project

Where you save is independent of the source, and saving doesn't change the
active project or bucket.

Compile in Zetl is text-only and doesn't list picture slips. Kastn's text views
keep readable picture markers, and its Markdown, HTML, and PDF output can embed
the pictures.

## Templates And Creation Types

Zetl and Kastn share a template catalog in `%AppData%\Zetl\templates\`.
Templates define a project's buckets, behavior, compile defaults, TSV
settings, and optional starter cards.

- **Capture templates** create projects to collect into.
- **Consumable templates** create ordered Replay queues.

When Kastn starts a consumable template, its Temporary setting becomes a
per-use default: the new project can be kept, or deleted automatically when it
leaves its lane.

Hold `Ctrl+T` to open the template picker. Kastn creates, duplicates, and
deletes templates; Zetl uses the same catalog and can start a template in
either lane.

A **creation type** pairs a template with a default output view, so a project
starts with both an input structure and an intended result.

## The Board

The Board is Zetl's quick project workspace. From it you can:

- create, rename, activate, finish, and delete projects
- create, nest, rename, and delete buckets
- change the active bucket
- set Standard, Replay, Pop, compile, and TSV defaults
- create, edit, and delete slips
- view picture thumbnails, previews, and captions
- export a project
- open the selected project in Kastn

`Scratch` and `Deleted` are protected buckets. `Scratch` is the quick-note
fallback in projects; `Deleted` holds slips deleted in Kastn and stays out of
capture, compile, Replay, and Pop.

Board shortcuts:

- `Alt+A`: toggle whether the selected project is active
- `Enter`: save a bucket name
- `Ctrl+Enter`: save a changed slip and close the Board

A Board opened by shortcut hides when you click away; one opened from the tray
stays open.

## Kastn

Kastn is the workbench over the same projects. Zetl remains the only writer to
project storage; Kastn talks to it locally and checks revisions, so neither
silently overwrites the other's changes.

Kastn provides:

- project cards, lifecycle actions, and handoff back to Zetl
- live browsing while Zetl keeps capturing
- a bucket/slip tree with drag-and-drop organization
- **Board Mode**, a Kanban view with drag-and-drop and card editing
- **Linked Slips**: wiki-style links between slips (`Ctrl+Click` or `F12` to
  follow) with a backlinks index
- search, plus source, session, and date filters
- autosaving editing with clear conflict resolution
- optional slip titles, Markdown formatting, and font, size, and color
- picture previews, capture details, and attaching or removing a picture
- switching a text-and-picture slip's preferred side
- delete and restore
- templates, creation types, and per-project views
- Formatted, Plain, TSV, Markdown, HTML, and PDF output

Views are always generated from the current slips; an exported document never
becomes a second copy you have to keep in sync.

More: [product direction](kastn.md), [workbench behavior](kastn-workbench.md),
and the co-editing [contracts](kastn-contracts.md) and [IPC](kastn-ipc.md).

## Undo

Hold `Ctrl+Z` for the main lane or `Ctrl+Shift+Z` for the Shift lane. The undo
history keeps the latest 100 Zetl actions for the session and covers:

- copy capture
- held-copy and quick-note saves
- compile-to-bucket saves
- Pop removal
- Replay pastes and their review copies

Project and bucket management isn't part of undo yet.

## Settings And Themes

Settings live in `%AppData%\Zetl\settings.json` and include:

- capture origin detail and toast duration
- automatic copy capture, and what copies do with no project active
- quick-note clipboard behavior
- popup position (top center by default, centered, a corner, or at the mouse
  pointer); with several monitors, popups open on the screen you're working on
- default project buckets, compile mode, and TSV row length
- Journal interval, day start hour, and quiet-project deactivation
- Hold Actions: what each tap and hold does, and the hold indicator
- Chordl hold and repeat timings
- Kastn autosave, startup, close behavior, reading view, and template handoff

**Popup opacity** (60-100%) is on the Theme page. It's a single setting that
applies in every theme rather than part of any theme file.

The theme editor controls the shared Zetl and Kastn colors, typography,
spacing, padding, and corner radius. Themes have light and dark palettes and
can be duplicated, reset, imported, and exported. Custom themes live in
`%AppData%\Zetl\themes`; an invalid or missing theme falls back to a built-in
one.

## Storage And Privacy

Everything is stored locally as readable JSON:

```text
%AppData%\Zetl\
  workspace.json
  settings.json
  kastn-state.json
  kastn-draft.json       # only while an unsaved Kastn draft needs recovery
  projects\
    Project-name-3f2a91\
      project.json
      assets\
  templates\
  creation-types\
  themes\
  views\
```

`workspace.json` remembers each lane's active project, Journal, and last
project, so routing survives a restart. Each project has its own folder, and
saving a slip rewrites only that project. If a write fails, Zetl keeps the last
saved state rather than carrying the failed change forward.

Older installs with a single `state.json` are migrated on first launch, keeping
`state.json.bak`.

### Project export

The Board exports a project as a self-contained `.zetl.zip` (`manifest.json`,
`project.json`, and `assets/`), in one of two modes:

- **Clean copy for sharing** removes application, window-title, and
  downloaded-image link details.
- **Archive copy** keeps where everything came from, for private backup or
  moving machines.

Export works from a snapshot and never changes the live project. Importing a
project isn't available yet.

### Activity log

Every notification is also kept in a protected `Zetl Logs` project, grouped by
day. It's never made active, and old entries are trimmed so it can't grow
without limit. Notification History in the tray shows recent messages.
