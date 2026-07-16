# Kastn Undo

Kastn's in-app undo is landed (see Status). Zetl's session undo stack lives in
the shortcut coordinator and is fed only by the coldkey capture, quick-note,
compile, Pop, and Replay handlers; Kastn's edits flow through
`ZetlProjectService` and never reach it, so Kastn keeps its own history.

This record fixes the approach for an in-Kastn undo that stays out of Zetl's
coldkey undo.

## Boundary

- In-app `Ctrl+Z` undoes the last Kastn edit. Held `Ctrl+Z` (and
  `Ctrl+Shift+Z`) remain Zetl coldkeys for the capture lanes and are unchanged.
- Undo is a Kastn-local history of **inverse domain commands**, replayed over the
  existing IPC. Zetl stays the sole writer; an undo is just another
  revision-checked command.
- No protocol change. The common slip operations invert with the existing
  `AddSlip`, `UpdateSlip`, `MoveSlip`, `ReorderSlip`, and `DeleteSlip` kinds, so
  no new wire contract or protocol version is required.
- A server-side undo log is explicitly rejected: it would force Zetl to track
  per-client history across its multi-client model and would entangle Kastn's
  undo with the coldkey stack. Keeping undo client-side keeps the two histories
  isolated.

## Scope (v1)

Slip-level editing only:

- `UpdateSlip` (title / body / format / block kind)
- `MoveSlip` (across buckets)
- `ReorderSlip` (within a bucket)
- `DeleteSlip` (soft-delete to `Deleted`)
- `AddSlip`

Bucket editing is also covered (landed after canonical bucket order):
`UpdateBucket` (rename / reparent / settings / render kind), `SetBucketHeading`,
and `ReorderBucket` record and invert as bucket operations alongside the slip
operations in one entry. Bucket **creation and deletion** stay unrecorded —
their inverses need re-creation semantics — as do project-level commands
(rename / status / view documents).

## Inverse Map

Each entry captures the prior state and the revision to invert against at the
moment the forward command succeeds.

| Forward command | Undo command | Captured before sending |
| --- | --- | --- |
| `UpdateSlip` | `UpdateSlip` with prior title, body, format, block kind | the pre-edit slip snapshot |
| `MoveSlip` | `MoveSlip` back to the original bucket, then `ReorderSlip` to the original anchor | original bucket id and neighbour |
| `ReorderSlip` | `ReorderSlip` with the prior anchor | the prior preceding-slip id |
| `DeleteSlip` | `MoveSlip` back to the original bucket (then `ReorderSlip`) | original bucket id and position |
| `AddSlip` | `DeleteSlip` of the new slip id | the id returned in the response |

### AddSlip undo is a soft delete

Undoing an add moves the new slip to `Deleted` rather than hard-removing it.
This is deliberate: a change of mind can restore it, consistent with the rest of
Kastn's non-destructive delete model. There is no hard-delete command and v1 does
not add one.

## Conflict Handling

Each inverse command carries the revision Kastn recorded for the forward
mutation. If the record moved since — a Zetl capture or another edit bumped its
revision — the inverse returns `conflict` (or `notFound`).

Undo does not silently force or silently abort. It shows the same
current-vs-target choice the editor conflict panel offers, as a dialog
(`KastnDialogs.UndoConflictAsync`) rather than the docked panel — Board Mode
collapses the Detail pane, so a docked affordance would be invisible exactly
where drags and card edits happen:

- **Undo anyway** — re-issue the inverse against the record's current revision,
  overwriting the intervening change (it can conflict again if another change
  races; the dialog re-asks with fresh text).
- **Keep newer change** — abandon this operation and leave the record as it is.

A conflicted entry is consumed either way; it is not silently retried.

## Undo History

- A bounded client-side stack of entries, each pairing a description with an
  async inverse closure. Capacity matches Zetl's coldkey stack (100).
- A single user gesture that issues several commands (multi-slip delete, batch
  formatting) pushes one compound entry whose inverses replay in reverse order,
  so one `Ctrl+Z` reverts the whole gesture.
- The stack is cleared on disconnect, a server-instance-id change, or a
- A transient disconnect no longer clears history by itself. Entries remain
  revision-checked across reconnect to the same server instance; a project
  change or different server instance clears them after reconciliation.

### Failure atomicity and repair

Undo and redo peek at the source entry and remove it only after all meaningful
inverse commands have confirmed results. A command whose IPC write started but
lost its response is marked outcome-unknown and retried with the same command ID
only when Kastn reconnects to the same Zetl instance.

If a compound entry is interrupted after one or more commands may have applied,
Kastn refreshes the authoritative project and builds an explicit repair entry
for the actual partial state. The repair sits above the original entry: running
it returns every touched record to its pre-attempt state without creating a redo
entry, after which the original undo/redo can be retried. If Zetl restarted, the
history is cleared rather than replayed against an instance that cannot prove the
old command outcome.

Redo is the symmetric twin of undo (see below) and is included.

## Symmetric Undo / Redo

