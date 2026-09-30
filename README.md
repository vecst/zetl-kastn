# Zetl

Zetl is a local-first Windows capture and replay tool. It turns familiar
keyboard shortcuts into two gestures:

- **Tap** the shortcut and the foreground application behaves normally.
- **Hold** the shortcut and Zetl opens a capture, project, Replay, or compile
  action.

Three pieces make up the system:

- **Chordl** is the portable tap/hold keyboard interaction layer.
- **Zetl** is the resident tray app for fast capture, replay, and output.
- **Kastn** is the companion workbench for browsing, organizing, editing, and
  publishing captured material.

The guiding split is: **capture and move on in Zetl; sit down and work in
Kastn**.

Zetl is local-only. Project content, settings, templates, views, and themes are
stored under `%AppData%\Zetl`.

## Running

Run the Avalonia tray application with the .NET SDK:

```powershell
dotnet run --project Zetl.App
```

Publish the self-contained Windows bundle with:

```powershell
.\scripts\publish-win-x64.ps1
```

The script stages both Zetl and Kastn, verifies that both executables identify
the current clean Git commit, and then replaces the previous bundle. The
published apps are `artifacts\publish\win-x64\Zetl.exe` and `Kastn.exe`.
Use `-RunArtifactChecks` for the RC publish to run the live Windows clipboard
self-tests and disposable persistence scenario before the bundle is installed.

Zetl lives in the Windows notification area. Its tray menu provides:

- `Open Board`
- `Open Shift Board`
- `Open Kastn`
- `New Project`
- `How Zetl Works`
- `Notification History`
- `Clear Notification History`
- `Toggle Active Bucket Pop Mode`
- `Settings`
- `Quit`

Only one Zetl process runs per Windows login session. A second launch detects
the resident instance and exits.

> Windows blocks keyboard hooks and synthetic input across integrity levels.
> If shortcuts do not work in an elevated application, run Zetl elevated too.
> Windows secure-desktop input cannot be intercepted.

## Coldkeys

Chordl shortcuts are called **Coldkeys**: tap for the ordinary shortcut, hold
for the Zetl action.

| Coldkey | Tap | Hold |
| --- | --- | --- |
| `Ctrl+A` | Normal select-all | Select all and capture it |
| `Ctrl+B` | Normal `Ctrl+B` | Open the Board |
| `Ctrl+C` | Copy; optionally capture into the active bucket | Open capture or project management |
| `Ctrl+J` | Normal `Ctrl+J` | Toggle between Journal and last project |
| `Ctrl+P` | Normal `Ctrl+P` | Toggle Pop Mode |
| `Ctrl+R` | Normal `Ctrl+R` | Toggle Replay Mode |
| `Ctrl+T` | Normal `Ctrl+T` | Start a project from a template |
| `Ctrl+X` | Normal cut | Open a quick note |
| `Ctrl+V` | Normal paste, or paste the next Replay item | Open Compile |
| `Ctrl+Z` | Normal undo | Undo the latest Zetl action |

Add `Shift` to use the independent Shift project lane:
`Ctrl+Shift+A`, `Ctrl+Shift+C`, `Ctrl+Shift+J`, `Ctrl+Shift+X`, `Ctrl+Shift+V`, and so on. This makes it
possible to keep a Replay/data-entry project on one lane while using the other
lane for ordinary capture—or leaving it inactive.

The default hold threshold is `353 ms`. Timing and low-level dispatch behavior
are configured in [`hotkeys.json`](hotkeys.json).

## Projects, Buckets, And Slips

A **project** contains buckets. A **bucket** contains ordered slips.

A slip may contain text, a picture, or both, plus optional metadata:

- title and body text or caption
- source such as `copy`, `cut`, `compile`, or `replay`
- creation time and capture session
- originating application and window title
- picture dimensions, content hash, and asset information

The normal and Shift lanes each have their own active project, or none. Held
captures with no active project file to the rolling Journal, the always-present
default capture home (`Journal` and `Journal Shift`). The Journal organizes
captured slips into day buckets named for the weekday and date (e.g.
`Mon 07-06`).

Filing to the Journal does not make it active. A project becomes active only
when you choose it: saving a capture dialog with **Activate** on (on by default
for a held copy; on for a quick note only when its project is already active),
activating it from the Board, or `Ctrl+J`. Dismissing a dialog with `Esc` never
changes the active project. The tray icon is green while a project is active and
grey when none is.

