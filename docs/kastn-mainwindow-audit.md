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
- Extracted `KastnDropPlanner` and `KastnMoveOperation`: drop plans carry project,
  source, dragged IDs, placement anchors, and marker IDs rather than live tree
  nodes. Tree/board adapters retain pointer hit tests, feedback, and scrolling.
  Indexed placement rules reject Deleted targets, invalid anchors, self/descendant
  nesting, and malformed parent cycles. Multi-slip order is captured before
  autosave, after-slip placement skips dragged anchors, and unchanged placements
  avoid mutation commands. Parent choices now use indexed, cycle-safe ancestry.
- Move execution captures fresh target revisions and bucket properties after
  autosave, checks workflow and destination validity before unsent commands, and
  threads only confirmed response revisions. Typed outcomes preserve partial
  completion, conflicts, interruptions, and uncertain results. A drop remains
  one gesture; confirmed work stays undoable when a later step fails.
- Drop responses and matching early reorder snapshots share editor-safe
  acceptance, preserving later typing/styles and rebasing recovery journals.
  Refresh deferral coalesces ordinary mutation events while allowing navigation
  snapshots through. The synchronized snapshot is applied before gesture disposal
  to record final neighbours reliably; delayed autosave tests reproduced and
  fixed stale positions that previously broke redo ordering. Bucket selection
  settles immediately after synchronization.
- Added pure planner, service-backed execution/undo, and delayed real-IPC UI
  coverage for tree/board placements, multi-drag ordering, fresh bucket properties,
  partial failures, malformed responses, disconnection/unknown outcomes, draft
  preservation, captured IDs, changed anchors, reused IDs across projects, and
  grouped undo/redo. Validation: solution build with zero warnings/errors;
  753 tests passed, with the same two intentional latency-probe skips. Shared slip
  content and reader/board presenters are next; window lifetime remains open.
- Extracted `KastnSlipContentRenderer`: it owns Markdown blocks/inlines, authored
  typography, picture captions, hanging list markers and task interactions, and
  compact board text/footer markers. Reader and view-editor preview share rich
  content; board cards retain their existing three-line plain-text presentation.
  The renderer receives an indexed snapshot, theme resources, and ID callbacks.
  MainWindow supplies settings/placement and guards callback project/generation;
  stale links and checkboxes cannot act in another project or a revisited session.
- Wiki resolution now uses indexed ID lookups instead of scanning every slip for
  each inline link. Rendered content retains only its actual wiki dependencies;
  losing or restoring a target invalidates its source reader blocks while
  unrelated controls retain identity. Target content changes preserve authored
  cached link labels and do not rebuild those source blocks. Live view previews
  capture the list-kind preference once per render rather than once per slip.
- Removed about 350 lines from the window partials. Picture loading, bitmap cache
  ownership, reader/board reconciliation, selection/highlights, scroll restoration,
  and the board composer still belong to their existing adapters; presenter
  ownership is the next extraction. Corrected the remaining board thumbnail
  comment that incorrectly described its decoded bitmap as card-owned.
- Added headless rendering regressions for Markdown/whole-note kinds, inline
  ranges, typography, link styling/resolution, task click routing, compact cards,
  captions, reader/live-preview parity, selective wiki dependency invalidation,
  and retired callbacks across project changes and return navigation. Validation:
  solution build with zero warnings/errors; 768 tests passed, with the same two
  intentional latency-probe skips. The reader presenter is next, followed by the
  board presenter; window lifetime remains open.

- Extracted `KastnReaderPresenter`: it owns reader grouping, heading and block
  reuse, reconciliation, selection highlights, scroll restoration, and picture
  assignment. MainWindow supplies captured render inputs, a cloned view, theme
  resources, and project/generation-guarded ID callbacks. The shared picture cache
  retains bitmap ownership; `KastnPanelReconciler` now serves reader and board.
- Pending pictures now follow their live block rather than a global render
  generation. An unrelated rebuild no longer strands a reused picture block at
  "Loading picture…". Replaced, filtered, cleared, and disposed blocks cannot
  receive late assignments. Retired reader controls cannot navigate or toggle
  tasks, and queued scrolling checks the latest selection and control lifetime.
