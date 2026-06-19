# Zetl

Zetl is a native Windows tray app built on Chordl.

Chordl is the keyboard interaction layer: tap a familiar shortcut and the foreground app behaves normally; hold that same chord for a second action. Zetl is the workflow app on top of Chordl. It interprets held copy, cut, paste, board, and undo chords as project, bucket, note, Replay, compile, and clipboard actions.

The core idea is simple:

- Tap `Ctrl+C`, `Ctrl+X`, or `Ctrl+V` and the foreground app behaves normally.
- Hold the same chord for a moment; Chordl detects the hold and Zetl opens the matching capture, note, board, or compile flow.
- Use the `Ctrl+Shift` variants for a separate project lane, so one lane can be Replay/data-entry focused while the other stays normal.

Zetl is local-only. Each project is stored in its own folder under:

```text
%AppData%\Zetl\
  workspace.json                     active-project pointers (both lanes) + version
  projects\
    2026-06-05-3f2a91\project.json   one folder + json per project
    Vehicles-9c4e02\project.json
```

`workspace.json` holds only the small store-wide state: the version and which
project is active in each of the two lanes. Every project — its buckets and
notes — lives in its own `projects\<name>-<id>\project.json`. Saving a note
rewrites just that one project file, never the whole store. The folder is named
from the project name plus a short slice of its id, so renaming a project only
rewrites a field inside the json; the folder never has to move.

Older installs that still have a single `%AppData%\Zetl\state.json` are migrated
automatically on first launch: each project is split into its own folder and the
original file is renamed to `state.json.bak`.

Tiny app-wide settings are stored at:

```text
%AppData%\Zetl\settings.json
```

## Running

Build or run with the .NET SDK:

```powershell
dotnet run --project Zetl.App
```

Publish the primary self-contained Windows artifact with:

```powershell
dotnet publish Zetl.App\Zetl.App.csproj -p:PublishProfile=win-x64
```

The result is `artifacts\publish\win-x64\Zetl.exe`. The Avalonia application is
the primary Windows artifact. The old WinForms head remains available during
A7 as `Zetl.Legacy.exe`.

The app runs in the Windows tray. Use the tray menu for:

- `Open Board`
- `New Project`
- `How Zetl Works`
- `Notification History`
- `Clear Notification History`
- `Toggle Active Bucket Pop Mode`
- `Settings`
- `Quit`

Windows blocks keyboard hooks and synthetic input across integrity levels. If
hotkeys do not work in an elevated app, run Zetl elevated too. When Windows
rejects a compiled paste, Zetl keeps the text on the clipboard and reports the
privilege mismatch. Windows secure-desktop input cannot be intercepted.

## First Run

On first launch, Zetl shows a compact `How Zetl Works` guide. It introduces the distinction between Chordl, the hold-keyboard interaction, and Zetl, the note/bucket workflow built on top of it. It also explains that `Ctrl+C` and `Ctrl+V` behave normally when no project is active, while held `Ctrl+X` can still create a quick note in `Scratch`.

It also introduces the main Coldkeys:

- `Ctrl+C`
- `Ctrl+X`
- `Ctrl+V`
- `Ctrl+B`
- `Ctrl+P`
- `Ctrl+R`
- `Ctrl+Z`
- the `Ctrl+Shift` project lane

The guide is shown once, controlled by `hasSeenFirstRun` in `%AppData%\Zetl\settings.json`.

You can reopen it any time from the tray menu with `How Zetl Works`.

## Zetl Toasts

Zetl uses its own toast overlay instead of Windows balloon notifications.

Toasts appear near the bottom-right notification area, do not steal focus, and dismiss quickly so fast copy/paste workflows are not blocked by Windows' longer notification timing.

Examples:

- `Captured to Inbox in 2026-06-05.`
- `Saved to Scratch in 2026-06-05.`
- `Pasted next item from Vehicles.`
- `Vehicles replay complete.`
- `Compiled to Review in 2026-06-01.`

Save toasts name the destination project (and therefore the lane) as well as the bucket, so it is clear where a note landed when the normal and Shift lanes have different active projects.

Use the tray menu's `Notification History` item to review recent Zetl messages. Use `Clear Notification History` to empty that history.

## Activity Log

