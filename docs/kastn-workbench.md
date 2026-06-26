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

A slip's body is plain text — a small Markdown subset — and how the slip renders
is governed by its **block kind**, a per-note property toggled from the editor
toolbar. The marker is never written into the body:

- **Paragraph** — the default.
- **Bullet**, **Numbered**, or **Task** list item. A task note carries a checked
  flag toggled from its checkbox in the View.
- **Heading** (a softened sub-heading), **Quote**, or **Code** block — the whole
  note renders as that block.

Pressing a block-kind button toggles that kind on the selected note (or every
note in a multi-selection); pressing it again clears it, and the buttons
highlight the active kind. Inline formatting — bold, italic, strikethrough,
inline code, web links, and stable-ID wiki-links — wraps the current selection.
Alignment (left, center, right) is a per-note property.

Typing Markdown by hand in the body is also supported: `**bold**`, `## heading`,
`> quote`, fenced code, `---` dividers, and `- ` / `1. ` lists all render. The
buttons are the decision-free path; typing is the explicit one — both are valid.

The on-screen View always renders the body richly; a view's kind only governs
its Copy/Export artifact. So when the selected view would drop formatting on
export (a Plain or TSV view, or alignment in Markdown), the toolbar shows a
fidelity note. The editor also offers idle autosave, `Ctrl+S`, and optional
include/exclude from views.

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

Dragging a member of a multi-selection moves the whole selection (the selection
is held through the press rather than collapsing). A drop-target marker shows
where a drop will land, the tree auto-scrolls when a drag is held near its top or
bottom edge, and a multi-slip move is sent as one batch so the tree rebuilds once.
Cycle checks and protected-bucket rules remain enforced by Zetl.

### Structural Elements

A bar above the tree inserts **structural elements** — Kastn-only rendering
constructs that Zetl ignores:

- **Divider** is a content-less structural slip (rendered as a rule). It is
  inserted as its own slip rather than by reformatting a note. Zetl skips
  structural slips in capture, compile, Replay, and Pop, and the content-format
  controls do not apply to one.
- **Group** is a container *bucket* (a render kind) shown as a boxed, labelled
  section. Slips and whole buckets are dragged into it through the ordinary
  move/reparent drag-and-drop. A container is an otherwise normal bucket — Zetl
  still captures and compiles its contents; only the render kind is Kastn-only.
  **Table** and **LaTeX** container kinds are reserved for later.

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
