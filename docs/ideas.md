# Ideas

Future ideas worth remembering: not committed work. Committed work lives in
[`kastn-roadmap.md`](kastn-roadmap.md) and [`linux-roadmap.md`](linux-roadmap.md).
When an idea is picked up, move it to a roadmap and link it from here.

## Capture

### `@bucket` at the start of a note

Start a quick note with `@bucket` to file it in that bucket, creating the bucket
when it does not exist yet: `@reading Thinking Fast and Slow` lands in Reading.
Decision-free capture stays decision-free, while the destination can still be
chosen without touching the dialog. Questions to settle:

- Names with spaces (`@"Reading list"`, `@reading-list`, or closest match) and
  nested buckets (`@projects/zetl`).
- Whether `@project/bucket` reaches another project or only the active one.
- Typos: an unknown name creates a bucket, so a slip could quietly start a junk
  bucket. Options: create only with a distinct form (`@+name`), show the
  resolved bucket in the dialog before saving, or match case-insensitively and
  by prefix first.
- Notes that really start with `@` (a handle, an email): an escape such as
  `@@`, or only treating `@word` followed by a space as a destination.
- Whether captures (held Ctrl+C) and Board notes honour it too, or quick notes
  only.

### Different quick note and capture dialogs

Choices of dialog for quick notes and captures, rather than one fixed popup, for
example a minimal single-line box for fast notes beside the full dialog with
project and bucket pickers. Open: which styles, and whether the choice is a
setting, per lane, or per shortcut.

### Spreadsheet copies into TSV buckets

A CSV, TSV, or spreadsheet copy (Excel, LibreOffice Calc) could become a table
bucket instead of one slip: each cell a slip, the row length set from the copied
columns, and the first row as the bucket's column headers (its starting text),
so Compose's TSV output gives the table back. From a TSV bucket, Kastn could then
split it into buckets by column (one bucket per column, named by its header) or
by row. Questions:

- Where the choice appears: in the capture dialog for a table copy, or as a
  Kastn action on a captured table slip (which may keep the original slip).
- Sources: Calc's native bundle, an HTML table, tab-separated text, and CSV with
  quoting; what happens to empty and merged cells.
- Naming row buckets (first column, row number) and whether a split keeps the
  original bucket.

Table copies already capture as text plus a picture; see the dual-format
capture work.

### File references from held shortcuts

Held Ctrl+C on files makes a slip that references them; held Ctrl+X also records
where the files moved. Decisions so far and the routing design are in
[`hold-routing-discussion.md`](hold-routing-discussion.md).

### More hold shortcuts

Held Ctrl+F (find in Zetl), held Ctrl+S (checkpoint), a portable
text-interaction layer, and other candidates:
[`hold-shortcut-ideas-discussion.md`](hold-shortcut-ideas-discussion.md).

## Naming: Holdcuts

"Holdcuts" is the chosen future name for Zetl's tap/hold shortcuts (decided
October 2, 2026). It is not used in Zetl or Kastn yet; the UI and guide say
"tap and hold" or "hold shortcut" until it is rolled out. Fine to use in the
repo's docs.

## Zetl as a hold host

Other programs claiming hold gestures through Zetl, Zetl staying the only
keyboard hook: a cut-and-rename-on-paste file tool is the first example. See
[`hold-routing-discussion.md`](hold-routing-discussion.md).

## Sync across machines

Syncing slips between desktop and laptop, Syncthing first. Lukas's direction
(October 7, 2026): profiles, each its own set of files, loaded on a profile
switch; a lock file per profile saying who holds it and since when, with a way
to request it; for advanced users, a profile under git with automatic merging.
Today Zetl loads state once at startup and writes whole project files, so
naive two-way sync loses edits; the design is Lukas's to shape.

## Find and time

- **Held Ctrl+F to find across Zetl.** Lukas wants it (October 8, 2026): there
  is no good find yet. It is already planned: global search in
  [`kastn-roadmap.md`](kastn-roadmap.md#priority-3-workspace-discovery), with
  held Ctrl+F opening it once search is solid, and the hold design in
  [`hold-shortcut-ideas-discussion.md`](hold-shortcut-ideas-discussion.md#held-ctrlf-find-in-zetl).
- **A timeline view in Kastn.** Slips laid out along time, within a project or
  across projects. The planned Calendar landing mode (projects active on a
  day) is its cross-project cousin; both should share the search/Calendar
  query model, which groups slips by capture date.

## Kastn

Saved filters, tags, permanent delete, template and view import/export,
project history, inspectors, Markdown/HTML copy and print, publishing
controls, and editor tools: the Backlog in
[`kastn-roadmap.md`](kastn-roadmap.md#backlog). The long-term vision is
[`kastn.md`](kastn.md).

## Platform and packaging

- **Start at login on Windows,** a setting like Linux's autostart (a gap Lukas
  called one of the biggest).
- **A smaller Windows download:** ship Zetl and Kastn in one folder sharing one
  .NET runtime, as the AppImage does, instead of two self-contained
  executables. Probably about half the size, at the cost of a folder of many
  files.
- **Native Wayland for Avalonia,** when upstream offers it: per-monitor scaling
  (a 1.2x and a 0.9x monitor side by side) is impossible for an Xwayland app.
