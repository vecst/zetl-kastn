# Kastn MainWindow Cleanup Audit

Source baseline: `codex/cleanup`, commit `d94d845`, reviewed October 3, 2026.
This is a source-level audit of all ten MainWindow partials, their XAML wiring,
existing collaborators, and relevant tests. It records cleanup candidates and
verification work; proposed behavior problems below have not been reproduced
with new tests during this audit. No application code changes accompany it.

MainWindow contains **10,609 lines of C#**, plus its XAML. The partial files split
the text, but they still share one object's state and transaction boundaries.
The next pass should establish ownership of selection and command context before
extracting undo or the renderers.

## Cleanup Progress

- Completed the first cleanup pass: removed `ReloadViews`, the two write-only
  fields, the slip-list wrappers and unused preview/detail formatting, and the
  inspector's unused filter argument. Made the template catalog load local and
  corrected the settings, bitmap-ownership, and seed comments.
- Validation: solution build with zero warnings/errors; 131 focused Kastn and
  Avalonia UI tests passed. The responsibility map below remains the historical
  audit baseline.
- Extracted `KastnSelectionContext` and `KastnCommandAvailability`: selection
  targets resolve against one snapshot, and toolbar updates reuse those targets
  for availability, alignment, list state, and inline formatting. Selected slips
  use indexed document positions instead of scanning the whole project.
- Moved window selection adapters into `MainWindow.Selection.cs`, including the
  detail-pane fallback previously housed in the creation-type partial. Existing
  deleted, structural, batch, and bucket-heading policies are preserved.
- Added coverage for snapshot ordering, command availability, toolbar selection
  transitions, and failed offline saves retaining the original dirty draft.
  Navigation orchestration and editor-safe formatting/picture mutations remain
  follow-up work.
- Validation after the selection extraction: solution build with zero
  warnings/errors; 548 tests passed, with the same two intentional latency-probe
  skips.
- Extracted `KastnEditorMutationAcceptance`, shared by ordinary saves and
  single-slip formatting/picture commands. It captures the editor session and
  draft, acknowledges matching early snapshots, validates response identity and
  revision, and preserves later typing/styles and newer remote conflicts.
- Formatting commands now carry dirty inline styles with the submitted text.
  Picture loading is separate from submission, which revalidates project,
  selection session, and the current target after the picker/file read.
- Added delayed real-IPC UI coverage for formatting and picture mutations,
  journal rebasing, and project switching with a reused slip ID; picker tests
  cover project changes and selecting away and back. Visibility loops and
  asynchronous link dialogs still need their own captured workflow context.
- Validation after mutation acceptance: solution build with zero warnings/errors;
  568 tests passed, with the same two intentional latency-probe skips.
- Added `KastnEditorWorkflowContext` for workflows spanning saves or dialogs.
  Web/slip-link dialogs check the original project, editor session, text, inline
  styles, and pending formatting intent after every prompt. They re-resolve the
  destination slip before applying a link; removed/deleted destinations cancel
  the edit. Unchanged drafts can survive an ordinary baseline save.
- Visibility changes capture the clicked action and target IDs before autosave,
  use indexed target resolution, and share editor-aware command construction and
  acceptance with formatting/pictures. Later typing and remote conflicts survive;
  navigation/reselection stops unsent commands, while already-sent changes remain.
- Added headless dialog interruption tests and delayed real-IPC visibility tests,
  including journal rebasing, project changes during the prerequisite save, and
  a newer remote conflict. Board edit dialogs and export context remain separate
  follow-up workflows.
- Visibility clicks still join an autosave already started by focus loss; they
  reject other overlapping mutations without clearing another workflow's busy
  state. Validation: solution build with zero warnings/errors; 605 tests passed,
  with the same two intentional latency-probe skips.
- Extracted `KastnViewExportOperation`: copy/export capture the project snapshot,
  a cloned view, filtered slips, list preference, and PDF page/font settings before
  awaiting any picker, asset load, clipboard write, or output stream. File options
  and completion messages also use the captured view/project.