Every toast is also persisted as a note in a dedicated `Zetl Logs` project, so the activity trail survives restarts and is browsable in the Board like any other project. Notes are grouped into a bucket per day:

```text
Zetl Logs
  2026-06-05
    [09:14:02] Captured to Inbox in 2026-06-05.
    [09:14:20] Pasted next item from Vehicles.
    [09:14:21] Paste failed; Vehicles item kept.
    [09:15:03] Vehicles replay complete.
```

The `Zetl Logs` project is infrastructure: it is never made the active project, so it cannot hijack a lane. Lines are buffered and flushed every few seconds (and on exit) so logging never sits on the per-keystroke path, and retention is bounded — each day's bucket is capped and only the most recent days are kept.

This is handy for spotting issues during real use: a run of `Paste failed; … item kept.` entries, for example, means a target app rejected the synthetic paste.

## Single Instance

Only one Zetl runs per login session. A second launch detects the first (via a session-scoped mutex) and exits with a notice instead of installing a competing keyboard hook. Separate Windows sessions — for example, two RDP sessions — can each run their own Zetl.

## Chordl Timing

Chordl timing lives in `hotkeys.json`.

```json
{
  "repeatSuppressionDelayMs": 33,
  "holdDelayMs": 353
}
```

The default hold threshold is `353 ms`.

Tap-only coldkeys also tolerate a quick key-up gallop. If the target key was pressed while `Ctrl` was down, Zetl still treats it as a tap when `Ctrl` comes up before the target key, as long as the target key is released before the hold threshold.

After a chord is held, Zetl suppresses target-key repeats for about `400 ms` after `Ctrl` is released. This prevents a still-held `V`, `C`, `X`, `B`, `P`, or `R` key from leaking repeated letters into the foreground app after the coldkey action has already happened.

## Coldkeys At A Glance

Coldkeys are Chordl shortcuts: hotkeys you hold. Tapping the chord keeps the normal app behavior; holding it opens the Zetl layer.

| Coldkey | Tap | Hold |
| --- | --- | --- |
| `Ctrl+B` | Normal `Ctrl+B`, replayed on key-up. | Opens the Board without depending on selected text or clipboard contents. |
| `Ctrl+C` | Normal copy. If a project is active, changed non-empty clipboard text is captured into the active bucket. | Capture/manage. With copied text, opens the note dialog. With no copied text, opens the Board/project management flow. |
| `Ctrl+P` | Normal `Ctrl+P`, replayed on key-up. | Toggles Pop Mode for the active bucket. |
| `Ctrl+R` | Normal `Ctrl+R`, replayed on key-up. | Toggles Replay Mode for the active bucket. |
| `Ctrl+X` | Normal cut. Does not auto-capture. | Quick note. Prefills with cut text if available, otherwise starts empty. Defaults to the project's remembered quick-note bucket, starting with `Scratch`. |
| `Ctrl+V` | Normal paste. If the active bucket is in Replay Mode, pastes the next replay item instead. | Compile. Opens the compile dialog when there are current-session notes. |
| `Ctrl+Z` | Normal undo. | Zetl undo for the normal project lane. |
| `Ctrl+Shift+B` | Normal `Ctrl+Shift+B`, replayed on key-up. | Opens the Shift Board without depending on selected text or clipboard contents. |
| `Ctrl+Shift+C` | Normal copy through Zetl's replay path. | Same as held `Ctrl+C`, but using the Shift project lane. |
| `Ctrl+Shift+P` | Normal `Ctrl+Shift+P`, replayed on key-up. | Toggles Pop Mode for the Shift lane's active bucket. |
| `Ctrl+Shift+R` | Normal `Ctrl+Shift+R`, replayed on key-up. | Toggles Replay Mode for the Shift lane's active bucket. |
| `Ctrl+Shift+X` | Normal cut through Zetl's replay path. | Same as held `Ctrl+X`, but using the Shift project lane. |
| `Ctrl+Shift+V` | Normal paste through Zetl's replay path. If the Shift active bucket is in Replay Mode, pastes the next Shift-lane replay item. | Same as held `Ctrl+V`, but using the Shift project lane. |
| `Ctrl+Shift+Z` | Normal redo, replayed as `Ctrl+Shift+Z`. | Zetl undo for the Shift project lane. |

