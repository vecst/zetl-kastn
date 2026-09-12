# Kastn and the Zetl Compile Split

> **Status — historical design record:** This document supplied the product
> rationale for separating ambient Zetl capture, foreground Compile, and the
> deliberate Kastn workbench. Kastn and its revision-checked IPC relationship
> with Zetl are now implemented. Proposed mechanics here—including offline
> direct storage access, removing Compile save-back, and a separate .kstn
> bundle—are not current commitments. See [kastn.md](kastn.md),
> [kastn-workbench.md](kastn-workbench.md), and
> [kastn-roadmap.md](kastn-roadmap.md) for current behavior and priorities.


This note organizes the product direction for Zetl, its Compile workflow, and a
separate program called Kastn. It records the ideas behind the split, the
current decisions, possible designs, and questions that remain open. It is not
yet an implementation plan.

## Direction At A Glance

The product family serves three different moments:

| Product or flow | User's primary task | Core question |
| --- | --- | --- |
| Zetl | Working in another application when a thought or useful value appears | How do I capture this without leaving my work? |
| Compile | Putting captured material back into the application currently in use | Which zetls from this project should go here now? |
| Kastn | Reviewing how a capture session unfolded, then arranging or developing its material | What happened, what matters, and what should these zetls become? |

The intended boundary is:

- Zetl captures and moves zetls while staying mostly invisible.
- Compile produces immediate clipboard or foreground output from one project.
- Kastn begins as a chronological review surface and can grow into richer views,
  organization, and development.

## Origin

Zetl began with a notebook kept behind the keyboard.

Even while working at a computer, making a quick note on paper was easier than
opening a notes app, choosing where the note belonged, saving it, and returning
to the original task. Existing note apps require the user to enter the "kasten"
before making the "zettel": capture happens inside the application's ecosystem.

Zetl reverses that relationship:

> The slip exists where the thought occurs. The box can wait.

Held `Ctrl+C` captures and edits something already encountered on screen. Held
`Ctrl+X` extends the computer's existing language naturally: cut a thought from
your head into the computer. Zetl stays in the tray and tries to behave like a
small piece of operating-system behavior rather than a destination application.

This is not a literal reproduction of the historical Zettelkasten method. It
translates the method's purpose into computer-native verbs:

- Copy captures something encountered.
- Cut externalizes a thought.
- Paste puts collected material back into work.
- Replay turns an ordered stack into sequential input.
- Pop treats a slip as disposable after use.
- Spreadsheet output casts structured slips into cells.
- Undo reverses the latest slip-box action.

Replay, Pop, and spreadsheet output are not exceptions to the idea. They are
natural results of taking the computer as the medium seriously.

## Product Principle

Zetl should be faster than reaching for the notebook and should not require the
user to organize a thought before it exists.

> You should not have to enter the box to make a slip.

That principle makes invisibility a product requirement:

- Capture should not require opening a main application.
- Foreground context should be preserved.
- The common path should ask for as few decisions as possible.
- Zetl should disappear as soon as the immediate action is complete.
- Organization should usually happen after capture, not during it.

## Why Compile Grew

Compile began as a computer-workflow action: gather captured values and put them
back into the foreground application.

It gradually became the only place to organize accumulated zetls, so it gained:

- project browsing
- bucket and individual-note selection
- session filtering
- multiple output formats
- preview
- destination project and bucket routing
- structured versus flattened saves

Those capabilities combine two different modes of attention:

- "Put these zetls into what I am doing."
- "Let me sit down and work with my zetls."

The first belongs to Compile. The second belongs to Kastn. The current Compile
dialog is useful as a behavioral prototype for both, but the future products
should no longer share one crowded surface.

## Settled Boundaries

The following direction is agreed in principle:

- Kastn is a separate program, not a hotkey-opened Zetl window.
- Kastn can be launched and used independently from Zetl.
- Compile remains part of Zetl and remains available through the held-paste
  workflow.
- Compile uses one source project per invocation.
- Compile may switch to any source project, including the project active in the
  other lane.
- Compile does not combine projects.
- Compile retains fast formatted, plain-text, and spreadsheet output.
- Compile-to-project, Compile-to-bucket, and Flatten move out of Compile.
- Cross-project gathering and deliberate organization belong to Kastn.
- Compile can launch Kastn for the selected project and current session.
- Kastn's first milestone is chronological session review, not a general view
  editor.
