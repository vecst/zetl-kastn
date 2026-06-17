# Kastn Work Map

This is the working checklist for building Kastn as the deliberate workbench
over Zetl's project store. Product direction and the Zetl/Kastn feature split
live in [`kastn.md`](kastn.md).

## Target

- Kastn is a separate, normal windowed Avalonia application.
- Zetl remains the resident capture process and the sole writer to project
  storage.
- Kastn starts Zetl when needed, then communicates with it through versioned
  local IPC.
- Project data remains canonical, current, human-readable JSON on disk.
- A project can be active in Zetl while it is open and edited in Kastn.
- Captures and edits never silently overwrite one another.
- SQLite is deferred until measured storage behavior shows that JSON is the
  limiting factor.

## Decisions Made

- **One writer:** only the Zetl process mutates workspace or project files.
- **Kastn is a client:** Kastn sends commands to Zetl and receives snapshots and
  change notifications. It never writes project JSON directly.
- **Independent lifetime:** Kastn may start Zetl, but Zetl is not a child whose
  lifetime ends with Kastn. Closing or crashing Kastn must not stop capture.
- **JSON stays authoritative:** the readable project folder is the live store,
  not an occasional export from another source.
- **Atomic acknowledgement:** Zetl acknowledges a mutation only after the
  corresponding JSON write succeeds.
- **Conflict detection:** editable records carry revisions. Kastn sends the
  revision it edited; Zetl rejects stale same-record updates instead of
  overwriting newer content.
- **Typed capture direction:** project captures evolve into separate JSON
  collections for text, URLs, pictures, and files. The single-writer rule,
  rather than the file split, provides write safety.
- **Backend isolation:** IPC commands describe domain operations, not JSON file
  operations. A future SQLite backend can be introduced behind Zetl without
  changing Kastn's protocol or UI.

## Target Architecture

```text
Keyboard and clipboard
          |
          v
     Zetl process <---------- named-pipe IPC ----------> Kastn process
          |                     commands, snapshots,
          |                     revisions, changes
          v
 Human-readable project JSON
          |
          +-- project.json       metadata and bucket settings
          +-- text.json          text captures
          +-- urls.json          URL captures
          +-- pictures.json      picture metadata and asset paths
          +-- files.json         file metadata and asset paths
          +-- assets/            copied binary content
```

The exact typed filenames and serialized shapes are finalized during the data
model milestone. The important boundary is already fixed: Kastn talks to Zetl,
not to these files.

## Current Baseline

Already present:

- [x] `Zetl.Core` owns domain state and JSON persistence.
- [x] `Zetl.Runtime` owns portable workflow orchestration.
- [x] `Zetl.App` is the canonical Avalonia tray application.
- [x] Zetl already enforces one process per login session.
- [x] Projects already have stable IDs and self-contained folders.
- [x] Project writes are atomic.
- [x] The current per-project JSON model has been exercised with approximately
      20,000 text notes without an observed usability problem. The repeatable
      baseline is recorded in
      [`kastn-storage-baseline.md`](kastn-storage-baseline.md).

Still needed:

- [x] A domain mutation boundary that is safe to call from local IPC.
- [x] Bucket/slip revisions and a project change sequence.
- [x] A versioned IPC protocol and Zetl-hosted server.
- [x] Kastn process startup, reconnect, and single-instance behavior.
- [x] Live project snapshots and change notifications.
- [x] The Kastn application shell and workbench surfaces.
- [ ] Typed capture storage and migration.

## K0. Contracts, Invariants, And Measurements

Goal: freeze the boundaries before building either side of IPC.

- [x] Define stable IDs and revision semantics for projects, buckets, and slips.
- [x] Define the first command set:
      `ListProjects`, `GetProject`, `CreateProject`, `RenameProject`,
      `DeleteProject`,
      `AddBucket`, `UpdateBucket`, `DeleteBucket`, `AddSlip`, `UpdateSlip`,
      `MoveSlip`, and `DeleteSlip`.
- [x] Define command envelopes with protocol version, command ID, project ID,
      the expected target-record revision when applicable, and response status.
- [x] Use a project change sequence for snapshots and notifications, not a
      blanket project lock that makes unrelated captures conflict with an
      active edit.