The normal lane and Shift lane have separate active projects. This lets you keep, for example, a Replay inventory-entry project on `Ctrl+Shift` while normal `Ctrl` copy/paste remains attached to a different project or no project.

## Startup And Active Projects

Zetl starts with no active project.

That matters because plain `Ctrl+C` should not silently start collecting notes just because the app is running. A project becomes active only when you explicitly start one through a held gesture or the Board.

Default project names use the current date, for example:

```text
2026-06-01
2026-06-01 Shift
```

Zetl reuses the same dated default project instead of creating duplicates each time it opens. If you rename the project, future compile output uses the updated name.

## Projects, Buckets, And Notes

A project contains buckets. A bucket contains notes.

Each note stores:

- text
- source, such as `copy`, `cut`, `compile`, or `replay`
- creation timestamp
- current session id

The Board window lets you:

- add and delete projects
- rename projects
- mark a project active or inactive
- add and delete buckets (the `Scratch` bucket cannot be renamed or deleted)
- rename buckets
- double-click a bucket to edit bucket settings
- switch the active bucket
- switch bucket kind between `Standard` and `Replay`
- toggle Pop Mode for Standard buckets
- edit and delete notes
- autosave note edits when the note editor loses focus

Keyboard shortcuts in the Board:

- `Alt+A` toggles whether the selected project is active.
- `Enter` in the bucket name box saves the bucket name.
- `Ctrl+Enter` saves the selected note if it changed, then closes the Board.

Zetl windows also use `Ctrl+Enter` as a close/complete shortcut. If a dialog has a default action such as `Save`, `Create`, `OK`, or compile `Copy to Clipboard`, `Ctrl+Enter` performs that action. Otherwise, it closes the window.

Zetl popup windows use explicit in-app `Cancel` or `Close` buttons instead of OS minimize, maximize, or close buttons. They open centered horizontally, with the top of the window placed about one-sixth of the way down the working screen.

Clicking off a popup into another app commits and closes it: the note capture dialog saves the note with whatever toggle state it is in, just like pressing its default button. Use the `Cancel` button to dismiss without saving. The compile dialog has no default action, so clicking off simply closes it. Popups detect the click-off by watching the foreground window, so this works even when the popup did not win the foreground when it first opened.

## Zetl Undo

Tap `Ctrl+Z` and `Ctrl+Shift+Z` still go to the foreground app as normal undo/redo.

Holding those chords runs Zetl undo instead:

- held `Ctrl+Z` undoes the latest normal-lane Zetl action
- held `Ctrl+Shift+Z` undoes the latest Shift-lane Zetl action

Undo is session-only and keeps the most recent 100 Zetl actions. Restarting Zetl clears the undo stack.

Current undo coverage:

- removes the last auto-captured copy note
- removes a note saved through held `Ctrl+C` or held `Ctrl+X`
- removes a compile saved to a bucket
- restores a note removed by Pop Mode
- restores a replay-consumed note to the front of its Replay bucket
- removes the replay review copy when restoring a replay-consumed note

Project and bucket management changes are not part of undo yet.

## Bucket Settings

Bucket settings are stored inline with each bucket, so moving or exporting a project carries the bucket rules with it.

Double-click a bucket in the Board to edit:

- bucket name
- default kind: `Standard` or `Replay`
- default compile mode: `Formatted`, `Plain`, or `TSV`
- TSV row length
- default starting text / TSV headers

Default kind is bucket metadata. A Replay bucket may temporarily switch its current kind back to `Standard` after the replay completes, but the saved default kind remains part of the bucket settings.

For TSV buckets, default starting text can act as headers. Put one header per line:

```text
VIN
Make
Model
Year
Mileage
```

Zetl uses those non-empty lines to infer TSV row length. In this example, row length becomes `5`.

Compiling to another bucket creates a note in the destination bucket. Future compiles of that saved note use the destination bucket's rules, not the source bucket's rules.

## Quick Notes

Held `Ctrl+X` is the quick-note path.

With no active project:

- Zetl creates or reuses today's default project.
- The note defaults to `Scratch`.
- The project is not considered started unless you enable `Start project`.
- Bucket creation is hidden until the project is started.