- Kastn can compile a deliberate selection to the clipboard using the same
  output engine as Zetl Compile.
- Zetl owns content identity, membership, order, and storage. Kastn owns
  interpretation, relationships, and views.
- TSV can remain the internal representation, but normal users should see
  Spreadsheet language.
- Output settings are bucket defaults, not bucket restrictions. Compile may
  override them for one action.
- Spreadsheet behavior is a bucket preset, not a special bucket type.

## Zetl

Zetl is ambient capture and movement while some other activity remains the
user's primary task.

It owns:

- held-key capture
- quick notes
- projects and buckets as lightweight workflow context
- bucket behavior and output defaults
- Replay and Pop
- fast Compile into the foreground application
- session undo

Zetl should remain a tray application and mostly invisible. Its Board may expose
settings and management, but Zetl should not grow into the place where extended
note development happens.

## Compile

### Purpose

Compile answers:

> Which zetls from this project should go into the application I am using?

It is an endpoint for a live computer workflow. The user is still primarily
working in the foreground application, and Compile should help them send
captured material there with as little friction as possible.

### Source Scope

One Compile invocation has one source project.

- Held `Ctrl+V` starts with the normal lane's active project.
- Held `Ctrl+Shift+V` starts with the Shift lane's active project.
- A source dropdown can switch to the other lane or any other project.
- Switching projects replaces the working set; it never mixes projects.
- The newly selected project loads its own active bucket and bucket defaults.
- The source project's active bucket is the initial scope.

Being able to switch projects is part of the fast workflow, not an organization
feature. It lets a user reach the Shift project even when Compile was opened
without Shift and avoids forcing the user to close and repeat the gesture.

### Selection

The active bucket's zetls should begin selected. More detailed selection remains
available within the same source project:

- choose another bucket
- select multiple buckets
- select or clear individual zetls
- select all or none
- optionally limit the view to the current session

The common path should not require opening the full checklist. Detailed bucket
and zetl selection can live behind an expansion such as `Choose notes...`.

### Immediate Output

Compile owns output intended for the clipboard or the currently active
application:

- copy the selected output
- paste the selected output
- paste selected zetls as plain text
- paste the latest applicable zetl

Copy and Paste should complete the action and close the window. Paste restores
the captured foreground target before sending input.

Compile does not save a derived result back into Zetl. If the user wants to
arrange, transform, or preserve a new body of work, that is a Kastn workflow.

### Output Modes

Suggested user-facing output names:

- `Formatted notes`
- `Plain text`
- `Spreadsheet`

#### Formatted Notes

Formatted notes include project and bucket context around the selected zetls.
This remains useful when pasting a readable collection into a document, message,
or prompt.

#### Plain Text

Plain text includes only the selected zetl text, one item after another, without
project or bucket headings.

#### Spreadsheet

Users should not need to understand TSV, CSV, delimiters, or serialization
formats to paste structured values into a spreadsheet.

`Spreadsheet` remains TSV internally. The user-facing promise is:

> Paste into a spreadsheet and Zetl fills the cells and rows.

The relevant controls are:

- `Columns per row`
- column headings, when supplied by the bucket

Bucket `DefaultStartingText` already supports one heading per line. Those lines
can continue to provide spreadsheet column headings and infer the number of
columns.

### Bucket Defaults And Overrides

The selected bucket supplies Compile's initial output behavior:

- default output
- spreadsheet columns per row
- spreadsheet column headings

Compile allows a temporary output or column override for an ad hoc task. The
override applies only to the current action and does not silently mutate the
bucket.

Possible future affordance:

- an explicit `Save as bucket default` action

That affordance is not required for the first simplified Compile.

### Possible Compact Surface

The exact layout remains open. A representative compact state is:

```text
From: Vehicles (Shift)
Bucket: Sheet                         15 values

Output: Spreadsheet                   Columns per row: 5

[Paste]  [Copy]  [Latest]             [Choose notes...]
```

The expanded state can reveal:

- current-session filtering
- bucket and individual-zetl selection
- preview

Compile should also provide a lightweight contextual launcher such as:

- `Review in Kastn`

That action opens Kastn on the selected source project and, by default, the
current capture session. It does not turn Compile into a Kastn panel; it simply
hands off to the separate program when reviewing the material becomes the task.
It is a convenience shortcut, not Kastn's required entry point.

### Compile Does Not Own

The streamlined Compile should not contain:

