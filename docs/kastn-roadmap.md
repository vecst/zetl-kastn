# Kastn And Zetl Work Map

This is the authoritative roadmap for remaining Kastn/Zetl product work.
Product direction lives in [`kastn.md`](kastn.md); current workbench behavior
lives in [`kastn-workbench.md`](kastn-workbench.md).

## Product Invariants

- Zetl is the resident capture, Replay, Pop, and fast-output process.
- Kastn is the deliberate browsing, organizing, editing, and publishing
  workbench.
- Zetl is the sole writer to workspace and project storage.
- Kastn uses versioned local IPC, immutable snapshots, and revision-checked
  domain commands.
- Human-readable project JSON remains the canonical live store.
- Slips are the source of truth. Views and generated artifacts are projections.
- Finished and archived projects are set aside, not made immutable.

## Current Baseline

The two-application foundation is in place:

- [x] Portable state, persistence, workflow runtime, and public contracts
- [x] Serialized sole-writer project service with durable acknowledgement
- [x] Stable IDs, record revisions, project change sequences, and command
      deduplication
- [x] Current-user named-pipe IPC with reconnect and ordered change events
- [x] Kastn startup, single-instance forwarding, Zetl launch, and offline state
- [x] Coordinated tray/minimize/shutdown behavior
- [x] Live co-editing while Zetl continues capturing
- [x] Text and picture slips, picture IPC, previews, and picture-aware exports
- [x] Project landing cards and lifecycle actions
- [x] Bucket/slip tree, search, filters, autosave, conflict resolution, batch
      actions, soft-delete, restore, and drag-and-drop
- [x] Optional titles, Markdown formatting, alignment, lists, and document
      structure
- [x] Per-note block kinds (paragraph / list / heading / quote / code) as a slip
      property, blessed in-body Markdown, and an export-fidelity advisory
- [x] Whole-slip bold/italic/strike as slip properties (inline emphasis stays
      typed Markdown), and one ordered undo/redo history covering the editor
- [x] Structural elements Zetl ignores: divider slips and container Group buckets
      (Table / LaTeX reserved), with multi-select drag, a drop indicator, and
      edge auto-scroll
- [x] Formatted, Plain, TSV, Markdown, HTML, and PDF views
- [x] Global and project-scoped view documents
- [x] Template catalog, authoring, project creation, and Zetl template picker
- [x] Creation types pairing templates with default views
- [x] Shared settings and live theme consumption
- [x] Reversible Active, Finished, and Archived project states
- [x] Repeatable JSON storage measurements through 20,000 slips

The current JSON baseline is recorded in
[`kastn-storage-baseline.md`](kastn-storage-baseline.md).

## High-Win Work List

Small, user-visible fixes to pull forward while the larger Board overhaul takes
shape:

- [x] Keep empty ancestor bucket headings visible when descendant slips match the
      current View.
- [x] Switch the Deleted toggle to `Read Slips` while browsing deleted slips.
- [x] Let Board Mode card edits save with `Ctrl+Enter`.
- [x] Restore/focus Kastn during Zetl quit when Kastn is minimized or hidden.
- [x] Make Markdown/HTML task export idempotent: authored task syntax is not
      double-prefixed, and HTML task output uses checkbox inputs.
- [x] Keep Zetl quick Compile `Plain` and `TSV` literal while letting
      `Formatted` stage printable rich clipboard content with plain-text
      fallback.
- [ ] Tighten Board Mode spacing, column state, and card creation as a focused
      overhaul rather than a new data model.

## Priority 1: Reliability And Release

The system is feature-rich enough that trustworthiness now has the highest
leverage.

- [ ] Exercise simultaneous Kastn/Zetl startup races.
- [ ] Test Zetl restart, Kastn restart, forced termination, login, and reboot.
- [ ] Stress reconnect retries, duplicate command delivery, stale revisions,
      sequence gaps, and partial client messages.
- [ ] Test JSON write failure, disk-full behavior, corrupt files, and migration
      recovery.
- [ ] Re-run large-project measurements and investigate material regressions.
- [ ] Verify diagnostics carry useful context without captured content.
- [ ] Define packaging, installation, app identity, and protocol compatibility
      policy.
- [ ] Dogfood both applications together through restart, capture, editing, and
      publishing workflows.
- [x] Fix minimized-Kastn quit handoff: when the user chooses Quit from Zetl,
      Kastn should surface the quit path or take focus instead of requiring the
      user to manually restore Kastn first.

Done when no tested crash path loses an acknowledged mutation, conflict recovery
never silently discards an edit, and capture latency remains acceptable while
Kastn is active.

## Priority 2: Finish Existing Workbench Designs

### Linked Slips

