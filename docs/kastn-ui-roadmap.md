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

Each lands as its own buildable, GUI-verified commit.
