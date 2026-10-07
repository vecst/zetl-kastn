# Building Zetl

For developers and anyone who wants to run Zetl from source. To just use Zetl,
download a release instead; see the [README](../README.md).

## Requirements

- Windows x64
- The .NET 10 SDK

## Running from source

Run the tray application:

```powershell
dotnet run --project Zetl.App
```

Build the full solution:

```powershell
dotnet build Zetl.slnx
```

All projects share one output directory:

```text
artifacts\bin\<Configuration>\
  Zetl.exe
  Kastn.exe
  Zetl.Tests.exe
```

A running published `Zetl.exe` does not lock this directory, but a Zetl run
from it does; quit that one before rebuilding.

## Tests

Run the portable test suite and the Windows-only self-tests:

```powershell
dotnet test Zetl.Tests\Zetl.Tests.csproj --no-build
dotnet .\artifacts\bin\Debug\Zetl.dll --self-test
```

The self-tests exercise the live Windows clipboard; they back up and restore
your clipboard, and skip the clipboard checks when it can't be backed up.

The manual Windows smoke checklist is
[`windows-parity-checklist.md`](windows-parity-checklist.md), and the release
candidate gate is [`rc-testing-workflow.md`](rc-testing-workflow.md).

## Publishing

Publish the self-contained Windows bundle:

```powershell
.\scripts\publish-win-x64.ps1
```

The script stages both Zetl and Kastn, checks that both executables identify
the current clean Git commit, and then replaces the previous bundle in
`artifacts\publish\win-x64\`. Add `-RunArtifactChecks` for a release candidate
to run the live clipboard self-tests and a disposable persistence scenario
before the bundle is installed.

## Linux

On Linux, build and test the same way with the .NET 10 SDK:

```bash
dotnet build Zetl.slnx
dotnet test Zetl.Tests/Zetl.Tests.csproj --no-build
```

Tests that need the kernel or the compositor run only where they can: the
keyboard tests need write access to `/dev/uinput` (they drive synthetic
keyboards and grab Zetl's output, so no keystroke reaches the desktop), and
the clipboard tests need a Wayland session with `wl-copy`/`wl-paste`. They skip
elsewhere, including on Windows.

Publish the self-contained Linux bundle (on Linux, from a clean checkout):

```bash
scripts/publish-linux-x64.sh
```

It stages Zetl, Kastn, the installer, desktop entries, icons, and the udev
rule, checks that both executables (`--version`) identify the current commit,
and replaces `artifacts/publish/linux-x64/`. Installing and permission setup
are in [`linux-setup.md`](linux-setup.md).

## Previews

Open individual windows against disposable state:

```powershell
dotnet run --project Zetl.App -- --preview=board
dotnet run --project Zetl.App -- --preview=settings
dotnet run --project Zetl.App -- --preview=hold-actions
dotnet run --project Zetl.App -- --preview=hold-indicator
dotnet run --project Zetl.App -- --preview=toast
dotnet run --project Zetl.App -- --preview=export
dotnet run --project Zetl.App -- --preview=note-image
```

`--preview=hold-indicator` drives the hold indicator through a simulated hold,
tap, and slow tap, with a detailed-overlay toggle (`--detailed` turns it on at
start). `--stress` and `--stress-fast` run hundreds or thousands of simulated
presses, for chasing show/hide problems. Any popup preview also takes
`--popup-position=` (`TopCenter`, `Center`, `TopRight`, `TopLeft`,
`BottomRight`, `BottomLeft`, `Pointer`) and `--popup-opacity=` (60-100).

## Testing shortcuts with synthetic input

Zetl ignores injected keyboard events, so automation can't trigger holds. With
a disposable data directory, a testing flag lets injected events through:

```powershell
dotnet run --project Zetl.App -- --data-dir=C:\tmp\zetl-hotkey-smoke --allow-injected-input-for-testing
```

Never use this flag with a real profile; it exists so release smoke scripts
can exercise the Windows hook. Zetl's own replayed keys stay filtered so
shortcuts can't trigger themselves.

## Diagnostics

Run the isolated native popup memory/lifetime probe:

```powershell
dotnet run --project tools/PopupMemoryProbe -- 100 both --detailed --opacity=90
```

It opens and closes real note windows without installing hooks, using the
clipboard, or accessing your profile. Renderer comparisons and measurement
limits are documented in [`zetl-popup-memory.md`](zetl-popup-memory.md).

Zetl writes a diagnostics log to `%AppData%\Zetl\diagnostics.log`: shortcut,
hold, and popup events, plus `Latency` summaries every ten minutes for the key
path and the hold path, and a line for any unusually slow key event.

## Repository layout

- `Chordl/`: the tap/hold engine
- `Zetl.Core/`: state, persistence, settings, templates, views, and themes
- `Zetl.Contracts/`: versioned Kastn/Zetl wire contracts
- `Zetl.Runtime/`: shortcut routing and workflow orchestration
- `Zetl.Linux/`: the Linux keyboard (evdev/uinput) and clipboard (Wayland) backends
- `Zetl.App/`: the Avalonia tray application
- `Kastn.App/`: the Avalonia workbench
- `Zetl.Tests/`: behavior and storage tests
- `Zetl.Linux.Spike/`: the original evdev/uinput safety spike
- `packaging/linux/`: installer, desktop entries, icons, and udev rule

Linux architecture and remaining platform work are in
[`linux-roadmap.md`](linux-roadmap.md). The full documentation map is
[`docs/README.md`](README.md).

## License

Zetl is free software licensed under the
[GNU General Public License version 3](../LICENSE) or any later version
(`GPL-3.0-or-later`).
