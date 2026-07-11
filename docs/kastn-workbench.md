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

## Board Mode

Kastn provides an alternate Board Mode (Kanban board layout) projection next to the standard document list view. In Board Mode, buckets are mapped to horizontal columns, and slips inside each bucket are rendered as cards in that column.

Key features of Board Mode:
- **Layout Toggles**: Switch between List and Board Mode using the segmented buttons in the toolbar or the View menu.
- **Auto-Hiding Panes**: Entering Board Mode automatically collapses the left Tree pane and the right Details pane, giving the columns full screen width. Returning to List Mode restores pane visibilities and original widths.
- **Drag-and-Drop Column Reordering**: Cards can be dragged between columns or reordered within a column. Auto-scrolling scrolls the board horizontally when a card is dragged near the left or right boundaries.
- **Quick Creation**: Each column features a `+` button to instantly add a new card directly to that bucket.
- **Double-Click Modal Editor**: Double-clicking a board card opens a responsive popup editor, allowing rapid card modification without using the main pane editor. Adding a new card via the `+` button automatically triggers this dialog.
- **Dual-Slip Picture Peek**: A text-presenting dual slip's card shows a small thumbnail on its right edge; clicking it expands the attached picture below the text and clicking again collapses it. The peek is view state only — it does not change the slip's preferred representation — and it survives card refreshes.

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
highlight the active kind. Bold, italic, and strikethrough are whole-slip
properties exactly like alignment and the block kind: the toolbar toggles the
slip's flag (uniformly across a multi-selection), nothing is written into the
body text, and the buttons light from the slip's own state. Bold on a selected
bucket title toggles the heading's bold instead. Only inline code, web links,
and stable-ID wiki-links remain selection-scoped style ranges (kept for a
future inline editor). Alignment (left, center, right) is a per-note property.

Typing Markdown by hand in the body is also supported: `**bold**`, `## heading`,
`> quote`, fenced code, `---` dividers, and `- ` / `1. ` lists all render. The
buttons are the decision-free whole-slip path; typed Markdown is how emphasis
is applied to a span within the text — both are valid.

Kastn keeps one ordered edit history: `Ctrl+Z` / `Ctrl+Y` undo and redo typing,
style toggles, moves, adds, and deletes strictly in the order they happened,
regardless of which pane has focus. The editor has no separate text-box undo;
pending typing joins the history as one entry when undo runs.

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

## Linked Slips

Kastn supports wiki-style inter-slip linking using stable IDs.

- **Syntax**: Wiki-links are written using double brackets: `[[targetId|cachedTitle]]`.
- **Interactive On-Screen Rendering**:
  - *Resolved Links*: Rendered inline as clickable accent-colored buttons. Clicking them navigates directly to the target slip.
  - *Unresolved/Stubs*: Rendered in a muted gray with a "Slip not found" tooltip.
- **Keyboard and Editor Navigation**:
  - *Ctrl+Click*: In the slip editor, holding `Ctrl` and clicking on a wiki-link navigates to the target slip.
  - *F12 (Go to Definition)*: Pressing `F12` with the caret inside a link navigates to the target slip.
- **Backlinks Details Inspector**: The Details pane compiles and displays a "Linked from" section containing clickable button shortcuts for all slips linking to the currently active slip.
- **Fidelity in Exports**: Resolving links translates correctly to HTML (`<a href="#id">`) and PDF bookmarks, allowing clickable cross-references in exports.

## Column Styles and Formatting Inheritance

Buckets can have an assigned formatting style (Column Style) configured in the bucket editor.

Available styles:
- **Standard (Notes)**: Standard default behavior.
- **Checklist**: Plain child slips automatically inherit checklist (`task`) formatting and display checkboxes.
- **Bullet List**: Plain child slips automatically inherit bullet formatting.
- **Numbered List**: Plain child slips automatically inherit numbered formatting.
- **Group Box**: Renders the bucket as a boxed container.
- **Table**: Renders the bucket's slips in a tabular grid (reserved).
- **LaTeX Block**: Renders the bucket as a LaTeX formatting block (reserved).

### Inheritance Rules
- Plain notes (slips with an empty/default block kind) automatically inherit the list formatting (Checklist, Bullet List, Numbered List) defined on their parent bucket/column.
- Compatible list choices compose: a numbered bucket with a task slip renders as a numbered checkbox item, and a bullet bucket with a task slip renders as a bulleted checkbox item.
- A slip can opt out with `Ignore bucket style for this slip` in the editor or
  board edit dialog. Kastn settings can also prefer explicit slip kinds over
  bucket styles globally.
- Whole-note block kinds (Heading, Quote, Code Block, Divider) render as their own block behavior.

## Pictures And Details

Picture slips render inline in the View and above their editable captions in the
Editor. Kastn requests normalized PNG content from Zetl through read-only IPC
and keeps a bounded in-memory cache; it never opens project asset paths.

A **dual slip** carries text content and a picture together — the shape a
spreadsheet copy captures. Its slip type is the preferred representation:
captured slips present as text, and a `Show as picture` / `Show as text`
button in the editor pane flips the presentation. Renderers, exports, and
Zetl's text-first flows all follow the current representation, the alternate
one stays attached, and the flip participates in Kastn undo.

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
  **Table** and **LaTeX** container kinds are reserved for later. See the **Column Styles and Formatting Inheritance** section above for details on list formatting kinds.

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
