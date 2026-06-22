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

### 1. Persistent View + Detail pane (the structural change) — Done
- The View (rendered text / picture document) is always shown in the center,
  read-only.
- The right Detail pane carries a **`Editor | Details`** segmented toggle:
  - **Editor** — the slip text editor, Include-in-views checkbox, and
    Move / Delete / Save. Selecting a slip defaults here.
  - **Details** — the rich slip inspector (Overview / Capture / Picture /
    Lifecycle / Technical).
- Removes the whole-pane Slip/View swap from step 1; the editor "lives in the
  right pane" at Detail width rather than filling the region.

### 2. Drop the inspector's slip dropdown — Done
The "Slip details" inspector has a slip-picker `ComboBox` — redundant now that
the tree drives selection. Remove it; metadata follows the tree.

### 3. Relocate "New slip" — Done
It sits awkwardly in the View toolbar. Move it to the tree's bottom toolbar as
`+ Slip` (adds to the selected bucket), beside `+ Bucket`. The View toolbar is
then purely about output.

### 4. Streamline the top bar — Done
Today: a title row (title · Save as Template · Close Project) plus a wide
filter row (Search 2★ · Source · Session · Captured) with lots of dead space.
Collapse to **one row**: title + counts on the left; an inline search box, a
single **Filters ▾** popover (source / session / captured), and Close on the
right. Recovers a band of vertical space and removes the empty gaps.

### 5. Tree visual polish — Done
- Proper type icons: folder (bucket), text (text slip), photo (picture) —
  replacing the bare label and the occasional stray `✓` expander glyph.
- Keep the excluded-slip treatment (dim + eye-off) already shipped.
- Make the eye directly clickable on every slip. Bucket eyes recursively hide or
  show every slip in that bucket and its descendants; mixed buckets keep an open
  eye with a small accent indicator.
- Per-bucket count badge (`12 · 2 hidden`) — pairs with step 4 of the build
  workflow.

### 6. Tree ⇄ View link (falls out of the persistent View) — Done
With the View always visible, selecting a slip in the tree should scroll/
highlight it in the rendered View, and clicking it in the View selects it in
the tree. This is the step-6 "bridge" from the build workflow, now natural.
Shipped: the center View is one whole-project per-slip document
(`BuildViewDocument`); a bucket click no longer scopes it; selecting a slip
scrolls to + accent-highlights its block; clicking a block selects the slip in
the tree. The view kind now governs Copy/Export only (on-screen stays the
readable document). GUI-verified.

- **The View always renders the whole project.** Today a bucket click scopes
  `CurrentFilteredSlips()`, so selecting a bucket narrows the center View. That
  has to stop: the View is the constant whole-project document; the tree is for
  navigation, selection, and management, and a bucket selection only moves the
  highlight/scroll position in the View — it never trims what the View renders.
  (Filters and search remain the explicit ways to narrow the rendered set.)
- **Prerequisite: an addressable, element-based View.** The flat read-only
  `TextBox` can't host per-slip click/scroll/highlight, and items 7–9
  (formatting, nested outline, clickable wiki-links) all need the same thing.
  So this step re-bases the center View onto a single per-slip element document
  (unifying today's text-`TextBox` and picture-`StackPanel` paths), where each
  slip is its own selectable, clickable, scroll-target element.

### 7. Slip text formatting (the Editor toolbar)
A small formatting toolbar above the Editor so a slip can be dressed up enough
to drop into a shareable PDF or HTML without leaving Kastn. Intentionally
modest — Markdown under the hood, not a word processor.

Capabilities:
- **Inline emphasis:** bold (`**…**`), italic (`*…*`), strikethrough
  (`~~…~~`), inline code (`` `…` ``).
- **Links** (`[text](url)`): high value — slips often capture URLs, and
  clickable links in the exported PDF/HTML are nearly free once the parser is
  in.
- **Lists inside a slip:** bullet (`- `), numbered (`1. `), and checkbox / task
  (`- [ ]` / `- [x]`) lists.
- **Alignment:** left / center / right.

Approach (proposal, to confirm):
- **Markdown as the storage.** The toolbar inserts/wraps Markdown around the
  selection. Slip text becomes Markdown-capable on the authoring side;
  **capture in Zetl stays plain text**, and it's opt-in per slip (plain text is
  valid Markdown). Avalonia has no native rich-text editor, so this is a plain
  `TextBox` with a Markdown-inserting toolbar, not a WYSIWYG surface.
- **Alignment → slip metadata, not inline markup.** Markdown has no alignment,
  and it's almost always whole-slip, so carry it as a per-slip `Align` field
  (left/center/right, default left) the way `ExcludedFromViews` rides along; the
  renderer applies it as a block style in HTML/PDF.
- **Render flow.** The Markdown / HTML / PDF views parse slip text as Markdown
  (today they treat it as literal/escaped) and honor `Align`. The literal
  views — Formatted / Plain / TSV, which mirror Zetl's compile — keep text
  verbatim and ignore formatting, so the fast path is unchanged.
- Once the parser is in, headings, blockquotes, and code blocks are trivial
  later adds — no need to design each one now.
- **Open question:** pull in a small Markdown library or hand-roll the limited
  subset we need. Lean hand-rolled first, library if it gets fiddly.

### 8. View document structure (the renderer)
How a view projects the whole bucket tree into a document — distinct from
item 7, which formats a single slip's text.
- **Nested buckets → nested lists.** Sub-buckets render as nested lists with
  cascading outline numbering (1 → i → ii, or 1 → 1.1 → 1.1.1) via per-depth
  list styling in HTML/PDF.
- **Per-view list style.** Render each bucket's slips as bullets, numbered,
  checkboxes, or plain paragraphs — a setting on the view document, alongside
  the existing section mapping.