- a destination project
- a destination bucket
- Save to Bucket
- Flatten into one note
- moving or copying zetls between projects
- combining multiple source projects
- durable arrangements
- long-form editing, synthesis, or export work

## Kastn

### First Purpose

Kastn is a separate, visible program entered intentionally when working with the
notes is the task.

Kastn can be opened directly from the desktop, Start menu, application launcher,
or a `.kstn` bundle. When launched independently, it can open a recent project,
browse available projects, or open a bundle. `Review in Kastn` from Compile is
only a fast contextual launch that skips project and session selection.

Its first useful question is:

> What did I capture, and how did this session take shape?

Only after that does the broader question become relevant:

> What should these zetls become?

Unlike Zetl, Kastn does not need to be invisible or disappear immediately. It
can provide a durable workspace for attention, comparison, arrangement, and
development.

### First Milestone: Session Review

The first Kastn should center on a Review mode. Compile can launch that mode
directly for the selected project and current session, while a normal Kastn
launch lets the user choose what to review.

A chronological feed is the neutral truth of a capture session. People naturally
remember temporal relationships:

- "I wrote it after that screenshot."
- "It was just before I copied the error."
- "Delete the note immediately before this image."
- "I added those values after finishing the form."

Images and other rich captures become visual landmarks that help reconstruct the
surrounding thought process.

A minimal Review surface should provide:

- the current session by default
- a toggle between the current session and all project history
- zetls in stable capture order
- light bucket grouping or bucket labels
- text rendered directly
- images rendered inline
- links rendered as clickable references
- files rendered with their name, location, and availability
- edit, delete, move, and reorder
- individual, multi-item, and contiguous-range selection
- nearby context around the selected item

This Review mode is enough to validate Kastn's product role before building
Board, Deck, Gallery, Document, or inferred-view systems.

### Compile From Review

Kastn should be able to compile a deliberate selection to the clipboard.

Example workflow:

1. Review 50 captures in chronological context.
2. Select the 15 relevant zetls.
3. Choose an output mode.
4. Copy the result to the clipboard.
5. Draft an email or document elsewhere and paste it.

The initial output modes can reuse the shared engine:

- `Copy as Formatted Notes`
- `Copy as Plain Text`
- `Copy as Spreadsheet`

The distinction between the two compile surfaces is selection context:

- Zetl Compile makes a quick selection while the foreground application remains
  the primary task.
- Kastn Review supports careful visual selection while the captured material is
  temporarily the primary task.

Neither surface needs destination-project routing or save-back compilation.

### Later Source Material

After Review is useful, Kastn can work across Zetl's organizational boundaries:

- browse all projects and buckets
- gather zetls from multiple projects
- create a working collection without changing source organization
- move or copy zetls deliberately
- return later to an arrangement

Cross-project selection belongs here because the user is intentionally
organizing material rather than completing a foreground paste.

### Later Organization And Development

Potential later Kastn workflows include:

- arranging and reordering zetls
- grouping and regrouping material
- editing and annotating zetls
- combining collections
- developing notes into documents
- developing structured material into datasets
- comparing related material from different projects
- saving durable arrangements or workspaces

These are candidate capabilities, not a committed first-version feature list.

### Later Output

Kastn may eventually produce durable or deliberate outputs:

- documents
- structured datasets
- files
- clipboard output
- reusable arrangements
- reusable transformation or export recipes

This differs from Compile output:

- Compile output is immediate and returns to the foreground workflow.
- Kastn output is the result of deliberate organization or development.

The exact Kastn export formats and recipe system remain undefined. They should
not be used to justify complexity in the simplified Compile.

### Views And Interpretation

The longer-term opportunity is to infer useful views from the shape and order of
captured data:

- mostly chronological text suggests a Feed or Timeline
- status-oriented buckets suggest a Board
- images separating groups of text suggest a Deck
- repeating values with a consistent width suggest a Spreadsheet
- mostly images suggest a Gallery
- heading-like text followed by longer notes suggests a Document

These should be suggestions, not permanent classifications. The same underlying
zetls can be viewed in several ways without being transformed or duplicated.

The system can follow this model:

> Projects contain material. Views assign meaning without taking ownership of
> it.

A project does not become a Board or a Deck. Saved views decide:

- which buckets participate
- which buckets define the primary structure
- what role each bucket has in that view
- which zetls receive view-specific arrangement or presentation metadata

The same bucket may appear in several views with different roles. For example,
`Complete` may be a Board column, a source of Deck slides, and part of a
chronological Feed.