- Removed about 400 lines from MainWindow, including its reader caches and
  picture generation. Added headless coverage for control identity across
  grouping/reorder, scroll preservation/clamping, empty views, delayed pictures,
  retired actions, queued scrolling, and reader/board switching. Validation:
  solution build with zero warnings/errors; 792 tests passed, with the same two
  intentional latency-probe skips. The board presenter is next; broader window
  lifetime cleanup remains open.

- Extracted `KastnBoardPresenter`: it owns columns/cards, reconciliation, picture
  expansion, selection highlights, scroll preservation/clamping, and independent
  per-column composer drafts. Captured inputs include settings, theme resources,
  indexed snapshots, live node contexts for drop overlays, and ID callbacks.
  Removed the parallel card-border/panel lookup dictionaries; drag hit testing
  accesses owned column records. Native drag gestures and project mutations stay
  in `MainWindow.BoardPresentation.cs` and their existing workflow adapters.
- Composer completion now clears only the submitted draft version. Later typing
  and discard/reopen survive a delayed response; overlapping submits are rejected,
  failed submissions retain the text, and completion does not steal focus. Add
  commands and completion status stay scoped to the captured project/generation.
  Retired columns cannot submit, and retired cards cannot select, edit, toggle
  tasks/picture expansion, or drive native drag handlers. Queued scrolling and
  picture assignments check live control identity and presenter lifetime.
- Removed about 690 net lines from MainWindow; its Views partial is now 729 lines.
  Added headless regressions for reuse/reorder/cross-column moves, marker contexts,
  settings invalidation, scroll restoration, delayed pictures, composer survival,
  and mode switching/close. Delayed real-IPC composer tests cover original-project
  targeting, early snapshots, newer writing, navigation, focus, and undo recording.
  Validation: solution build with zero warnings/errors; 832 tests passed, with
  the same two intentional latency-probe skips. View catalog/editor ownership is
  the next extraction target; broader window lifetime cleanup remains open.

- Fixed cross-bucket navigation after autosave: an early save snapshot could mark
  the old draft clean, then the new bucket's filter loaded its first slip before
  save acceptance finished. That changed the editor session and caused the click
  handler to restore the previous row despite a successful save. A scoped pending
  selection now keeps the saved editor session until acceptance, then applies the
  captured clicked IDs. Newer clicks, typing, and project/server changes cannot
  be overwritten by an older completion. Save failures retain the original draft.
- Added delayed real-IPC regressions for same/cross-bucket clicks, joined focus
  autosaves, non-first targets, early snapshots, later selections/typing, and
  project changes with reused IDs. Validation: solution build with zero
  warnings/errors; 838 tests passed, with the same two intentional latency-probe
  skips. View catalog/editor ownership remains the next extraction target.

- Unified equivalent tree drop edges: below the previous slip and above the next
  now share one insertion marker. Crossing that gap no longer toggles marker
  flags on different rows. Explicit board marker hints retain visible card edges.
- Shared hover/release resolution retains the last valid slot through actual
  spacing, but clears it on unchanged or invalid targets. This prevents an old
  slot from remaining active while hovering over a no-op position.
- Added pure single/multi-slip placement tests and headless routed tree-hover
  regressions for stable markers, spacing, and stale-target clearing. Validation:
  solution build with zero warnings/errors; 842 tests passed, with the same two
  intentional latency-probe skips. View catalog/editor ownership remains next.

- Reproduced the remaining tree-hover flash using Avalonia's platform drag
  device and real hit testing: crossing a row's child controls emits leave/enter
  pairs. The tree previously cleared its marker on every leave and only resolved
  drag-over, leaving the marker absent until another pointer movement.
- Tree enter now resolves feedback immediately. Leave cleanup runs after the
  paired enter and checks a feedback version, preserving unchanged markers and
  newer targets while still clearing a real tree exit. Added horizontal/vertical
  pointer sweeps and exit/reentry regressions. Validation: solution build with
  zero warnings/errors; 846 tests passed, with two intentional latency-probe
  skips. View catalog/editor ownership remains the next extraction target.