- [x] Define project snapshot and change-event DTOs separately from mutable
      persistence models.
- [x] Place shared wire contracts in the portable `Zetl.Contracts` project so
      Kastn does not reference storage internals.
- [x] Specify behavior for stale revisions, duplicate command IDs, missing
      projects, validation failures, and unsupported protocol versions in
      [`kastn-contracts.md`](kastn-contracts.md).
- [x] Add repeatable measurements for load time, atomic write time, file size,
      and memory at representative project sizes, including 20,000 notes.
- [x] Record the initial JSON performance baseline before changing storage.

Done when:

- [x] Kastn can be designed entirely against command and snapshot contracts.
- [x] No IPC contract exposes a file path as a mutation mechanism.
- [x] Conflict and retry behavior is deterministic and testable.

## K1. Sole-Writer Mutation Service

Goal: make Zetl's state operations callable through one serialized service
without changing existing shortcut behavior.

- [x] Introduce a project service above `ZetlStateStore`.
- [x] Route existing Zetl mutations through explicit domain operations.
- [x] Serialize existing Zetl mutations and service commands through one writer
      monitor.
- [x] Increment the project change sequence after every durable mutation.
- [x] Increment bucket or slip revisions when those records change.
- [x] Accept an expected target-record revision for edits and reject stale
      updates to that record.
- [x] Deduplicate recently completed command IDs so a reconnect cannot repeat
      an `AddSlip` or another non-idempotent command.
- [x] Return updated records or the new project change sequence with successful
      responses.
- [x] Publish in-process change events after durable writes.
- [x] Keep reads consistent while a write is in progress. Reads may wait for the
      short atomic project write rather than observing a half-applied mutation.
- [x] Preserve current JSON layout and migration behavior during this milestone.

Done when:

- [x] Existing Zetl UI/runtime operations and external service commands share
      the same writer monitor and domain operations.
- [x] Parallel test callers cannot produce lost updates or malformed JSON.
- [x] A simulated retry cannot create a duplicate slip.
- [x] A stale edit receives a conflict response containing the current record.

## K2. Zetl IPC Host

Goal: expose the mutation service and live read model to trusted local clients.

- [x] Add a local named-pipe server hosted by the running Zetl process.
- [x] Scope the endpoint to the current login session/user.
- [x] Add handshake and protocol-version negotiation.
- [x] Support request/response commands and a subscribed change-event stream.
- [x] Keep keyboard capture and UI dispatch independent from IPC work.
- [x] Bound message sizes and reject malformed messages without stopping Zetl.
- [x] Log connection, protocol, command, and persistence failures without
      logging private slip content.
- [x] Add clean shutdown and abandoned-client handling.
- [x] Add portable protocol tests plus a Windows process-level smoke test.

Done when:

- [x] A test client can list, open, and mutate a disposable project.
- [x] Two clients receive ordered project-change notifications.
- [x] Disconnecting or crashing a client does not affect capture.
- [x] Zetl remains the only process touching project files.

## K3. Kastn Process And Application Shell

Goal: establish Kastn as a real application and prove the complete connection
lifecycle.

- [x] Add `Kastn.App` as a normal Avalonia desktop application.
- [x] Give Kastn its own single-instance identity and taskbar presence.
- [x] On startup, connect to Zetl; if absent, launch Zetl and wait for readiness.
- [x] If Kastn is launched again with a project ID, focus the existing instance
      and navigate it to that project.
- [x] Reconnect automatically after a Zetl restart and request fresh snapshots.
- [x] Show a clear offline/read-only state while Zetl is unavailable.
- [x] Add the main window, menu structure, project navigator, and empty states.
- [x] Reuse shared theme tokens where appropriate without making Kastn imitate
      Zetl's popup window policy.
- [x] Add an `Open in Kastn` handoff from Zetl for a selected project.

Done when:

- [x] Launching Kastn from a cold desktop starts or finds Zetl reliably.
- [x] `Open in Kastn` opens the requested project in the one Kastn instance.
- [x] Closing Kastn leaves Zetl and capture running.
- [x] Restarting Zetl does not require restarting Kastn.

## K4. Read And Organize MVP

Goal: deliver the first useful Kastn workbench against the current text-note
model.

- [x] Browse all non-infrastructure projects without changing Zetl's active
      project lanes.
