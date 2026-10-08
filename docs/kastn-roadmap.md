# Kastn And Zetl Work Map

This is the authoritative roadmap for remaining Kastn/Zetl product work.
Product direction lives in [`kastn.md`](kastn.md); current workbench behavior
lives in [`kastn-workbench.md`](kastn-workbench.md).

## Working Order

Agreed with Lukas on October 8, 2026. Work top to bottom on the main track; the
Linux track runs alongside at the pace of hardware testing. Ideas not listed
here wait in [`ideas.md`](ideas.md#deferred).

**Main track**

1. **Quick wins.** Start at login from Settings (done, Windows and Linux),
   then the open Priority 1 fixes: capture and Replay destination defaults and
   fresh-profile Journals (verify first; they may be fixed), the recovered-draft
   layout, and `data:image` clipboard payloads.
2. **RC 3.1.** Finish RC 3 testing and ship the post-RC 3 branch with the quick
   wins: transparency and Compose wording, delta reads, the Board overwrite
   fix, the Linux keyboard and single-instance fixes, the log settings fix.
   Publish Windows and Linux builds.
3. **Release gate.** The testing half of Priority 1, the delta IPC acceptance
   matrix and measurements, the Windows parity checklist on a published build,
   and the packaging, identity, and protocol compatibility policy.
4. **Logs as their own store** (plan in [`ideas.md`](ideas.md#logs-out-of-project-storage)),
   including recording project and bucket creation. Before find, so search
   never has logs to exclude.
5. **Find** (Priority 3): the core engine, the held `Ctrl+F` popup, result
   actions and the `Ctrl+Enter` setting, then Kastn's search on the engine.
6. **`@bucket` filing** of quick notes ([`ideas.md`](ideas.md#bucket-at-the-start-of-a-note)).
7. **Calendar** landing mode, then the timeline view; both reuse find's engine.
8. **Workbench growth:** Board spacing and interaction cleanup, then Priorities
   4, 5, and 6.

**Linux track** (details in [`linux-roadmap.md`](linux-roadmap.md))

1. Checks: the `xclip` and GNOME Shell version tests on Ubuntu and Solus, and
   `Xft.dpi` after a fresh Plasma login.
2. Daily use on the box: scaling, suspend/resume and logout, settings, templates,
   and the tray menu end to end, multiple monitors.
3. The X11 selection clipboard (the lite tier for X11 sessions and GNOME); the
   GNOME Shell extension only if Lite falls short.
4. Packaging: AppImage autostart; draft the AerynOS recipe, file the package
   request when policy allows.
5. Housekeeping: the keyboard exception exit test, retire `Zetl.Linux.Spike`,
   drop the headless milestone, tick the RC items RC 3 met.

## October 5, 2026 Checkpoint

The application cleanup and performance pass is complete on `codex/cleanup`:

- MainWindow ownership, presenters, final wiring, and dead-code audit are
  complete. The window now delegates navigation, mutations, snapshots,
  settings, creation, rendering, and application lifetime to focused owners.
- Tree, reader, Board, and inspector rendering follows the viewport. Snapshot
  updates reuse indexes and structure; picture decoding and retention are
  bounded. Large-project and many-bucket work has been exercised interactively.
- Autosave across buckets, tree drag feedback, application icons, exported PDF
  task checkboxes, and first-use tray-menu layout have been corrected.
- Latest full automated validation: 1,271 tests passed and two intentional
  latency-probe skips. The final solution build had no warnings or errors.
- Zetl uses software rendering on Windows. An isolated 100-cycle popup probe
  settled near 103 MiB working set / 33 MiB private memory, with no closed note
  windows retained after diagnostic collection. The real-profile host measured
  about 106 MiB working set / 51 MiB private memory after disabling previously
  enabled full page heap. These are different workloads, not universal limits.
  The debugging configuration repair is separate from the renderer change;
  see [`zetl-popup-memory.md`](zetl-popup-memory.md) for evidence and reproduction.

The next requested pass is the existing GitHub Pages landing page on
`gh-pages`: correct wording and simplify overlapping download/install options
while preserving its overall design and behavior.

After that, the remaining near-term work is Kastn memory measurement (startup,
project switching, pictures, and long sessions), release reliability and
published Windows smoke checks, and Board spacing/interaction polish. Global
search, Calendar, cross-project organization, and production Linux input and
clipboard support remain subsequent product work. This checkpoint does not
close the outstanding reliability items below.

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
- [x] Dual text+picture capture (spreadsheet copies keep both formats) with a
      Kastn preferred-representation toggle
- [x] Persist captured rich text privately and stage native LibreOffice Calc
      formats before HTML/plain fallbacks, including review, restart, and
      clean-export safety
- [x] Project landing cards and lifecycle actions
- [x] Bucket/slip tree, search, filters, autosave, conflict resolution, batch
      actions, soft-delete, restore, and drag-and-drop
- [x] Optional titles, Markdown formatting, alignment, lists, and document
      structure
- [x] Whole-slip font family, font size, and text color with multi-select,
      undo/redo, rich-reader, HTML, and PDF support
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
- [x] Restore/focus Kastn for the initial coordinated Quit request when Kastn is
      minimized or hidden.
- [x] Make Markdown/HTML task export idempotent: authored task syntax is not
      double-prefixed, and HTML task output uses checkbox inputs.
- [x] Keep Zetl quick Compile `Plain` and `TSV` literal while letting
      `Formatted` stage printable rich clipboard content with plain-text
      fallback.
- [ ] Tighten Board Mode spacing. (The rest of the focused overhaul — column
      state, precise drops, incremental rendering, and inline card creation —
      is landed; see the Board Mode slices.)

## Priority 1: Reliability And Release

The system is feature-rich enough that trustworthiness now has the highest
leverage.

- [x] Opening the Zetl Board (held Ctrl+B) while editing a slip in Kastn: Kastn
      saves on focus loss just after the Board loads that slip. The Board now
      tracks the text it loaded, reloads Kastn's newer text, and never writes
      untouched text back (`ZetlBoardNoteDraft`). Verified by hand: Board edits
      reach Kastn at once, and an edit resumed in the Board saves back to it.
      True simultaneous edits would still be last-writer-wins (the Board writes
      in-process without a revision check), but Kastn saves on focus loss
      before the Board can be typed in, so no real flow reaches that today.
- [ ] Exercise simultaneous Kastn/Zetl startup races.
- [ ] Test Zetl restart, Kastn restart, forced termination, login, and reboot.
- [ ] Stress reconnect retries, duplicate command delivery, stale revisions,
      sequence gaps, and partial client messages.
- [x] Inject project and workspace JSON write failures and verify that live
      state and revisions roll back to the last durable snapshot.
- [x] Keep the low-level Windows keyboard hook fail-open: callback and recovery
      failures cannot cross the unmanaged boundary or suppress physical input.
- [x] Serialize rapid Replay taps through durable consumption per lane while
      keeping Main and Alternate Replay independent.
- [x] Resume visible durable Replay queue items across Zetl restarts instead of
      treating prior-session slips as an empty queue and passing through paste.
- [x] Preserve the complete multi-format Windows clipboard across Replay,
      write anyway (without restoring) when the clipboard cannot be backed up
      so paste never blocks, and bind delayed restoration to the exact staged
      clipboard change token.
- [x] Replay captured text slips with allowlisted native Calc formats so cell
      styling survives without HTML import adding wrap or alignment rules.
- [x] Preserve undo/redo entries until inverse commands are confirmed; retry
      outcome-unknown IPC mutations by command ID on the same server instance,
      and generate repair entries for reconciled partial compounds.
- [x] Allowlist active authored links across the reader, HTML, Markdown,
      clipboard, and PDF paths; unsupported schemes render inert, new links are
      validated, and self-contained HTML carries a restrictive CSP.
- [x] Save-or-confirm dirty Kastn edits before normal and coordinated exits; when
      saving is unavailable, offer durable local recovery, explicit discard, or
      cancellation instead of silently closing.
- [x] Journal the one active editor draft locally with its baseline revision,
      text, and styles; reopen it on launch and surface a conflict if Zetl changed
      the authoritative slip while Kastn was away.
- [ ] Test disk-full behavior and recovery from interruption between related
      project/workspace operations.
- [ ] Re-run large-project measurements and investigate material regressions.
- [ ] Verify diagnostics carry useful context without captured content.
- [ ] Define packaging, installation, app identity, and protocol compatibility
      policy.
- [ ] Dogfood both applications together through restart, capture, editing, and
      publishing workflows.
- [x] Make coordinated Quit re-entrant and reliably foreground its existing
      Kastn confirmation (b29ab3c: a repeated Quit waits on Kastn's pending
      answer; 02cfb4c: Zetl hands Kastn the foreground).
- [ ] Initialize and activate both rolling Journals on a fresh profile, keep
      Shift Board fallback inside the Shift lane, and remember the last selected
      Shift project when that lane has no explicit active project.
- [ ] Keep the recovered-draft conflict comparison above Kastn's status bar at
      the supported window sizes.
- [ ] Record project and bucket creation in Notification History and the
      protected `Zetl Logs` project.
- [ ] Correct capture and Replay destination defaults: prefer the lane's current
      project, and within a rolling Journal prefer today's child rather than the
      first seeded Monday child.
- [ ] Decode valid clipboard `data:image/...;base64,...` payloads into normalized
      image slips under the same MIME, dimension, and size validation used for
      clipboard and HTTP images; malformed or unsupported data URIs stay text.
- [x] Preserve lane activation when a Quick Note is submitted to the
      already-active project (1274e99 made activation deliberate;
      `RuntimeQuickNoteKeepsActiveProject` covers it).

Done when no tested crash path loses an acknowledged mutation, conflict recovery
never silently discards an edit, and capture latency remains acceptable while
Kastn is active.

## Delta IPC Adoption

Kastn reads its open project by cursor-based delta when Zetl advertises
`projectChangesV1`, instead of a full `GetProject` on every changed refresh.
This came out of the HAIUX plugin, which drives Zetl/Kastn for shared AI and
human work and needs cheap polling of large projects. Protocol:
[`delta-ipc.md`](delta-ipc.md); plan and acceptance checklist:
[`kastn-delta-ipc-handoff.md`](kastn-delta-ipc-handoff.md).

- [x] Bring the generic `GetProjectChanges` command, change feed, reducer, and
      Welcome capability onto the Kastn branch (HAIUX itself stays separate).
- [x] Transport integration: `KastnProjectSync` owns the applied snapshot and
      cursor for the open project and rebuilds full snapshots, so the editor,
      draft, tree, Board, reader, and picture reconciliation are unchanged. The
      summary fast path, navigation/client admission, and older-Zetl
      `GetProject` fallback are preserved; the cursor commits only with the
      snapshot it describes; an invalid batch gets one bootstrap retry.
- [x] Same-Zetl reconnect resumes from the retained cursor; a restarted Zetl
      answers the old cursor with an explicit reset.
- [ ] Widen the acceptance matrix from the handoff: styles, reorders, nested
      buckets, metadata/views, restore/purge, events before responses,
      deferred batches, concurrent same-slip edits, cursor expiry and capacity
      resets, malformed or gapped responses, failed refresh after a confirmed
      save, rapid A-to-B-to-A navigation, and project deletion.
- [ ] Keep baselines for more than the open project if A-to-B-to-A bootstraps
      show up in practice (a deliberately bounded cache, kept out of the
      publication path).
- [ ] Measure full, delta, reset, and idle reads in Kastn: payload bytes,
      allocations, refresh time, and UI latency on large-text, many-bucket, and
      picture-heavy projects. Count retained baselines and Zetl's indexes before
      claiming memory savings.
- [ ] Only if profiling shows value: pass changed-entity hints to indexes and
      presenters instead of rediffing full snapshots.
- [ ] Verify on Linux.

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
- [x] Run a small Board Mode overhaul focused on everyday capture/editing:
      clearer column state, faster card creation, and keyboard-save parity,
      without changing the underlying bucket/slip model. All four slices below
      are landed; only spacing polish remains (see the High-Win list).

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
- [x] **Faster card creation.** The column `+` opens an inline composer under
      the cards: Enter adds and keeps composing, Shift+Enter inserts a newline,
      Esc discards, and leaving the box commits typed text (a failed add keeps
      the composer open so capture is never silently lost).

### Interaction Cleanup

- [x] Add a clear live drop indicator to the existing tree drag-and-drop.
- [x] Replace the landing page's role-group project list with two stable lane
      places. Each lane uses user-configurable labels that default to Main and
      Alternate; blank settings keep the defaults.
- [x] Show active temporary consumables as stacked overlay cards on their lane,
      with a timer/replay cue and progress, while leaving the underlying lane
      project reachable from the card behind.
- [ ] Remove or demote redundant Move/parent controls after direct manipulation
      is comfortably accessible.
- [ ] Make nested bucket creation and placement faster in Zetl's quick Board.
- [ ] Finish the remaining dogfood polish notes in
      [`kastn-ui-roadmap.md`](kastn-ui-roadmap.md#dogfood-notes-from-live-kastnzetl-use):
      cross-output task/list verification, Delete-key confirmation, pane rhythm,
      Board drag jitter, reader zoom/width, and project bulk actions.

### Kastn Undo

Kastn-local slip undo/redo is landed as one ordered history over the existing
IPC, isolated from Zetl's undo stack.

- [x] Add a client-side undo history of inverse domain commands bound to in-app
      `Ctrl+Z` and isolated from the held-`Ctrl+Z` undo stack. All mutations
      route through a single `ExecuteMutationAsync` choke point; the planner and
      history are unit-tested.
- [x] Add gesture grouping (coalesce by slip id) and enable `AddSlip`, `MoveSlip`,
      and `ReorderSlip` undo. Divider insert, drag, the per-card edit dialog, and
      the batch loops each record as one entry.
- [x] Add redo via a symmetric undo/redo model, bound to `Ctrl+Y` (not
      `Ctrl+Shift+Z`, which would overload Zetl's Shift-lane undo hold shortcut).
- [x] One ordered history covering the editor: the slip editor's native TextBox
      undo is disabled, `Ctrl+Z`/`Ctrl+Y` there drive Kastn history (pending
      typing flushes in as one entry), and stacked same-slip entries re-thread
      their expected revisions so a run of undos walks the whole history.
- [x] Route undo conflicts through an Undo-anyway / Keep-newer-change choice: a
      conflicted operation opens a current-vs-target dialog (the panel's
      affordance, but visible in Board Mode too); apply-anyway re-issues against
      the record's current revision, declining keeps the newer change.
- [x] Bucket undo for rename/reparent/settings/heading/reorder, coalescing a
      drag's reparent + reorder into one entry and re-threading bucket revisions
      like the slip stacks. Bucket creation/deletion and project-level undo
      remain deliberately out of scope (their inverses need re-creation
      semantics).

The detailed design is in [`kastn-undo-roadmap.md`](kastn-undo-roadmap.md).

## Priority 3: Workspace Discovery

Find is a core Zetl feature (decided October 8, 2026): held `Ctrl+F` from any
app should be as quick as a quick note, so the search engine lives in the shared
core and Zetl, which is resident and already holds every project, answers it
with its own popup. Kastn's workspace search, Calendar, and a later timeline
view use the same engine. This is read-only work and can ship before
cross-project mutation or asset-store migration.

- [ ] Put a read-only workspace search engine in the shared core: project
      summaries and addressable slip hits, with Zetl the storage authority.
      Kastn reaches it over IPC rather than reading project files.
- [ ] Search slip title/body, project name, bucket path, and stable slip id.
      Search active, finished, and Journal projects by default; make archived
      projects opt-in.
- [ ] Held `Ctrl+F` opens a Zetl find popup over the app it was held in, without
      changing ordinary tap-`Ctrl+F` behavior in that app. It always searches
      the whole workspace, including over Kastn. Typing filters
      results live; each result shows project, bucket path, capture date, type,
      and a short matching snippet.
- [ ] Result actions: paste the slip into the app `Ctrl+F` was held in, copy it,
      or open it in Kastn with the exact project and slip selected.
- [ ] A setting chooses what `Ctrl+Enter` does on a result (paste, copy, or open
      in Kastn), like Compose's `Ctrl+Enter` setting; the other actions stay
      one key away.
- [ ] Kastn's workspace search runs on the same engine for browsing inside the
      workbench.
- [ ] Add a Calendar landing mode beside Projects. A local calendar day shows
      every project with activity that day (slips captured, the project
      created, or its details edited), with activity counts; a project may
      therefore appear on multiple days. `Zetl Logs` is hidden by default.
- [ ] Selecting a Calendar project opens it with the chosen day's filter active.
- [ ] Remember Projects or Calendar as the user's preferred Kastn landing mode.
- [ ] Measure query latency and memory across the existing 20,000-slip baseline
      before introducing a persistent index, and keep the popup's first results
      within capture-popup latency. JSON remains canonical unless the
      measurements show it is the limiting factor.

The held-shortcut background is in
[`hold-shortcut-ideas-discussion.md`](hold-shortcut-ideas-discussion.md#held-ctrlf-find-in-zetl).
Search, Calendar, and the timeline idea in [`ideas.md`](ideas.md) share one
query/result model.

Decided (October 8, 2026):

- Creating a project and editing its details count as Calendar activity, not
  only captured slips.
- `Zetl Logs` stays off the Calendar by default. (Moving the logs out of
  project storage altogether is under consideration; see [`ideas.md`](ideas.md).)
- Held `Ctrl+F` always searches the whole workspace, even over Kastn with a
  project open. Searching just the open project is a tapped `Ctrl+F` inside
  Kastn, which already focuses its project search.

## Priority 4: Project Board And Cross-Project Organization

After the read-only discovery layer, add a project-level board where projects
are columns, buckets are expandable cards, and slips can move or copy across
projects. This depends on explicit Zetl-owned cross-project transfer commands,
a workspace-level asset store, and cross-project wiki-link/export rules.

- [ ] Add a read-only Project Board projection over projects, buckets, and
      slips.
- [ ] Move live picture/file assets toward a workspace-level content-addressed
      asset store while keeping project exports self-contained.
- [ ] Add cross-project slip copy/move commands with active-project copy
      defaults.
- [ ] Add cross-project bucket copy/move commands, starting with leaf buckets
      and then bucket subtrees.
- [ ] Add "promote bucket to a new project" as the new-project destination case of
      `MoveBucketToProject` (reuses the existing subtree primitives; see the
      roadmap doc).
- [ ] Extend wiki-links to resolve cross-project targets while keeping
      single-project exports clean for Obsidian-style wiki-links.
- [ ] Add cohesive multi-project export rules for included linked material.

The detailed design is in
[`kastn-project-board-roadmap.md`](kastn-project-board-roadmap.md).

## Completed Foundation: Temporary Consumable Templates

Consumable templates already seed ordered Replay queues. A temporary consumable
instantiates a disposable project, activates it in a chosen lane, and is deleted
when it leaves that lane.

- [x] Add a `Temporary` template flag and validation.
- [x] Implement Zetl-owned disposal when the Replay queue empties, the lane is
      cleared, or another project replaces it.
- [x] Leave the lane inactive after disposal.
- [x] Add Kastn's Main/Alternate lane choice with optional remembered default.
- [x] Show the two lanes as stable landing cards and active temporary projects
      as progress-bearing overlays above their underlying durable lane project.
- [x] Include currently active temporary projects in compile/source pickers; the
      landing page makes their lane-bound, disposable status visible.
- [x] Decide disposal ordering relative to Replay's empty-queue Standard-mode
      transition.

The detailed lifecycle is in
[`kastn-templates-roadmap.md`](kastn-templates-roadmap.md).

## Priority 5: Project Lifecycle Polish

The durable lifecycle model is implemented, including lane clearing and
switching a quiet project off after the configured idle window.

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