Undo and redo share one shape and one executor. A reversible action is a set of
per-slip operations, each saying "the slip is in state *From* (with neighbour
*FromFollowing*); return it to *To* (before *ToFollowing*)", where *To* is either
a snapshot to restore or a soft delete. Applying an operation yields its opposite
by swapping *From* and *To* and reading the slip's resulting snapshot, so undo
produces a redo entry and redo produces an undo entry from the same machinery.
Soft delete is what makes this closed: a deleted slip still exists, so the
opposite of a delete is always a restore.

A gesture coalesces by slip id: the first time a gesture touches a slip it records
that slip's pre-gesture snapshot, and it tracks the latest snapshot after. At
gesture close it emits one operation per affected slip — diffing pre against final
to issue only the needed move / update / reorder. This is what lets a gesture that
hits one slip with several commands (divider insert = add + reorder, drag = move +
reorder) invert correctly, where naive per-command recording would bake a stale
revision.

The active gesture is `AsyncLocal`, scoped to the async flow that opened it. A
gesture's commands await IPC round-trips and the UI stays live during those
awaits, so a shared field would let an unrelated interleaved action (clicking
another slip, its autosave) join the open gesture and get reverted with it by
one Ctrl+Z. Input-driven flows always start without a gesture.

Applying an undo/redo entry first selects the entry's primary target (and
re-selects it after the refresh), so the user watches the step apply instead of
hunting afterwards for which record changed.

## Keybinding

- `Ctrl+Z` undoes and `Ctrl+Y` redoes, from the main `OnKeyDown` handler.
- `Ctrl+Shift+Z` is deliberately **not** a redo binding: held `Ctrl+Shift+Z` is
  Zetl's Shift-lane undo coldkey, so mapping its tap to redo would overload one
  chord with opposite meanings (tap redo vs hold undo). `Ctrl+Y` is free of any
  Zetl coldkey.
- Kastn keeps **one ordered history** covering the slip editor. The editor's
  native TextBox undo is disabled (an earlier focus-split model made Ctrl+Z
  answer differently mid-edit and could resurrect another slip's text from the
  box's own stack). The editor's tunnel handler claims `Ctrl+Z`/`Ctrl+Y` for
  Kastn history; pending typing is flushed into the history as one entry when
  undo runs, so typing, style toggles, and moves all undo strictly in the order
  they happened. Typing granularity is one entry per saved burst, not per
  character. Incidental text boxes (bucket name, search) keep their own undo.

## Status

Slices 1 and 2 landed: full slip undo/redo.

- `KastnUndoHistory`, the symmetric `KastnUndoEntry` / `KastnUndoOperation`
  model, and `KastnUndoPlanner` (`BuildSteps`, `Opposite`, `IsNoOp`) — pure,
  snapshot-based, unit-tested.
- `MainWindow.ExecuteMutationAsync` is the single choke point for Kastn mutations;
  it records into the active gesture or as its own one-operation entry, and passes
  non-slip commands through unchanged. Workbench, Views, drag-and-drop, and
  batch-format mutations route through it.
- All five slip kinds (`AddSlip`, `UpdateSlip`, `MoveSlip`, `ReorderSlip`,
  `DeleteSlip`) record. Same-slip gestures are wrapped with `BeginGesture` so they
  coalesce into one entry: divider insert, drag move/reorder, the per-card edit
  dialog, and the batch move / delete / format loops.
- `Ctrl+Z` / `Ctrl+Y` drive one ordered history from every surface, including
  the slip editor (whose native text-box undo is disabled — see Keybinding).
  `Ctrl+Shift+Z` is avoided so it does not overload Zetl's Shift-lane undo
  coldkey. History clears on project switch or loss of a live connection.
- Stacked entries on one slip re-thread their expected revisions as history
  steps apply (`KastnUndoHistory.RethreadRevision`), so a run of undos or redos
  walks the whole history for that slip. A change made outside the history
  still bumps the revision without re-threading and conflicts as designed.
- Conflicts are handled without data loss but without the rich panel yet: an
  operation whose record changed since is skipped and reported in the status line
  ("N items changed since and were skipped"); the rest of the entry still applies.

Also landed:

- Conflict resolution UI: a conflicted operation opens the Undo-anyway /
  Keep-newer-change dialog (see Conflict Handling) instead of the old
  skip-and-report fallback; declined operations count as "kept the newer
  change" in the status line.
- Bucket undo for `UpdateBucket` / `SetBucketHeading` / `ReorderBucket`, with
  gesture coalescing (a column drag's reparent + reorder is one entry) and
  bucket revision re-threading mirroring the slip stacks.
- Failure-safe history commit, same-instance uncertain-command retry, and
  authoritative repair entries for interrupted compound undo/redo.

Remaining:

- Bucket creation/deletion and project-level undo (rename / status / view
  documents) — excluded from v1 scope.

## Test Gates

- `KastnUndoTests` covers the history, the inverse builder for each operation
  shape (delete, move+reorder, property restore, reorder-only, no-op), the
  `Opposite` swap, and following-slip ordering.
- Manual GUI checks that automated tests cannot reproduce: editor-focus
  `Ctrl+Z`/`Ctrl+Y` passthrough; one `Ctrl+Z` reverting a whole drag or batch;
  redo replaying it; undo refreshing the editor/view; and that held `Ctrl+Shift+Z`
  still reaches Zetl's Shift-lane undo while Kastn is focused.