The whole period's day buckets are seeded up front — the full Mon–Sun week for a
weekly Journal, or every day of the month for a monthly one — so a note can be
dropped into a *future* day as a lightweight reminder that is waiting there when
that day arrives. Each day is a small nested structure: copy captures land in a
`Capture` child and quick notes in a `Quick Note` child, both created lazily the
first time that gesture fires on the day. The day boundary is the `DayStartHour`
setting.

Selecting a bucket of your own in the Journal (one that isn't a day or a day's
`Capture` / `Quick Note`) keeps copies going there until you select a day
bucket again.

Finishing a deliberate project sets it aside and clears it from its lane.
Setting a project aside does not delete or lock it; it can be reactivated later.

**Deactivate a quiet project after** (hours, off by default) switches a
deliberate project off once it has gone that long without a capture, so a
forgotten project never traps captures. Every capture, including tapped copies,
counts as activity.

## Capture

### Copy Capture

Tap `Ctrl+C` to copy normally. When auto-capture is enabled and a project is
active, changed clipboard content is added to the active bucket.

With no project active, the **When no project is active** setting decides what a
tapped copy does:

- **Don't capture copies** (default): copy and paste behave exactly as they
  would without Zetl.
- **Capture copies to the Journal**: every copy is captured into today's
  Journal `Capture` bucket, all day, without activating the Journal.

The grey tray icon's tooltip names the current choice.

Zetl captures:

- Unicode text
- HTML-rich text with a Unicode fallback
- clipboard pictures
- direct HTTP(S) image URLs that resolve to an image

Pictures are normalized to PNG and stored as content-addressed project assets.
Copying the same picture again reuses the existing asset file.

Some applications put text and a picture on the clipboard together — copying
spreadsheet cells is the common case. Zetl captures both on one slip, which
presents as text so compile, Replay, and views see the table, while the picture
rides along. Kastn can flip the slip's preferred representation later.

For text captures, Zetl privately retains the clipboard's HTML fragment when
one is available. LibreOffice Calc captures additionally keep its allowlisted,
self-contained native source formats so Replay can reproduce cell formatting
without HTML import defaults such as added wrapping or alignment. Editing the
slip text clears these source representations so Replay never pastes stale rich
content. Clean/shareable project exports omit them; archival exports retain
them.

Resolving a copied image URL contacts that URL's server. Downloads time out
after 10 seconds, are limited to 25 MB, and fall back to an ordinary text slip
when the response is unavailable or is not a usable image.

Hold `Ctrl+C` to review and route the copied text or picture before saving. If
there is no copied content, the held gesture opens project management instead.

### Quick Notes

Hold `Ctrl+X` to open a quick note. The dialog uses cut text when available and
otherwise starts empty.

When the note is filed to the Journal, it goes to today's `Quick Note` child,
while copy captures go to today's `Capture` child.

When a deliberate project is active, quick notes default to its protected
`Scratch` bucket (unless another bucket is selected), and the dialog remembers
the last quick-note bucket used for that project. `Alt+B` focuses inline
bucket creation, and `Ctrl+Enter` saves.

Quick notes leave the clipboard unchanged by default. This can be changed in
Settings.

### Capture Origin

Zetl can record where a capture began:

```text
Microsoft Edge · Zetl roadmap — Google Docs · 10:42 AM
```

The available levels are:

- `Off`
- `Application only`
- `Application and window title`

Executable paths are never stored. Capture origin is private metadata rather
than part of the authored slip text, and clean exports remove it.

## Replay And Pop

### Replay Mode

Replay pastes a bucket back in capture order. It is designed for ordered data
entry. Durable Replay queues resume with their remaining items after Zetl
restarts:

1. Activate Replay Mode with held `Ctrl+R`.
2. Copy values into the bucket in the order needed.
3. Tap `Ctrl+V` repeatedly in the destination application.

For each paste, Zetl temporarily places the next slip on the clipboard, sends
the paste, archives the consumed slip into a review bucket, and restores the
user's previous clipboard. On Windows, restoration preserves the complete set
of clipboard formats, including rich HTML/RTF, spreadsheet payloads, file lists,
images, and their fallback representations. Some clipboards cannot be backed
up — a file copied in File Explorer, for example, carries its contents as a
stream Windows will not hand over. Replay still pastes in that case and says
the previous clipboard can't be restored; it never blocks paste. A clipboard
changed by the user during Replay's restore delay is never overwritten. Captured text slips prefer a stored native rich representation
when supported, then HTML, with plain text available for applications that do
not accept either. Text and picture slips are both supported.