- Export picture loads use the captured project and asset hash, sharing preview
  fetches while allowing validated export results to survive a preview-cache
  reset. Retired results do not repopulate that cache; preview/disposal guards
  remain active. Literal text formats skip picture fetching entirely.
- Added delayed picker, stream, and image-load tests across project/view/filter
  changes, PDF page/font checks, and real IPC navigation while copy shares an
  image fetch with the reader. Cancelled pickers and destination failures are
  covered. Window lifetime and board edit workflows remain follow-up work.
- Validation after export extraction: solution build with zero warnings/errors;
  625 tests passed, with the same two intentional latency-probe skips.
- Extracted `KastnBoardEditOperation` to own captured dialog values, original
  project/target identity, command construction, and confirmed revision threading
  from a column move into its content update. The window's board-edit adapter
  joins prerequisite autosaves, settles a queued saved snapshot, re-resolves
  stale card targets, and rechecks draft/session/revision and destination validity
  after the dialog. An interrupted move stops before sending the content update.
- Board save/move/delete responses now share editor-safe acceptance. Matching
  early move/deletion snapshots advance the baseline without losing later writing
  or styles; deletion acknowledges a lazily created Deleted bucket. A late delete
  only clears an unchanged original editor session. Removed the unused direct
  `AcceptEditorSaved` wrapper, and reject old-project undo records/gestures after
  navigation while retaining grouped move-and-edit undo/redo.
- Added delayed dialog and real IPC regressions for typing, formatting intent,
  reselection, reused IDs across projects, remote revisions/conflicts, offline
  transitions, prerequisite saves, early snapshots, recovery journals, and
  interrupted moves. Validation: solution build with zero warnings/errors;
  669 tests passed, with the same two intentional latency-probe skips. The next
  extraction target is edit-history ownership; window lifetime remains open.
- Extracted `KastnEditHistory`: it owns mutation recording, undo/redo stacks,
  gesture accumulation, inverse execution, revision threading, conflict decisions,
  and reconciliation/repair of partial or uncertain outcomes. It receives snapshot
  supply, transport, refresh deferrals, and an async conflict callback. MainWindow
  keeps autosave, tree selection, native dialogs, and status presentation; its undo
  partial is now about 90 lines instead of roughly 800.
- Gesture context is scoped to each history owner and async flow. A history
  generation retires pending recordings and unsent inverse commands after project
  navigation or a server restart, including a switch away and back to the same
  project. Transient reconnection to the same server retains checked entries.
  Unconfirmed/malformed responses do not advance history; inverse outcomes can
  reconcile into repair entries without discarding the original transaction.
- Rendering tracks its own server lifetime so history reconciliation observing a
  restart early does not suppress later UI cache invalidation. Undo completion
  preserves later typing and avoids restoring selection/status into a different
  editor session; navigation during prerequisite autosave cancels the step.
- Added service-backed owner tests for gesture isolation/grouping, sequential
  undo/redo, redo invalidation, slip/bucket conflict choices, best-effort position
  failures, interrupted and unknown-outcome repair/retry, busy rejection, response
  identity/revision checks, and generation invalidation. Delayed real-IPC UI tests
  cover typing, reselection, and project changes during undo or its initial save.
  Validation: solution build with zero warnings/errors; 706 tests passed, with
  the same two intentional latency-probe skips. Drop planning and move execution
  are the next extraction target.

## Responsibility Map