- [x] Show bucket hierarchy and ordered slips.
- [x] Add project, bucket, source, session, and date filtering.
- [x] Add text search.
- [x] Edit slip text through revision-checked commands.
- [x] Create, rename, move, and delete buckets through Zetl.
- [x] Move and delete slips through Zetl.
- [x] Preserve selection and editor state across unrelated live captures.
- [x] Present same-slip conflicts with both versions and an explicit resolution.
- [x] Add keyboard navigation and autosave behavior suitable for a dwell
      workspace.

Done when:

- [x] Zetl can capture into a project while Kastn is browsing or editing it.
- [x] New captures appear live without reloading the whole application.
- [x] Unrelated changes do not interrupt an active edit.
- [x] No Kastn workflow writes a project file directly.

**First usable Kastn milestone:** K0 through K4 form the initial MVP. It is a
separate workbench that can safely browse and organize existing text projects
while Zetl continues capturing.

## Next Docket: Active Organizing

This docket deliberately stays on the current project JSON model. The goal is
to make Kastn better for daily organization before taking on typed capture
storage, templates, or full artifact rendering.

1. **K6.1 Create slips in Kastn** - Done
   - Add a `New Slip` command in Kastn.
   - Create slips through Zetl's existing `AddSlip` IPC command.
   - Default the destination to the selected bucket, or the first active bucket
     when `All buckets` is selected.
   - Select the new slip and focus the editor after creation.

2. **K6.2 Protected Deleted bucket** - Done
   - Add a readable protected bucket kind/name for deleted slips.
   - Ensure Zetl Board, capture targets, compile, Replay, and Pop ignore the
     Deleted bucket in normal workflows.
   - Keep the bucket visible in JSON and intentionally visible in Kastn.

3. **K6.3 Soft-delete and restore** - Done
   - Change Kastn slip delete to move slips into Deleted instead of hard
     deleting them.
   - Preserve original bucket information enough to restore.
   - Add restore from Deleted back to the original bucket, falling back to
     Scratch or a chosen bucket if the original bucket no longer exists.
   - Leave permanent hard-delete / empty-trash as a separate later action.

4. **K6.4 Multi-select slip actions** - Done
   - Add multi-select in Kastn's slip list.
   - Batch move selected slips.
   - Batch soft-delete selected slips.
   - Keep single-slip edit behavior clear when multiple slips are selected.

5. **K6.5 Bucket movement polish** - Done
   - Improve parent/child bucket movement in Kastn.
   - Keep selection, editor state, and visible hierarchy stable after bucket
     moves and renames.
   - Make nested placement obvious in bucket pickers.

6. **K7.1 Read-only viewer mode** - Done
   - Add a Kastn viewer mode over the current filtered slip set.
   - Reuse compile-style grouping and indentation for an easy reading layout.
   - Keep this as a view over slips, not a second editable document.

7. **K7.2 Landing page and project cards**
   - Replace the always-visible projects pane with a Kastn landing page.
   - Add a landing toggle for Projects and Templates with template cards present
     as a future creation lane.
   - Show project cards with cheap thumbnail previews, project name, active or
     finished state, slip count, bucket count, and recent activity metadata.
   - Open a project card into the project workbench.
   - Add a `Close Project` action that unloads the current project and returns
     to the landing page without closing Kastn.
   - Keep `Open in Kastn` from Zetl as a direct handoff that bypasses the
     landing page and opens the requested project.
   - Keep thumbnails cheap: use summaries, small snippets, or cached preview
     metadata rather than loading every full project just to render cards.
   - Move project-level actions such as delete, rename, archive, and new-from-
     template toward the landing page instead of the project workbench.
   - Highest-win card finish:
     1. Replace placeholder thumbnails with cheap preview snippets. Done
     2. Add richer summary metadata to each card. Done
     3. Move project-level card actions onto the landing page.

