# Documentation Map

The repository documentation is organized by purpose. Roadmaps describe current
open work; design and contract documents explain stable decisions or preserve
implementation context.

## Using And Building

- [`guide.md`](guide.md) — the user guide: every Zetl and Kastn feature
- [`building.md`](building.md) — running from source, tests, publishing,
  previews, and diagnostics
- [`releases/`](releases/) — release notes, one file per release

## Current Roadmaps

- [`rc-code-health-worklist.md`](rc-code-health-worklist.md) — P1/P2 release
  risks, cleanup sequencing, and acceptance gates

- [`kastn-roadmap.md`](kastn-roadmap.md) — current Kastn/Zetl product work
- [`linux-roadmap.md`](linux-roadmap.md) — current Linux platform work
- [`linux-setup.md`](linux-setup.md) — installing Zetl on Linux, keyboard
  permissions, and undoing them
- [`rc-testing-workflow.md`](rc-testing-workflow.md) — ordered Windows RC gate,
  disposable-profile procedure, and failure evidence workflow
- [`windows-parity-checklist.md`](windows-parity-checklist.md) — manual Windows
  dogfood and release-candidate smoke pass

These are the authoritative places to answer “what remains?”

## Product And Architecture

- [`kastn.md`](kastn.md) — product boundary between Zetl and Kastn
- [`kastn-workbench.md`](kastn-workbench.md) — current Kastn workbench behavior
- [`kastn-process.md`](kastn-process.md) — process, single-instance, reconnect,
  tray, and shutdown lifecycle
- [`linux-port.md`](linux-port.md) — Linux keyboard/UI architecture background

## Contracts And Measurements

- [`kastn-contracts.md`](kastn-contracts.md) — mutation, revision, snapshot, and
  protocol invariants
- [`kastn-ipc.md`](kastn-ipc.md) — named-pipe framing and lifecycle
- [`kastn-storage-baseline.md`](kastn-storage-baseline.md) — repeatable JSON
  storage measurements
- [`kastn-rendering-performance.md`](kastn-rendering-performance.md) — large-project
  layout measurements, tree row reuse and the remaining rendering costs
- [`zetl-popup-memory.md`](zetl-popup-memory.md) — native quick-note/hold-indicator
  lifetime checks and Windows renderer memory measurements

## Focused Design Records

- [`kastn-mainwindow-audit.md`](kastn-mainwindow-audit.md) — MainWindow ownership
  map, confirmed stale code, and the proposed cleanup sequence
- [`hold-shortcut-ideas-discussion.md`](hold-shortcut-ideas-discussion.md) — post-RC
  Hold-shortcut and portable text-interaction ideas; discussion only
- [`hold-routing-discussion.md`](hold-routing-discussion.md) — routing held
  gestures by context, file-reference slips, and move tracking; discussion only
- [`kastn-compile-split.md`](kastn-compile-split.md) — historical product
  rationale for the Zetl, Compile, and Kastn boundary
- [`kastn-ui-roadmap.md`](kastn-ui-roadmap.md) — Kastn UI work already landed
  and the remaining linked-slip/board designs
- [`kastn-templates-roadmap.md`](kastn-templates-roadmap.md) — template and
  creation-type design, including temporary consumables

- [`kastn-undo-roadmap.md`](kastn-undo-roadmap.md) — in-Kastn undo via
  client-side inverse commands, distinct from Zetl's held-Ctrl+Z undo

- [`kastn-project-board-roadmap.md`](kastn-project-board-roadmap.md) - proposed
  project-level board, workspace asset store, and cross-project link/export
  design

Focused design records are not the master project queue. Their remaining work
is summarized in [`kastn-roadmap.md`](kastn-roadmap.md).
