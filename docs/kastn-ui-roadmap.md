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

Typed Markdown remains valid and first-class, so Zetl capture stays
decision-free. Toolbar formatting should become property-backed structured
intent: the button changes slip metadata or style ranges, and each export mode
decides how to express those properties.

- [x] Optional block alignment stored as slip metadata
- [x] Bold, italic, strikethrough, inline code, and web links render from typed
      Markdown markers
- [x] Formatting toolbar for block kinds (the block kind is a property)
- [x] Shared parsing across on-screen, HTML, and PDF renderers
- [x] Literal Formatted/Plain/TSV behavior left unchanged
- [x] Per-note **block kind** (paragraph / bullet / ordered / task / heading /
      quote / code) toggled from the toolbar — the marker is a slip property, not
      body text; task notes carry a checked flag toggled from the View
- [x] Blessed in-body Markdown: typed `##` / `>` / fences / `---` / lists render
- [x] Export-fidelity advisory when the selected view drops formatting on export
- [x] Move toolbar buttons away from inserting Markdown markers and toward
      persisted whole-slip properties or inline ranges. Markdown can still be
      typed directly; buttons do not mutate body text.

Shipping worklist for property-backed formatting:

- [x] Add a persisted inline style range contract on slips, with command
      semantics where `null` preserves existing ranges and `[]` clears them.
- [x] Reconcile style ranges during text edits. Start with clamping/dropping
      invalid ranges, then add selection-aware shifting so edits before a range
      keep the intended styled text attached.
- [x] Keep bold, italic, and strike as batchable whole-slip properties; inline
      code remains a selection range and an empty selection arms the next text
      without inserting a Markdown marker.
- [x] Convert web-link and slip-link toolbar actions to structured link ranges
      (`href` or target slip id + cached title) while keeping typed Markdown and
      typed wiki-link tokens fully supported.
- [x] Feed whole-slip emphasis and property ranges into the shared inline AST so
      the center View, HTML, PDF, Markdown export, and rich clipboard translate
      the same intent.
- [x] Keep `Plain` and `TSV` export/compile modes literal; they ignore inline
      style ranges just like they ignore typed Markdown styling.
- [x] Add editor affordances for range state: button active state over the
      current selection/caret, clearer link edit/remove, and overlap policy
      tests for bold+italic and link/code exclusion.
- [x] Add batchable whole-slip font family, font size, and text color. Empty
      values inherit the theme; the reader, HTML, and PDF preserve them while
      the fidelity advisory identifies formats that cannot.

### Note Kinds And Structural Elements

- [x] `BlockKind` slip property + `RenderKind` bucket property, centralized in
      `ZetlBlockKinds` / `ZetlBucketRenderKinds`
- [x] **Divider** structural slip inserted from a tree-side bar; Zetl skips
      structural slips in capture/compile/Replay/Pop and content controls disable
- [x] **Group** container bucket rendered as a boxed section; insertion asks for
      its authored label before creation, and slips/buckets drag in through
      ordinary move/reparent (Table / LaTeX kinds reserved)
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
- [x] Render interactive links in the center View.
- [x] Select and reveal the target slip when a link is activated.
- [x] Add `Linked from` backlinks to the Detail pane.
- [x] Render resolved and unresolved links appropriately in Markdown, HTML, and
      PDF exports.
- [x] Support cursor-in-link keyboard and mouse navigation.

Deleted or missing targets should remain visible as stubs. Bucket movement and
title changes must not break links because organization and display text are not
identity.

## Board Mode

Board mode is an alternate projection over existing buckets and slips:

- buckets become columns;
- slips become cards;
- cross-column movement uses `MoveSlip`;
- within-column ordering uses `ReorderSlip`;
- the Tree and Detail panes auto-hide to give columns full width.

Card content should include title/preview, picture thumbnail, capture origin,
and include state. Column headers should expose bucket name, count, and quick
card creation.

Landed:

- [x] Nested buckets flatten into tree-order columns.
- [x] `Deleted` stays out of the normal Board projection.
- [x] Add the List/Board mode switch.
- [x] Reuse tree drag plumbing for cards and columns.

Board mode must remain a projection. It does not introduce board-owned content
or a second ordering model.

### Bucket Ordering (canonical order)

Buckets had three conflicting orders: the tree and board sorted alphabetically by
name, the default view and export followed `project.Buckets` storage order, and
custom-section views followed their section list. So the left pane never matched
the viewer or an exported file.

The canonical order is the manual `project.Buckets` list order, sibling-relative,
mirroring how slip order already works:

- A `ReorderBucket` command repositions a bucket among its siblings, anchored
  immediately before a sibling id (null = end), revision-checked like
  `ReorderSlip`. Reorder never changes a bucket's parent — that stays
  `UpdateBucket` reparenting.
- The tree and board read storage order instead of alphabetical, so they match the
  default view and export.
- Custom-section views keep their explicit section order as a deliberate per-view
  projection that intentionally overrides the canonical order.
- No migration: existing projects show buckets in creation order until the user
  reorders them.

This is the prerequisite for column reordering in Board mode and for the deferred
bucket undo.