8. **K7.3 Editable view sessions**
   - Blend the read-only viewer with an explicit multi-slip edit session.
   - Render the current filtered slip set as editable blocks inside one flowing
     view while keeping each slip's identity visible through a block handle,
     bucket label, and status.
   - Add explicit block selection so multiple visible slips can be moved or
     acted on together without relying on a separate slip list.
   - Show each block's visible order as `n/total`; later, allow typing a new
     number and shifting the surrounding slips once Zetl exposes a durable
     reorder command.
   - Track each block by slip ID, original revision, original text, draft text,
     dirty state, and conflict state.
   - Add session controls such as `Save All`, `Cancel`, dirty count, and
     per-block conflict handling.
   - Save only dirty blocks through ordinary revision-checked Zetl `UpdateSlip`
     commands.
   - Keep this as a projection over slips, not a second document that can drift
     away from project truth.

Defer for now:

- Typed capture storage and migration.
- Picture/file/URL inspectors.
- Templates and project lifecycle.
- Markdown/PDF artifact production.
- Drag/drop, unless the button/menu-based movement feels too slow after the
  batch actions land.

## Highest-Win Path

This is the short dogfood path for making Kastn feel like its own product
quickly. It favors changes that reshape daily use before deeper storage or
artifact work.

1. **Navigation foundation**
   - Let Kastn launch with no project open.
   - Add `Close Project` so the user can return to project selection without
     closing Kastn or Zetl.
   - Keep direct Zetl handoff opening the requested project immediately.

2. **Landing page and cheap project cards**
   - Replace the permanent project sidebar with a landing page.
   - Render cheap cards from project summaries and lightweight preview snippets.
   - Move project-level actions toward the cards.
   - Add a Projects/Templates toggle so the landing page is ready to become
     both an opener and a creation surface.

3. **Unified theme and settings**
   - Make Kastn load the same selected theme as Zetl.
   - Add a Kastn section to Zetl Settings.

4. **Editable view sessions**
   - Turn the viewer into a deliberate multi-slip edit surface.
   - Save dirty blocks back to their original slips through revision-checked
     Zetl commands.
   - Add a Zetl reorder command before making block order numbers editable.

5. **Richer summaries and activity**
   - Add the Zetl-side summary fields needed for cards, recent activity,
     lifecycle states, and later project history.

6. **Template lane after cards**
   - Once thumbnails, metadata, and card actions are working, turn the
     Templates landing tab from placeholders into a real project creation flow.
   - Define the template document format, default built-in templates, and
     template picker behavior before wiring creation into Zetl.

## K5. Typed Capture Log

Goal: evolve containment-based notes into typed slips without changing the
client protocol.

- [ ] Finalize the project metadata and typed JSON schemas.
- [ ] Give every slip a type, stable ID, bucket ID, capture time, revision,
      source, and session ID.
- [ ] Make buckets ordered queries over slips rather than owners of nested note
      arrays.
- [ ] Migrate existing `project.json` note content to typed text slips.
- [ ] Make migration atomic, restartable, and backed up before replacement.
- [ ] Preserve note order, bucket hierarchy, Replay review relationships, and
      session behavior.
- [ ] Add URL classification without introducing a capture-time decision.
- [ ] Add picture and file metadata plus self-contained `assets/` storage.
- [ ] Define asset naming, duplicate handling, missing-asset behavior, and
      cleanup rules.
- [ ] Keep each project folder readable, complete, and ready to share without a
      conversion step.

Done when:

- [ ] Old projects migrate with no lost notes or changed ordering.
- [ ] Zetl and Kastn can query by bucket and capture type.
- [ ] A copied project folder opens on another installation with its assets.
- [ ] Migration and typed writes remain within the recorded UX budget.

## K6. Deliberate Workbench

Goal: build the richer organizing surface that justifies Kastn as a separate
application.

- [x] Add slip creation in Kastn through the Zetl mutation service.
- [ ] Add saved filters and faceted views over type, bucket, date, source, and
      session.
- [ ] Add multi-select, batch move, batch tagging/metadata, and batch delete
      with undo or confirmation appropriate to the action.
- [ ] Make Kastn slip deletion a soft-delete workflow: batch delete moves slips
      into a protected project trash/deleted view instead of immediately
      removing them from project history.
- [x] Hide the project trash/deleted view from Zetl's quick Board and capture
      flows while keeping it visible and restorable in Kastn.
- [ ] Add click-drag movement for slips and buckets, including clear drop
      affordances for parent/child bucket placement.
- [ ] Keep the editor selection/viewer synchronized after bucket moves,
      renames, and hierarchy changes.
