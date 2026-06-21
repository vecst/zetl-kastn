# Kastn Templates Roadmap

This is the implementation path for turning the Kastn landing-page Templates
tab into real project creation. It builds on the current decision that
templates are input-side scaffolds: they create a new project with useful bucket
structure, bucket behaviors, and optional starter cards.

Views are related but separate. A later creation type may bundle a template and
one or more views, but templates should not wait for artifact rendering.

## Template Types

Templates split by **how they are used**:

- **Capture templates** create a project to collect into. They define buckets,
  bucket settings, compile defaults, starting text, TSV defaults, and optional
  starter cards. A starter card may have a title with an empty note body.
- **Consumable templates** seed an *ordered Replay queue* you paste through —
  e.g. your details into a form, field by field. Because Replay consumes and
  empties its queue, and only replays current-session slips, the **template** is
  the durable source: each use **instantiates a fresh project** from it, pastes
  through, and is discarded. The real payoff lands with user-authored consumable
  templates (your actual info baked in); built-ins ship placeholder fields.

## Template Principles

- **Templates may create starter cards.** A template may define buckets,
  nesting, bucket settings, compile defaults, starting text, TSV defaults, and
  titled starter cards. Consumable templates replay those cards in order.
- **Zetl remains the writer.** Kastn can present templates, but project creation
  (and any seeding) still goes through Zetl's command service.
- **Built-ins come first.** Ship a small protected set before supporting
  user-authored/imported templates.
- **Readable JSON later.** User templates should eventually live as versioned
  JSON documents, similar in spirit to themes.
- **Templates are not views.** A template answers "what structure should this
  project start with?" A view answers "how should this project render?"

## K8.1 Built-In Template Catalog

Goal: replace placeholder template cards with a real, non-mutating catalog.

- Define a portable template model for built-in presets:
  - template id
  - display name
  - category/kind
  - description
  - bucket definitions
  - optional bucket settings
- Move the current hardcoded landing cards into a reusable catalog.
- Render template cards from that catalog.
- Show enough detail on the card to explain what the template creates.
- Keep cards non-mutating until creation UX is wired.

Done when:

- The Templates tab is data-driven instead of manually seeded UI strings.
- Built-in templates can be tested without launching Kastn.
- No project storage or template JSON format is required yet.

## K8.2 Create Project From Built-In Template

Goal: make a template card create a project through existing Zetl commands.

- Add a `Use Template` action to template cards.
- Prompt for a project name.
- Convert the selected built-in template into the existing
  `CreateProjectCommand` bucket definitions.
- Send the command through Kastn's existing Zetl connection.
- Navigate to the new project after successful creation.
- Keep duplicate command IDs and durable acknowledgement behavior unchanged.

Done when:

- A user can pick `Empty project`, `Draft stack`, or `Research board`, name it,
  and land in the created project.
- Creation goes through Zetl IPC and never writes JSON directly.
- Failed creation leaves no partial project visible in Kastn.

## K8.3 Versioned Template Documents — Done

Goal: introduce the shareable JSON shape for user templates.

- [x] Define a versioned template document format. `ZetlTemplateDocument` /
  `ZetlTemplateBucketDocument` live in `Zetl.Core` (namespace `ZETL`), modeled on
  the theme system, so Zetl can reuse the same catalog and validation for its own
  New Project flow (K8.6). The built-in catalog is now authored as these documents
  in `ZetlTemplateDefaults`; `KastnTemplateCatalog` is a thin facade over it.
- [x] Include forward-compatible unknown-field behavior via `[JsonExtensionData]`
  on the document and each bucket, plus an integer `Version`.
- [x] Validate (in `ZetlTemplateValidator`, returning every error, not just the
  first):
  - [x] required name/id fields
  - [x] duplicate bucket names
  - [x] missing (and self-referential) parent bucket references
  - [x] invalid bucket settings (kind, default kind, compile mode, TSV row length)
  - [x] reserved/protected bucket names (`Scratch`, `Deleted`)
  - [x] template type and capture/consumable seed rules
- [x] Add tests for valid, invalid, and future-version documents
  (`ZetlTemplateDocumentTests`), plus the existing built-in catalog tests.

Decisions on optional content:

- **Type** is stored as a readable word (`Capture` / `Consumable`), matching the
  string convention used for bucket kind and compile mode.
- **Nesting** is expressed as an optional parent bucket *name* (validated now),
  but creation is still flat: wiring nested creation through Zetl
  (resolving parent names to ids) is deferred, so built-ins stay flat.
- **Deferred fields** — default project state, default active bucket, default
  quick-note bucket, and paired view ids are intentionally *not* in v1. The first
  bucket already becomes the active capture bucket by convention; paired views
  belong to creation types (K8.7). `[JsonExtensionData]` plus the version field
  let these arrive in a later version without breaking v1 documents.

Done when:

- [x] Built-ins can be represented in the same shape as future user templates.
- [x] A copied template document can be inspected and understood by a person
  (readable JSON, covered by a round-trip test).
- [x] Invalid templates fail with actionable errors before project creation.

Not yet (next slices): loading user template JSON from disk is K8.4; the
authoring UI is K8.5.

## K8.4 Template Store — Done

Goal: load built-in and user templates into one catalog.

- [x] Add a templates directory under the shared Zetl app data folder
  (`%AppData%\Zetl\templates`, via `ZetlTemplateStore`, default path alongside
  `themes`).
- [x] Load user templates from JSON (`ZetlTemplateStore.LoadAll` returns built-ins
  plus every valid user document).
- [x] Keep built-ins protected and always available; a user file whose id
  collides with a built-in (or duplicates another user id) is skipped, not merged.
