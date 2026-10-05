# Zetl popup memory

## Machine-level debugging overhead

The reported real-profile session had **full page heap enabled for Zetl.exe**:
the image-specific registry values were `GlobalFlag = 0x02000000` and
`PageHeapFlags = 3`, and the process loaded `verifier.dll`. After switching the
renderer, that host still used roughly 209 MiB of private memory with only
19 MiB of live managed objects after a diagnostic collection. Its committed
private allocations occupied almost 40,000 memory regions.

Full page heap adds guarded memory around native allocations and can consume
substantially more memory than normal operation. Its image-specific settings
persist across builds and take effect at process start. See Microsoft's
[GFlags and PageHeap](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/gflags-and-pageheap)
and [Enable page heap](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/enable-page-heap).
This explains why the production executable's footprint differed so sharply
from the native probe, whose executable name had no page-heap flag.

The two Zetl-specific values were backed up to
`artifacts/zetl-popup-memory/pageheap-settings-backup.json`. Removing only the
page-heap bit and its `PageHeapFlags` value requires administrator access, then
a Zetl restart. Other image flags and other applications' settings must be
preserved. Check for page heap before interpreting process-memory comparisons;
removing it is a debugging-configuration repair, separate from a code leak fix.

## Renderer and popup lifetime

The Ctrl+X quick-note path creates a fresh note window for each hold. Its
click-away registration is removed on close, placement uses weak keys, and the
activation/arming timers stop when the window closes. Native repeated-open
measurements found every closed note window collectable. They did not reproduce
an indefinitely growing managed-window leak in isolation.

An additional first-use and retained cost came from Windows rendering: the
default ANGLE/D3D renderer initializes a device and keeps native graphics
resources even after the small note window closes. Zetl now uses software Skia
on Windows. This avoids that renderer's device/cache footprint without forcing
garbage collection or trimming the process working set in production. Kastn's
renderer and Zetl's Linux configuration are unchanged.

## Native probe

`tools/PopupMemoryProbe` loads Zetl's real styles and opens its real quick-note
window and hold indicator against disposable state. It installs no keyboard
hook, runs no production host or IPC server, and accesses no user clipboard or
profile. Use the detailed indicator and 90% opacity to match the reported case:

```powershell
dotnet run --project tools/PopupMemoryProbe -- 100 both --detailed --opacity=90
```

Modes `popup`, `indicator`, and `both` separate the two rendering paths. The probe
uses Zetl's production Windows options when available; `--default-renderer`
restores Avalonia's default renderer for comparison. `--software` and
`--default-renderer --gpu-cache=16` support focused experiments. `--tray-warmup`
also applies the built-in Dusk theme and prepares a native tray/menu against
the same COM support as Zetl, without showing a tray icon. It measures working set, private
bytes, managed bytes, process handles, collections, CPU time, and popup frame
delivery. At the end it performs diagnostic-only collections and fails if any
closed note window remains rooted. Memory sizes are observations, not portable
pass/fail thresholds.

## Windows measurements, 2026-10-05

Thirty empty open/close cycles, including a detailed hold indicator and 90%
popup opacity, using Avalonia 11.3.17 and SkiaSharp 2.88.9:

| Renderer | Working set after natural settling | Private bytes after natural settling | Managed after diagnostic GC | Closed windows alive after diagnostic GC |
| --- | ---: | ---: | ---: | ---: |
| Default ANGLE/D3D | 167.3 MiB | 156.6 MiB | 2.3 MiB | 0 / 30 |
| ANGLE/D3D, 16 MiB GPU cache | 166.8 MiB | 153.9 MiB | 2.3 MiB | 0 / 30 |
| Software Skia | 100.8 MiB | 33.8 MiB | 2.3 MiB | 0 / 30 |

Software reduced settled working set by about 40% and private bytes by about
78% in this fixture. Limiting the GPU cache alone did not remove the device
cost. Total process CPU time for those runs was approximately 12.1, 10.1, and
8.3 seconds respectively; these are whole-process measurements, including
initialization, layout, the animated indicator, and collection, rather than
isolated rendering benchmarks.

The production renderer then completed 100 cycles: naturally settled working
set was 102.5 MiB, private bytes 32.3 MiB, and managed bytes after collection
2.3 MiB. All 100 closed windows were released; handles stayed near 405-414
after the first open. Construction through two animation frames measured
285.7 ms cold, 31.9 ms warm median, and 46.2 ms warm maximum.
The default renderer measured 282.4 ms cold, 24.3 ms warm median, and 41.5 ms
warm maximum in a 30-cycle timing comparison. Software therefore adds roughly
8 ms to typical popup delivery here. A repeat default-renderer run used 8.3
seconds of CPU, so the CPU figures above should not be read as a reliable
software-rendering speed advantage.

The fixture has one small synthetic project. It bypasses physical shortcuts,
clipboard observation and production-host notifications. The measurements do
not establish a fixed memory cap or rule out another retention path in a real
session. Real-profile Ctrl+X repetition remains the manual acceptance check;
large Board/Settings windows and picture captures should also be checked for
software-rendering responsiveness.