With an active project:

- You can choose any bucket.
- Zetl remembers the last bucket you saved a quick note to for that project.
- You can create a new bucket directly in the note dialog.
- `Alt+B` focuses the inline new-bucket field.
- `Ctrl+Enter` saves the note.

When a copied or cut text value is prefilled, Zetl adds a trailing space so you can immediately keep typing.

By default a quick note does not touch the clipboard: a held `Ctrl+X` jot is just saved, leaving whatever you had copied intact. Turn on **Quick note goes to clipboard** in Settings if you want the saved quick note placed on the clipboard. A held `Ctrl+C` copy note still keeps the clipboard in sync with your edits regardless of that setting.

## Copy Capture

Plain `Ctrl+C` still copies normally.

If an active project and active bucket exist, Zetl waits briefly for the clipboard to change. Changed, non-empty text is captured into the active bucket.

Plain `Ctrl+C` does nothing Zetl-specific when no project is active.

Held `Ctrl+C` is the project/capture path:

- with copied text: opens the note dialog
- without copied text: opens project management/Board
- when no project exists yet: starts from a dated project name and default buckets

## Compile

Held `Ctrl+V` opens the compile dialog when the active project has any notes to compile.

A `Compile from` selector at the top picks which project to compile. It defaults to the active project, but you can switch to any other project and compile it without changing which project is active in the app. Switching the source project compiles that whole project.

The notes are grouped by bucket in a checklist tree. Check a bucket to select or clear all of its notes, or check individual notes. `Select All` and `Select None` toggle the whole tree at once.

The compile dialog lets you:

- choose which project to compile from, without making it the active project
- select individual notes, or whole buckets, or everything
- choose a compile format: `Formatted`, `Plain`, or `TSV`
- set the TSV row length when TSV is selected
- preview the selected output format
- copy the compiled text to the clipboard
- paste the compiled text immediately
- paste only the last copied item
- paste selected notes unformatted
- save the selected notes into a bucket in any project, existing or new

The `Compile to` row has its own project and bucket selectors, so you can save into a different project than the one you compiled from. Type a new name in the bucket box to create it. Saving there does not change the destination project's active bucket, nor the app's active project.

By default, saving to a bucket keeps the note structure: each selected note is copied across as its own note. Check `Flatten into one note` to instead save the compiled output (in the chosen format) as a single combined note. Flattening only affects `Save to Bucket`; the clipboard and paste actions always use the compiled output shown in the preview.

When the dialog opens, its default compile format and TSV row length come from the active bucket or single scoped bucket. Notes from the active bucket start checked.

Formatted compile output looks like:

```text
Project Name

Bucket Name
First note
Second note

Another Bucket
Another note
```

`Paste Selected Plain` outputs only the selected note text, one note per line, without project or bucket headings.

`TSV` outputs the project name, then each bucket name, then optional bucket headers, then selected notes split into tab-separated rows. If the TSV row length is `5`, every 5 notes becomes one row.

Example TSV output with row length `3`:

```text
Project Name
Bucket Name
one	two	three
four	five	six
```

Line breaks and tabs inside note text are normalized to spaces for TSV output.

Compile spans the whole active project, including notes from earlier sessions. Reactivate an old project from the Board and held `Ctrl+V` can recompile everything in it. Check `This session only` in the dialog to narrow the list back to notes captured this session.

If no project is active, held `Ctrl+V` can still compile. When a `Scratch` bucket holds a current-session quick note, Zetl opens the compile dialog on that whole project — all of its buckets are available to select — without activating it. So a quick note jotted with held `Ctrl+X` is fully compilable even though it never started the project.

## Standard Buckets

`Standard` is the normal bucket kind.

Standard buckets can use Pop Mode.

### Pop Mode

Pop Mode is for "paste once, then remove it from the active bucket." It suits interrupted work: a plain paste is treated as a disposable one-off, so it pops back out of the bucket instead of cluttering what you are collecting.

Toggle Pop Mode for the active bucket by holding `Ctrl+P` (think "print" the matching item back out), or from the tray menu. `Ctrl+Shift+P` toggles it for the Shift lane.

When Pop Mode is on:

