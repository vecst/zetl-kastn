# Delta project reads

Zetl IPC can return what changed in a project since a client's last read
instead of the whole project. A client reads one full snapshot with a cursor,
then hands that cursor back to get only the state that changed. Zetl stays the
only writer; delta reads change nothing in storage, revisions, or mutation
handling. Kastn reads its open project this way, and so does HAIUX.

## Protocol

The version-1 Welcome advertises the optional `projectChangesV1` capability
(`ZetlIpcCapabilities.ProjectChanges`). Clients connected to a Zetl without it
keep using `GetProject`.

Send `GetProjectChanges` with a project ID and a `GetProjectChangesCommand`
payload. A null cursor bootstraps an atomic full snapshot and cursor. Later
requests supply that exact cursor and receive `ZetlProjectChanges`:

- `Cursor` identifies the returned boundary; `FromCursor` identifies the request.
- `Metadata` replaces project fields and views when changed, with empty entity lists.
- `Buckets` and `Slips` replace changed or new records by ID.
- `RemovedBucketIds` and `RemovedSlipIds` describe physical removals. Soft deletion
  remains a changed slip with its deleted state, just as in a full snapshot.
- `BucketOrder`, when present, gives the complete ordered bucket IDs.
- `SlipOrders` replaces the complete ordered slip IDs for each affected bucket,
  including empty orders. Moves update both affected orders.

Picture records carry metadata; bytes stay available through `GetSlipPicture`.
An unchanged boundary returns empty changes and the same cursor. Reads do not
advance revisions, publish notifications, or enter the mutation deduplication
cache.

Use `ZetlProjectChangesReducer.Apply` to build and validate a candidate snapshot
before adopting the new cursor. It checks project and cursor continuity, entity
IDs, complete orders, and bucket membership without modifying the prior
snapshot, and it keeps the full snapshot's bucket-major slip order. Unchanged
records are reused from the prior snapshot, and an idle batch returns the prior
snapshot itself.

## Resets and retention

A cursor binds a project, the change feed's epoch, a sequence, and an opaque
token. A sequence alone cannot tell a restored or replaced project or a
restarted Zetl apart, so clients treat cursors as opaque and never build one
from `ListProjects` or an event. Unusable cursors return `ResyncRequired: true`,
an explicit reason, and a full snapshot with its cursor together. Reasons are
`epoch_changed`, `cursor_expired`, `cursor_invalid`, `project_replaced`, and
`capacity_limit`; an initial read reports `bootstrap`.

Zetl keeps lightweight revision stamps, metadata hashes, and ordered IDs, not
old text or picture bodies. Defaults bound retained versions to 128 globally,
32 per project, and 200,000 index units (two per bucket or slip per version),
evicted by recency. An oversized project keeps a cursor marker so idle reads stay
cheap, but its next changed read returns a full snapshot.

Indexing is lazy: capture and other writes build no delta index. Reads run under
the same lock as mutations, so a snapshot, delta, and cursor always describe one
state. Idle reads do not scan the project. A changed read still scans revision
stamps, and the client still rebuilds its snapshot collections.

These are observed state versions, not an event log: several edits can collapse
into the latest state. `projectChanged` remains a coalesced wake-up hint; after
one, a client reads from its last applied cursor. A retained cursor can bridge a
notification gap or a reconnect to the same Zetl.

## Payload sizes

For a synthetic 2,000-slip project with roughly 1 KiB text bodies:

| Read | Compact payload bytes |
| --- | ---: |
| Initial full snapshot and cursor | 3,130,349 |
| One slip edit | 1,085 |
| Idle read | 541 |

These exclude the response envelope and framing. They measure transport, not
Kastn frame time, CPU, or memory; see the
[Kastn delta IPC handoff](kastn-delta-ipc-handoff.md) for what remains to measure.