If Windows rejects the synthetic paste, the slip stays in the queue. When the
queue becomes empty, the bucket returns to Standard mode. Rapid taps are
serialized within each lane so one slip cannot be pasted twice, while Main and
Alternate Replay queues can continue independently.

### Pop Mode

Pop Mode is for disposable copy/paste work:

1. Activate it with held `Ctrl+P`.
2. Tap `Ctrl+V` normally.
3. If the pasted clipboard content matches the latest current-session slip in
   the active bucket, Zetl removes that slip.

Replay and Pop are mutually exclusive. Switching one on switches the other off.

## Compile

Hold `Ctrl+V` to open Compile. The source project defaults to the active project
but can be changed without changing either active lane.

Compile supports:

- selecting whole buckets or individual slips
- all-project or current-session scope
- `Formatted`, `Plain`, and `TSV` output
- copying or immediately pasting the result
- pasting the last copied item
- pasting selected slips without headings
- saving into a bucket in any project
- preserving slip structure or flattening into one compiled slip
- finishing the source project

The destination project and bucket are independent from the source. Saving a
compile does not change the active project or active bucket.

Zetl Compile remains text-only and does not list picture slips. Kastn's text
views retain readable picture-caption markers, while its Markdown, HTML, and
PDF output can embed the pictures.

## Templates And Creation Types

Zetl and Kastn share a template catalog under:

```text
%AppData%\Zetl\templates\
```

Templates define project buckets, behavior, compile defaults, TSV settings, and
optional starter cards.

- **Capture templates** create projects to collect material into.
- **Consumable templates** create ordered Replay queues.

When Kastn starts a consumable template, its saved Temporary setting becomes a
per-use default: the new project can be kept normally or deleted automatically
when it leaves the selected lane.

Hold `Ctrl+T` to open the template picker. Held `Ctrl+V` also opens it,
defaulting to Consumable templates, when there is no active project or compile
content.

Kastn authors, duplicates, and deletes templates. Zetl consumes the same catalog
and can start a template in either project lane.

A **creation type** pairs a template with a default output view, allowing a
project to begin with both an input structure and an intended artifact format.

## The Zetl Board

The Board is Zetl's quick project workspace. It supports:

- creating, renaming, activating, finishing, and deleting projects
- creating, nesting, renaming, and deleting buckets
- changing the active bucket
- configuring Standard, Replay, Pop, compile, and TSV defaults
- creating, editing, and deleting slips
- viewing picture thumbnails, previews, and captions
- exporting a project
- opening the selected project in Kastn

`Scratch` and `Deleted` are protected buckets. `Scratch` is the quick-note
fallback inside deliberate projects; `Deleted` holds slips soft-deleted through
Kastn and stays out of normal capture, compile, Replay, and Pop flows.

Useful Board shortcuts:

- `Alt+A`: toggle whether the selected project is active
- `Enter`: save a bucket name
- `Ctrl+Enter`: save a changed slip and close the Board

Shortcut-opened Boards hide when focus moves away. Boards opened from the tray
remain open.

## Kastn

Kastn is the deliberate workbench over the same projects. Zetl remains the
sole writer to project storage; Kastn communicates with it through local,
revision-checked IPC.

Kastn provides:

- project cards, lifecycle actions, and direct Zetl handoff
- live browsing while Zetl continues capturing
- a bucket/slip tree with drag-and-drop organization
- alternate **Board Mode** (Kanban board projection) with drag-and-drop, horizontal auto-scrolling, and double-click modal card editing
- wiki-style **Linked Slips** navigation (Ctrl+Click/F12 to follow double-bracket stable-ID links) and backlink details index
- search and source, session, and date filters
- autosaving slip editing with explicit conflict resolution
- optional slip titles, Markdown formatting, and whole-slip font, size, and color
- picture previews, capture details, and attaching or removing a slip's picture
- flipping a dual (text + picture) slip's preferred representation
- soft-delete and restore
- templates, creation types, and project-specific views
- Formatted, Plain, TSV, Markdown, HTML, and PDF output

Views are projections over current slips; generated output never becomes a
second authoritative document.

See:

- [`docs/kastn.md`](docs/kastn.md) for product direction
- [`docs/kastn-workbench.md`](docs/kastn-workbench.md) for current workbench
  behavior
- [`docs/kastn-contracts.md`](docs/kastn-contracts.md) and
  [`docs/kastn-ipc.md`](docs/kastn-ipc.md) for the co-editing contract

## Undo

