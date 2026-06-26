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

Publish the self-contained Windows artifact with:

```powershell
dotnet publish Zetl.App\Zetl.App.csproj -p:PublishProfile=win-x64
```

The published app is `artifacts\publish\win-x64\Zetl.exe`.

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
| `Ctrl+J` | Toggle between Journal and last project | (None) |
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

A slip may contain text or a picture, plus optional metadata:

- title and body text or caption
- source such as `copy`, `cut`, `compile`, or `replay`
- creation time and capture session
- originating application and window title
- picture dimensions, content hash, and asset information

The normal and Shift lanes each have their own active project. When no deliberate
project has been started or activated, Zetl falls back to the rolling Journal
as the always-present default capture home (`Journal` and `Journal Shift`). The
Journal organizes captured slips into dated day buckets (e.g. `2026-06-01`).

Finishing a deliberate project sets it aside, clears it from its lane, and
automatically returns the lane back to the rolling Journal. Setting a project
aside does not delete or lock it; it can be reactivated later.

## Capture

### Copy Capture

Tap `Ctrl+C` to copy normally. When auto-capture is enabled and a project is
active, changed clipboard content is added to the active bucket.

Zetl captures:

- Unicode text
- clipboard pictures
- direct HTTP(S) image URLs that resolve to an image

Pictures are normalized to PNG and stored as content-addressed project assets.
Copying the same picture again reuses the existing asset file.

Resolving a copied image URL contacts that URL's server. Downloads time out
after 10 seconds, are limited to 25 MB, and fall back to an ordinary text slip
when the response is unavailable or is not a usable image.

Hold `Ctrl+C` to review and route the copied text or picture before saving. If
there is no copied content, the held gesture opens project management instead.

### Quick Notes

Hold `Ctrl+X` to open a quick note. The dialog uses cut text when available and
otherwise starts empty.

When the rolling Journal is active, the note goes to today's dated day bucket.

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
entry:

1. Activate Replay Mode with held `Ctrl+R`.
2. Copy values into the bucket in the order needed.
3. Tap `Ctrl+V` repeatedly in the destination application.

For each paste, Zetl temporarily places the next slip on the clipboard, sends
the paste, archives the consumed slip into a review bucket, and restores the
user's previous clipboard. Text and picture slips are both supported.

If Windows rejects the synthetic paste, the slip stays in the queue. When the
queue becomes empty, the bucket returns to Standard mode.

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
- optional slip titles and Markdown formatting
- picture previews and capture details
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
  projects\
    Project-name-3f2a91\
      project.json
      assets\
  templates\
  themes\
  views\
```

`workspace.json` contains store-wide state such as active lane pointers. Each
project owns its own folder and `project.json`; saving a slip rewrites only that
project.

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
- Kastn autosave, startup, reading-view, and template handoff preferences

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
dotnet run --project Zetl.Tests
dotnet run --project Zetl.App -- --self-test
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
