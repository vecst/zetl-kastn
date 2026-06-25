# Kastn Workbench

Kastn is the deliberate project, template, view, and creation-type workspace.
It never reads or writes project JSON directly. It renders Zetl snapshots and
sends revision-checked domain commands back to Zetl.

## Landing

Kastn opens to a card-based landing page unless a direct project handoff or
startup preference opens a project.

The landing page provides:

- **Projects** — project summaries, preview snippets, lifecycle state, counts,
  recent activity, rename/delete, archive/reactivate, and an archived toggle
- **Templates** — protected built-ins and user-authored Capture or Consumable
  templates
- **Create** — creation types pairing a template with a default view

Opening a project does not change either of Zetl's active capture lanes.
`Close Project` returns to the landing page without closing Kastn or Zetl.

## Project Layout

The workbench uses three persistent panes:

- **Tree** — buckets and slips, plus creation and organization actions
- **View** — the rendered project document, copy/export, and view management
- **Detail** — `Editor | Details` for the selected slip

The View remains visible while a slip is edited. Selecting a tree item scrolls
and highlights its rendered block; clicking a View block selects it in the tree.

The tree shows bucket hierarchy and persisted slip order. A bucket selection is
navigation rather than a hidden View scope change. Search and explicit filters
control which slips are rendered.

## Search And Filters

Filters compose without mutating project state:

- case-insensitive text search
- source
- capture session
- today, last 7 days, or last 30 days

The tree, counts, and View reflect the active filter set. Stable IDs preserve
selection across snapshot refreshes where the selected item remains visible.

## Editing

Text slips have an optional title and body. Without an explicit title, Kastn
derives a display title from the body. Title-only slips are valid.

The editor supports:

- idle autosave and `Ctrl+S`
- optional include/exclude from views
- left, center, or right block alignment
- Markdown-backed bold, italic, strikethrough, inline code, and web links
- bullet, numbered, and task lists
- stable-ID wiki-link insertion

`New` creates one title-only `Untitled` draft and focuses its title. Repeated
creation attempts return to the untouched draft rather than accumulating empty
slips.

Zetl accepts an edit only when the slip revision still matches the version Kastn
opened. Unrelated captures preserve the local draft. A same-slip remote edit
opens a conflict panel:

- `Use Zetl Version` discards the local draft.
- `Keep Mine` retries against the newest revision.

There is no silent last-writer-wins overwrite.

## Pictures And Details

Picture slips render inline in the View and above their editable captions in the
Editor. Kastn requests normalized PNG content from Zetl through read-only IPC
and keeps a bounded in-memory cache; it never opens project asset paths.

The Details pane groups:

- bucket and capture time
- source and session
- application and window provenance
- picture dimensions, size, and original URL
- deletion history
- technical identity and revisions

HTTP(S) image provenance can be opened explicitly.

## Organization

Bucket actions support creation, rename, reparenting, reorder, and confirmed
tree deletion. Slip actions support creation, move, reorder, batch movement,
include/exclude, and deletion.

Drag-and-drop maps to ordinary Zetl commands:

- slip onto bucket → `MoveSlip`
- slip onto slip → `ReorderSlip` or move-before
- bucket onto bucket → parent change
- bucket onto root space → promote to top level

Cycle checks and protected-bucket rules remain enforced by Zetl.

### Soft Delete

Ordinary Kastn deletion moves slips into the protected `Deleted` bucket.
Deleted slips retain their prior bucket and deletion time and can be restored.

The Deleted view is intentionally separate from ordinary capture and publishing.
Zetl's Board, capture targets, Compile, Replay, and Pop ignore it. Permanent
empty-trash behavior remains a separate future action.

## Views And Exports

The View is a projection over current slips. It never becomes a second editable
document.

Available output kinds:

- Formatted
- Plain
- TSV
- Markdown
- HTML
- PDF

Document views support custom sections, bucket merging/omission, nested
structure, heading numbering, slip list style, Markdown formatting, alignment,
and embedded pictures.

Universal views live in `%AppData%\Zetl\views`. Views with project-specific
section structure are stored in the owning project and mutate through Zetl's
revision-checked service.

Markdown and HTML exports embed pictures as PNG data URIs. PDF embeds them
directly. Literal text formats retain readable picture-caption markers.
Unavailable assets produce placeholders without aborting the remaining export.

## Templates And Creation Types

Kastn authors the shared template catalog and creation types. Template use,
project creation, bucket setup, starter slips, and default-view assignment still
go through Zetl.

See [`kastn-templates-roadmap.md`](kastn-templates-roadmap.md) for the document
model and remaining temporary-consumable design.

## Keyboard

- `Ctrl+F`: focus search
- `Ctrl+S`: save the active editor immediately
- `F2`: focus and select the current bucket name
- `F5`: request fresh snapshots

Standard Avalonia keyboard navigation applies to cards, tree items, controls,
and lists.

## Live Changes

Zetl publishes project-sequence notifications for IPC mutations and direct
keyboard captures. Kastn requests a fresh selected-project snapshot when a
notification arrives.

The editor tracks its draft separately from refreshed snapshots, allowing
unrelated live changes to update the project without interrupting active work.
