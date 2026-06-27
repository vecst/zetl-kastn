# kastn — Direction

> Status: kastn is implemented as the deliberate project and template workbench.
> Zetl consumes its shared template catalog read-only and can start a named project
> from a template directly from the Board. Current remaining work lives in
> [`kastn-roadmap.md`](kastn-roadmap.md).

## Premise

Zetl and kastn are two applications over one store. The split is by **nature of
the task**, not by feature:

- **Zetl** is throughput: capture, replay, and fire output, fast and invisibly.
  It stays the tiny resident process. Capture must remain decision-free.
- **kastn** is real work: browse the archive, organize, synthesize, and produce
  documents. It is the deliberate workbench — the *kasten*.

## The window-model test

The clearest test for where any feature belongs is its window behavior:

- **Appears and vanishes → Zetl.** Zetl's UI is chromeless, momentary popups (no
  minimize/maximize/close, in-app buttons, click-away commit). You don't travel
  to them; they show up and leave.
- **You go there and dwell → kastn.** kastn is a normal windowed application — a
  menu bar, minimize/maximize/close, a taskbar presence. It is where you go when
  you are done capturing and want to do real work.

## The fork: what stays in Zetl vs. moves to kastn

Several surfaces fork along this seam rather than moving wholesale:

- **Compile forks.** Genuine fast workflows stay in Zetl — TSV compile is the
  archetype (slips → spreadsheet rows, fired and forgotten), alongside the
  compile-and-paste chord sentence and replay data entry. The deliberate
  synthesis surface becomes kastn.
- **The Board forks.** Zetl keeps a **quick-view** Board: a glance at workflow
  state, a bookmark for "where was I" after a lunch or meeting. The full,
  better-designed organizing surface lives in kastn.

Rule of thumb: *fire it and move on → Zetl; sit down and build something →
kastn.*

## Co-editing is a requirement

A project may be **active in Zetl and open in kastn at the same time**, both
editing — e.g. capturing into a project in Zetl while arranging it in kastn.
This is a hard requirement, not read-mostly.

The concurrency strategy is **one writer, two applications**:

- Zetl remains the resident process and is the only process allowed to mutate
  workspace or project files.
- kastn checks whether Zetl is running and starts it when necessary. Zetl then
  remains independently resident if kastn closes.
- kastn sends versioned domain commands to Zetl over local IPC and subscribes to
  project snapshots and change notifications.
- Editable records carry revisions. A stale same-record edit is rejected and
  resolved explicitly rather than silently overwriting newer content.

This keeps capture active while kastn is open without requiring two processes to
coordinate direct JSON writes.

## Storage direction

Human-readable JSON remains the canonical live store. It is not a secondary
export generated from a database. The project folder should always be current,
inspectable, and ready to share without a conversion step.

The existing JSON model has been exercised with approximately 20,000 slips in a
project without an observed usability problem. Storage work should therefore
start by consolidating writes in Zetl and measuring real behavior rather than
introducing SQLite preemptively.

IPC commands describe domain operations rather than JSON operations, so a future
SQLite implementation can live behind Zetl without changing kastn. SQLite is
reconsidered only if measured project sizes or future transaction requirements
show that JSON is the limiting factor.

## Storage evolution: single project file affirmed

The previously proposed "typed capture log" storage evolution (splitting capture types into separate JSON files like text, url, picture, etc.) has been explicitly rejected. Reconstructing buckets and slips from multiple type-scoped files introduces significant complexity, query overhead, and state-reconstruction lag.

Instead, the single-file `project.json` design is affirmed:
- Each project directory remains self-contained with a single canonical `project.json` file.
- Buckets explicitly contain ordered lists of slips.
- Binary assets (pictures, files) are stored in the project's `assets/` subfolder and referenced by content-addressable hashes in the main JSON file.
- Measurements confirm that even at 20,000 slips, atomic reads and writes of a single file are well below the threshold of user perception, rendering file partitioning unnecessary.

## Future direction: project-level board

The proposed Project Board is a portfolio view across projects: projects become
columns, buckets become expandable cards, and slips can move or copy between
projects. It complements the current project-local Board Mode rather than
replacing it.

That surface should be built on two storage/link evolutions:

- live binary assets move toward a workspace-level content-addressed asset store,
  while project export/import remains self-contained with local `assets/`
  folders;
- wiki-links gain cross-project addressing internally, while single-project
  Markdown exports can still emit clean Obsidian-style local wiki-links.

The detailed design is in
[`kastn-project-board-roadmap.md`](kastn-project-board-roadmap.md).

## Two extension points authored in kastn

These are authored in kastn and modeled like the theme system: versioned JSON
documents with protected presets and user-defined copies.

- **Templates (input side).** A named scaffold for a new project: its
  default buckets and their behaviors (kind, pop/replay defaults, compile mode,
  TSV headers / row length, starting text, and optional starter cards). Zetl's
  "New Project" has a template
  picker so you can start, say, a "Recipe" project with the right buckets and
  rules already in place. Zetl treats the shared catalog under
  `%AppData%\Zetl\templates` as read-only.
- **Views (output side).** A renderer that projects an existing project's slips
  into Formatted, Plain, TSV, Markdown, HTML, or PDF output. Universal views
  live in the shared catalog; structured views can live with their project.
- **Creation types.** A small bundle pairing a template with a default view.

A single creation-type such as "Recipe" plausibly ships **both**: a template
(start with Ingredients / Steps / Notes buckets) and a view (render those slips
as a recipe document → PDF).

## Views are projections

The firm principle: **slips are the only truth.** A view renders (and sometimes
edits) slips, but never persists its own authoritative content. This keeps the
archive durable and view-agnostic, and makes views shareable like themes.

Capture provenance follows the same rule: application and window context enrich
the private slip but are not part of its authored text. Archive exports may keep
that envelope; clean sharing exports strip it from a detached project snapshot.
Zetl now exposes both choices from the Board and writes a versioned `.zetl.zip`
containing a manifest and project snapshot. The package is intentionally ready
to gain binary asset entries when typed image and file capture lands.
Future image cleanup must apply the same policy to EXIF and identifying original
filenames.

This also serves the archival philosophy: nothing captured is junk, slips are
living documents enrichable years later, and cleanup mechanics (pop, replay
consumption) are workspace hygiene only — they never destroy history.

The same principle applies to deletion. Ordinary Kastn delete should be a
soft-delete operation: selected slips move into a protected project
trash/deleted view that Kastn can browse and restore, while Zetl's fast capture
and quick Board stay focused on active buckets. Permanent removal is a separate
empty-trash style action, not the default meaning of delete.

## Handoff: "Open in kastn"

The Zetl Board can open its selected project directly in kastn. The tray can
also open or restore kastn. Both paths focus the existing single instance when
it is running or launch it when it is not.

## The project lifecycle ties it together

The Compile window's `Finish Project` action is the **seam** between the two
apps: it sets the project aside, clears its lane, and automatically returns the lane
to the rolling Journal. Finishing is reversible—set aside, not locked. Combining finish
with a default copy/fire output remains an open workflow decision.

## Groundwork already in place

The split is supported by the `Zetl.Core` / `Zetl.Runtime` / application separation,
the sole-writer project service, versioned IPC contracts, self-contained project
folders, and shared theme/template/view document systems.