| Partial | Lines | Responsibilities currently mixed together |
| --- | ---: | --- |
| `MainWindow.axaml.cs` | 1,967 | Construction and event wiring; snapshot application; landing catalogs and cards; tree reconciliation and selection restoration; bucket fields; filter state; connection/status UI; shortcuts; shutdown; board layout; presentation records |
| `MainWindow.Workbench.cs` | 2,254 | Editor adapter; formatting and links; picture import; project/bucket/slip commands; selection interpretation; command availability; metadata and preview text |
| `MainWindow.Views.cs` | 2,414 | Reader rendering; markdown/inlines and typography; inspector; picture loading; settings cache; clipboard/export; view catalog and editor persistence; board reconciliation, controls, composer and editing |
| `MainWindow.DragDrop.cs` | 1,101 | Pointer gestures and hit tests; tree/board auto-scroll; drop rules; multi-command moves; revision threading; selection restoration |
| `MainWindow.Undo.cs` | 795 | Mutation interception; async gesture recording; undo/redo stacks; inverse execution; conflict decisions; interruption repair; status and selection |
| `MainWindow.Templates.cs` | 692 | Template catalog actions; draft editing and bucket fields; parsing seed cards; template persistence; project creation, seeding and temporary-lane choice |
| `MainWindow.ViewEditor.cs` | 595 | Section draft model; section controls and drag/reorder; bucket choices; preview rendering; calls into the reader's content helpers |
| `MainWindow.CreationTypes.cs` | 287 | Creation-type catalog actions and draft editor; template/view resolution; persistence; the unrelated `SelectedSlip` property |
| `MainWindow.BatchFormat.cs` | 275 | Toolbar routing for single/batch/bucket selection; typography and block-property commands; batch execution and status |
| `MainWindow.DraftRecovery.cs` | 228 | Journal debounce and durability; save acceptance; startup restoration; unsaved-close decisions; window-close completion |

## Confirmed Stale Code

References were checked in application code, XAML, and test code. These are
narrow cleanup candidates, distinct from the active behavior listed later.

| Candidate | Evidence | Cleanup |
| --- | --- | --- |
| `ReloadViews` (`Views.cs:1477`) | Private method has no callers or XAML/test references. Its work is already done directly through `RefreshViewCatalog` and `RefreshViewer`. | Delete the method and its comment. |
| `editingTemplateIsNew` (`axaml.cs:69`) | Assigned on template open and reset on close; never read. The `isNew` parameter itself is still used to choose the editor title. | Delete the field and assignments; retain the parameter. |
| `inspectedSlipId` (`axaml.cs:235`) | Written in `RefreshSlipInspector`, `InspectSlip`, and selection clearing; never read. | Delete the field and assignments; retain inspector rendering. |
| `SlipListItem.Text` and `.Detail` (`axaml.cs:1875`) | The `slips` collection has no UI binding. Its consumers only select by `.Id` and read `.Slip`. Preview text and bucket/source/session strings are rebuilt without a consumer. | Replace the presentation wrappers with the filtered snapshot list. `SlipPreviewText` can then disappear if it still has no other caller. |
| Board bitmap ownership comment (`Views.cs:1487`) | Says a card owns and disposes its bitmap. `BoardCardUi` has no bitmap ownership or disposal; decoded images now belong to `KastnPictureCache`. | Correct the comment alongside renderer ownership cleanup. |
| Settings comment (`Views.cs:1032`) | Claims settings are read fresh at every use and that reads are infrequent; the implementation immediately below uses a one-second cache for hot paths. | Consolidate the comments when extracting settings access. |
| Seed comment (`Templates.cs:646`) | Claims capture templates have no seeds and skip seeding; the method tests bucket contents and does not exclude capture templates. | Describe the actual empty-bucket skip, or verify the intended product rule before changing behavior. |

### Active Code That Looks Stale But Needs A Behavior Decision

- `saveSlipMenuItem` and `deleteSlipMenuItem` remain declared in XAML and wired
  to actions, but `SetEditingEnabled` always disables both. Decide whether the
  menus should work or be removed. Ctrl+S also has a separate keyboard path;
  deleting menu items is not equivalent to deleting that shortcut.
- `batching` still has real writers in batch formatting and slip dragging and a
  reader in `OnSnapshotChanged`. It overlaps the newer controller deferral
  mechanism, but deleting it without checking project/server notifications and
  deferred snapshot ordering would change behavior.
- `boardSlipCards` duplicates each `boardCards[id].CardBorder`;
  `boardColumnCardPanels` duplicates each `boardColumns[id].CardsPanel`. Both
  still have readers. Consolidate their ownership in a board presenter rather
  than classifying them as unused dictionaries.
- `loadedTemplates` only feeds `RebuildTemplateCards`; it can be local. The
  catalog itself must remain available to creation types and template editing.
- The parameterless constructor may serve Avalonia tooling. Check that role
  before deleting it even if production construction injects a connection.

## Proposed Ownership Boundaries

