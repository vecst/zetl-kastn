# Kastn Templates And Creation Types

This document records the template model and the remaining temporary-consumable
design. Current priority is tracked in
[`kastn-roadmap.md`](kastn-roadmap.md).

## Concepts

Templates are input-side project scaffolds:

- **Capture templates** create projects to collect into.
- **Consumable templates** create ordered Replay queues.

Templates may define:

- bucket names and ordering
- Standard or Replay behavior
- Pop and compile defaults
- TSV row length and starting text
- optional titled starter cards

Views are output-side projections. A **creation type** pairs one template with a
default view so a project begins with both an input structure and an intended
artifact format.

Zetl remains the sole project writer. Kastn authors templates and creation types,
but project creation and starter-slip seeding go through Zetl's domain service.

## Implemented

### Shared Template Documents

- [x] Versioned, readable `ZetlTemplateDocument` JSON shape
- [x] Forward-compatible unknown fields
- [x] Capture/Consumable type validation
- [x] Bucket settings and ordered starter cards
- [x] Validation for IDs, names, duplicates, parents, reserved buckets, kind,
      compile mode, and TSV values
- [x] Protected built-in templates
- [x] User template store under `%AppData%\Zetl\templates`
- [x] Corrupt-file quarantine and invalid-file diagnostics

`Scratch` and `Deleted` are reserved because Zetl manages them.

### Kastn Authoring

- [x] Create, edit, duplicate, and delete user templates.
- [x] Duplicate protected built-ins into editable user templates.
- [x] Edit metadata, bucket settings, and starter cards in-window.
- [x] Save an existing project as a template.
- [x] Refresh the landing catalog when template files change.

Nested parent names are represented and validated by the document shape, but
the current authoring/creation flow remains flat.

### Project Creation

- [x] Create projects from templates through Zetl commands.
- [x] Apply bucket settings and seed starter slips.
- [x] Navigate Kastn to the newly created project.
- [x] Offer the shared catalog from Zetl's New Project and held-`Ctrl+T` flows.
- [x] Preserve the fast custom-buckets path for users who do not want a
      template.

### Creation Types

- [x] Versioned creation-type documents and protected built-ins
- [x] Template and view references by stable ID
- [x] Kastn Create landing tab and authoring
- [x] Project `DefaultViewId` persisted through revision-checked IPC
- [x] Automatic default-view selection when a project opens

Creation types never own project content. They only describe how to instantiate
and initially view a project.

## Remaining Template Polish

- [ ] Import and export template files.
- [ ] Import and export view files.
- [ ] Add nested-bucket authoring and creation.
- [ ] Decide whether creation types eventually support multiple ordered default
      views.

## Temporary Consumable Templates

A temporary consumable is a reusable snippet set whose instantiated project is
disposable.

### Lifecycle

1. The durable template remains in the catalog.
2. Using it creates a fresh project and seeds its Replay queue.
3. The project becomes active in the chosen normal or Shift lane.
4. The user pastes through the queue.
5. When the project stops being active in that lane, Zetl deletes it outright.

Deactivation includes:

- the Replay queue becoming empty;
- explicitly clearing the lane;
- replacing it with another project.

After natural completion, the lane remains inactive. Reuse means firing the
template again to create a fresh queue.

### Entry Points

- Held `Ctrl+T` or `Ctrl+Shift+T` already identifies the lane. User-facing
  labels are Main and Alternate; the stored values remain Normal and Shift.
- Held `Ctrl+V` with no active project opens the picker on Consumable templates.
- Kastn's current `Use` action creates a named project from the template and can
  minimize Kastn after use. For temporary consumables, Kastn asks for Main or
  Alternate unless a default lane has been saved.

### Ownership

Zetl owns:

- instantiation and activation;
- Replay and lane state;
- disposal on deactivation;
- exclusion from compile and ordinary project surfaces.

Kastn owns:

- the `Use` action;
- lane choice;
- minimize-after-use behavior.

### Status

- [x] Add and validate a `Temporary` flag on consumable templates.
- [x] Store temporary instances as `TemporaryConsumable` projects with source
      template id and owning lane metadata.
- [x] Order disposal correctly against Replay's existing empty-queue
      Replay-to-Standard transition.
- [x] Leave the lane inactive after temporary-project disposal.
- [x] Ensure a lane switch cannot leave an orphaned temporary project.
- [x] Ensure startup recovery removes abandoned temporary instances safely.
- [x] Add explicit Kastn lane choice and an optional remembered default for
      temporary template use.
- [x] Show temporary projects in Kastn's landing project list through role
      groups (Pinned, Main, Alternate, Projects), rather than hiding them.

Done when firing a temporary consumable provides an immediately usable Replay
queue and every completion, clear, switch, restart, or failure path avoids
permanent project clutter.
