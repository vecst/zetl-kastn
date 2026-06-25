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
- [x] Formatted, Plain, TSV, Markdown, HTML, and PDF views
- [x] Global and project-scoped view documents
- [x] Template catalog, authoring, project creation, and Zetl template picker
- [x] Creation types pairing templates with default views
- [x] Shared settings and live theme consumption
- [x] Reversible Active, Finished, and Archived project states
- [x] Repeatable JSON storage measurements through 20,000 slips

The current JSON baseline is recorded in
[`kastn-storage-baseline.md`](kastn-storage-baseline.md).

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

Done when no tested crash path loses an acknowledged mutation, conflict recovery
never silently discards an edit, and capture latency remains acceptable while
Kastn is active.

## Priority 2: Finish Existing Workbench Designs

### Linked Slips

Stable-ID wiki-link parsing, cached readable titles, backlink calculation, and
the editor link picker are implemented.

Remaining:

- [ ] Render wiki-links as interactive links in the reading view.
- [ ] Add a backlinks section to the Detail pane.
- [ ] Render resolved and unresolved links appropriately in exports.
- [ ] Finalize the cursor-in-link repoint interaction.

The complete design is in [`kastn-ui-roadmap.md`](kastn-ui-roadmap.md).

### Board Mode

Add an alternate kanban projection where buckets are columns and slips are
cards. Reuse existing `MoveSlip`, `ReorderSlip`, editor, picture, and include
state rather than introducing board-specific content.

- [ ] Decide how nested buckets map to columns or swimlanes.
- [ ] Add the List/Board mode switch.
- [ ] Reuse drag-and-drop for cards and columns.
- [ ] Keep the existing Detail pane docked while Board mode is active.

### Interaction Cleanup

- [ ] Add a clear live drop indicator to the existing tree drag-and-drop.
- [ ] Remove or demote redundant Move/parent controls after direct manipulation
      is comfortably accessible.
- [ ] Make nested bucket creation and placement faster in Zetl's quick Board.

## Priority 3: Temporary Consumable Templates

Consumable templates already seed ordered Replay queues. A temporary consumable
should instantiate a disposable project, activate it in a chosen lane, and
delete it when it leaves that lane.

- [ ] Add a `Temporary` template flag and validation.
- [ ] Implement Zetl-owned disposal when the Replay queue empties, the lane is
      cleared, or another project replaces it.
- [ ] Leave the lane inactive after disposal.
- [ ] Add Kastn's lane choice and minimize-after-use behavior.
- [ ] Keep temporary projects out of compile/source and ordinary project lists,
      except where visibility is needed while active.
- [ ] Decide disposal ordering relative to Replay's empty-queue Standard-mode
      transition.

The detailed lifecycle is in
[`kastn-templates-roadmap.md`](kastn-templates-roadmap.md).

## Priority 4: Project Lifecycle Polish

The durable lifecycle model is implemented, including lane clearing and dated
project advancement.

Remaining:

- [ ] Decide whether `Finish Project` should optionally copy or fire a default
      output in the same action.
- [ ] Add clearer slip-level handoff cues for finished and archived projects.
- [ ] Verify the complete template → capture → finish → Kastn → render path as
      one documented workflow.

## Priority 5: Storage Evolution

Typed slips already bridge text and picture content inside the current
per-project `project.json`. Splitting captures into type-specific files remains
a strategic option, not a prerequisite for ordinary product work.

Before changing the live format:

- [ ] Reconfirm that measured JSON behavior or query needs justify the
      migration.
- [ ] Finalize metadata and typed-file schemas.
- [ ] Design an atomic, restartable migration with backup and rollback.
- [ ] Preserve slip order, bucket hierarchy, Replay review links, sessions,
      revisions, and asset references.
- [ ] Define URL and file capture semantics.
- [ ] Define missing-asset and orphan-cleanup behavior.
- [ ] Keep copied project folders readable and self-contained.

SQLite is not scheduled. Reconsider it only when measurements show that JSON,
rather than UI/query design, is the actual bottleneck. If introduced, Zetl
remains the sole writer and the IPC protocol stays storage-agnostic.

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
dotnet .\artifacts\bin\Debug\Zetl.Tests.dll
dotnet .\artifacts\bin\Debug\Zetl.dll --self-test
```

Add focused contract, service, IPC, migration, rendering, and UI tests with each
slice. Manual GUI and process-lifecycle checks remain necessary where automated
tests cannot reproduce Windows focus, tray, clipboard, or termination behavior.
