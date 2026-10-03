# Hold Shortcut Ideas Discussion

> **Status — design backlog:** This discussion was recovered and reviewed in
> September 2026. It records possible post-RC directions, not committed release
> work. The root README is authoritative for implemented hold shortcuts, and
> [kastn-roadmap.md](kastn-roadmap.md) remains the product queue.


Hold shortcuts are Zetl's tap-or-hold keys. The tap keeps the user's normal
muscle memory intact; the hold asks Zetl to do the larger, project-aware action.

This note captures possible next hold shortcuts and the larger idea of advanced
user-defined hold shortcuts. It is intentionally a discussion record, not a committed
implementation plan.

## Current Baseline

Already implemented and documented:

- `Ctrl+A`: tap selects all; hold selects all and captures the selection.
- `Ctrl+B`: tap is normal board/bold behavior; hold opens Zetl Board.
- `Ctrl+C`: tap copies and may auto-capture; hold opens capture or project
  management.
- `Ctrl+J`: tap is normal; hold toggles Journal/project lane.
- `Ctrl+P`: tap is normal; hold toggles Pop Mode.
- `Ctrl+R`: tap is normal; hold toggles Replay Mode.
- `Ctrl+T`: tap is normal; hold starts a project from a template.
- `Ctrl+X`: tap cuts; hold opens quick note/capture behavior.
- `Ctrl+V`: tap pastes or replays; hold opens Compile.
- `Ctrl+Z`: tap undoes in the target app; hold undoes the latest Zetl action.

The strong pattern is: a held chord should feel like the natural Zetl extension
of the normal shortcut, not a random command that merely found an available key.

## Strong New Candidates

### Held Ctrl+F: Find In Zetl