- Extracted `KastnViewCatalog`, `KastnViewEditorDraft`,
  `KastnViewEditorPresenter`, and `KastnViewPersistence`. The catalog owns indexed
  global/project merging, scope lookup, defaults, and metadata-based reloads;
  leaving a project removes its private views from the picker. The draft owns
  independent fields/sections, saved baselines, and captured project identity.
  The presenter owns controls, section styling/reordering, and bounded previews.
- View writes capture documents and project revisions before awaiting, validate
  response identity and authoritative contents, and preserve durable copies on
  failed scope migration. Saves retain later writing/sections and newer editors;
  navigation or a retired project session prevents stale completion UI. Changed
  or removed remote views cannot silently overwrite their replacement. Delete
  and discard confirmations retain their original targets and protect later
  drafts, selections, reused IDs, and newer global files.
- Section callbacks and preview links retire with their controls/session.
  Unchanged previews reuse their controls; section drops that preserve ordering
  avoid rebuilding. Global draft captures retain unknown document/section fields.
  Copy/export continue to capture the picker's actual selected document. Removed
  about 750 net lines from MainWindow; its Views partial is now about 410 lines.
- Added pure/service-backed scope, catalog, draft, failure, and response tests,
  plus headless and delayed real-IPC authoring regressions. Validation: solution
  build with zero warnings/errors; 883 tests passed, with the same two intentional
  latency-probe skips. Broader window lifetime and shutdown ownership is next.

- Fixed Kastn's missing executable icon and PDF task exports. The executable and
  window now share a multi-resolution K icon. Exported task checkboxes preserve
  their checked state and have editable, printable AcroForm widgets with explicit
  on/off appearances. Save/reopen, layout, and authored-task regressions passed;
  rendered samples were inspected before and after toggling. Validation: clean
  solution build; 888 tests passed, with two intentional latency-probe skips.

- Extracted `KastnWindowLifetime` and `KastnApplicationLifetime`. Window close,
  tray hiding, coalesced preparation, coordinated-shutdown approval, and retirement
  now have explicit owners. App composition supplies native/UI actions, connection
  teardown, and theme-watcher cleanup; Program retains the control server and mutex.
- Shutdown approval holds the close gate until the pipe reply is flushed. A lost
  requester releases that approval; disposing the server cancels a pending UI
  decision instead of waiting forever. App retirement detaches all control handlers
  and prevents queued activation/confirmation callbacks from touching old resources.
- Window retirement stops draft/save/drag timers, unsubscribes snapshots, retires
  edit-history generations, and releases presenters and pictures once. Queued
  snapshots, focus restoration, theme changes, and save completions check lifetime.
- Unsaved-close decisions capture their editor session, draft, revision, conflict,
  and project/server generation. Stale answers cannot discard a different draft;
  keeping recovery requires a successful write of the latest draft. Native-close
  focus loss cannot start a new autosave after an explicit discard.
- Added owner, named-pipe, headless UI, and delayed real-IPC regressions for repeated
  requests, retry/cancel/error paths, reply ordering and requester loss, recovery
  choices and failed journal writes, interrupted drafts/saves, resource cleanup,
  and retired callbacks. Validation: clean solution build; 925 tests passed, with
  the same two intentional latency-probe skips. Settings ownership is next, followed
  by template/creation workflows and measured performance work.

- Extracted `KastnSettings`, shared by startup, MainWindow, and the theme watcher.
  It owns the resolved settings path and cached snapshots, invalidates immediately
  on local saves, and refreshes external changes through file events or a monotonic
  one-second fallback. Window/test contexts no longer share a static cache.
- Untitled-slip detection, lane/default-view preferences, autosave/close policy,
  preview, and captured exports use that owner. Standalone tree/PDF helpers use
  deterministic defaults; application calls supply their captured preferences.
- Remembering a temporary template lane merges into the latest settings file at
  save time. Failed reads/writes preserve the existing file and cached snapshot;
  corrupt settings cannot be replaced with defaults by this selective write.
- Added 19 owner and headless UI regressions for cache reuse/expiry, immediate
  invalidation, path isolation, external refresh, close policy, relevant theme
  events, delayed preference changes, and failed/corrupt writes. Validation: clean
  solution build; 944 tests passed, with the same two intentional latency-probe
  skips. Template/creation workflows are next, followed by measured performance.