- [ ] Make nested bucket placement obvious in every bucket picker and tree.
- [ ] Add picture, URL, and file inspectors.
- [ ] Add cross-bucket arranging without changing the underlying capture truth.
- [ ] Add project-level history and recent activity.
- [ ] Define archival behavior so Replay and Pop cleanup never destroys history.
- [ ] Define the hard-delete/empty-trash boundary separately from ordinary
      delete so permanent removal is explicit and rare.
- [ ] Keep the Zetl Board intentionally quick-view rather than duplicating this
      workspace.

Done when:

- [ ] Kastn is materially better than the Zetl Board for sustained organization.
- [ ] All organizing operations remain projections or mutations of slips.
- [ ] No workbench-only document becomes a second authoritative copy.

## K6.5. Kastn Navigation And Landing Page

Goal: separate project selection from project organization so Kastn opens like
a workspace, not like a permanent project sidebar.

- [ ] Add a landing page for project selection when Kastn launches without a
      direct project handoff.
- [ ] Replace the in-workbench projects pane with project cards on the landing
      page.
- [ ] Add a landing-page Projects/Templates toggle so templates have a visible
      home before template creation is implemented.
- [ ] Show cheap project thumbnails using summaries, snippets, or cached
      preview metadata instead of loading every full project.
- [ ] Include project name, active/finished/archive state, slip count, bucket
      count, and recent activity on each card.
- [ ] Open a selected project into the existing buckets/slips/editor workbench.
- [ ] Add a `Close Project` action that returns to the landing page without
      shutting down Kastn or Zetl.
- [ ] Keep Zetl's `Open in Kastn` action as a direct route into the requested
      project, bypassing the landing page.
- [ ] Move project-level actions such as rename, delete, archive, and future
      new-from-template toward the landing page.
- [ ] Keep settings and theme access available from both landing and workbench
      contexts.
- [ ] Decide whether normal startup restores the last open project or always
      starts at the landing page, then make it a user setting if both behaviors
      are valuable.

Done when:

- [ ] Kastn can launch to a clear project selection screen.
- [ ] Opening, closing, and direct handoff all land in the expected view.
- [ ] The workbench no longer needs a permanent projects pane.
- [ ] Large workspaces can render the landing page within the agreed UX budget.

## K7. Views And Artifact Production

Goal: render project slips into useful documents without making rendered output
the source of truth.

- [ ] Add a Kastn viewer mode that behaves like an expanded compile preview for
      reading all selected notes in layout.
- [ ] Extract current Formatted, Plain, and TSV compile behavior into view
      definitions or shared renderer contracts.
- [ ] Add a versioned view document format.
- [ ] Map buckets and filters to named output sections.
- [ ] Render Markdown and HTML previews.
- [ ] Add Markdown-to-PDF and HTML-to-PDF output after renderer evaluation.
- [ ] Keep generated artifacts outside authoritative slip state.
- [ ] Define exactly which rendered fields may edit slips and how those edits
      map back without ambiguous round-tripping.
- [ ] Add protected presets, duplicate, import, export, validation, and
      forward-compatible unknown fields.

Done when:

- [ ] A project can produce a repeatable artifact from a shareable view.
- [ ] Re-rendering reflects current slips without manual content synchronization.
- [ ] Existing fast compile workflows remain available in Zetl.

## K8. Templates, Handoff, And Project Lifecycle

Goal: connect project creation, fast capture, finishing, and later work.

- [ ] Use the Kastn landing page Templates tab as the primary template entry
      point.
- [ ] Start with non-mutating template cards, then graduate them into project
      creation once the template schema is finalized.
- [ ] Add a versioned template document format for buckets and behaviors.
- [ ] Ship a small set of built-in templates before adding user-authored
      templates.
- [ ] Define whether templates can include default views, project lifecycle
      state, bucket settings, and sample slips.
- [ ] Author and manage templates in Kastn.
- [ ] Add a template picker to Zetl's New Project flow.
- [ ] Allow a creation type to pair a template with one or more views.
- [ ] Add reversible project states such as active, finished, and archived.
- [ ] Add Zetl's Finish action and default finish output behavior.
- [ ] Advance dated default projects after finish without reusing a finished
      session.
- [ ] Make handoff state visible in both applications.