## Landing Lane Cards

The landing page should expose the two active lanes as stable places rather
than mixing lane state into an ordinary project list. The built-in labels are
Main and Alternate, but users may rename both labels in settings. Blank custom
labels mean "use the default." Custom labels should be short, with a maximum of
20 characters, so card headers, buttons, and menus stay predictable.

Temporary consumable projects are lane overlays, not a third lane. When a
temporary queue is active in a lane, Kastn should render it as a stacked card on
top of that lane's underlying project card:

```text
Main                         Alternate
+----------------------+     +----------------------+
| timer Temporary      |     | Project B            |
| Recipe Queue         |     | 7 slips              |
| 3 replay items left  |     +----------------------+
+----------------------+
  +--------------------+
  | Project A          |
  | underlying project |
  +--------------------+
```

Clicking the overlay opens the temporary project. Clicking the visible card
behind opens the underlying lane project. If no underlying project exists, the
rear card is an empty lane state. Completing, clearing, or replacing the
temporary project removes only the overlay and reveals the lane's normal state.

Compile/source pickers may include currently active temporary projects because
the landing page makes them visible as lane-bound disposable queues. Inactive
temporary projects should not become ordinary browseable library entries; they
should be disposed by Zetl.

## Interaction Cleanup

- [x] Add a live drop-target indicator during drag-and-drop.
- [x] Add an insertion line for both bucket and slip tree drags: the row under the
      pointer and its vertical fraction choose before / after / nest, drawn as an
      accent line (between) or a row highlight (into). Slip and bucket drags share
      one hit-test (`RowUnderPointer` + `EdgeFromFraction`) and one move-then-reorder
      step (`SendThreadedAsync`); the apply paths stay separate because slips are
      multi-select and never nest into each other while buckets do both. A sticky
      last-resolved position keeps a between-rows gap from rejecting the drop.
- [ ] Insertion-line jitter: the line can still flicker at the before/after flip
      point (a row's vertical midpoint) when the pointer hovers right on the
      boundary. Add a small dead-band/hysteresis around the flip so it does not
      oscillate. Minor; deferred.
- [ ] Keep keyboard-accessible movement controls while reducing redundant
      always-visible Move/parent UI.
- [ ] Add reader-wide width and zoom controls. Authored per-slip font size is
      implemented; this item is the separate non-persisted reading preference.

## Dogfood Notes From Live Kastn/Zetl Use

These are field notes captured while using Zetl and Kastn together. Treat them
as interaction bugs or near-term polish candidates rather than new product
directions.

### Export And Compile Fidelity

- [x] Markdown task output should render as usable task checkboxes in common
      Markdown targets where supported, and should not show an extra bullet dot
      in front of each checkbox.
- [x] Zetl quick Compile should keep `Plain` and `TSV` free of styling while
      `Formatted` can copy/paste rich clipboard content with printable task box
      symbols for quick worksheets.
- [ ] Recheck Markdown, HTML, PDF, and rich clipboard task/list rendering
      together so on-screen View, Copy, Export, and Compile agree.

### Detail Pane And Editor Layout

- [x] Let the style toolbar wrap in the Detail pane and keep typography selectors
      compact so added controls consume vertical space instead of overflowing.
- [x] Make inline formatting buttons property-backed. Typed Markdown remains
      supported, but toolbar actions should set style metadata/ranges that the
      on-screen View, Markdown, HTML, PDF, and rich clipboard exporters translate
      appropriately.
- [x] Add a way to apply task/checklist/list styling to an entire bucket via
      bucket selection and the list toolbar buttons.
- [x] Allow compatible bucket/slip list kinds to compose, with a per-slip opt-out
      and a Kastn setting to prefer explicit slip kinds globally.

### Deleted Slips

- [ ] Deleted slips should be accessed through an explicit `View Deleted` button
      rather than letting the protected `Deleted` bucket appear wherever it falls
      in the tree.
- [x] While browsing deleted slips, the button text should switch to `Read Slips`
      or similar so the return path is obvious.
- [ ] Pressing Delete with a selected slip in the Buckets and Slips pane should
      ask for confirmation before moving the slip to Deleted.

### View And Layout Polish

- [ ] Add per-view title controls: hide the project name, use the project name,
      or provide a custom document title.
- [ ] Hiding an item low in a long View briefly flashes/scrolls to the top before
      restoring position. Preserve scroll without the visible jump.
- [ ] Compare the left Buckets and Slips pane layout against the center View
      layout; spacing, hierarchy, and visual rhythm currently feel mismatched.

### Board Mode

- [x] Creating a new slip/card in Board Mode should save with `Ctrl+Enter`, just
      like the main editor.

### Project Landing And Bulk Actions

- [ ] Add project-card multi-select through a checkbox in the top-right corner of
      each card.
- [ ] When any project is selected, show a compact bulk-action bar/dropdown for
      archive, delete, rename where applicable, set active, and set alternate.
- [ ] When all selected projects share an action menu, applying an action from
      that menu should affect the whole selection.
- [ ] Add a right-click context menu mirroring the card overflow and bulk actions.

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