- Extracted `KastnProjectCreationWorkflow`, shared by template cards and creation
  types. It captures the template and completion preference, owns temporary-lane
  resolution/remembering, creates through Zetl, seeds in listed bucket/card order,
  and validates the default-view assignment. A busy gate includes final navigation.
- Creation, seed, and view outcomes remain separate. Failed independent fields
  do not prevent later seeds; transport interruption stops unsent work. Missing or
  mismatched confirmations produce partial results without recreating the project
  or retrying fields. Setup warnings keep the window visible and survive unchanged
  refreshes; mutations/navigation clear the scoped completion notice.
- `MainWindow.ProjectCreation.cs` supplies dialogs, editor-save preparation,
  session validity, navigation, and native minimize effects. Changed projects,
  server sessions, editor selection/drafts, pending navigation intent, and retired
  windows invalidate older answers/completions. Ordinary landing refreshes remain
  valid; later writing during a prerequisite save is retained.
- Added 53 owner, headless UI, and delayed real-IPC checks for ordered seeds,
  capture/temporary modes, cancellation, concurrent starts, captured drafts,
  remembered preferences, partial/uncertain replies, view conflicts, interrupted
  saves, navigation, and retirement. Validation: solution build with zero warnings
  or errors; 997 tests passed, with the same two intentional latency-probe skips.
  Template/creation editor ownership remains next, before measured performance.

- Extracted `KastnTemplateEditorPresenter`, `KastnCreationEditorPresenter`, and
  `KastnCatalogEditorSession`. Presenters own isolated drafts, field capture,
  bucket selection/order, and editor controls. The shared session owns dirty
  baselines, validation, stable IDs across retries, and successful-write acceptance.
  MainWindow supplies stores, dialogs, catalog refresh, and visibility effects.
- Save, cancel, and bucket selection capture current controls before buffered
  text events dispatch. Discard answers check both session identity and captured
  content; later writing, replacement editors, and retired windows remain intact.
  Switching editors closes the previous owner, preventing snapshots from restoring
  an inactive authoring session.
- Metadata edits preserve legacy seeds, multiline cards, independent bucket
  defaults/review routing, missing catalog references, additional view priorities,
  and unknown document fields. Explicit card/view changes still replace those
  fields. Built-in editing creates independent copies; failed saves retain drafts.
- Removed nearly 400 lines from MainWindow and redundant edit-time cloning.
  Added 38 session and headless UI regressions for preservation, buffered edits,
  stale confirmations, editor switching, built-in copies, validation/write retries,
  and bucket structure changes. Validation: solution build with zero warnings or
  errors; 1,035 tests passed, with the same two intentional latency-probe skips.
  Representative performance measurement is next.

- Investigated the first Zetl tray right-click delay independently of Kastn.
  A fresh Windows probe reproduced 1.2–1.7 seconds of cold menu initialization.
  An unshown startup layout warm-up reduced the representative first-render
  measurement to 304 ms, with no menu actions or focus changes. Remaining frame
  initialization still has a first-use cost. See `tray-menu-latency.md` and the
  isolated `tools/TrayMenuProbe` harness for measurements and reproduction.
  Validation: solution and probe builds with zero warnings/errors; 1,038 tests
  passed, with the same two intentional latency-probe skips.

- Extracted `KastnNavigationCoordinator` after the viewport/performance passes.
  It owns pending bucket/slip requests, one-shot editor focus, tree restoration,
  editor-binding decisions, and save-before-leave intent. MainWindow applies
  selection, panes, and native focus; shared saves and project transport retain
  their existing owners. Reader, inspector, board, undo, and drop selection use
  the same window adapter.
- Project cards, close, tray activation, and prepared creation/deletion handoffs
  use the navigation owner. Activation now saves before changing projects.
  Older save completions cannot override a later project/tree choice, later
  typing, replaced editor/project/server sessions, or a retired window. Failed
  saves preserve the editor; pending focus resets across project/server scopes.
