# Kastn UI Design Record

This document records the major Kastn workbench UI decisions and the remaining
linked-slip and Board designs. Current priority and release status are tracked
in [`kastn-roadmap.md`](kastn-roadmap.md).

## Current Workbench Shape

Kastn uses three persistent panes:

```text
┌─ project title · counts · search · filters · close ───────────┐
├──────────────┬──────────────────────────────┬─────────────────┤
│ Buckets and  │ Rendered project view        │ Detail          │
│ slips tree   │ View · Copy · Export · Manage│ Editor/Details  │
│              │                              │                 │
│ navigation   │ selectable slip blocks       │ selected slip   │
│ + Slip       │ pictures and formatted text  │ metadata/actions│
│ + Bucket     │                              │                 │
└──────────────┴──────────────────────────────┴─────────────────┘
```

The rendered View remains visible while the right Detail pane switches between
editing and metadata. The tree is navigation and organization; filters and
search determine what the View contains.

## Landed

### Persistent View And Detail

- [x] Replace the old Slip/View swap with a permanent center View.
- [x] Put `Editor | Details` in the right pane.
- [x] Let tree selection drive the editor, inspector, and View highlight.
- [x] Keep the View whole-project rather than bucket-scoped.
- [x] Scroll to and highlight a selected slip.
- [x] Select the corresponding tree item when a View block is clicked.

### Navigation And Tree

- [x] Move `+ Slip` beside `+ Bucket`.
- [x] Collapse title, counts, search, filters, and Close into one top row.
- [x] Add bucket, text, and picture icons.
- [x] Add include/exclude controls and per-bucket counts.
- [x] Support slip move/reorder and bucket reparenting through drag-and-drop.
- [x] Protect `Deleted` from invalid drag sources and targets.

The existing drag implementation uses `MoveSlip`, `ReorderSlip`, and
`UpdateBucket`; it adds no new persisted state.

### Slip Formatting

Formatting is Markdown-backed rather than WYSIWYG. Plain text remains valid
Markdown, so Zetl capture stays decision-free.

- [x] Optional block alignment stored as slip metadata
- [x] Bold, italic, strikethrough, inline code, and web links
- [x] Formatting toolbar (inline wraps a selection; the block kind is a property)
- [x] Shared parsing across on-screen, HTML, and PDF renderers
- [x] Literal Formatted/Plain/TSV behavior left unchanged
- [x] Per-note **block kind** (paragraph / bullet / ordered / task / heading /
      quote / code) toggled from the toolbar — the marker is a slip property, not
      body text; task notes carry a checked flag toggled from the View
- [x] Blessed in-body Markdown: typed `##` / `>` / fences / `---` / lists render
- [x] Export-fidelity advisory when the selected view drops formatting on export

### Note Kinds And Structural Elements

- [x] `BlockKind` slip property + `RenderKind` bucket property, centralized in
      `ZetlBlockKinds` / `ZetlBucketRenderKinds`
- [x] **Divider** structural slip inserted from a tree-side bar; Zetl skips
      structural slips in capture/compile/Replay/Pop and content controls disable
- [x] **Group** container bucket rendered as a boxed section; slips and buckets
      drag in through ordinary move/reparent (Table / LaTeX kinds reserved)
- [x] Multi-select drag with deferred selection, a drop-target marker, edge
      auto-scroll, and batched multi-slip moves

### View Structure And Authoring

- [x] Per-view bullet, ordered, task, or paragraph slip style
- [x] Optional cascading heading numbers
- [x] Nested bucket rendering
- [x] Quick-compile and Document format families
- [x] Relevant settings shown only for the selected format
- [x] Visual custom-section builder replacing the text DSL
- [x] Section rename, merge, omit, and drag ordering
- [x] Live preview using the production renderer
- [x] Universal views in `%AppData%\Zetl\views`
- [x] Structured project views stored in the owning `project.json`
- [x] Revision-checked save/delete for project-scoped views

## Linked Slips

Linked slips use wiki-link tokens:

```text
[[slip-id|Readable title]]
```

The slip ID is authoritative. The cached title keeps raw text readable and is
refreshed when the link is saved or exported. Backlinks are computed from
forward links rather than persisted.

Landed:

- [x] Parse and resolve stable-ID wiki-links.
- [x] Preserve unresolved tokens as readable stubs.
- [x] Refresh stale cached titles without changing unresolved links.
- [x] Compute deduplicated backlinks.
- [x] Provide a searchable editor picker.
- [x] Repoint a token rather than nesting a new token when editing an existing
      link.

Remaining:

- [ ] Render interactive links in the center View.
- [ ] Select and reveal the target slip when a link is activated.
- [ ] Add `Linked from` backlinks to the Detail pane.
- [ ] Render resolved and unresolved links appropriately in Markdown, HTML, and
      PDF exports.
- [ ] Finalize the cursor-in-link keyboard/mouse interaction.

Deleted or missing targets should remain visible as stubs. Bucket movement and
title changes must not break links because organization and display text are not
identity.

## Board Mode

Board mode is an alternate projection over existing buckets and slips:

- buckets become columns;
- slips become cards;
- cross-column movement uses `MoveSlip`;
- within-column ordering uses `ReorderSlip`;
- the existing Detail pane remains docked for editing.

Card content should include title/preview, picture thumbnail, capture origin,
and include state. Column headers should expose bucket name, count, and quick
card creation.

Open design:

- [ ] Decide whether nested buckets become leaf columns or collapsible
      swimlanes under top-level columns.
- [ ] Decide how `Deleted` appears, if at all.
- [ ] Add the List/Board mode switch.
- [ ] Reuse tree drag plumbing for cards and columns.

Board mode must remain a projection. It does not introduce board-owned content
or a second ordering model.

## Interaction Cleanup

- [x] Add a live drop-target indicator during drag-and-drop.
- [ ] Keep keyboard-accessible movement controls while reducing redundant
      always-visible Move/parent UI.
- [ ] Improve reading width and font-size controls.

## Publishing Backlog

- Tags and labels
- Auto-generated table of contents
- Front matter
- Page size, margins, and base typography
- Footnotes and Markdown tables
- Figure numbering, width, and alignment
- Copy as Markdown/HTML and direct Print
- Per-view document styling
- ~~Structural divider or heading slips~~ — divider landed; container Group
  buckets landed (Table / LaTeX reserved)
- Word/character count and project-wide find/replace
- ~~Headings, blockquotes, code blocks, and horizontal rules~~ — landed as
  per-note block kinds plus blessed in-body Markdown