- Renderer-side only, on the Markdown / HTML / PDF views; the literal
  Formatted / Plain / TSV views are untouched.

### 9. Linked slips — wiki-links + backlinks
The missing zettelkasten piece, and the highest-leverage structural add. A slip
can reference another with `[[…]]`; the Detail pane shows a "linked from"
backlinks list. Turns the *kasten* from a folder tree into a linked knowledge
base, and composes with the Markdown plan (links land in item 7).

Link model (decided): **anchor on the slip id, cache the title for readability.**
- The stored token is `[[id|Title]]`. The **id is authoritative**; the title
  after `|` keeps the raw Markdown human-readable/inspectable.
- **Resolve at display time from the id** — Editor, View, and exports look up
  the slip by id and show its *current* title, so renaming a target updates
  every link automatically. On save/export, rewrite the cached title so even
  the raw text stays fresh.
- **Re-link = re-point the id:** invoking the `[[` picker again on an existing
  link swaps the id (and refreshes the cached title); no manual text surgery.
- **Backlinks key on the id**, so they survive renames and moves between buckets;
  they are computed (a scan for `[[id…]]`), never stored — no new persisted
  state, slips stay the source of truth.
- **Unresolved / deleted target:** id resolves to nothing → fall back to the
  cached title as a visible dashed stub (a "worth filling," not an error,
  mirroring memory links). Once soft-delete/trash lands it can still resolve to
  the trashed slip.
- Interaction detail to design at build time: how to re-trigger the picker on an
  existing link inside the plain-text editor (cursor-in-link + key/click).

### 10. Board (kanban) mode
An alternative project surface for organizing and triage: **buckets become
columns, slips become cards.** A mode toggle in the top bar swaps the center
between the List/Outline layout and the Board.
- **Costs almost no new model.** Buckets already serve as the columns/statuses;
  dragging a card across columns is `MoveSlip`, reordering within a column is
  `ReorderSlip` — both existing, sole-writer through Zetl. The Board is the most
  natural home for the planned drag-and-drop (build-workflow step 5).
- **Card:** title/preview, picture thumbnail, capture-origin line, and the
  include eye-toggle; excluded cards dim like the tree.
- **Column:** bucket name + count header, a `+ card` to add a slip there;
  adding a column = adding a bucket.
- **Detail pane stays docked** on the right, so clicking a card still edits it
  in the Editor — you don't lose authoring when you switch to the Board.
- **Open questions:** nested buckets (leaf buckets as columns, or top-level
  columns with sub-buckets as collapsible swimlanes); whether a Trash column
  ties into Phase 3 soft-delete.
- Pure projection over slips — no new persisted board state beyond bucket
  membership and order, which already exist.

### 11. Drag-and-drop in the side pane (build-workflow step 5) — Done
The tree is currently button/picker-driven (Move dropdown, parent-bucket combo).
Promote it to direct manipulation in the side pane:
- **Drag a slip** onto a bucket to move it (`MoveSlip`), or within a bucket to
  reorder it (`ReorderSlip`) — both already exist and stay sole-writer through
  Zetl.
- **Drag a bucket** onto another bucket to re-parent it, or to the root strip to
  promote it to top level (`UpdateBucket` parent change, cycle-guarded), with a
  clear drop affordance for parent/child vs. reorder.
- Respect the protected `Deleted` bucket: it is never a drop target for re-parent
  and never gets dragged.
- Keep selection, editor state, and the View highlight stable across the move,
  the same way the button-based commands already do.
- Once this lands it deletes the Move dropdown (workflow step 5 note); the Board
  (item 10) reuses the same drag plumbing for cards/columns.

Shipped (`MainWindow.DragDrop.cs`): drag a slip onto a bucket (move/append) or
onto a slip (reorder/move-before); drag a bucket onto a bucket to re-parent, or
onto empty tree space to promote to top level; `Deleted` is never dragged or a
drop target; cycles (a bucket onto its own descendant) are rejected. Each drop is
one of `MoveSlip` / `ReorderSlip` / `UpdateBucket`, and the moved slip stays
selected with the editor/inspector/View re-synced through the canonical tree
path. GUI-verified. Still pending: a live drop-indicator affordance and removing
the now-redundant Move dropdown / parent combo (kept for now as a fallback).

### Parked ideas (captured, not yet scheduled)
From the brainstorm, worth keeping but not yet sized:
- **Tags / labels** — cross-bucket faceting; design with the typed-capture-log.
- **Publishing:** auto table of contents (from the outline), front matter
  (title/subtitle/date/author), page setup (size/margins/base font), footnotes,
  Markdown tables, figure numbering + image width/alignment.
- **Frictionless output:** Copy as Markdown / Copy as HTML; direct Print.
- **Editor:** live raw/rendered preview toggle; word/character count;
  find & replace across slips.
- **View/reading:** per-view document styling (font/accent/headings) like the
  theme system; comfortable reading width + font size for the View.
- **Structural slips:** divider / heading-only slips to shape a document.
- Trivial Markdown later-adds (free once the parser is in): headings,
  blockquotes, code blocks, horizontal rules.

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
4. Filters drive the tree (workflow step 4) + Tree ⇄ View link (item 6),
   including the whole-project View re-base (bucket clicks stop scoping the
   View) and the element-based addressable View it depends on.
5. Drag-and-drop in the side pane (item 11 / workflow step 5): slips move and
   reorder, buckets re-parent; retire the Move dropdown.
6. Publishing phase (items 7–8): Markdown-aware render + `Align` data model
   first, then the Editor formatting toolbar, then view structure (outline
   numbering + list style).

Each lands as its own buildable, GUI-verified commit.
