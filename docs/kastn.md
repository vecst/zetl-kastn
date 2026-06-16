# kastn — Direction

> Status: forward-looking direction, not built and not scheduled against A7. This
> document records the shared design intent so it has a home in the repo. Nothing
> here describes current behavior. The staged implementation checklist lives in
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

The existing JSON model has been exercised with approximately 20,000 notes in a
project without an observed usability problem. Storage work should therefore
start by consolidating writes in Zetl and measuring real behavior rather than
introducing SQLite preemptively.

IPC commands describe domain operations rather than JSON operations, so a future
SQLite implementation can live behind Zetl without changing kastn. SQLite is
reconsidered only if measured project sizes or future transaction requirements
show that JSON is the limiting factor.

## Planned data model: typed capture log

Evolve the store from bucket-owns-notes containment to a **typed capture log**:

- One JSON per clipboard type (text, url, picture, file) per project.
- Each captured item is stamped with its bucket and capture time. A **bucket
  becomes a time-ordered query** over the typed files, not a container.
- Payoff: direct faceted queries ("all URLs in this project", "all pictures")
  and smaller type-scoped reads and writes. Co-editing safety comes from Zetl's
  sole-writer role, not from relying on the applications to touch different
  files.
- Buckets keep their settings in a small per-project metadata file. Binary
  captures (pictures, files) are copied into the project folder and
  path-referenced, keeping each project directory a self-contained export.
- Capture stays decision-free because the clipboard format itself declares the
  type.

## Two extension points authored in kastn

Both are authored in kastn and stored like the theme system (versioned,
import/export JSON documents, protected presets), but they are **distinct**:

- **Templates (input side).** A named scaffold for a *new, empty* project: its
  default buckets and their behaviors (kind, pop/replay defaults, compile mode,
  TSV headers / row length, starting text). Zetl's "New Project" gains a template
  picker so you can start, say, a "Recipe" project with the right buckets and
  rules already in place.
- **Views / creation-types (output side).** A renderer that projects an
  *existing* project's slips into an artifact — **Markdown, Markdown → PDF, or
  HTML → PDF** — based on the data available. View configuration (which buckets
  map to which sections) lives as metadata with the project, the same way
  per-bucket TSV headers already do.

Today's compile formats (Formatted / Plain / TSV) are the first three views, just
hardcoded.

A single creation-type such as "Recipe" plausibly ships **both**: a template
(start with Ingredients / Steps / Notes buckets) and a view (render those slips
as a recipe document → PDF).

## Views are projections

The firm principle: **slips are the only truth.** A view renders (and sometimes
edits) slips, but never persists its own authoritative content. This keeps the
archive durable and view-agnostic, and makes views shareable like themes.

This also serves the archival philosophy: nothing captured is junk, slips are
living documents enrichable years later, and cleanup mechanics (pop, replay
consumption) are workspace hygiene only — they never destroy history.

## Handoff: "Open in kastn"

So you don't have to navigate away to launch the app, Zetl's compile dialog
gains an **Open in kastn** button. It signals kastn with the project id — focus
it if kastn is already running (single-instance IPC), or launch it pointed at
that project if not. This makes "where you go when you're done" a one-click trip.

## The project lifecycle ties it together

The planned project "finish" action (a Finish button in compile, with a default
compile action so finishing can copy and/or fire the output) is the **seam**
between the two apps: you wrap a session in Zetl, it is sealed, and kastn is
where that finished session goes to become real work. Finishing also lets the
dated-default project advance to the next session of the day (e.g. a per-date
counter) instead of reusing a closed one. "Finished" is reversible — set aside,
not locked.

## Groundwork already in place

The split is incidentally well-seeded today, even though nothing is labeled for
kastn: the `Zetl.Core` / `Zetl.Runtime` / head split, head-agnostic persistence,
self-contained per-project folders under `%AppData%\Zetl`, and the theme system
as the template for versioned, shareable, import/export JSON definitions.
