# Kastn Project Board Roadmap

This records the proposed project-level board design and the storage/link work
that makes it safe. The current single-project Board Mode remains the bucket
and slip projection inside one project; this design is a separate portfolio
surface across projects.

## Intent

The Project Board answers a different question than the existing Board Mode:

- **Board Mode**: what is inside this project?
- **Project Board**: how do projects relate, and where should buckets or slips
  move next?

The target layout is a kanban-style board where each project is a column, each
bucket is a card, and each bucket card can expand to show its slips. Users can
drag buckets or slips between projects to split, merge, triage, archive, or
promote captured material.

## Core Model

- Project columns represent active, finished, or optionally archived projects.
- Bucket cards represent whole buckets, including nested bucket structure when
  the move/copy operation supports it.
- Expanded cards show slips as draggable rows or compact cards.
- Same-project movements continue to use existing `MoveSlip`, `ReorderSlip`,
  and `UpdateBucket` behavior.
- Cross-project movements must go through explicit Zetl-owned domain commands.

The Project Board must remain a projection over normal projects. It must not
introduce board-owned content or a second project organization model.

## Default Action Rules

Cross-project drag behavior should be explicit and conservative:

- Moving out of a project active in either lane defaults to **copy**.
- Moving out of an inactive project defaults to **move**.
- A setting can force cross-project drags to always copy.
- A modifier key or drop choice can override the default for a single action.
- Drag feedback must visibly say `Copy` or `Move` before the user drops.

The active-project rule prevents capture or Replay lanes from losing material
while they are armed. If the source project is active in the normal lane or the
Shift lane, default to copy.

## Required Domain Commands

Do not implement cross-project transfer by composing existing single-project
commands in Kastn. Zetl must remain the sole writer and must own the
cross-project mutation.

Likely command surface:

- `CopySlipToProject`
- `MoveSlipToProject`
- `CopyBucketToProject`
- `MoveBucketToProject`

Each command should include:

- source project ID
- source bucket/slip ID
- destination project ID
- destination parent bucket or bucket ID
- optional destination order anchor
- expected source and destination revisions or change sequences
- command ID for deduplication
- explicit transfer mode: `copy` or `move`

For moves, the safe storage rule is copy-then-delete, never delete-then-copy. If
the destination write succeeds and the source delete fails, the result is a
duplicate plus a diagnostic, not data loss. Recovery can retry or clean up the
source later.

## Bucket Transfer Semantics

Bucket transfer needs an explicit depth policy:

- MVP can support only leaf buckets and clearly reject buckets with children.
- Full behavior should copy or move the selected bucket subtree, preserving
  bucket settings, render kind, headings, slip order, and nested structure.
- `Scratch` and `Deleted` remain protected system buckets and cannot be moved.
- Deleted slips should stay deleted unless the user explicitly transfers from
  the deleted surface.

When moving a bucket, every transferred bucket and slip gets new IDs in the
destination project unless the transfer is part of an archive/import operation
that preserves identity. Cross-project wiki-links should be repaired according
to the link policy below.

## Workspace Asset Store

Project-level transfer becomes much cleaner if live assets move from per-project
folders to a workspace-level content-addressed store:

```text
Zetl/
  workspace.json
  assets/
    sha256-aabbcc.png
    sha256-ddeeff.png
  projects/
    Project-A/project.json
    Project-B/project.json
```

Live project JSON stores asset identity and metadata, not a project-local path:

```json
"picture": {
  "sha256": "...",
  "mimeType": "image/png",
  "width": 1200,
  "height": 800
}
```

Single-project export/import remains self-contained:

```text
project.json
assets/
  sha256-aabbcc.png
```

The live store can deduplicate across all projects, while export gathers only
the referenced assets into the package. Import copies package assets back into
the workspace asset store and rewrites nothing unless collision or migration
requires it.

Required asset work:

- migrate existing project `assets/` folders into the workspace asset store
- keep old project-local asset paths readable during migration
- scan all projects before deleting orphaned workspace assets
- detect missing assets during load without aborting the project
- keep project export/import fully self-contained

## Cross-Project Wiki-Links

Single-project links should remain pleasant and export cleanly to tools that use
standard wiki-links. Cross-project addressing should be explicit only when it is
needed.

Current local form:

```text
[[slip-id|Readable title]]
```

Potential cross-project internal form:

```text
[[project-id:slip-id|Readable title]]
[[project-id:bucket-id#slip-id|Readable title]]
```

The ID portion is authoritative. The readable title remains a cached label that
can refresh when the target is available. The parser should continue to treat
malformed or unavailable links as readable text rather than corrupting content.

Open link targets:

- slip target: exact note
- bucket target: section or context
- project target: whole source project

The first implementation can support cross-project slip links only, then expand
to bucket/project targets when export behavior is settled.

## Export Semantics

Authoring and storage can be ID-stable while export adapts to the target
ecosystem.

For a single-project Markdown/Obsidian-style export:

- links to slips inside the exported project become ordinary local wiki-links
- cross-project links not included in the export become readable stubs or plain
  text with an unresolved-link marker
- archive export preserves stable IDs and project metadata for round-trip

For a cohesive export that includes linked material from multiple projects:

- gather explicitly selected projects/slips plus included cross-project
  references
- copy referenced assets into the export package
- rewrite cross-project links to local export names
- preserve a manifest mapping export names back to source project/slip IDs

Example export rewrite:

```text
[[project-id:slip-id|Ada]]
```

could become:

```text
[[Characters/Ada|Ada]]
```

or, when exporting one note per slip:

```text
[[Ada|Ada]]
```

The exact exported path should be an exporter setting, not the live storage
syntax.

## UI Surfaces

Initial Project Board controls:

- project filters: active, finished, archived, search
- transfer mode setting: smart default, always copy
- visible copy/move drag badge
- collapsed bucket card with count, status, and recent preview
- expanded bucket card with slips
- drop targets for project column, bucket card, and slip order
- conflict/error summary when a transfer cannot complete

Archived and finished projects should be visible but not default drop targets
unless the user enables them or chooses an explicit action.

## Implementation Order

1. Add a read-only Project Board projection over projects, buckets, and slips.
2. Add workspace-level asset store with migration and export/import compatibility.
3. Add cross-project slip copy commands.
4. Add cross-project slip move commands with copy-then-delete safety.
5. Add bucket copy/move for leaf buckets.
6. Add bucket subtree transfer.
7. Add cross-project wiki-link addressing and local export rewrites.
8. Add cohesive multi-project export.

## Open Questions

- Should active temporary projects appear on the Project Board?
- Should cross-project links use project IDs only, or allow project slugs for
  readability with IDs stored in metadata?
- Should bucket transfer preserve IDs when moving, or always create new IDs
  across project boundaries?
- How much cross-project linked material should compile pull in by default?
- Should finished projects accept drops by default, or only through explicit
  "copy into finished project" actions?

## Done

The Project Board is ready when users can safely copy or move slips and buckets
between projects, active projects default to copy, no cross-project transfer can
lose an acknowledged mutation, exports remain self-contained, and ordinary
single-project Markdown exports keep clean wiki-link output.