The names below are proposals. Extract responsibilities with independent
invariants, not one new class for every group of methods.

### Selection, Navigation, And Availability

Keep `KastnSelection` as the selection value. Add a derived selection context
containing the current project/index, selected IDs in document order, editor
target, bucket-title target, and filter values. Compute it once for a UI update.
An availability function can return the edit/move/delete/format states consumed
by the controls.

A selection/navigation controller should own pending selection and focus
requests, save-before-leaving, failed-save selection restoration, and the
tree/reader/board selection bridge. UI controls remain adapters.

Evidence: `CurrentSelection`, `SelectedSlips`, `TitleModeBucket`, and the toolbar
update methods repeatedly derive the same state. Selection can also mean an
explicit tree item, an editor fallback, or a pending newly created item. The
unrelated `SelectedSlip` property lives in the creation-type partial.

Checks: single/multiple/bucket/deleted selections, cross-bucket batches, filter
changes, new-slip focus, failed saves, and selection changes during IPC.

### Editor-Aware Mutation Coordinator

Generalize the captured-draft/revision approach in `KastnEditorSaveOperation` to
other editor-affecting mutations. Capture project identity, target, revision,
submitted draft/styles, and whether the command changes text. Accept responses
against that context, preserving later edits and newer authoritative revisions.
Keep connection retry/deduplication in `KastnConnectionController` and durable
state ownership in Zetl.

The window should build intents and display outcomes. The coordinator should
provide consistent busy scopes, refresh deferrals, and typed mutation outcomes.
Operation-specific workflows can use it without reproducing the guard/catch/
refresh/accept sequence.

**Gap identified at the audit baseline, now addressed:** `SendSlipCommandAsync`
called `AcceptEditorSaved` for text-carrying formatting actions without
capturing later typing. Picture commands preserved drafts, but lacked the save
operation's selection-version and response-order checks. Both paths now use
the shared acceptance component described in Cleanup Progress.

Checks: delayed formatting/picture responses, typing and selection changes
during them, early snapshots, offline transitions, and journal rebasing.

### Edit History And Move Workflows

An edit-history component should own undo/redo stacks, mutation recording,
gesture accumulation, inverse execution, revision threading, and repair
outcomes. Inject command execution and an async conflict-decision callback.
Keep selection, status strings, and dialogs at the window boundary.

Preserve the existing AsyncLocal gesture isolation: an unrelated UI action
must not join another async gesture. Inverse commands must bypass recording,
and a changed server instance must invalidate history. Reuse `KastnUndoPlanner`
and `KastnUndoHistory` rather than replacing their models.

A drop planner should accept IDs, snapshot/index data, and a resolved drop
position, returning a plan without `DragEventArgs`, controls, or tree nodes.
Tree/board adapters retain hit testing, markers, and pointer timers. A move
executor owns move/reorder sequences, partial results, and threaded revisions.

Checks: cross-bucket multi-drag order, self/descendant/Deleted rejection, bucket
reparent-plus-reorder as one gesture, conflicts, partial failures, disconnects,
and unknown command outcomes.

### Reader, Board, And Shared Slip Content

Reader and board presenters should own their respective live controls, reuse
caches, highlights, layout reconciliation, and picture-load validity checks.
A shared slip-content renderer should own markdown blocks, inline runs,
typography, captions, and list-marker presentation used by the reader, board,
and view-editor preview. Pass settings as render inputs and route actions by ID.

The board presenter can replace its parallel card/panel lookup dictionaries
with access to its owned card and column records. Its composer draft should
remain separate from card reconciliation so snapshots do not erase writing.

Keep decoded bitmap ownership in `KastnPictureCache`. Eviction requires a way
to determine which controls still display a bitmap; removing dictionary entries
and disposing displayed images is unsafe. Measure large-project rendering and
memory before choosing viewport virtualization or a bitmap retention budget.

Checks: identity reuse on selection/reorder, moves across columns, picture loads
finishing after rebuild/reset, composer survival, scroll/highlight restoration,
and preview/reader content parity.

### Catalog And Document Editors

