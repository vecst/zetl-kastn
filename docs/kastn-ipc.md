# Kastn IPC Protocol

Kastn talks to the running Zetl process through a local named pipe. Zetl owns
the project files and is the only process that reads or writes them for normal
application operations.

## Endpoint

- The default pipe name is derived from the current protocol version, user
identity, and login session.
- The pipe uses the operating system's current-user-only restriction.
- Tests and diagnostic hosts may override the pipe name with `--ipc-pipe=`.
- A Zetl process hosts multiple simultaneous clients.

## Framing

Each message is UTF-8 JSON preceded by a four-byte little-endian payload
length. Frames must be between 1 byte and 36 MiB. The larger ceiling carries
one base64-encoded image asset while image capture itself remains capped at 25
MiB.

The outer message contains:

- `protocolVersion`
- `kind`
- optional `correlationId`
- optional typed `payload`

Message kinds are `hello`, `welcome`, `command`, `response`,
`projectChanged`, and `error`.

## Handshake

The client sends `hello` first with its name, instance ID, and whether it wants
project-change notifications. Zetl accepts only the current protocol version
and replies with `welcome`, which includes the server instance ID and accepted
version.

An unsupported version receives a protocol error and the connection closes.
The server instance ID changes whenever Zetl restarts, allowing Kastn to know
that cached state must be refreshed.

The server requires the hello to complete within five seconds. Once a frame has
started, every header or payload read must continue making progress within five
seconds. Established clients may otherwise remain idle indefinitely.

## Commands And Events

Commands and responses use the contracts in `Zetl.Contracts`. The correlation
ID is the command ID. Zetl executes commands through `ZetlProjectService`, so
IPC mutations share the same writer monitor, validation, revision checks, and
deduplication as Zetl's own operations.

`GetSlipPicture` is a read-only exception to mutation-response caching. It
requires a project and picture-slip ID and returns the normalized bytes,
dimensions, MIME type, and content hash without exposing an asset path,
advancing revisions, or publishing a `projectChanged` event. Kastn uses it for
onscreen previews and picture-aware exports.

Subscribed clients receive `projectChanged` invalidations after a durable
write. Each project has a monotonically increasing change sequence. Delivery is
monotonic per project, but a slow client may receive only the newest pending
event for that project because intermediate invalidations are coalesced. A
client should request a fresh snapshot when:

- it first connects;
- the server instance ID changes;
- it reconnects after any disconnection;
- it observes a gap in a project's change sequence.

An event caused by a command may arrive before that command's response because
the durable change is published while the command executes. Clients should use
the command ID for response correlation and the project sequence for read-model
ordering.

## Failure And Backpressure

Malformed messages receive an error when framing still permits a response.
Bad framing, abandoned pipes, and I/O failures close only the affected client.

Each client has a dedicated bounded response queue of 256 messages. Project
changes do not consume response capacity: while a client is slow, pending
changes are coalesced by project and retain that project's newest sequence.
Responses are written before pending invalidations. If a client cannot keep up
with command responses, Zetl disconnects it; the client then reconnects and
recovers confirmed results by command ID or refreshes authoritative snapshots.

Host logs may include connection IDs, client names, command kinds, command IDs,
response statuses, and exception types. They must not include command payloads
or slip text.

## Lifecycle

IPC work runs on asynchronous pipe tasks and does not run on keyboard capture
or Avalonia UI dispatch paths. Closing Kastn leaves Zetl and capture running.
Stopping Zetl cancels the accept loop, closes connected clients, and causes
pending client commands to fail.
