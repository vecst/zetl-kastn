# Kastn docket proposal: adopt delta IPC reads

Prepared October 7, 2026 for Claude. Generic Zetl support and the HAIUX client
were implemented in the `codex/cleanup` workspace.

**Status, October 7, 2026:** the generic protocol, feed, and reducer are on the
Kastn branch, without HAIUX. Steps 1, 2, 4, and the read half of 5 and 6 below
are done (`Kastn.App/KastnProjectSync.cs`); the remaining work is tracked in the
[Kastn roadmap](kastn-roadmap.md#delta-ipc-adoption).

## What was built and why it helps

Zetl now exposes a generic `GetProjectChanges` read command. A client fetches one
full project snapshot initially, then supplies the returned cursor to request
only the state that changed. Zetl remains the sole authoritative project writer;
delta reads do not modify storage or change mutation/revision semantics.

The current consumer is HAIUX. Kastn still uses `GetProject` for changed project
reads. Importantly, Kastn already skips that read when its current online snapshot
matches the `ListProjects` summary's change sequence and metadata revision. Delta
IPC's immediate value for Kastn is therefore reducing changed refreshes and
eligible reconnect reads, rather than eliminating full reads on every idle event.

For a synthetic 2,000-slip project with roughly 1 KiB text bodies, compact payload
sizes were:

| Read | Bytes |
| --- | ---: |
| Initial snapshot and cursor | 3,130,349 |
| One slip edit | 1,085 |
| Idle delta response | 541 |

The edited response mapped one slip. Figures exclude response envelopes/framing.
They are transport measurements, not measured Kastn frame-time, CPU or memory
improvements. Expected savings are less server snapshot mapping/serialization,
less IPC traffic and less client JSON deserialization/allocation. Changed reads
still scan server revision indexes, and the reducer still rebuilds snapshot
collections. Renderer/index improvements should be measured separately.

## Existing API

Welcome includes an optional capabilities list. Check
`client.Capabilities.Contains(ZetlIpcCapabilities.ProjectChanges)` before using
the new command. Its advertised string is `projectChangesV1`. It is an additive
protocol-v1 feature; older clients and `GetProject` remain supported.

Send the command with a project ID and `GetProjectChangesCommand { Cursor = ... }`.
Use a fresh command ID for each read. A null cursor bootstraps a full snapshot and
cursor together. Later reads return `ZetlProjectChanges` with:

- `Cursor`: the returned state boundary; `FromCursor`: the requested boundary.
- `Snapshot`: a full bootstrap/reset snapshot when needed.
- `Metadata`: all project fields and views, with empty bucket/slip lists, when
  project metadata changes.
- `Buckets` / `Slips`: new or changed complete records, replaced by ID.
- `RemovedBucketIds` / `RemovedSlipIds`: physical removals. Soft deletion is a
  changed slip that remains in the snapshot, including its deleted state.
- `BucketOrder`: the complete ordered bucket IDs when that order changes.
- `SlipOrders`: complete ordered slip IDs for each affected bucket. A move
  updates both bucket orders; a new empty bucket has an empty order.

Picture slips carry metadata. Keep `GetSlipPicture` and the existing picture
cache for on-demand bytes. The feed sends no picture bodies with every edit.

Use the shared reducer instead of writing a second merge implementation:

```csharp
var candidate = ZetlProjectChangesReducer.Apply(baseline, appliedCursor, changes);
// Commit candidate and changes.Cursor together only after validation and
// connection/navigation admission. Publish the usual KastnSessionSnapshot.
```

It validates continuity, IDs, ordering and bucket membership, preserves the full
snapshot's bucket-major order and leaves the old snapshot untouched. Unchanged
entity DTOs are reused from the baseline. An idle batch at the same sequence
returns the same snapshot instance.

## Boundaries, resets and notifications

The cursor contains service epoch, project ID, sequence and an opaque token.
Treat it as opaque; never manufacture one from `ListProjects` or an event.
The authoritative snapshot and cursor must always describe the same boundary.

Zetl reads under the mutation lock. Its feed retains lightweight metadata hashes,
revision stamps and ordered IDs, without retaining old text or picture bodies.
Indexing is lazy, so ordinary capture does not build a delta index. Defaults bound
history to 128 observed versions globally, 32 per project and 200,000 index units
(two per bucket/slip per retained version). Idle reads avoid whole-project scans.

A restart, expired/invalid cursor, replaced project or index-capacity limit returns
an atomic full reset snapshot with `ResyncRequired` and an explicit `ResetReason`:
`epoch_changed`, `cursor_expired`, `cursor_invalid`, `project_replaced` or
`capacity_limit`. A normal initial read uses `bootstrap`. Handle these as normal
recovery, not as permission to overwrite the editor's unsaved text.

`projectChanged` remains a coalesced invalidation hint, not a delta payload.
Fetch from the last applied cursor after a relevant hint. A notification sequence
gap can still be covered by a retained cursor; it need not force a full snapshot.
Several intermediate edits may collapse into the returned latest state. Reliable
enter/leave events for extension triggers are a separate HAIUX roadmap item,
HX-05; Kastn state synchronization does not need to wait for it.

## Suggested first implementation pass

1. Introduce a small Kastn-owned project-sync abstraction beside the connection
   controller. Own an authoritative snapshot/cursor pair, initially for one active
   project or a deliberately bounded cache. Keep its state in memory; HAIUX's
   on-disk journal exists for runner/CLI restart sharing and is not required here.
2. In `KastnConnectionController.RefreshCoreAsync`, keep `ListProjects` and its
   project selection/catalog behavior. Preserve the summary-based unchanged
   snapshot fast path. Replace the changed `GetProject` read with negotiated
   delta/bootstrap reads. Older servers continue down the existing path.
3. Validate a candidate before committing either snapshot or cursor. Preserve
   the current refresh gate and final `stateGate` checks for navigation version,
   client identity and live connection. An obsolete A-project response must not
   overwrite B or an A-to-B-to-A session. If caching obsolete results is useful,
   isolate it explicitly from the active publication path.
4. Publish the same `KastnSessionSnapshot` through `KastnSnapshotCoordinator` and
   `MainWindow.Snapshots`. This lets the first pass use the existing editor,
   selection, draft, tree, board, reader and picture reconciliation.
5. On a same-server reconnect, reuse a retained baseline/cursor when available,
   but still publish the connection transition. On a new server, bootstrap or
   accept the server's explicit reset and preserve the current server-epoch UI
   retirement behavior. Never shortcut that behavior merely because sequences
   match. Drop state for deleted projects; fresh visits can bootstrap.
6. If a batch cannot be validated, leave the applied pair intact and attempt one
   null-cursor bootstrap. If that fails, use the existing refresh failure path
   and retain pending refresh work. Do not replay writes to repair a read failure.

Keep the boundary at the connection/sync layer initially. MainWindow should not
learn the transport merge format. A second pass can pass changed-entity hints to
indexes/presenters if profiling demonstrates value; first establish equivalence
with the existing full-snapshot pipeline.

## Behaviors to preserve

Successful mutation responses remain successful if the following refresh fails.
Expected entity revisions and same-command-ID handling for uncertain mutations
are unchanged. A read reset does not authorize retrying an uncertain mutation
against a restarted server. Existing batch deferral and notification coalescing
must still settle the final authoritative state.

Remote updates to other slips must preserve a local draft. Same-slip concurrent
changes must enter the existing conflict/reconciliation behavior. Preserve save
acknowledgment, bucket/project navigation, undo/history epochs, scroll/selection
anchors, structured view drafts and picture cache retirement. An empty delta
must not prevent a required selection-settling or connection-status publication.
Workspace lane/catalog changes can invalidate without changing the open project
sequence, so keep the current project-event handling and `ListProjects` refresh.

## Acceptance and measurement

- Compare reconstructed and direct full snapshots after edits/captures, styles,
  moves, reorders, nested bucket changes, metadata/views, soft delete/restore/purge.
- Cover coalesced bursts, events arriving before command responses, deferred
  multi-command work and concurrent human edits.
- Exercise same-server reconnect, restart, cursor expiration/capacity resets,
  malformed/gapped responses, failed writes and failed refresh after confirmed save.
- Cover rapid A-to-B-to-A navigation, disconnect during read, same-sequence server
  replacement, project deletion and an older host without the capability.
- Run editor/draft, snapshot ordering, selection and incremental-rendering tests.
  Unchanged slips should keep their DTO identity across a delta application; old
  snapshots/indexes must remain valid when a successor is built.
- Measure full reads, delta/reset/empty reads, payload bytes, managed allocations,
  refresh time and UI frame/interaction latency separately. Include large text,
  many-bucket and picture-heavy fixtures. Do not infer lower working-set memory
  from payload savings alone; account for retained indexes and client baselines.

## Source map and existing evidence

Protocol/reducer: `Zetl.Contracts/ZetlProjectChanges.cs`, `ZetlCommands.cs`,
`ZetlIpcContracts.cs` and `ZetlContractRules.cs`.

Server/client: `Zetl.Core/ZetlProjectChangesFeed.cs`, `ZetlProjectService.cs`,
`ZetlProjectSnapshotMapper.cs`, `ZetlIpcConnectionServices.cs`, `ZetlIpcClient.cs`.

HAIUX reference: `tools/Haiux/Board.cs` and `ProjectSync.cs`. Adapt the concepts;
Kastn should not depend on the external runner's executable or cache implementation.

Kastn entry points: `Kastn.App/KastnConnectionController.cs`,
`KastnRefreshCoordinator.cs`, `KastnSnapshotCoordinator.cs`,
`MainWindow.Snapshots.cs`, `KastnProjectIndex.cs` and `KastnTreeProjection.cs`.

Existing regression sources: `Zetl.Tests/ZetlProjectChangesTests.cs`,
`ZetlFaultInjectionTests.cs`, `ZetlIpcTests.cs`, `KastnContractTests.cs`,
`KastnRefreshCoordinatorTests.cs`, `KastnSnapshotCoordinatorTests.cs`,
`KastnIncrementalSnapshotTests.cs`, `KastnIncrementalSnapshotUITests.cs`,
`KastnSnapshotOrderingUITests.cs`, `KastnEditorSaveTests.cs`,
`KastnEditorMutationTests.cs`, `KastnNavigationTests.cs` and
`tools/Haiux.Tests/ProjectSyncTests.cs`.

The implemented generic/HAIUX work passed 134 core/IPC/persistence/snapshot checks
and 157 HAIUX tests on Windows, including a real isolated Zetl IPC process.
Kastn's delta adoption and Linux runtime behavior have not been implemented or
validated in this pass. Ensure the shared contracts and server changes are
present on the branch Claude uses before starting the client work.

Full protocol/cache details: [delta IPC guide](delta-ipc.md).