Roadmap status: tracked as future work in
[`kastn-roadmap.md`](kastn-roadmap.md#priority-3-workspace-discovery);
implementation has not started.

Tap behavior: normal app find.

Hold behavior: open Zetl/Kastn search, ideally scoped by context:

- If Kastn is open, focus its search box.
- If Zetl is resident only, open a lightweight search window.
- Default scope could be current active project plus Journal.
- A Shift-lane hold could search the alternate active project/lane.

Wins:

- Extremely natural mapping: "find here" becomes "find across my Zetl work."
- Low conceptual burden for users.
- Likely useful from almost any app.
- Complements capture: users can collect and then retrieve without opening a
  full workbench first.

Challenges:

- Need to decide whether search lives in Zetl, Kastn, or both.
- Cross-process focus behavior needs care: held `Ctrl+F` should not steal focus
  in surprising ways when the user expected in-app find.
- Search scope needs a simple default. Too many scope choices in the hot path
  would make the gesture feel slow.
- Results need enough context to avoid opening the wrong project/slip.

Suggested first slice:

- Held `Ctrl+F` opens/focuses Kastn search when Kastn is running.
- If Kastn is not running, launch/focus Kastn and land in search.
- Search scope defaults to all active, finished, and Journal projects, with
  archived projects excluded unless explicitly toggled.

### Held Ctrl+S: Checkpoint Or Snapshot

Tap behavior: normal app save.

Hold behavior: make a durable Zetl checkpoint of the current project state.

This is less immediately magical than held `Ctrl+A` or held `Ctrl+F`, but the
mapping is clean: save means "do not lose this"; hold means "do not lose this
version."

Possible meanings:

| Option | Description | Notes |
| --- | --- | --- |
| Lightweight checkpoint | Full project state saved as a restore point | Easiest mental model |
| Export snapshot | Self-contained project package written to a snapshots folder | Most portable |
| Git-style history | Store structured diffs/history entries | Powerful, but too heavy for v1 |
| Release snapshot | Compile/export project artifacts for handoff | Useful, but more publishing-oriented |

Wins:

- Gives users confidence before restructuring, importing, template runs, or
  large edits.
- Atomic filesystem writes can make the physical save cheap and durable.
- Can reuse existing project export/import concepts.
- Opens the door to richer project history later without requiring it now.

Challenges:

- The logic contract is heavier than the filesystem write.
- Need to define exactly what is checkpointed: project metadata, buckets, slips,
  views, assets, active lane state, temporary/template/replay state.
- Asset handling must be honest. A checkpoint that later loses images is worse
  than no checkpoint.
- Restore semantics need to be conservative: replace current state, import as a
  copy, or preview before restoring?
- Schema migrations must handle old checkpoints.
- Temporary projects probably should not checkpoint by default unless the user
  explicitly asks.

Suggested first slice:

- Checkpoint is a full self-contained project export package.
- Restore imports as a copy, never overwrites the live project.
- Assets are included in the checkpoint package.
- No deltas, partial restore, or automatic pruning in v1.
- Optional prompt for a checkpoint name, defaulting to timestamp plus project.

## Advanced Custom Hold Shortcuts

Longer term, users could define a chord by choosing separate tap and hold
behaviors. This is probably more valuable than hard-coding many more defaults.

The deeper design goal is: perform a better edit right here where I am. Zetl
actions can compose on top of that later, but the foundation should be a
portable text-interaction layer rather than a pile of app-specific commands.

### Text Interaction Layer

This layer improves the user's current editing context without necessarily
opening Zetl UI:

- transpose characters
- transpose words
- select word, sentence, paragraph, or all
- expand or shrink selection
- duplicate line or selection
- move line up or down
- wrap selection with quotes, brackets, Markdown, or a wiki-link
- clean whitespace
- change case
- normalize smart quotes or bracket pairs

If this layer works well, Zetl-specific behavior becomes a composition step:

- select all, then capture
- select current word or line, then link it
- copy selection, then send it to the active bucket
- clean selection, then capture the cleaned version
- find text locally, then search Zetl when held longer

Conceptual model:

```text
Chord
Tap behavior:
  - pass through immediately
  - dispatch original shortcut on key-up
  - dispatch original shortcut immediately

Hold behavior:
  - perform a local text edit
  - transform or replace selected text
  - run a Zetl command
  - fire another synthetic shortcut
  - open a picker/dialog
  - capture, compile, search, checkpoint, or toggle a mode
```

Wins:

- Lets power users build personal workflows without bloating the default hotkey
  set.
- Keeps defaults conservative while still supporting more experimental ideas.
- Makes current `DispatchMode` behavior user-understandable instead of purely
  internal configuration.
- Could become a portable workflow layer across applications.
- Lets Zetl interaction grow out of better text interaction instead of requiring
  every hold shortcut to open a Zetl surface.

Challenges:

- UI must explain timing and dispatch behavior without sounding like keyboard
  driver documentation.
- Reliable local text edits may need different tactics per target app: synthetic
  keystrokes, selection expansion, clipboard-backed replacement, or no-op when
  the target cannot be safely understood.
- Some shortcuts have app-specific meaning; a global custom chord can surprise
  users in certain applications.
- Synthetic input safety remains important. The testing-only injected-input
  switch should not become a casual production escape hatch.
- Conflict detection matters: OS shortcuts, app shortcuts, existing Zetl
  defaults, and user-defined chords can overlap.
- Per-app profiles may eventually be necessary, but adding them too early would
  increase complexity.
- Export/import of custom hotkey settings needs versioning.

Potential guardrails:

- Keep default hold shortcuts blessed and small.
- Put custom hold shortcuts behind Advanced Settings.
- Show a plain-language preview: "Tap passes through Ctrl+A immediately; hold
  captures the selected text."
- Require explicit confirmation when overriding a default hold shortcut.
- Add a "test this hotkey" panel that shows tap, hold, pass-through, and
  dispatch behavior before saving.

## Other Candidate Hotkeys

These feel plausible, but should probably stay custom or backlog until a clear
workflow demands them.

| Hotkey | Tap | Possible hold | Concern |
| --- | --- | --- | --- |
| `Ctrl+H` | Replace | Open transform/cleanup action | Replace varies by app |
| `Ctrl+D` | Duplicate/delete line/bookmark | Duplicate active slip or capture line | Meaning varies widely |
| `Ctrl+L` | Select line/address/location | Link selected text or capture location | Browser address bar conflict |
| `Ctrl+T` | Transpose characters on Linux; Zetl template picker today | Transpose words, or bring transpose-characters behavior to Windows | Already used by Zetl templates |
| `Ctrl+Enter` | Submit/confirm | Send selection/input to active bucket | Not universal text behavior |
| `Ctrl+Backspace` | Delete previous word | Undo/pop last Zetl capture | Risky because deletion is destructive |
| `Ctrl+Arrow` | Move by word | Navigate slips/buckets | Too likely to interfere with editing |

### Ctrl+T Transpose Variant

Linux gives `Ctrl+T` a text-editing meaning: transpose characters. Zetl already
uses held `Ctrl+T` for template/project creation, so changing the default would
be disruptive. Still, transpose is a useful example for advanced custom
hold shortcuts:

- Tap `Ctrl+T`: keep or synthesize transpose characters.
- Hold `Ctrl+T`: transpose words.
- Platform option: expose transpose characters on Windows for users who want
  that editing behavior everywhere.

Wins:

- Natural "same family, larger unit" mapping: character transpose becomes word
  transpose.
- A good proof case for custom hold shortcuts that fire synthetic editing actions
  instead of only Zetl commands.
- Could make cross-platform editing behavior more consistent for users who move
  between Linux and Windows.

Challenges:

- Conflicts with the current template picker hold shortcut.
- Transpose behavior depends heavily on the target control and app. Zetl may
  need to synthesize keystrokes, edit selected text through clipboard-style
  replacement, or do nothing when the target cannot be safely inferred.
- Bringing Linux editing behavior to Windows is appealing, but should probably
  be opt-in so Zetl does not surprise Windows users who expect `Ctrl+T` to mean
  a new tab or an app-specific command.

## Recommended Priority

1. Add held `Ctrl+F` as the next first-class hold shortcut.
2. Keep held `Ctrl+S` as a checkpoint/snapshot design candidate, but do not
   implement until the restore/import contract is deliberately boring and safe.
3. Treat broader custom hold shortcuts as an Advanced Settings feature after the
   default hold shortcut model has settled.

## Open Questions

- Should held `Ctrl+F` always open Kastn, or should Zetl have its own compact
  search window?
- Should Shift-lane search mean "search the alternate active project" or simply
  "include lane context in the result filter"?
- Should checkpoints live inside the workspace, next to exports, or beside each
  project?
- Should snapshot restore always import as a copy, or should there eventually be
  an explicit destructive restore path?
- Should custom hold shortcuts be global only at first, or should per-app profiles be
  part of the initial design?