Hold `Ctrl+Z` for the normal lane or `Ctrl+Shift+Z` for the Shift lane.

The session-only undo stack keeps the latest 100 Zetl actions and currently
covers:

- copy capture
- held-copy and quick-note saves
- compile-to-bucket saves
- Pop removal
- Replay consumption and review-copy restoration

Project and bucket management changes are not currently part of undo.

## Storage And Privacy

The live store is human-readable JSON:

```text
%AppData%\Zetl\
  workspace.json
  settings.json
  kastn-state.json
  kastn-draft.json       # present only while an unsaved Kastn draft needs recovery
  projects\
    Project-name-3f2a91\
      project.json
      assets\
  templates\
  creation-types\
  themes\
  views\
```

`workspace.json` contains both lanes' active project, default Journal, and last
deliberate project pointers so capture routing survives a restart. Each project
owns its own folder and `project.json`; saving a slip rewrites only that project.
If a project or workspace write fails, live state returns to its last durable
snapshot instead of carrying the failed mutation into a later save.

Older installations with a single `state.json` are migrated automatically on
first launch and retain `state.json.bak`.

### Project Export

The Board exports a project as a self-contained `.zetl.zip`:

```text
manifest.json
project.json
assets/...
```

Two privacy modes are available:

- **Clean copy for sharing** removes application, process, window-title, and
  downloaded-image URL metadata.
- **Archive copy** retains capture provenance for private backup or transfer.

Export works from a detached snapshot and never modifies the live project.
Project import is not yet implemented.

### Activity Log

Every toast is also buffered into a protected `Zetl Logs` project, grouped by
day. The project is never made active, and bounded retention prevents the log
from growing without limit.

Notification History provides a lighter recent-message view from the tray.

## Settings And Themes

Settings are stored in `%AppData%\Zetl\settings.json`. They include:

- capture-origin detail
- toast duration
- automatic copy capture
- quick-note clipboard behavior
- default project buckets
- default compile mode and TSV row length
- Kastn autosave, startup, close behavior, reading-view, and template handoff
  preferences

The Avalonia theme editor controls shared Zetl and Kastn colors, typography,
spacing, padding, and corner radius. Themes have light and dark palettes and
can be duplicated, reset, imported, and exported.

Custom theme files live under `%AppData%\Zetl\themes`. Invalid or missing themes
fall back to a built-in preset.

## Development

Build the full solution:

```powershell
dotnet build Zetl.slnx
```

Projects share one output directory:

```text
artifacts\bin\<Configuration>\
  Zetl.exe
  Kastn.exe
  Zetl.Tests.exe
```

Run the portable and Windows-specific test gates:

```powershell
dotnet test Zetl.Tests\Zetl.Tests.csproj --no-build
dotnet .\artifacts\bin\Debug\Zetl.dll --self-test
```

The manual Windows smoke checklist is
[`docs/windows-parity-checklist.md`](docs/windows-parity-checklist.md).

Development previews use disposable state:

```powershell
dotnet run --project Zetl.App -- --preview=board
dotnet run --project Zetl.App -- --preview=toast
dotnet run --project Zetl.App -- --preview=export
dotnet run --project Zetl.App -- --preview=note-image
```

Shortcut automation can accept injected keyboard events only when a disposable
data directory is supplied:

```powershell
dotnet run --project Zetl.App -- --data-dir=C:\tmp\zetl-hotkey-smoke --allow-injected-input-for-testing
```

Do not use that flag for normal profiles; it exists so release smoke scripts can
exercise the Windows hook with synthetic input. Zetl's own replayed pass-through
keys remain filtered to avoid recursive shortcut handling.

Repository layout:

- `Chordl/`: portable tap/hold engine
- `Zetl.Core/`: state, persistence, settings, templates, views, and themes
- `Zetl.Contracts/`: versioned Kastn/Zetl wire contracts
- `Zetl.Runtime/`: portable shortcut and workflow orchestration
- `Zetl.App/`: canonical Avalonia tray application
- `Kastn.App/`: deliberate Avalonia workbench
- `Zetl.Tests/`: portable behavior and storage tests
- `Zetl.Linux.Spike/`: evdev/uinput safety work

Linux architecture and remaining platform work are documented in
[`docs/linux-roadmap.md`](docs/linux-roadmap.md).

The complete documentation map is [`docs/README.md`](docs/README.md).

## License

Zetl is free software licensed under the
[GNU General Public License version 3](LICENSE), version 3 or any later version
(`GPL-3.0-or-later`).