Done when:

- [ ] A project can begin from a template in Zetl, collect slips, finish, open
      in Kastn, and render through a paired view.
- [ ] Finished means set aside, not locked or immutable.
- [ ] Reopening or reactivating a finished project is explicit and lossless.

## K8.5. Unified Settings And Theme

Goal: make Zetl and Kastn feel like two surfaces of one local system, not two
apps with unrelated preferences.

- [ ] Treat the selected theme as a global Zetl/Kastn preference.
- [ ] Load the same `settings.json` theme id, theme variant, and `themes/`
      documents in Kastn.
- [ ] Apply global theme changes across open Zetl and Kastn windows without
      requiring either app to restart.
- [ ] Keep one theme editor rather than forking separate Zetl and Kastn theme
      editors.
- [ ] Add Kastn-specific settings to the Zetl Settings window in a clearly
      labeled Kastn section.
- [ ] Store Kastn app preferences in the shared app settings document unless a
      setting is project-specific.
- [ ] Keep project-specific Kastn choices with the project or view definition,
      not in global app settings.

Done when:

- [ ] Changing the active theme in Zetl updates Kastn, and changing it from any
      future Kastn entry point updates Zetl.
- [ ] A user can discover and edit Kastn preferences from Zetl Settings.
- [ ] Backing up the app settings and theme folder preserves the visible
      behavior of both applications.

## K9. Hardening And Release

Goal: make the two-process system trustworthy for daily use.

- [ ] Fix Zetl popup/dropdown focus behavior where choosing an item beyond the
      note dialog bounds can dismiss the note on mouse-up.
- [ ] Improve Zetl bucket management so nesting buckets is discoverable and
      quick.
- [ ] Exercise startup races where Kastn and Zetl launch simultaneously.
- [ ] Test Zetl restart, Kastn restart, forced termination, and machine reboot.
- [ ] Test command retries, duplicate delivery, stale revisions, and partial
      client messages.
- [ ] Test JSON write failure, disk-full behavior, corrupt files, and migration
      recovery.
- [ ] Test large projects against the K0 baseline and investigate regressions.
- [ ] Verify that diagnostics contain enough context without captured content.
- [ ] Add packaging, installation, app identity, icons, and protocol compatibility
      policy.
- [ ] Run a dogfood period with both applications editing live projects.

Done when:

- [ ] No tested crash path loses an acknowledged mutation.
- [ ] Recovery never silently chooses between conflicting user edits.
- [ ] The project folder remains current and independently inspectable.
- [ ] Zetl capture latency remains acceptable while Kastn is active.

## SQLite Reconsideration Triggers

SQLite is not a scheduled milestone. Reconsider it only when measurements show
that the JSON implementation, not UI or query design, is the actual limit.

Useful triggers include:

- Atomic project writes become visibly disruptive at real project sizes.
- Startup or project switching cannot meet the agreed UX budget after ordinary
  indexing and caching work.
- Typed queries require repeatedly parsing enough JSON to become a measured
  bottleneck.
- Storage growth makes routine backups or sharing impractical.
- A future feature requires transaction shapes that the sole-writer service
  cannot implement safely with atomic JSON replacement.

If SQLite is introduced, Zetl remains the sole writer and the IPC protocol stays
stable. Human-readable JSON must remain an automatically current project
projection or interchange format, with freshness visible and repairable.

## Recommended Execution Order

1. Complete K0 and agree on the contracts and measurements.
2. Build K1 before adding IPC so existing Zetl tests harden the mutation
   boundary.
3. Complete K2 and test it with a small diagnostic client.
4. Build K3 and K4 to reach the first usable Kastn release.
5. Move to K5 before adding rich type-specific workbench features.
6. Build K6, then K7 and K8 according to dogfood priorities.
7. Keep K9 checks running throughout rather than leaving reliability to the end.

## Checks Kept Green

Run the existing gates after each milestone:

```powershell
dotnet build Zetl.slnx --no-restore -p:UseAppHost=false
dotnet .\Zetl.Tests\bin\Debug\net10.0\Zetl.Tests.dll
dotnet .\bin\Debug\net10.0-windows\Zetl.dll --self-test
```

Add contract, service, IPC, migration, and Kastn UI tests to these gates as their
milestones land.
