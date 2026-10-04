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
and visible Details. Three-line text slips share a fixed capture time, link to the
first slip and are distributed round-robin across buckets. All settings, drafts,
catalogs and remembered-project
state live in a disposable directory. No connection or Zetl process is started.

These are Avalonia **headless layout** timings including dispatcher jobs; they
exclude native pixel rendering, graphics/GPU work and frame delivery. Samples
include JIT and GC variation and are diagnostic observations, not timing gates.
Raw drag-event sweeps in the headless UI and ordinary UI regressions remain part
of the suite.

## Remaining costs

Initial realization remains expensive and every row/block is still realized.
Half-project filtering allocates roughly 25,777 KiB and 114,135 KiB when averaged
across hiding/restoring content. Large visible backlink lists still rebuild all
their controls; after opening Details, its last controls are retained while
hidden and reused if the inputs are unchanged. Rebuilds, project changes and
cleared selections retire them.

Next, evaluate rendering only visible rows/blocks and limiting layout to changed
content. Tree hierarchy, variable-height reader blocks, scrolling, multi-selection,
keyboard navigation, nested collapse and drag/drop boundaries need explicit
coverage before viewport virtualization. Preserve bitmap ownership while measuring
picture-heavy projects separately; this text-only probe does not characterize
image decoding or a suitable bitmap retention budget.