1. Tap `Ctrl+V`.
2. The foreground app pastes normally.
3. Zetl checks the clipboard shortly after paste.
4. If the clipboard text matches the last current-session note in the active bucket, that note is removed.

Only the last matching note can pop. Pop Mode and Replay Mode are exclusive: holding `Ctrl+P` on a Replay bucket switches it straight to Pop, just as `Ctrl+R` switches a Pop bucket to Replay.

## Replay Mode

`Replay` buckets paste a list back in the order you built it. Copy items into the bucket one at a time, then paste them out in sequence — the paste replays your copy order. It is ideal for ordered data entry.

Example workflow:

1. Set a bucket to `Replay` (or hold `Ctrl+R` on the active bucket).
2. Copy values in the order you want to paste them: item name, description, cost, color, etc.
3. Move to the target app.
4. Tap `Ctrl+V` repeatedly while tabbing between fields.

When the active bucket is in Replay Mode, tap `Ctrl+V` does not paste the current clipboard directly. Instead, Zetl:

1. remembers whatever you currently have on the clipboard
2. takes the oldest current-session note from the bucket
3. places it on the clipboard and sends paste to the foreground app
4. removes that note from the bucket only if Windows accepts the synthetic paste input
5. archives the consumed note into a review bucket
6. restores your remembered clipboard once the paste lands

Replay only borrows the clipboard for each paste. It does not pre-load the next item, and it puts your own clipboard back afterwards, so a plain `Ctrl+V` (or a paste in any other app) still pastes whatever you last copied — not a leftover replay item. If you copy something new mid-replay, Zetl notices and keeps your new copy instead of overwriting it.

If your Replay bucket is named `Queue`, the review bucket is named:

```text
Queue Review
```

The review bucket lets you inspect what was pasted after the list has been consumed. Review notes use source `replay`.

If Windows rejects the synthetic paste input, Zetl keeps the item in place and shows `Paste failed; [bucket] item kept.` This verifies that the paste command was queued successfully, though individual apps still may not report whether they inserted the text.

When a Replay bucket becomes empty, Zetl turns Replay off and switches it back to `Standard`. Your own clipboard is restored after the final paste, so repeated paste behaves normally again with whatever you last copied.

Replay Mode and Pop Mode cannot coexist. Turning on Replay turns Pop Mode off.

## Bucket Creation In Capture Dialogs

Held `Ctrl+C` and held `Ctrl+X` note dialogs include inline bucket creation when a project is active.

The inline bucket area lets you:

- type a new bucket name
- choose whether the new bucket is a child of the current bucket
- create it without leaving the note dialog

`Alt+B` jumps directly to the new bucket field.

With held `Ctrl+X` and no active project, only `Scratch` is available until `Start project` is enabled.

## Configuration

`hotkeys.json` defines the low-level Chordl handling.

Zetl loads an external `hotkeys.json` from beside the executable (or the source tree when run with `dotnet run`). The canonical config is also embedded in the app, so if no external file is found Zetl falls back to that embedded default and still launches. This matters for a single-file publish (`-p:PublishSingleFile=true`): the loose `hotkeys.json` next to the exe is an optional, editable override, not a launch requirement. Drop one in to customize timing or chords.

Dispatch modes:

- `None`: first physical shortcut passes through, then repeats are suppressed during the hold window.
- `Immediate`: physical shortcut is suppressed and the original action is replayed immediately.
- `TapOnly`: physical shortcut is suppressed; the original action is replayed only if released before the hold threshold.

Replay modifiers:

- default replay is `Ctrl`
- `replayModifiers: ["Ctrl", "Shift"]` replays a shifted action, used by `Ctrl+Shift+Z`

Zetl ignores injected `SendInput` events, so its own replayed copy/cut/paste actions do not recursively trigger the hook.

## Settings

The tray menu's `Settings` item edits app-wide preferences, stored in `%AppData%\Zetl\settings.json`:

- **Toast display time** — how long each toast stays on screen, in milliseconds.
- **Auto-capture on copy** — whether a plain `Ctrl+C` captures changed clipboard text into the active bucket. Turn it off to keep normal copy fully passive; held `Ctrl+C` still captures.
- **Quick note goes to clipboard** — whether a saved held `Ctrl+X` quick note places its text on the clipboard. Off by default, so a quick jot does not overwrite what you already had copied. Held `Ctrl+C` copy notes update the clipboard regardless.
- **Default project buckets** — the buckets a new project starts with (one per line), used for the dated default project and prefilled in `New Project`.
- **Default compile mode** and **default TSV row length** — applied to newly created buckets. Each bucket can still override these in its own settings.

