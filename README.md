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
    2026-06-05-3f2a91\assets\        hashed image assets for that project
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
- `Ctrl+T`
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
| `Ctrl+T` | Normal `Ctrl+T`, replayed on key-up. | Opens the template picker to start a fresh project from a template. A Capture/Consumable switcher shows both kinds; defaults to Capture. Quick access to your templates. |
| `Ctrl+X` | Normal cut. Does not auto-capture. | Quick note. Prefills with cut text if available, otherwise starts empty. Defaults to the project's remembered quick-note bucket, starting with `Scratch`. |
| `Ctrl+V` | Normal paste. If the active bucket is in Replay Mode, pastes the next replay item instead. | Compile when there are current-session notes. With no active project and nothing to compile, opens the template picker (same as held `Ctrl+T`) defaulting to Consumable. |
| `Ctrl+Z` | Normal undo. | Zetl undo for the normal project lane. |
| `Ctrl+Shift+B` | Normal `Ctrl+Shift+B`, replayed on key-up. | Opens the Shift Board without depending on selected text or clipboard contents. |
| `Ctrl+Shift+C` | Normal copy through Zetl's replay path. | Same as held `Ctrl+C`, but using the Shift project lane. |
| `Ctrl+Shift+P` | Normal `Ctrl+Shift+P`, replayed on key-up. | Toggles Pop Mode for the Shift lane's active bucket. |
| `Ctrl+Shift+R` | Normal `Ctrl+Shift+R`, replayed on key-up. | Toggles Replay Mode for the Shift lane's active bucket. |
| `Ctrl+Shift+T` | Normal `Ctrl+Shift+T`, replayed on key-up. | Same as held `Ctrl+T`, but starts the template's project in the Shift project lane. |
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

Finishing a project (see Compile) seals it and advances the dated default: the
next capture starts a fresh session named with a per-date counter, for example
`2026-06-01 (2)`, instead of reopening the sealed one. A finished project is set
aside, not deleted, and can be reactivated later.

## Projects, Buckets, And Notes

A project contains buckets. A bucket contains notes.

Each note stores:

- an optional card title (Kastn falls back to the note text when it is absent)
- text
- source, such as `copy`, `cut`, `compile`, or `replay`
- creation timestamp
- current session id
- optional capture origin: application, process, and window title
- content kind (`Text` or `Image`) and image asset metadata when applicable

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

## Project Templates

Zetl and kastn share one template catalog. Kastn authors, duplicates, and deletes
templates; Zetl consumes that catalog read-only.

On the Board, the primary half of `New Project` opens the normal blank-project
setup. Open the button's arrow to choose a template instead. Selecting one asks
for a new project name, then creates, selects, and activates that project in the
Board's current normal or Shift lane.

Templates carry bucket structure and behavior, including Standard or Replay
kind, Pop Mode, compile defaults, TSV headers, and row length. Either template
type can include starter cards, including title-only blank cards; Consumable
templates use their starter cards as an ordered Replay queue. Zetl loads
the protected built-ins plus versioned user template JSON files from:

```text
%AppData%\Zetl\templates\
```

The menu refreshes when the Board regains focus, so a template saved in kastn
becomes available without restarting Zetl.

Kastn reads picture content through Zetl's local IPC service rather than opening
project asset paths itself. Picture slips render inline in Read View and above
their editable captions in Edit View. Markdown and HTML exports embed PNG data
as self-contained data URIs, PDF embeds the pictures directly, and text/TSV
views retain readable picture-caption markers.

Kastn Edit View gives every card an optional title. With no explicit title the
card derives one from its note. `New` creates one title-only `Untitled` draft,
focuses its title, and redirects repeated clicks to that draft until it is named
or given note content.

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

## Capture Origin

Copy and quick-note captures can remember where the gesture began. Zetl takes
the origin snapshot at keydown, before clipboard polling or a popup can change
the foreground window. The metadata is stored with the note and shown as a
muted secondary line in the Board, for example:

```text
Microsoft Edge · Zetl roadmap — Google Docs · 10:42 AM
```

Capture-origin detail is configurable in Settings:

- `Off` stores no origin metadata.
- `Application only` stores the friendly application and process names.
- `Application and window title` also stores the foreground window title and is
  the default.

Zetl never stores the executable path. Older notes without origin metadata load
normally. Replay review notes keep the origin of the item that was captured.

Capture origin is private archival context, not part of the note text. Project
export uses a detached snapshot and can remove the complete origin envelope for
a clean sharing export without modifying the live project.

## Image Capture