Kastn stores this interpretation separately from Zetl's content. Zetl should
preserve stable IDs and Kastn metadata files without needing to understand view
roles, slide transitions, board columns, or annotations.

### Automatic Deck Example

A useful later Deck view may require no slide editor:

- the bucket name becomes the deck title
- text before the first image becomes an opening slide
- each image begins a new slide
- following text zetls become bullet points until the next image
- links and files become references
- bullets appear progressively with a simple default transition
- arrow keys advance bullets and slides
- `Escape` exits presentation mode

This can be exposed as `Present Bucket`. The bucket is not converted into a
slideshow; Kastn interprets its existing order as one.

### Bundles

Kastn can make a project portable as a `.kstn` bundle. The bundle is a ZIP
container with a manifest, project data, items, attachments, and optional Kastn
view metadata.

Possible structure:

```text
manifest.json
project.json
items/
attachments/
kastn/
  views.json
```

`Bundle Project` can optionally bring available external file references into
the bundle. Bundling creates a portable snapshot and does not mutate the live
project or convert its external references into managed attachments.

A lightweight Kastn viewer may be distributed beside a bundle for offline use,
but executable code should never be embedded in or run from an untrusted
`.kstn`. The bundle itself remains data-only.

Kastn should treat every imported bundle as untrusted:

- validate a strict manifest and supported version
- reject absolute paths and traversal outside the staging directory
- limit compressed size, expanded size, entry count, and compression ratio
- extract into a new staging directory without overwriting existing files
- never execute scripts, macros, plugins, or bundled programs
- never automatically open attachments or fetch URLs
- report missing or rejected resources clearly
- use antivirus scanning as defense in depth, not as the primary trust boundary

### Relationship With Zetl

Kastn should be able to work when Zetl is not running. The initial integration
boundary can be Zetl's local project storage rather than process-to-process
coordination.

Any write-back into Zetl should be deliberate and visible. Possible models
include:

- referencing live Zetl zetls
- importing snapshots into Kastn
- supporting both references and snapshots

This data relationship must be designed before Kastn gains cross-project
editing or movement.

## Shared Content Model

Zetl and Kastn benefit from a common data layer, but they should assign meaning
at different levels:

> Zetl preserves identity, membership, order, and content. Kastn supplies
> meaning, relationships, and views.

### Typed Zetls

Zetl will eventually need to capture more than text:

- text
- clipboard images
- files
- links
- unknown future item types

Every zetl should have a common envelope with:

- stable ID
- project and bucket membership
- stable explicit position
- type
- creation time and session
- content or a content reference
- provenance where available

Zetl should understand enough to capture, store, preview, move, copy, delete,
order, and compile supported content. It does not need to understand what a zetl
means in a Kastn view.

Unknown item types should remain preservable and receive a generic fallback
preview rather than being discarded.

### Attachments And File References

Clipboard images should always be copied into managed attachment storage and
referenced by the zetl.

Files can use a size-based policy:

- small files are copied into managed attachment storage
- files over a configurable threshold remain external references
- users may eventually override import versus reference behavior

Stale external references are acceptable, but Kastn should fail gracefully:

- retain the original filename and metadata
- show `File not found`
- allow the user to locate and repair the reference

Managed attachments are part of the project. External resources are things the
project points toward. Kastn should keep that distinction visible so portability
and backup behavior are understandable.

URLs should be recognized with normal URI parsing rather than regex alone.
Detection can enrich a text capture into a link-like presentation without
discarding the original captured text.

### Storage Direction

Binary attachment data should live outside JSON. One possible project shape is:

```text
project.json
items/
  z1.json
  z2.json
attachments/
  z1.png
kastn/
  views.json
```

The exact split between `project.json` and per-item JSON remains open. The
important constraints are:

- one authoritative shared envelope for every zetl
- stable ordering without reconstructing it from several type-specific lists
- atomic or recoverable mutations
- stable IDs for Kastn references
- separate Kastn metadata so Zetl does not erase meaning it does not understand

## Shared Bucket And Project Configuration

Bucket defaults and project creation are Zetl data-model concerns shared by
Compile and Kastn. They do not belong exclusively to either output surface.

### Existing Bucket Capabilities

Each bucket already stores:

- `DefaultCompileMode`
- `DefaultTsvRowLength`
- `DefaultStartingText`

The UI can present those fields without TSV terminology:

