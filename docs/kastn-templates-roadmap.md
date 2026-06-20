# Kastn Templates Roadmap

This is the implementation path for turning the Kastn landing-page Templates
tab into real project creation. It builds on the current decision that
templates are input-side scaffolds: they create a new, empty project with a
useful bucket structure and bucket behaviors.

Views are related but separate. A later creation type may bundle a template and
one or more views, but templates should not wait for artifact rendering.

## Template Types

Templates split by **how they are used**:

- **Capture templates** create an *empty* project to collect into. They define
  buckets, bucket settings, compile defaults, starting text, and TSV defaults,
  and never copy sample slips.
- **Consumable templates** seed an *ordered Replay queue* you paste through —
  e.g. your details into a form, field by field. Because Replay consumes and
  empties its queue, and only replays current-session slips, the **template** is
  the durable source: each use **instantiates a fresh project** from it, pastes
  through, and is discarded. The real payoff lands with user-authored consumable
  templates (your actual info baked in); built-ins ship placeholder fields.

## Template Principles

- **Capture templates create empty projects; consumable templates seed their
  payload.** A template may define buckets, nesting, bucket settings, compile
  defaults, starting text, and TSV defaults. Only consumable templates copy
  slips, and only their own declared seed fields.
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

## K8.3 Versioned Template Documents

Goal: introduce the shareable JSON shape for user templates.

- Define a versioned template document format.
- Include forward-compatible unknown-field behavior.
- Validate:
  - required name/id fields
  - duplicate bucket names where they would be ambiguous
  - missing parent bucket references
  - invalid bucket settings
  - reserved/protected bucket names such as `Deleted`
- Decide whether template documents may include:
  - default project state
  - default active bucket
  - default quick-note bucket
  - paired view ids
- Add tests for valid, invalid, and future-version documents.

Done when:

- Built-ins can be represented in the same shape as future user templates.
- A copied template document can be inspected and understood by a person.
- Invalid templates fail with actionable errors before project creation.

## K8.4 Template Store

Goal: load built-in and user templates into one catalog.

- Add a templates directory under the shared Zetl app data folder.
- Load user templates from JSON.
- Keep built-ins protected and always available.
- Handle corrupt template files without blocking Kastn startup.
- Add import/export/duplicate mechanics later, after load and validation work.

Done when:

- The Templates tab shows built-ins plus valid user templates.
- Invalid user templates are ignored with a visible diagnostic path.
- Removing a user template file removes it from the catalog after refresh.

## K8.5 Template Authoring In Kastn

Goal: let users create and edit templates deliberately.

- Add a template editor in Kastn.
- Start with bucket structure and bucket settings.
- Allow previewing the bucket tree before saving.
- Save user templates as JSON documents.
- Add duplicate-from-built-in so users can customize protected presets safely.

Done when:

- A user can create a template without hand-editing JSON.
- Built-in templates remain immutable.
- User-authored templates immediately appear in the landing Templates tab.

## K8.6 Zetl New Project Picker

Goal: bring template creation back to the fast capture side.

- Add a template picker to Zetl's New Project flow.
- Keep a quick default path for users who do not care about templates.
- Use the same template catalog and validation path as Kastn.
- Create the project through the same domain operation.

Done when:

- Zetl and Kastn create equivalent projects from the same template.
- Zetl's quick project creation remains fast.
- Template choice is optional, not a new forced decision during capture.

## K8.7 Creation Types

Goal: pair input templates with output views when views are ready.

- Define a creation type as a bundle of:
  - one template
  - zero or more default views
  - optional finish behavior
- Keep this after the view document format exists.
- Avoid making creation types the source of truth for project content.

Done when:

- A creation type can start a project and later render it without manual setup.
- The project still stores slips as the only authoritative content.
- Re-rendering uses current slips, not stale generated output.

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
