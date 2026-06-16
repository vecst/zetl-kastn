# Kastn Read And Organize Workbench

K4 turns the Kastn shell into the first useful project workbench. Kastn still
does not read or write project files. It renders immutable snapshots and sends
revision-checked domain commands to Zetl.

## Browsing

The project navigator lists every non-infrastructure project returned by Zetl.
Selecting a project does not change either of Zetl's active capture lanes.

The bucket pane shows the persisted bucket order and hierarchy. Selecting a
bucket filters the slip list to that bucket and its descendants. `All buckets`
shows slips in snapshot order, which is bucket order followed by the persisted
slip order inside each bucket.

The workbench also filters by:

- case-insensitive text search;
- source;
- capture session;
- today, the last 7 days, or the last 30 days.

Filters compose and do not mutate project state.

## Editing

Selecting a slip opens it in the editor. Text changes autosave after a short
idle delay and can also be saved with `Ctrl+S`. Zetl accepts the edit only when
the slip revision still matches the revision Kastn opened.

Kastn tracks the editor independently from refreshed snapshots:

- unrelated captures preserve the selected slip and unsaved text;
- an acknowledged local save is recognized even if its change event arrives
  before the command response;
- a same-slip remote edit keeps the local draft and opens the conflict panel.

The conflict panel displays both versions. `Use Zetl Version` discards the
local draft. `Keep Mine` retries the local text against the newest revision.
There is no automatic last-writer-wins overwrite.

## Organization

Bucket commands support:

- adding a root bucket or a child of the selected bucket;
- renaming a bucket;
- changing its parent without creating a hierarchy cycle;
- deleting a bucket tree after confirmation.

Slip commands support:

- moving the selected slip to another bucket;
- deleting the selected slip after confirmation.

All commands pass through Zetl's sole-writer service. Successful responses mean
the project JSON write is already durable.

## Keyboard

- `Ctrl+F`: focus search.
- `Ctrl+S`: save the editor immediately.
- `F2`: focus and select the current bucket name.
- `F5`: refresh snapshots.

Normal list keyboard navigation is provided by Avalonia for projects, buckets,
and slips.

## Live Changes

Zetl publishes project-sequence notifications for IPC mutations and its own
direct keyboard captures. Kastn requests a fresh selected-project snapshot when
a notification arrives. Stable project, bucket, and slip IDs allow the UI to
preserve selection and editor state while the visible read model updates.