The Avalonia Settings window also opens a live theme editor. Themes have
separate light and dark palettes plus configurable fonts, font sizes, window
padding, control spacing, and corner radius. Changes preview across every open
Avalonia window before saving. Custom themes can be duplicated, reset,
imported, and exported as versioned JSON files. The active theme is recorded in
`settings.json`; custom files live under `%AppData%\Zetl\themes`. Invalid or
missing files fall back to the built-in theme.

Zetl ships with a set of protected presets, each with a full light and dark
palette. `Zetl Default` and `Zetl Dusk` lead; Dusk was developed from the first
A7 dogfood theme and pairs warm aubergine dark surfaces, ivory text, and a
periwinkle accent with a soft paper-and-lilac light palette. The rest span warm,
cool, neutral, and playful looks — `Z-Olive`, `Z-Ember`, `Z-Tide`, `Z-Rose`,
`Z-Nord`, `Z-Sepia`, `Z-Carbon`, `Z-Mono`, `Z-Synthwave`, `Z-Matrix`, and
`Z-Bubblegum` — plus two accessibility presets: `Z-Contrast` (maximum contrast,
border-defined regions) and `Z-Colorsafe` (a colorblind-safe blue/orange pairing
that avoids red–green reliance). Editing a built-in saves a custom copy rather
than overwriting the preset.

Older `settings.json` files without these fields load with the built-in defaults. Chordl timing (`holdDelayMs`, `repeatSuppressionDelayMs`) stays in `hotkeys.json`.

## Development Previews

Preview mode uses disposable data and is development-only:

```powershell
dotnet run --project Zetl.App -- --preview=board
dotnet run --project Zetl.App -- --preview=toast
```

Shortcut-opened note capture commits on click-away. Shortcut-opened compile
cancels on click-away. Shortcut-opened Boards auto-hide, while Boards opened
from the tray stay open. On Windows, paste workflows restore the captured
foreground target before sending input.

## Development

Build:

```powershell
dotnet build Zetl.slnx
```

All projects build into one shared output directory rather than per-project
`bin` folders, so the executables sit side by side the way an installed copy
ships (`Directory.Build.props` sets this up):

```text
artifacts\bin\<Config>\
  Zetl.exe          Avalonia tray app
  Kastn.exe         workbench (finds Zetl.exe right beside it)
  Zetl.Legacy.exe   WinForms fallback
  Zetl.Tests.exe    portable test runner
```

A plain `dotnet build` produces the `.exe` apphosts; the test gate's
`-p:UseAppHost=false` builds only the `.dll`s.

Run portable and Windows-specific tests:

```powershell
dotnet run --project Zetl.Tests
dotnet run --project ZetlHotkeys.csproj -- --self-test
dotnet run --project Zetl.App -- --self-test
```

The `Zetl.App --self-test` run exercises the real Windows clipboard
(write/read round-trips, including Unicode) that the portable suite can't cover.
It captures and restores the caller's clipboard.

The code is split around the product distinction:

- `Chordl/` is a standalone class library that builds to `Chordl.dll`. It contains the tap/hold keyboard grammar: config loading, chord definitions, dispatch modes, hold timing, replay input, and the low-level event processor.
- `Zetl.Core/` owns portable state, persistence, settings, and themes.
- `Zetl.Runtime/` owns shared shortcut and workflow orchestration.
- `Zetl.App/` is the canonical Avalonia tray application.
- `ZetlHotkeys.csproj` builds `Zetl.Legacy.exe`, the temporary WinForms
  fallback retained through the A7 dogfood period.

The A7 hands-on pass is documented in
[`docs/windows-parity-checklist.md`](docs/windows-parity-checklist.md).

The longer-term direction — splitting the deliberate workbench into a separate
application, `kastn` — is sketched in [`docs/kastn.md`](docs/kastn.md). It is
forward-looking and not scheduled against A7.