- Added 25 coordinator and UI cases, including delayed real-IPC saves, early
  snapshots, competing project/tree requests, failed offline/conflict saves,
  retirement, focus consumption, and selection fallback. Validation: solution
  build with zero warnings/errors; 1,156 tests passed, with the same two intentional
  latency-probe skips. Remaining orchestration
  work: shared mutation/busy coordination, snapshot application ordering, then
  landing-page ownership and the stale-menu sweep.

- Extracted `KastnMutationCoordinator`: exclusive write leases, workflow
  reservations, coalesced editor saves, command admission, retirement, and scoped
  completion notices now have one owner. MainWindow retains the native 150 ms
  visual delay. A completion only releases its own lease; synchronous save
  reentry joins the already-published task. Undo/redo participates in write
  ownership, and template creation uses the shared command boundary.
- Extracted `KastnSlipMutationBatch` for formatting, move, and delete. Targets,
  revisions, source project and document order remain captured across awaits;
  navigation/selection/session changes stop unsent commands. Partial conflicts
  are counted, completed commands remain undoable, and later move/delete typing
  retains its draft and updated baseline. Restore, new-slip, and divider
  prerequisites also validate their source session before sending commands.
- Removed the window's volatile snapshot-dropping flag. Connection-owned refresh
  deferral coalesces ordinary mutation events while explicit/navigation snapshots
  still apply. Scoped completion notices keep partial failures visible through
  queued snapshots for the same unchanged project.
- Added 36 owner and headless UI cases for save reentry/retry, exclusive writes,
  retirement, captured batch order, delayed real-IPC commands, early snapshots,
  partial failure, competing selection/navigation, later drafts, and stale save/
  confirmation prerequisites. Validation: solution build with zero warnings/errors;
  1,192 tests passed, with the same two intentional latency-probe skips.
  Snapshot application ordering is next, followed by
  landing-page ownership and the stale-menu sweep.

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

Editor ownership is complete: view authoring uses its dedicated draft, presenter,
and persistence owner; template and creation authoring use dedicated presenters
and a shared local-document session. Their window adapters retain catalog actions,
dialogs, and visibility. Headless tests protect preserved fields, save retries,
buffered text capture, and delayed discard answers across editor changes.

Share small clone/baseline/discard helpers where appropriate. Keep view scope
changes explicit: project/global persistence has different failure ordering
from local template and creation-type saves.

The project-creation workflow extraction is complete: `KastnProjectCreationWorkflow`
owns template-to-project commands, ordered seeding, temporary-lane configuration,
and default-view assignment. It returns separate creation/seed/view outcomes and
validates confirmations before reporting complete setup. The unchecked default-view
response at the audit baseline is now covered by owner and real-IPC UI regressions.

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

The first large-project rendering pass is complete. A dedicated tree items panel
preserves actual row containers during collection moves, avoiding template
recreation and logical detachment. Headless full-reversal measurements improved
from 5.2 seconds to 330 ms at 1,000 slips and 18.8 seconds to 754 ms at 3,000.
Initial realization and edit-driven layout remain follow-up work; see
[`kastn-rendering-performance.md`](kastn-rendering-performance.md) for the probe,
allocation measurements, limits and proposed next targets.

The next pass defers hidden Details rendering and backlink indexing, skips
unchanged selection resets, and reduces each tree row from seven icon paths to
two with shared geometry. Initial allocations fell 23–25% in the probe;
single-edit allocations fell 86–87% in its backlink-heavy project. Initial
loading, large visible backlink lists and document filtering still need viewport
work. Metadata/selection freshness, theme-aware icons and drag feedback have UI
regression coverage.

The reader viewport pass is complete. Large documents use lightweight note rows
and realize controls near the viewport within each group. Selection, logical
scroll anchors, ordered runs, picture ownership and retired actions have focused
UI coverage. The probe realized 24 of 1,000 and 15 of 3,000 reader blocks;
whole-window initial allocations fell 22–30% and filter allocations 93–94%.
Eager reader headings/groups and visible backlinks are
remaining targets. See the performance document for timing variation and the
allocation tradeoff when reconciling reorders.