Template, view, and creation-type editors should each own their working draft,
baseline/dirty detection, validation, and editor UI state. Extract the view
editor's persistence code from `Views.cs` together with its section editor.
Reuse the existing document stores, defaults/cloners, and validators.

Share small clone/baseline/discard helpers where appropriate. Keep view scope
changes explicit: project/global persistence has different failure ordering
from local template and creation-type saves.

A project-creation workflow should own template-to-project commands, seeding,
temporary-lane configuration, and default-view assignment. Return separate
outcomes for project creation, seed failures, and view assignment. The current
default-view response is not checked before reporting project creation success.

Checks: built-in editing creates a copy, cancel leaves stored data intact,
validation failures keep the draft open, view scope migration failures,
repeated catalog refresh, ordered seeds, and partial project creation.

### Export And Window Lifetime

An export operation should capture a project snapshot, selected view clone,
visible slips, and preferences before awaiting a picker or picture loads. Use
the same captured inputs through rendering and writing. Clipboard/export UI
remains a window adapter.

Evidence at the audit baseline: `CopyRenderedViewAsync` and
`ExportRenderedViewAsync` read mutable window state at different points across
awaits. This gap is now addressed by `KastnViewExportOperation` and delayed
project/snapshot-change coverage. HTML/PDF still render with fetched picture
contents rather than reusing cached preview text.

Give window-owned subscriptions, queued snapshot application, debounce/busy
timers, and drag timers an explicit close lifecycle. The current `Closed`
handler unsubscribes snapshots and disposes the picture cache, but does not
stop those timers or invalidate already queued UI callbacks. Hiding to tray
must remain distinct from closing and disposing.

Checks: close/hide with pending timers, queued snapshots and picture loads;
offline close/recovery; activation and coordinated Zetl shutdown.

## Optimization Candidates

1. Remove unused slip-list presentation work before adding more caches.
2. Derive selection and availability once per UI update. Reuse project indexes
   for target resolution instead of repeatedly scanning all slips and buckets.
3. Use one settings provider. `IsUntitledKastnSlip` still constructs a settings
   store directly, bypassing `CurrentAppSettings`' hot-path cache. Preserve
   external-change detection and test path overrides when consolidating it.
4. Replace the repeated linear parent lookups in `IsDescendant` with indexed,
   cycle-safe traversal. Keep drop validation at command execution as well as
   pointer planning because the project can change between them.
5. Measure `SyncPanelChildren` on large reorder/filter changes. Its unchanged
   sequence has a cheap fast path; changed sequences repeatedly call `IndexOf`
   and can incur quadratic work. Optimize from a representative measurement.
6. Coalesce/debounce view-editor preview updates if measurement warrants it.
   It rebuilds the preview on metadata edits; it already limits input to 100
   non-deleted slips, so it is not an unbounded whole-project preview.
7. Review seed batching and picture-fetch concurrency after correctness and
   ownership are settled. Preserve command order and avoid unbounded loading.

## Recommended Commit Sequence

1. Remove the three confirmed dead members and unused slip-list presentation;
   correct stale comments. Build and run focused Kastn/UI tests.
2. Capture selection context and centralize availability/navigation decisions.
   Characterize failed-save restoration and async context changes first.
3. Extend editor-safe acceptance to formatting/picture mutations; capture
   export context. Land behavior fixes with delayed-response regressions.
4. Extract edit-history recording/execution, then drop planning and move
   execution. Keep gesture isolation and interruption repair intact.
5. Extract shared slip content and reader/board presenters. Keep controls and
   cache identity stable before introducing memory/layout optimizations.
6. Extract catalog editor sessions and project-creation workflow, preserving
   each store's persistence ordering and surfacing partial outcomes.
7. Finish window lifetime/settings ownership and profile representative large
   projects for the remaining performance work.

MainWindow should ultimately compose these owners, wire XAML events, apply UI
effects, and handle native window/clipboard/picker interactions. Moving methods
into more partials or a helper that still reads every window field would not
establish these boundaries.

Existing automated baseline at the reviewed commit: solution build with zero
warnings/errors; 537 passing tests and two intentionally skipped latency probes.
This audit adds documentation only, so that baseline was not rerun.