- [x] Handle corrupt template files without blocking startup: a corrupt file is
  quarantined to a `.corrupt-*` copy and skipped; an invalid (but parseable) file
  is skipped with a diagnostic. The built-ins always load regardless.
- [ ] Import/export/duplicate mechanics — deferred to authoring (K8.5), as planned.

Done when:

- [x] The Templates tab shows built-ins plus valid user templates. Kastn loads via
  `KastnTemplateCatalog.LoadAll()`.
- [x] Invalid user templates are ignored with a visible diagnostic path
  (quarantine on disk + a line on Kastn's `Console.Error` channel).
- [x] Removing a user template file removes it from the catalog after refresh:
  Kastn re-reads the store each time the Templates landing is opened or toggled.

## K8.5 Template Authoring In Kastn — Done

Goal: let users create and edit templates deliberately.

- [x] Add a template editor in Kastn as an **in-window mode that mirrors the
  project workbench** (not a separate dialog): template metadata (name, category,
  description, type) across the top, a bucket list on the left, and the selected
  bucket's settings on the right — the same shape as picking a slip in a project.
- [x] Start with bucket structure and bucket settings — per bucket: name, kind,
  compile mode, TSV row length, starting text, and ordered starter cards. The
  editor accepts `Title :: note`; the note side may be blank.
- [x] See the structure while editing: the left bucket list updates live as
  buckets are added, renamed, and reordered.
- [x] Save user templates as JSON documents through `ZetlTemplateStore.Save`
  (validated first; built-in ids refused). New templates get an id from their name.
- [x] Add duplicate-from-built-in (`ZetlTemplateDefaults.Duplicate`) so users can
  customize protected presets safely. Edit works on a clone; delete is confirmed.
- [x] Start a template from an existing project: a **Save as Template** button in
  the project view grabs that project's bucket structure and text cards (including
  optional titles) into a new draft, minus reserved buckets and replay links.

Landing wiring: a `+ New Template` button plus per-card `Edit` / `Duplicate` /
`Delete` (Edit and Delete only on user cards; Duplicate on every card). New / Edit
/ Duplicate / Save-as-Template all switch the window into the editor view; Save and
Cancel return to the templates tab (or the open project), and the tab re-reads the
store so authored templates appear immediately.

Done when:

- [x] A user can create a template without hand-editing JSON.
- [x] Built-in templates remain immutable (edited/duplicated via a copy; the store
  refuses to save or delete a built-in id).
- [x] User-authored templates immediately appear in the landing Templates tab.

Not yet (deferred): import/export of template files, and nested-bucket authoring
(templates still create flat buckets — see K8.3).

## K8.6 Zetl New Project Picker — Done

Goal: bring template creation back to the fast capture side.

- [x] Add a template picker to Zetl's New Project flow. `ProjectSetupWindow` gains
  an optional "Start from" selector; the tray New Project passes the catalog in.
- [x] Keep a quick default path for users who do not care about templates. The
  selector defaults to "Custom buckets" — the existing name/bucket-lines flow,
  untouched — and the Board's quick add-project stays template-free.
- [x] Use the same template catalog and validation path as Kastn. The host loads a
  shared `ZetlTemplateStore`; the picker lists built-ins plus user templates.
- [x] Create the project through the same domain operation: the template path runs
  `projectService` `CreateProject` (applying bucket settings) plus `AddSlip`
  seeding — the identical sequence Kastn uses.

Done when:

- [x] Zetl and Kastn create equivalent projects from the same template (both go
  through the same `ToCreateProjectCommand` + `AddSlip` service path, covered by
  the existing service/catalog tests).
- [x] Zetl's quick project creation remains fast: "Custom buckets" is the default
  and bypasses templates entirely.
- [x] Template choice is optional, not a new forced decision during capture.

Scope note: the picker is wired into the canonical tray New Project flow (which
owns the project service and is normal-lane). The Board's add-project remains the
quick names-only path.

## K8.7 Creation Types — Done

Goal: pair input templates with output views.

- [x] Define a creation type as a bundle of one template plus default view(s)
  (`ZetlCreationTypeDocument` / `ZetlCreationTypeDefaults` / validator /
  `ZetlCreationTypeStore`, mirroring the template/view systems; one built-in,
  "Research report"). Finish behavior is left for the lifecycle work.
- [x] Built after the view document format, referencing template + view by id.
- [x] Never the source of truth for project content — a creation type only
  references a template and views; using it creates a project via the existing
  template path and sets the project's default view.

Mechanics: a project now persists a `DefaultViewId` (Zetl project metadata, a
revision-checked `SetProjectView` IPC command, surfaced on the snapshot). Using a
creation type creates the project from its template, seeds it, sets its default
view, and navigates there; opening any project auto-selects its default view.
Kastn's landing gains a **Create** tab with creation-type cards (Use / Edit /
Duplicate / Delete / + New) and an in-window editor (name, category, description,
template picker, view picker).

Done when:

- [x] A creation type can start a project and later render it without manual setup
  (the default view persists on the project and is auto-selected on open).
- [x] The project still stores slips as the only authoritative content.
- [x] Re-rendering uses current slips, not stale generated output (views are live
  projections).

## First Build Slice

The next implementation slice should be **K8.1 plus the smallest part of
K8.2**:

1. Create a reusable built-in template catalog.
2. Render the Templates tab from that catalog.
3. Add `Use Template` to each card.
4. Prompt for a project name.
5. Create the project through the existing `CreateProjectCommand`.

This is attractive because it gives real value without inventing the full JSON
template store yet. The current IPC contract already supports the bucket
definition shape needed for built-in templates.
