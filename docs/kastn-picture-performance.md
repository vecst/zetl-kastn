# Kastn picture performance

Baseline: `2728b9a`. The text rendering probe cannot characterize native image
decoding or retained bitmap memory. This pass adds a separate native diagnostic
and changes decoded-image ownership, background decoding and picture viewport
thresholds.

## Findings and ownership

The previous cache retained every decoded `(asset hash, width)` bitmap until a
project reset or close, even after its reader block or board card left the viewport.
Compressed content already had a 128 MiB budget; decoded images did not. Cache
misses decoded synchronously on the UI thread, including during realization when
compressed content was already available.

Controls now acquire decoded-image leases. Reader blocks own their 1,100-pixel
preview lease; board cards own their 260-pixel thumbnail lease. A dual card's
thumbnail and expanded peek share one lease. Retiring a control clears all of its
image sources before releasing its lease. Reused controls retain their leases.
Reset/close retire cached entries; an outstanding lease keeps its bitmap alive
until the last release, even if another project has created the same cache key.

The default decoded budget is **64 MiB**, using actual bitmap pixel dimensions
times four bytes per pixel as an estimate. Least recently released idle entries
are evicted when total decoded residency exceeds that budget. Active leases are
protected: their total can exceed the budget, and an oversized active image remains
usable until release. This is a soft residency budget, not a process-memory cap;
decoder buffers, GPU resources, compressed originals and control/layout state
have separate costs.

Cache misses use one background decode slot. Cache hits acquire immediately;
queued requests recheck the cache before decoding, so concurrent requests for
the same hash/width share the result. Decoding does not hold the content/cache
gate, allowing UI cache lookups and fetch completions to proceed. Retiring controls
cancel queued work. A running native decode finishes, then cancellation or a
generation change disposes its result without caching or assigning it.
Image assignment and layout updates still run on the UI thread.

Picture-heavy views activate the existing variable-height viewport at **16
pictures**, in addition to the 128-slip threshold. The reader counts displayed
picture slips; the board also counts text slips with attached thumbnail/peek
pictures. This bounds realized picture controls in smaller collections while
retaining logical order, selection, scroll anchors and peek state.

## Native measurements

[`PicturePerformanceProbe`](../tools/PicturePerformanceProbe/Program.cs) initializes
Avalonia's native Skia backend without showing a window or starting Zetl. It
generates 96 distinct synthetic 2,048 × 1,536 PNG assets, browses each at both
reader and board widths, keeps the last four pictures' leases live, then releases
them and revisits recent/distant assets. Fixture generation happens before
measurement; actual SHA-256 hashes identify the assets. The same reflection-based
harness works against the old and new cache APIs.

Separate fresh-process observations:

| Measurement | Before | After |
| --- | ---: | ---: |
| Decoded entries after browsing | 192 | 35 |
| Estimated decoded pixels after browsing | 350.9 MiB | 62.3 MiB |
| Process private bytes after browsing | 399.1 MiB | 110.6 MiB |
| Decoded pixels after releasing controls | 350.9 MiB | 62.3 MiB |
| Native decode median | 25.3 ms | 24.9 ms |
| Recent reader revisit | about 0–0.2 ms | about 0.1 ms |
| Distant reader revisit | about 0 ms | about 29 ms |
| Decoded pixels after reset | 0 MiB | 0 MiB |

Decoded residency fell **82%**, and observed process private bytes fell **72%**.
Native decoding itself did not become faster. The asynchronous decode call
returned in a median 0.02 ms (cold maximum 2.9 ms), with completion waiting excluded;
the native work runs on the background worker. Distant evicted previews incur a
new decode, while recent previews retain immediate reuse.

The fixtures are highly compressible gradients, totaling only 1.2 MiB of encoded
content. Fetches return in-memory assets; their 0.02 ms median does **not** measure
disk reads, IPC/base64 transport or photographic/JPEG payloads. The probe excludes
native UI frame delivery and GPU drawing. Pixel-memory estimates exclude stride
and allocator overhead; private-byte samples include native allocations and
runtime variation. Forced GC before memory samples makes retention easier to
compare, but these are diagnostic observations rather than timing gates.

## Verification

Ten new owner cases cover idle eviction order, protected shared/oversized images,
idempotent release, reset/close with leases, background execution, concurrent miss
reuse, cancellation of queued work, running/queued retirement and failure retry.
Five new headless UI cases cover sharing across reader owners, independent
reader/board widths, 96-picture reader and board viewports, distant eviction and
return, dual peeks, cleared image sources and final lease retirement. Existing
delayed picture, export, project switching and lifetime regressions also run.

Headless correctness cases use tiny images; they are not native decoding benchmarks.
The standalone probe verifies the real native backend on the background path.
Original content and captured export fetch behavior remain unchanged; export
renderers receive original assets rather than preview-sized bitmaps.

## Reproduce

Run without concurrent builds/tests to reduce measurement noise:

```powershell
dotnet restore tools/PicturePerformanceProbe/PicturePerformanceProbe.csproj -p:UsedAvaloniaProducts=
dotnet run --project tools/PicturePerformanceProbe/PicturePerformanceProbe.csproj --no-restore -p:UsedAvaloniaProducts= -- 96
```

The optional argument sets an asset count between 8 and 300. Output reports
decoded counts/estimated bytes, process private/managed bytes, fetch/decode times,
recent/distant revisits and reset cleanup. No user project, settings, drafts,
clipboard, keyboard hook or live application state is accessed.

Further measurements can cover large JPEGs, very tall images, real disk/IPC
loading, rapidly changing viewports and native frame delivery. The serialized
decoder and soft budget should be evaluated against those workloads before
changing concurrency, resolution policy or retention limits.
