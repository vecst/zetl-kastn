# Large-project layout measurements

Measured October 4, 2026 on Windows, .NET 10, Avalonia 11.3.17, Debug
configuration. Baseline is `af0f53b`, before the tree panel change.

## First pass: preserve tree rows during moves

The existing projection preserved model nodes, but Avalonia's nonvirtualizing
[panel container generator](https://github.com/AvaloniaUI/Avalonia/blob/11.3.17/src/Avalonia.Controls/Presenters/PanelContainerGenerator.cs)
handles collection moves by removing and creating containers. A reversal moved
almost every slip and rebuilt the tree row templates. The reader's reused
controls were not the dominant reorder cost.

`KastnTreeItemsPanel` uses Avalonia's public container-generation hooks to move
realized controls directly within the panel. Rows remain attached, preserving
their headers, focus, selection and expansion. Inserts, removals, replacements
and resets still prepare/clear containers through the framework. It keeps no
node/container cache. Both root and nested tree item panels use this behavior.

The base class is `VirtualizingPanel` because it provides these hooks; this
implementation still realizes every row. It does not yet virtualize the viewport.

| Slips / buckets | Full reversal before | Full reversal after | Before allocations | After allocations |
| --- | ---: | ---: | ---: | ---: |
| 300 / 1 | 1,693 ms | 159 ms | 143,404 KiB | 6,671 KiB |
| 1,000 / 10 | 5,211 ms | 330 ms | 477,908 KiB | 21,860 KiB |
| 3,000 / 1 | 18,806 ms | 754 ms | 1,484,825 KiB | 64,565 KiB |

Times are medians of three reversals; allocations are mean managed bytes
allocated on the UI thread per reversal, **not retained memory**. Afterward,
every realized container survived: 301, 1,010 and 3,001 respectively. Before the change,
the separate 300-slip identity check retained only the bucket container (1/301).
The final probe also times one tree-only and reader-only reversal before the
three complete snapshot applications; baseline predates those phase timings.

At 1,000 slips, the isolated reversal spent 278 ms in tree refresh and 2 ms in
reader refresh. At 3,000 slips those values were 810 ms and 86 ms.

## Second pass: avoid hidden metadata work and unused icons

Baseline for this pass is `2a1323d`, after the tree panel fix. The selected slip
in this probe receives a backlink from every other slip. The hidden Details pane
was building thousands of buttons on every snapshot even while the editor was
showing. Deferring its controls and backlink index until Details opens removes
that work from normal editing. Opening Details resolves the current selection and
snapshot, including edits made while hidden; visible Details still refreshes.
Navigating to another slip hides metadata before rendering the destination.
Batch/bucket selections retain their empty metadata state. Retired inspector
buttons cannot navigate or open URLs after their pane becomes inactive.

Tree rows now construct two icon paths rather than seven. Geometry is shared,
and the node/visibility bindings select the displayed icons. Tooltip text, dynamic
theme brushes, structural/picture kinds and mixed bucket visibility are preserved.
Snapshot reconciliation also skips resetting an unchanged tree selection.

| Slips / buckets | Initial allocation before / after | One edit before / after | Edit allocation before / after |
| --- | ---: | ---: | ---: |
| 1,000 / 10 | 570,466 / 427,528 KiB | 188 / 50 ms | 20,684 / 2,807 KiB |
| 3,000 / 1 | 1,821,464 / 1,411,540 KiB | 366 / 90 ms | 60,863 / 7,725 KiB |

Initial allocations dropped 23–25%; edit allocations dropped 86–87% in this
backlink-heavy case. Initial timing was variable: 5.7 → 4.4 seconds at 1,000 slips
but 14.7 → 15.6 seconds at 3,000. This pass does **not** establish a consistent
initial-load latency improvement. Full reversal was 187 ms / 4,009 KiB at 1,000
slips and 450 ms / 11,409 KiB at 3,000, with every tree container retained.

The final probe separately opens Details and applies three more edits while it
is visible. It constructs 1,009 or 3,009 detail controls; visible-detail edits
still averaged about 20,365 or 60,052 KiB and had medians of 142 or 515 ms.
Projects with few backlinks will see a smaller editing benefit from deferral.

## Reader viewport rendering

Documents with at least 128 visible slips now keep lightweight rows and use
Avalonia's variable-height `VirtualizingStackPanel` inside each reader group.
It realizes note controls near the viewport, with half a viewport of overscan.
Small documents retain the existing reconciliation. Bucket headings and group
boxes keep their appearance and remain eager; an offscreen group can realize a
starter note to estimate its height, so this is not a strict global control cap
for projects with very many buckets.

Unchanged visible notes retain their controls. Edited notes refresh immediately;
offscreen edits render when visited. Eviction retires selection/link/checkbox
actions and pending picture assignments. Pictures are fetched only when their
note is realized, while the shared cache retains bitmap ownership. Offscreen
selection realizes and scrolls to its destination. Reorders preserve the first
visible note's position when it survives, and shorter filters clamp scrolling.
Exports still render the entire snapshot independently of the viewport.

Compared with `a18c46d`, the headless text probe reports:

| Slips / buckets | Reader blocks initially | Initial allocation before / after | Half-filter / clear before / after | Filter allocation before / after |
| --- | ---: | ---: | ---: | ---: |
| 1,000 / 10 | 24 / 1,000 | 427,528 / 334,469 KiB | 414 / 49 ms | 25,777 / 1,748 KiB |
| 3,000 / 1 | 15 / 3,000 | 1,411,540 / 990,036 KiB | 2,168 / 57 ms | 114,135 / 6,715 KiB |

Whole-window initial allocations fell 22–30%, and filter allocations fell
93–94%. Initial timing remains variable: 4.4 → 4.9 seconds at 1,000 slips and
15.6 → 9.4 seconds at 3,000. Single-edit medians were 46 / 126 ms versus the
previous 50 / 90 ms; this pass does not establish an edit-latency improvement.
The reader retained 25 / 16 blocks after jumping to the last note and back.

Reorders rebuild the affected group's bounded set of controls and restore a
logical anchor. This costs more allocation in the 1,000-note full reversal
(7,828 versus 4,009 KiB), with a 212 ms median versus 187 ms. At 3,000 notes,
full reversal was 420 ms / 12,009 KiB versus 450 ms / 11,409 KiB. Native layout
estimates offscreen heights; scroll extent can adjust as notes are measured.

Eight new UI cases cover bounded realization, ordinary scrolling, distant
selection and return, visible/offscreen edits, variable-height plain/container
groups, resize, filtering, project changes, reorders, latest-selection handling,
inactive readers, global ordered runs, cross-bucket navigation, retired links
and checkboxes, deferred pictures and completion after eviction. The existing
reader/action/picture regressions continue to run unchanged.

## Tree and board viewport rendering

Trees with at least 128 nodes flatten their expanded hierarchy into one viewport.
The full projection remains available for selection and commands; a dedicated
panel realizes fixed 32-pixel headers with half a viewport of overscan. Keeping
containers by model identity preserves visible rows and keyboard focus through
nearby moves, while collapsed buckets keep their expansion state after eviction.
Keyboard navigation, range selection and Select All operate on logical nodes,
including nodes without controls. A selected hidden child remains selected when
its bucket collapses, without disturbing the editor; navigating to a hidden
child expands its ancestors. Smaller trees retain the native hierarchy.

Boards with at least 128 visible slips share the reader's variable-height
viewport primitive. Cards are realized near each column's vertical viewport,
and distant horizontal columns release their card controls. One column of
horizontal overscan supports scrolling and drag targets. Column shells, headers
and composers remain eager, preserving drafts and their scroll positions.
Returning to a column restores a logical note anchor. Picture peeks and picture
completion preserve that anchor; evicted cards cannot act or receive a late
picture assignment. Bitmap ownership remains in the shared picture cache.

Drag planning uses realized card coordinates and the full project's logical
successor, so a trailing gap in the middle of a virtualized column does not
append the note at the bucket's end. Tree insertion feedback falls back to a
visible edge when the canonical successor has no control.

Fourteen new UI cases cover bounded tree/card realization, distant selection,
latest-selection ordering, keyboard and pointer ranges across unrealized rows,
Select All with collapsed children, nested collapse, reorders, focus, resize,
column eviction and return, drafts, filtering, project changes, picture peeks,
late picture completion, native tree drag hit testing and board drop boundaries.
The native drag test renders a headless frame after scrolling so its hit-test
scene reflects the new row positions.

Compared with `0dd7a18`, the final headless text probe reports:

| Slips / buckets | Tree rows initially | Board cards initially | Initial allocation before / after | Board first-render allocation before / after |
| --- | ---: | ---: | ---: | ---: |
| 1,000 / 10 | 20 / 1,010 | 96 / 1,000 | 334,469 / 20,726 KiB | 70,972 / 12,869 KiB |
| 3,000 / 1 | 20 / 3,001 | 19 / 3,000 | 990,036 / 25,198 KiB | 382,958 / 3,712 KiB |

Whole-window initial allocations fell 94–97%, and board first-render allocations
fell 82–99%. The observed initial application was 4,942 → 599 ms and
9,394 → 485 ms; board first render was 795 → 332 ms and 2,015 → 68 ms.
These are individual startup samples, not frame-rate or native-rendering results.
The narrower board interaction harness realizes fewer cards than this wide
full-window probe; realization depends on both viewport dimensions and overscan.

Single-edit medians were 11 / 22 ms versus the prior 46 / 126 ms, but allocations
increased to 3,164 / 8,614 KiB from 2,937 / 7,905 KiB because logical viewport
models and selection are reconciled. Full reversal recreates the bounded set
whose old nodes moved out of view; only one old container survives this probe's
complete reversal. At 1,000 notes, reversal was 233 ms / 14,964 KiB versus
212 ms / 7,828 KiB; at 3,000 it was 205 ms / 19,075 KiB versus
420 ms / 12,009 KiB. Nearby moves preserve visible control identity and focus.

Filtering was 27 / 69 ms versus 49 / 57 ms, with similar allocations. Visible
Details still builds 1,009 / 3,009 controls and is outside this virtualization
pass: its edit medians were 199 / 715 ms versus 136 / 353 ms, with allocations
about 20,874 / 61,433 KiB. These measurements do not establish a uniform latency
improvement; visible backlink lists remain a follow-up target.

## Reproduce

The normal suite skips the diagnostic. Run it alone, with no concurrent build or
test process, to reduce timing noise:

```powershell
$env:ZETL_RENDER_PROBE = '1'
$env:ZETL_RENDER_PROBE_SIZE = '1000'
$env:ZETL_RENDER_PROBE_BUCKETS = '10'
try {
    dotnet test Zetl.Tests/Zetl.Tests.csproj --filter FullyQualifiedName~MeasureUiApplyPass --logger 'console;verbosity=detailed' -p:UsedAvaloniaProducts=
} finally {
    Remove-Item Env:ZETL_RENDER_PROBE, Env:ZETL_RENDER_PROBE_SIZE, Env:ZETL_RENDER_PROBE_BUCKETS -ErrorAction SilentlyContinue
}
```

Repeat with size/buckets `300`/`1` and `3000`/`1`. Each fresh process measures
initial project application, identical snapshots, one-slip edits, individual
refresh phases, reorders, half-project search/clear, text export, board rendering,
reader/tree/board realization counts and distant selection jumps, and visible Details.
Three-line text slips share a fixed capture time, link to the
first slip and are distributed round-robin across buckets. All settings, drafts,
catalogs and remembered-project
state live in a disposable directory. No connection or Zetl process is started.

These are Avalonia **headless layout** timings including dispatcher jobs; they
exclude native pixel rendering, graphics/GPU work and frame delivery. Samples
include JIT and GC variation and are diagnostic observations, not timing gates.
Raw drag-event sweeps in the headless UI and ordinary UI regressions remain part
of the suite.

## Remaining costs

Tree and board controls now follow the viewport. The full tree projection and
expanded-node list still reconcile on snapshots; board row models still cover
the full filtered result. Reader headings/group boxes and board column shells
remain eager, so projects with very many buckets retain those costs.
Large visible backlink lists still rebuild all
their controls; after opening Details, its last controls are retained while
hidden and reused if the inputs are unchanged. Rebuilds, project changes and
cleared selections retire them.

Next, measure large visible backlink lists and limiting reconciliation to changed
content. Preserve bitmap ownership while measuring
picture-heavy projects separately; this text-only probe does not characterize
image decoding or a suitable bitmap retention budget.
