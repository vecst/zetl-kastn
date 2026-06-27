# Kastn JSON Storage Baseline

This records the pre-Kastn K0 storage baseline for the current per-project JSON
implementation. It is a comparison point, not a permanent performance budget.

Measured June 15, 2026 with:

- .NET 10.0.8
- Microsoft Windows 10.0.26200
- x64 process
- Five measured runs per size after a warm-up
- Ten buckets with representative text slips
- Median atomic rewrite, load time, and loaded managed-memory delta

| Slips | JSON MiB | Atomic rewrite ms | Load ms | Loaded memory MiB |
| ---: | ---: | ---: | ---: | ---: |
| 1,000 | 0.27 | 5.31 | 3.41 | 0.37 |
| 5,000 | 1.34 | 11.91 | 10.35 | 1.84 |
| 20,000 | 5.36 | 36.63 | 53.06 | 7.33 |

The measurement helper lives in `Zetl.Tests/StorageBaselineScenario.cs`. It uses
disposable temporary projects and does not read or write user data. Wire that
helper into a temporary harness before refreshing the table.

## Initial Reading

The 20,000-slip result does not justify introducing SQLite.
The current JSON file remains small enough to load quickly for a deliberate
workbench, and atomic rewrites remain short enough to evaluate behind Zetl's
single-writer queue.

Future measurements should use this same scenario after revision fields, IPC,
typed capture files, and live notifications land. SQLite should be reconsidered
only if measured user experience or storage requirements become materially
worse after ordinary optimization.

## K1 Revision Comparison

K1 added a revision to every project, bucket, and slip plus a project change
sequence. Two isolated runs after that change put the 20,000-slip atomic rewrite
between `80.27 ms` and `83.19 ms`, with load between `42.92 ms` and `43.80 ms`.

Representative second-run results:

| Slips | JSON MiB | Atomic rewrite ms | Load ms | Loaded memory MiB |
| ---: | ---: | ---: | ---: | ---: |
| 1,000 | 0.30 | 5.72 | 2.89 | 0.38 |
| 5,000 | 1.47 | 13.07 | 13.90 | 1.87 |
| 20,000 | 5.85 | 83.19 | 43.80 | 7.48 |

The revision metadata adds about `0.49 MiB` at 20,000 slips. The rewrite remains
off the keyboard-hook path and short enough for the current single-writer JSON
design. Continue measuring as typed capture files split the project data.
