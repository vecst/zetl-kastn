# Kastn Process And Connection Lifecycle

Kastn is a normal Avalonia desktop application with its own taskbar window and
single-instance identity. It does not open project JSON files. All project
reads and mutations go through the Zetl IPC host.

## Startup

Kastn connects to the current user/session Zetl pipe on startup. If the pipe is
absent, Kastn starts Zetl and keeps retrying until the host is available.

The launcher resolves Zetl in this order:

1. An explicit `--zetl-path=` argument.
2. The `ZETL_EXECUTABLE` environment variable.
3. A Zetl executable or DLL beside Kastn.
4. A local Debug or Release build when running from the repository.

Installed builds should place Zetl and Kastn together. Zetl uses the equivalent
`--kastn-path=` argument or `KASTN_EXECUTABLE` environment variable for its
`Open in Kastn` action.

## Single Instance

The first Kastn process owns the `KastnSingleInstance` mutex and a current-user
control pipe. A later launch sends its optional `--project=` ID to the first
process and exits.

The first process restores and activates its main window, then navigates to the
requested project. Activation requests carry stable project IDs only.

## Control Pipe

Kastn hosts a local control pipe whose name is derived deterministically from
the user and login session, so Zetl computes the same name without it being
passed at launch. The pipe carries three signals:

- **Activate** — focus Kastn and optionally navigate to a project. Used by the
  single-instance forward and by Zetl's `Show Kastn` tray item. Fire-and-forget.
- **Shutdown** — Zetl is quitting and asks whether Kastn should close with it.
  Kastn replies with its decision (see Tray And Shutdown Coordination).

The pipe is restricted to the current user. Activation is the default; only a
shutdown request expects a reply.

## Tray And Shutdown Coordination

Kastn participates in Zetl's single system-tray presence rather than carrying its
own tray icon:

- **Minimize hides into Zetl's tray.** Minimizing Kastn takes it off the taskbar
  and hides the window. It returns through the `Show Kastn` item in Zetl's tray
  menu, which is enabled only while a Kastn client is connected and sends an
  activate request over the control pipe. Closing (the window's X) still exits
  Kastn outright and leaves Zetl resident.
- **Quitting Zetl coordinates the shutdown.** When the user quits Zetl with a
  Kastn connected, Zetl sends a shutdown request and waits for Kastn's decision.
  A Kastn minimized to the tray closes silently, so quitting Zetl closes
  everything. An open Kastn raises itself and shows a `Close both / Cancel`
  confirmation; cancelling aborts Zetl's quit, so closing Zetl no longer silently
  relaunches because Kastn was still open. On a confirmed close, Kastn suppresses
  the reconnect-driven relaunch and exits.

This coordinated path applies to the Zetl tray's `Quit`. An unexpected Zetl exit
(a crash or kill) is still treated as an outage: Kastn reports offline and
relaunches Zetl, since it needs a resident host.

## Connection State

The main window presents three states:

- `Connecting`: a progress indicator is visible while Kastn waits for Zetl.
- `Online`: project summaries and the selected immutable snapshot are current.
- `Offline`: the last snapshot remains visible as a read-only view and a clear
  banner says that Kastn is reconnecting.

Kastn requests fresh project summaries and a fresh selected-project snapshot
after every connection. It also refreshes when Zetl publishes a project change.
This includes direct keyboard captures made inside Zetl, not only IPC commands.

If Zetl restarts, Kastn detects the abandoned pipe, enters the offline state,
reconnects, and reloads snapshots. The user does not need to restart Kastn.

## Application Shell

K3 provides:

- a windowed application with a taskbar presence that minimizes into Zetl's
  tray (see Tray And Shutdown Coordination);
- a procedural window/taskbar icon (a white "K" over a dusk gradient, the sibling
  of Zetl's tray "Z");
- File, View, and Help menus;
- a project navigator;
- project, bucket, and recent-slip read views;
- connecting, offline, and no-project empty states;
- shared Zetl color and typography token names.

Editing and organization commands remain deferred to K4. K3 is deliberately
read-only so the connection lifecycle can settle before editor state and
revision conflicts are introduced.

## Zetl Handoff

The Zetl Board has an `Open in Kastn` action for its selected project. Zetl
starts Kastn with that project ID. If Kastn is already running, its second
process forwards the ID over the activation pipe and exits.

Closing Kastn disposes only its IPC client. Zetl, keyboard capture, and the Zetl
IPC host continue running.
