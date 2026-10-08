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