With an active project, plain `Ctrl+C` can capture either Unicode text or an
image from the Windows clipboard. When the clipboard supplies both, Image
Capture v1 prefers the image. Images are normalized to PNG and stored under the
project's `assets` folder; `project.json` contains only a typed slip and a
relative asset descriptor:

- content hash and relative path
- MIME type
- pixel width and height
- byte length
- original image URL, when the image was downloaded from a copied URL

The SHA-256 content hash is also the filename, so copying the same image more
than once reuses one asset file. The Board shows a thumbnail in the note list
and a larger selected-image preview. Image slips can have an optional caption,
which is editable beside the Board preview and becomes the slip's list label.
Capture-origin metadata works the same way for text and images.

If copied text is a single `http` or `https` URL, Zetl requests that URL to test
whether it is an image. A successful image response is decoded, normalized to
PNG, and saved through the same asset pipeline; redirects are followed and the
final URL is retained as private image metadata. Downloads time out after 10
seconds and are limited to 25 MB. A failed request, non-image response, or
undecodable body falls back to the original URL as a normal text slip. This
means copying a URL while capture is active contacts that URL's server.

Some image hosts (Cloudflare-fronted forum attachments and CDNs) block Zetl's
in-process request on its TLS handshake and answer `403` no matter the request
headers, while serving the image to a browser. When the in-process fetch hits
such a bot-challenge response, Zetl retries once through the system `curl`
(present on Windows 10 1803+ and Windows 11), which negotiates a handshake those
hosts accept, then normalizes the result through the same pipeline. The retry
runs only for those blocked responses — an ordinary copied link never spawns a
process — contacts the same URL with the same 10-second and 25 MB limits, and
falls back to a text slip if `curl` is unavailable or also blocked.

Held `Ctrl+C` on an image opens the capture dialog with an image preview and an
optional caption. It uses the same project, bucket, inline bucket-creation, and
click-away behavior as text capture. Saving creates a new image slip; it does
not replace the clipboard image with the caption.

Image assets are retained when a slip is deleted so Zetl undo cannot restore a
broken reference. Dated-project consolidation copies assets before removing a
duplicate project folder, and exports include only assets referenced by exported
slips. Orphan garbage collection will be added separately.

Current v1 boundaries:

- Compile remains text-only and does not list image slips.
- Linux image clipboard capture awaits the Linux clipboard backend.

## Project Export

Use `Export` on the Board to write the selected project as a self-contained
`.zetl.zip` package. The export dialog offers:

- `Clean copy for sharing` (the default), which strips application, process,
  window-title, and downloaded-image source URL metadata.
- `Archive copy`, which retains capture provenance for private backup or transfer.

Both modes preserve project and bucket structure, note content, settings, and
timestamps. Export never changes the live project. Each versioned package
contains:

```text
manifest.json   package version, project identity, export time, privacy mode
project.json    detached project snapshot
assets/...      referenced normalized image assets, when present
```

This package layout is also the extension point for future captured-image and
file assets. Project import is not implemented yet.

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

### Finishing A Project

The compile dialog has a `Finish Project` button. Finishing seals the project
you compiled from: it is marked `Finished` and cleared from whichever lane it
occupied, so the next capture advances to a fresh dated session rather than
reopening the sealed one. Finishing produces no compile output on its own —
copy or paste first if you want the text — and is reversible: a finished project
is set aside, not locked or deleted, and can be reactivated later. The button is
available only for an active project.

A finished or archived project shows a status marker in the Board's project
picker, and selecting it reveals a `Reactivate` button (a non-active project
cannot be made lane-active until it is reactivated). Kastn surfaces the same
states on its project cards — a status badge plus an `Archive` / `Reactivate`
action — and hides archived projects from its landing behind a `Show archived`
toggle. Archiving from Kastn also clears the project from its lane, so the
non-active-means-not-capturing invariant holds however a project is sealed.

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
4. If the clipboard content matches the last current-session slip in the active
   bucket, that slip is removed. Text compares by value; images compare by
   content hash.

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

Replay supports both text and image slips. For an image item, Zetl writes PNG
and standard Windows DIB clipboard formats before sending paste. The user's
previous clipboard is restored with its original text-or-image type, and Replay
review plus undo preserve the image asset reference.

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

- **Capture origin** — whether captures store no foreground context, the application only, or the application plus window title.
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
dotnet run --project Zetl.App -- --preview=export
dotnet run --project Zetl.App -- --preview=note-image
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

The deliberate workbench is the separate `kastn` application. Its architecture
and remaining direction are documented in [`docs/kastn.md`](docs/kastn.md).