The tree and board viewport pass is complete. Large trees flatten the expanded
hierarchy into a bounded fixed-height panel while keeping logical selection,
keyboard navigation, collapse state and visible control identity. Large boards
realize variable-height cards near vertical viewports and release cards in
distant horizontal columns. Column shells/composers retain drafts; logical
anchors retain scroll positions. Native drag hit testing, offscreen drop
successors, unsaved edits, range selection and late pictures have UI coverage.
Full projection/row-model reconciliation, eager bucket shells and visible
backlink lists were the remaining costs. See the performance document for the
before/after probe and its limits.

The inspector viewport pass is complete. `KastnInspectorPresenter` owns metadata
controls and guarded actions, reconciles unchanged fields/buttons, and bounds
long backlink lists to the viewport. Logical Tab navigation and scroll anchors
cover unrealized sources. Rendering/index queries defer while Editor or Board
hides Details. A corrected probe measures an actually visible right pane;
earlier Details timings measured eager construction while board mode hid it.
At 3,000 slips, visible edit allocation fell from 240,808 to 13,335 KiB and its
observed median from 2,130 to 44 ms. Full snapshot/index reconciliation and
picture-heavy rendering remain follow-up measurements.

The first incremental snapshot/index pass is complete. Snapshot indexes share
unchanged membership/parent IDs while returning fresh snapshot objects. A lazy
immutable backlink owner reuses parsed sources and reverse edges, including
unresolved links, and rebuilds affected target lists. Text edits parse one source;
title-only changes reuse tokens. Captured indexes retain their original results,
and project/server changes reset reuse.

Content-only tree updates adopt revisions, labels and visibility counts without
temporary full hierarchies or viewport reattachment. Structural changes keep the
full reconciliation path. Seventeen owner/UI cases include wire snapshots, mixed
edit comparison against the full backlink builder, cache isolation, hierarchy
fallbacks, server restart, focus and dirty drafts. At 3,000 slips, tree-refresh
allocation fell from 2,906 to 26 KiB and visible Details edits from 13,325 to
6,640 KiB. First Details open retains extra parsed state and allocates about
794 KiB more. Complete snapshot scans, filtered/render row models, many-bucket
shells and picture-heavy memory/loading remain measurement targets; see the
performance document for the timing limits and first-open tradeoff.

The first picture-performance pass is complete. Reader/board controls hold leases
on shared decoded bitmaps and clear their sources before retirement. A 64 MiB soft
budget evicts idle images while protecting displayed images; reset/close retain
outstanding leased bitmaps until release. Misses decode through one background
slot, with cache rechecks and cancellation/generation guards. Picture-heavy views
activate viewport rendering at 16 pictures, including board dual-image cards.
Fifteen owner/UI cases cover shared lifetimes, budget pressure, decode concurrency,
retirement, failure retries and 96-picture scrolling/peeks. A separate native
Skia probe retained 62.3 MiB rather than 350.9 MiB after 96 synthetic PNGs;
process private bytes fell from 399.1 to 110.6 MiB. Decoding remains about 25 ms
per miss but runs off the UI thread; evicted distant previews decode again.
See [`kastn-picture-performance.md`](kastn-picture-performance.md) for the fixture
limits and remaining real disk/IPC/JPEG/native-frame measurements.

The many-bucket container pass is complete. At 64 sections/buckets, the reader
realizes headings/group boxes near the viewport and the board creates nearby
column shells. Small reader sections form one viewport unit; large sections keep
their note viewport. Board spacers preserve empty columns and horizontal extent,
and focused/open/busy composers retain their shells. Distant navigation, fresh
drag contexts, nested heading-only parents, both scroll anchors, latest selection,
drafts, retired actions and picture completion/moves have sixteen focused UI
cases. Full result models, project grouping, filters and snapshot/index scans
remain measurement targets. See the performance document for measurements and
the distant-revisit allocation tradeoff.

1. Remove unused slip-list presentation work before adding more caches.
2. Derive selection and availability once per UI update. Reuse project indexes
   for target resolution instead of repeatedly scanning all slips and buckets.
3. Settings-provider consolidation is complete. `KastnSettings` owns cached
   reads, including untitled-slip checks, external-change detection, and resolved
   paths for isolated test/window contexts.
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