```text
Default output: Spreadsheet
Columns per row: 5
Column headings:
VIN
Make
Model
Year
Mileage
```

This lets users create spreadsheet-oriented buckets such as Vehicles, Expenses,
or Inventory without introducing a special Sheet bucket type.

### Possible Default Project

A possible default project starts with:

- `Inbox`
- `Sheet`
- `Scratch`

Possible output defaults:

- Inbox: Formatted notes
- Sheet: Spreadsheet
- Scratch: Plain text

`Sheet` is the bucket name; `Spreadsheet` is the output behavior. The distinction
keeps the bucket list concise while making the action understandable.

This default remains a proposal rather than a settled requirement.

### Project Templates

The state model can represent different settings on each instantiated bucket.
What is missing is project-template creation: current settings define bucket
names plus shared defaults, not full per-bucket definitions.

A future project template should store complete bucket definitions, for example:

```json
{
  "name": "Standard",
  "buckets": [
    {
      "name": "Inbox",
      "compileMode": "Formatted"
    },
    {
      "name": "Sheet",
      "compileMode": "TSV",
      "tsvRowLength": 3
    },
    {
      "name": "Scratch",
      "compileMode": "Plain"
    }
  ]
}
```

The UI should say Spreadsheet even if the persisted value remains `TSV`.
Existing projects and existing users' configured bucket lists should not be
silently changed.

## Migration Direction

The split can proceed incrementally:

1. Rename output concepts in the UI without changing persisted values.
2. Remove destination routing, Save to Bucket, and Flatten from Compile.
3. Keep source-project switching and make normal-lane and Shift-lane context
   visible.
4. Make the active bucket the compact default.
5. Move detailed selection and preview behind an expanded state.
6. Preserve formatted, plain-text, and spreadsheet copy/paste behavior.
7. Add full per-bucket project templates if the Inbox, Sheet, and Scratch
   direction is adopted.
8. Add `Review in Kastn` for the selected project and current session.
9. Build chronological Review with typed-item rendering and rich selection.
10. Reuse the output engine to copy a Kastn selection to the clipboard.
11. Extend the shared model to typed zetls, stable order, attachments, and file
    references.
12. Define Kastn's storage and read/write relationship with Zetl before building
    cross-project editing.
13. Add inferred or saved views only after Review reveals the next real
    friction.
14. Add secure `.kstn` bundle creation and import when portable sharing becomes
    part of the tested workflow.

## Open Questions

### Compile

- Should Compile always show a preview, or reveal it only in the expanded state?
- Should `Latest` mean the newest zetl in the project, the selected bucket, the
  selected scope, or the current session?
- How should the source dropdown identify normal-lane and Shift-lane active
  projects?
- Should a temporary output override offer `Save as bucket default`?
- How much selection should the compact surface expose before `Choose notes...`?

### Bucket And Project Defaults

- Should new projects default to Inbox, Sheet, and Scratch?
- What default column count should Sheet use before headings are configured?
- Should the first template system provide one editable default or multiple
  named templates?
- Should existing configurable bucket-name settings migrate into a full
  template automatically?

### Kastn

- Should Kastn reference live Zetl zetls, import snapshots, or support both?
- Does Kastn own moving source zetls, or only arranging references to them?
- Should Review group by bucket, use only labels, or offer both?
- What are the exact range-selection interactions for a chronological session?
- Which edits are safe to perform directly on live Zetl data in Review?
- What is the smallest useful durable Kastn workspace after Review?
- Which exports or transformations should Kastn support first?
- When should view inference become visible, and how should Kastn avoid making
  guesses feel mandatory?

### Content And Bundles

- What file-size threshold separates managed files from external references?
- Should file import/reference behavior be configurable globally, per project,
  or per capture?
- How should capture order positions be represented so insertion and reordering
  remain stable?
- Should item metadata remain in each `project.json` initially or move directly
  to per-item files?
- Which media types should the first Review milestone render inline?
- What limits should `.kstn` import enforce for archive size, expansion, and
  entry count?

## Design Tests

For future Zetl features, ask:

> Does this express slip-box thinking through a native computer action, or does
> it turn Zetl into another destination notes app?

For the Compile and Kastn boundary, ask:

> Is the user trying to put zetls into the work happening now, or have the zetls
> themselves become the work?

Immediate foreground output belongs in Compile. Powerful, visible, deliberate
note work belongs in Kastn. Zetl should remain ambient, immediate, and focused
on the work happening elsewhere.
