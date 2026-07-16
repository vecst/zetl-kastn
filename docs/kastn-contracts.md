# Kastn/Zetl Contracts

This document fixes the K0 invariants for the local protocol between Kastn and
the resident Zetl process. The public DTOs and enums live in `Zetl.Contracts`.

## Authority

- Zetl is the only process that mutates workspace or project storage.
- Kastn sends domain commands and never sends instructions to replace or edit a
  JSON file.
- A successful response means Zetl completed the durable JSON write.
- A change event is published only after that successful write.
- A failed project or workspace write restores Zetl's live state to its last
  durable snapshot; a later command must not accidentally commit the failed
  mutation.

## IDs And Revisions

- Project, bucket, slip, command, and event IDs are opaque non-empty strings.
- Persisted entity revisions are positive integers.
- Project metadata, each bucket, and each slip have independent revisions.
- A project also has a monotonic change sequence. It advances for every durable
  mutation in that project and orders snapshots and notifications.
- Capturing a slip advances the project change sequence and the new slip's
  revision. It does not advance the project metadata revision or revisions of
  unrelated buckets and slips.
- Rename, update, move, reorder, and delete commands include the revision of the
  record the client observed. Adds do not require an expected revision.
- `ReorderSlip` repositions a slip within its current bucket. It carries the
  optional id of the slip the target should sit immediately before; a null anchor
  moves the target to the end of its bucket. The anchor must belong to the same
  bucket as the target. Reorder never moves a slip across buckets — that remains
  `MoveSlip`.

This separation prevents a new Zetl capture from conflicting with an unrelated
slip edit in Kastn.

## Command Delivery

- Every request has a protocol version and command ID.
- A client retries an uncertain request with the same command ID.
- IPC distinguishes a command rejected before transmission from a command whose
  frame write started but whose response was lost. The latter is outcome-unknown:
  retrying is safe only with the same command ID against the same Zetl server
  instance. If Zetl restarted, the client refreshes and reconciles authoritative
  state instead of risking a duplicate mutation.
- Zetl keeps a bounded cache of completed command IDs and returns the original
  response for a duplicate. It must not execute the mutation twice.
- The eventual cache size and retention time are implementation details, but
  they must cover normal reconnect and timeout retries.
- Commands from one connection are answered in request order. Project change
  events carry the authoritative cross-connection order.

## Response Statuses

- `success`: the command completed. For mutations, the durable write succeeded.
- `conflict`: the target record exists but its revision changed. The response
  includes the expected revision, actual revision, and current record.
- `notFound`: the requested project or target record does not exist.
- `validationError`: IDs, revisions, payloads, or values are missing or invalid.
- `unsupportedProtocol`: the client and server cannot communicate using the
  requested protocol version.
- `failure`: an unexpected server or persistence failure occurred.

Clients do not automatically retry conflicts, validation errors, or unsupported
protocols. Kastn retries an outcome-unknown command for a bounded interval when
it reconnects to the same server instance; confirmed responses are never turned
back into command failures merely because the follow-up projection refresh fails.

## Snapshots And Changes

- `ListProjects` returns project summaries.
- `GetProject` returns one complete project snapshot.
- A project snapshot includes its project-scoped structured view documents.
- `SaveProjectView` and `DeleteProjectView` mutate those documents against the
  project's metadata revision; successful responses mean the containing
  `project.json` write is durable. Universal views remain outside project state.
- `GetSlipPicture` returns normalized picture bytes for one picture slip without
  exposing storage paths or changing project state.
- A snapshot includes project metadata revision, project change sequence,
  buckets, and slips.
- A slip title is optional metadata. Clients display body text as the fallback
  title when it is absent; non-picture slips require either a title or body text.
- A slip may carry optional whole-slip font family, point size, and `#RRGGBB`
  text color. Empty family/color and size `0` mean inherit; `UpdateSlip` uses
  null to preserve those fields and empty values/`0` to clear them.
- Change events identify the changed entity and resulting project change
  sequence.
- On reconnect, after a sequence gap, or after any uncertain local merge, Kastn
  requests a fresh snapshot.
- Kastn may merge an unrelated change into its view, but it must not replace an
  editor's unsaved text without an explicit same-slip conflict decision.

## Protocol Evolution

- Version 1 covers text and picture slips plus the first Kastn workbench.
- Unknown JSON properties are ignored for forward-compatible additions.
- Breaking changes require a new protocol version and handshake negotiation.
- Picture transfer is content-addressed in both directions: `GetSlipPicture`
  reads normalized bytes, and `SetSlipPicture` / `RemoveSlipPicture` attach or
  detach a slip's picture as revision-checked mutations carrying normalized
  PNG content capped at the capture limit. File-attachment transfer remains
  deferred. Version 1 contracts contain no storage paths.
