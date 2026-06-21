# Kastn UI Enhancement Roadmap

Polish pass over the Kastn workbench layout, building on the bucket/slip tree
and the per-slip include flag already landed (Phase 2 steps 1–3). The driving
idea: the **rendered View is the constant backdrop**, and a slim right pane is
where you read or edit one slip's detail.

## Target layout — three persistent panes

```text
┌─ top bar: title · counts ········· search · Filters ▾ · Close ─┐
├──────────────┬──────────────────────────────┬─────────────────┤
│ Buckets &    │  View (always visible)        │  Detail         │
│ slips tree   │  picker · Copy · Export ·     │  [ Editor |     │
│              │  Manage                       │    Details ]    │
│  • Inbox     │                               │                 │
│    the other │  Inbox                        │  <slip text>    │
│    now ◀────────── highlights the selection  │  ☑ Include      │
│    ⦸ Ctrl+C  │    the other test             │  Move  Del  Save│
│    🖼 photo   │    now                        │                 │
│  + Slip  + Bucket                             │                 │
└──────────────┴──────────────────────────────┴─────────────────┘
```

The current top-level **Slip ⇄ View** toggle goes away: View no longer leaves.
Selecting a slip in the tree fills the right Detail pane; nothing swaps the
center out.

## Enhancements

### 1. Persistent View + Detail pane (the structural change)
- The View (rendered text / picture document) is always shown in the center,
  read-only.
- The right Detail pane carries a **`Editor | Details`** segmented toggle:
  - **Editor** — the slip text editor, Include-in-views checkbox, and
    Move / Delete / Save. Selecting a slip defaults here.
  - **Details** — the rich slip inspector (Overview / Capture / Picture /
    Lifecycle / Technical).
- Removes the whole-pane Slip/View swap from step 1; the editor "lives in the
  right pane" at Detail width rather than filling the region.

### 2. Drop the inspector's slip dropdown
The "Slip details" inspector has a slip-picker `ComboBox` — redundant now that
the tree drives selection. Remove it; metadata follows the tree.

### 3. Relocate "New slip"
It sits awkwardly in the View toolbar. Move it to the tree's bottom toolbar as
`+ Slip` (adds to the selected bucket), beside `+ Bucket`. The View toolbar is
then purely about output.

### 4. Streamline the top bar
Today: a title row (title · Save as Template · Close Project) plus a wide
filter row (Search 2★ · Source · Session · Captured) with lots of dead space.
Collapse to **one row**: title + counts on the left; an inline search box, a
single **Filters ▾** popover (source / session / captured), and Close on the
right. Recovers a band of vertical space and removes the empty gaps.

### 5. Tree visual polish
- Proper type icons: folder (bucket), text (text slip), photo (picture) —
  replacing the bare label and the occasional stray `✓` expander glyph.
- Keep the excluded-slip treatment (dim + eye-off) already shipped.
- Per-bucket count badge (`12 · 2 hidden`) — pairs with step 4 of the build
  workflow.

### 6. Tree ⇄ View link (falls out of the persistent View)
With the View always visible, selecting a slip in the tree should scroll/
highlight it in the rendered View, and clicking it in the View selects it in
the tree. This is the step-6 "bridge" from the build workflow, now natural.

### 7. Light slip formatting for publishing
A small formatting toolbar above the Editor — **bold**, *italic*, and
left / center / right alignment — so a slip can be dressed up enough to drop
into a shareable PDF or HTML without leaving Kastn. Intentionally modest, not a
word processor.

This is a larger item than the rest because it touches the data model and the
renderer, so it lands as its own phase, after the layout polish.

Approach (proposal, to confirm):
- **Inline emphasis → Markdown.** Bold/italic wrap the selection in `**…**` /
  `*…*`. Slip text becomes Markdown-capable on the authoring side; **capture in
  Zetl stays plain text**, and the change is opt-in per slip (plain text is
  valid Markdown). Avalonia has no native rich-text editor, so this is a
  plain `TextBox` whose toolbar inserts Markdown around the selection, not a
  WYSIWYG surface.
- **Alignment → slip metadata, not inline markup.** Markdown has no alignment,
  and alignment is almost always whole-slip, so carry it as a per-slip `Align`
  field (left/center/right, default left) the same way `ExcludedFromViews`
  rides along. The renderer applies it as a block style when emitting HTML/PDF.
- **Render flow.** The Markdown / HTML / PDF views parse slip text as Markdown
  (today they treat it as literal/escaped) and honor `Align`. The literal
  views — Formatted / Plain / TSV, which mirror Zetl's compile — keep text
  verbatim and ignore formatting, so the fast path is unchanged.
- **Open question:** whether to pull in a small Markdown→inline parser or hand-
  roll the limited subset (bold/italic/escape) we need. Lean hand-rolled first.

## Reconciliation with the build workflow

The earlier Phase 2 step list still holds for the remaining mechanics; this
roadmap re-skins where they live:
- **Step 4 (filters → tree, count badges):** the Filters popover (item 4) feeds
  the tree; badges land with item 5.
- **Step 5 (drag-and-drop):** unchanged — reorder/move in the tree, re-parent
  buckets; deletes the Move dropdown.
- **Step 6 (tree ⇄ view bridge):** becomes item 6 above.
- **Phase 3 (trash / soft-delete):** unchanged, still deferred.

## Suggested sequence

1. Layout reshape: persistent View + `Editor | Details` Detail toggle; retire
   the Slip/View toggle; drop the inspector dropdown (items 1–2).
2. Top-bar streamline + relocate New slip (items 3–4).
3. Tree icons + count badges + expander-glyph fix (item 5).
4. Filters drive the tree (workflow step 4) + Tree ⇄ View link (item 6).
5. Drag-and-drop (workflow step 5).
6. Slip formatting for publishing (item 7) — its own phase: data model
   (`Align` + Markdown-aware render) first, then the editor toolbar.

Each lands as its own buildable, GUI-verified commit.