Stable-ID wiki-link parsing, cached readable titles, backlink calculation, and
the editor link picker are implemented.

Landed:

- [x] Render wiki-links as interactive links in the reading view.
- [x] Add a backlinks section to the Detail pane.
- [x] Render resolved and unresolved links appropriately in exports.
- [x] Finalize the cursor-in-link repoint interaction.

The complete design is in [`kastn-ui-roadmap.md`](kastn-ui-roadmap.md).

### Board Mode

Kastn provides an alternate kanban projection where buckets are columns and
slips are cards. It reuses existing `MoveSlip`, `ReorderSlip`, editor,
picture, and include state rather than introducing board-specific content.

- [x] Nested buckets currently flatten into tree-order columns.
- [x] Add the List/Board mode switch.
- [x] Reuse drag-and-drop for cards and columns.
- [x] Auto-collapse the Tree and Detail panes while Board mode is active.
- [ ] Run a small Board Mode overhaul focused on everyday capture/editing:
      clearer column state, faster card creation, keyboard-save parity, and
      tighter spacing without changing the underlying bucket/slip model.

The overhaul is sequenced as four slices (design in
[`kastn-ui-roadmap.md`](kastn-ui-roadmap.md#board-mode)):

- [x] **Canonical bucket order.** `project.Buckets` list order is now the one
      order: a sibling-relative `ReorderBucket` command (contract / store /
      service, revision-checked like `ReorderSlip`); the tree and board read
      storage order instead of alphabetical so they match the default view and
      export; and tree bucket drag shows an insertion line (top edge = before,
      bottom edge = after, middle = nest), reordering via `ReorderBucket` with a
      reparent step when the target sits under a different parent. Custom-section
      views keep their own order. No migration. Unblocks column reorder and the
      deferred bucket undo.
- [x] **Card placement precision + column reorder UI.** Cards and columns render
      the tree's drag feedback (insertion lines and a drop-into outline bound to
      the shared node flags); over a column's body the pointer's place among the
      cards picks the exact slot (gaps and the space below the last card mean
      "insert here"); and column headers drag via `ReorderBucket`, horizontal
      half picking before/after, never nesting.
- [x] **Incremental board rendering.** The board reconciles instead of
      rebuilding: columns and cards are keyed by id and reused while their render
      inputs are unchanged (a card rebuilds only when its slip revision or marker
      context changes), so a refresh keeps every scroll position and only touches
      the affected card/column. Card thumbnails are card-owned bitmaps disposed
      with their card rather than pooled per full rebuild.
- [ ] **Faster card creation.** Inline "type a card in place" instead of the
      `+` → modal round-trip (keyboard-save parity already exists).

### Interaction Cleanup

- [x] Add a clear live drop indicator to the existing tree drag-and-drop.
- [ ] Replace the landing page's role-group project list with two stable lane
      places. Each lane uses user-configurable labels that default to Main and
      Alternate; blank settings keep the defaults.
- [ ] Show active temporary consumables as stacked overlay cards on their lane,
      with a timer/replay cue and progress, while leaving the underlying lane
      project reachable from the card behind.
- [ ] Remove or demote redundant Move/parent controls after direct manipulation
      is comfortably accessible.
- [ ] Make nested bucket creation and placement faster in Zetl's quick Board.
- [ ] Work through the dogfood polish notes in
      [`kastn-ui-roadmap.md`](kastn-ui-roadmap.md#dogfood-notes-from-live-kastnzetl-use):
      Markdown task fidelity, deleted-slip browsing, editor/detail overflow,
      view scroll stability, Board Mode save shortcuts, and project bulk actions.

### Kastn Undo

Kastn-local slip undo/redo is landed as one ordered history over the existing
IPC, isolated from Zetl's coldkey stack.

- [x] Add a client-side undo history of inverse domain commands bound to in-app
      `Ctrl+Z` and isolated from the held-`Ctrl+Z` coldkey stack. All mutations
      route through a single `ExecuteMutationAsync` choke point; the planner and
      history are unit-tested.
- [x] Add gesture grouping (coalesce by slip id) and enable `AddSlip`, `MoveSlip`,
      and `ReorderSlip` undo. Divider insert, drag, the per-card edit dialog, and
      the batch loops each record as one entry.
- [x] Add redo via a symmetric undo/redo model, bound to `Ctrl+Y` (not
      `Ctrl+Shift+Z`, which would overload Zetl's Shift-lane undo coldkey).
- [x] One ordered history covering the editor: the slip editor's native TextBox
      undo is disabled, `Ctrl+Z`/`Ctrl+Y` there drive Kastn history (pending
      typing flushes in as one entry), and stacked same-slip entries re-thread
      their expected revisions so a run of undos walks the whole history.
- [ ] Route undo conflicts through the existing slip conflict-resolution panel
      with Undo-anyway and Keep-current actions (today a conflicted operation is
      skipped and reported in the status line).
- [ ] Bucket/project undo — unblocked now that canonical bucket order and
      `ReorderBucket` are landed; design its rules alongside the remaining Board
      Mode slices.

The detailed design is in [`kastn-undo-roadmap.md`](kastn-undo-roadmap.md).

## Priority 3: Project Board And Cross-Project Organization

Add a project-level board where projects are columns, buckets are expandable
cards, and slips can move or copy across projects. This depends on explicit
Zetl-owned cross-project transfer commands, a workspace-level asset store, and
cross-project wiki-link/export rules.

- [ ] Add a read-only Project Board projection over projects, buckets, and
      slips.
- [ ] Move live picture/file assets toward a workspace-level content-addressed
      asset store while keeping project exports self-contained.
- [ ] Add cross-project slip copy/move commands with active-project copy
      defaults.
- [ ] Add cross-project bucket copy/move commands, starting with leaf buckets
      and then bucket subtrees.
- [ ] Extend wiki-links to resolve cross-project targets while keeping
      single-project exports clean for Obsidian-style wiki-links.
- [ ] Add cohesive multi-project export rules for included linked material.

The detailed design is in
[`kastn-project-board-roadmap.md`](kastn-project-board-roadmap.md).

## Priority 4: Temporary Consumable Templates

Consumable templates already seed ordered Replay queues. A temporary consumable
should instantiate a disposable project, activate it in a chosen lane, and
delete it when it leaves that lane.

- [x] Add a `Temporary` template flag and validation.
- [x] Implement Zetl-owned disposal when the Replay queue empties, the lane is
      cleared, or another project replaces it.
- [x] Leave the lane inactive after disposal.
- [x] Add Kastn's Main/Alternate lane choice with optional remembered default.
- [x] Show temporary and durable projects in Kastn landing role groups: Pinned,
      Main, Alternate, and Projects.
- [x] Include currently active temporary projects in compile/source pickers; the
      landing page should make their lane-bound, disposable status visible.
- [x] Decide disposal ordering relative to Replay's empty-queue Standard-mode
      transition.

The detailed lifecycle is in
[`kastn-templates-roadmap.md`](kastn-templates-roadmap.md).

## Priority 5: Project Lifecycle Polish

The durable lifecycle model is implemented, including lane clearing and auto-return
to the rolling Journal.

Remaining:

- [ ] Decide whether `Finish Project` should optionally copy or fire a default
      output in the same action.
- [ ] Add clearer slip-level handoff cues for finished and archived projects.
- [ ] Verify the complete template → capture → finish → Kastn → render path as
      one documented workflow.

## Priority 6: Storage Consolidation (Single File Affirmed)

The single-file `project.json` model is affirmed as the canonical live store. Splitting project storage into multiple type-scoped JSON files has been explicitly rejected to avoid overhead and keep bucket hierarchies easy to rebuild.

Remaining focus:
- [x] Verify baseline JSON performance under load (completed via storage baseline scenario).
- [ ] Implement asset clean-up utility to purge orphaned files from the live
      asset store.
- [ ] Define missing-asset detection and handling behavior during load.
- [ ] Keep copied project folders completely readable and self-contained.

SQLite is not scheduled. Reconsider it only when measurements show that JSON, rather than UI/query design, is the actual bottleneck. If introduced, Zetl remains the sole writer and the IPC protocol stays storage-agnostic.

## Backlog

- [ ] Saved filters and broader faceting
- [ ] Tags/labels designed alongside storage/query work
- [ ] Explicit permanent-delete/empty-trash workflow
- [ ] Template and view file import/export
- [ ] Project-level history and richer recent activity
- [ ] File and URL inspectors
- [ ] Copy as Markdown/HTML and direct Print
- [ ] Publishing controls such as front matter, table of contents, page setup,
      tables, footnotes, figure treatment, and per-view styling
- [ ] Editor word count, find/replace, and raw/rendered preview

## Development Gates

All projects build into `artifacts\bin\<Configuration>\`.

```powershell
dotnet build Zetl.slnx --no-restore -p:UseAppHost=false
dotnet test Zetl.Tests\Zetl.Tests.csproj --no-build
dotnet .\artifacts\bin\Debug\Zetl.dll --self-test
```

Add focused contract, service, IPC, migration, rendering, and UI tests with each
slice. Manual GUI and process-lifecycle checks remain necessary where automated
tests cannot reproduce Windows focus, tray, clipboard, or termination behavior.
