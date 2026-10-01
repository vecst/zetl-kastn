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

## Previews

Open individual windows against disposable state:

```powershell
dotnet run --project Zetl.App -- --preview=board
dotnet run --project Zetl.App -- --preview=settings
dotnet run --project Zetl.App -- --preview=hold-actions
dotnet run --project Zetl.App -- --preview=toast
dotnet run --project Zetl.App -- --preview=export
dotnet run --project Zetl.App -- --preview=note-image
```

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

Zetl writes a diagnostics log to `%AppData%\Zetl\diagnostics.log`: shortcut,
hold, and popup events, plus `Latency` summaries every ten minutes for the key
path and the hold path, and a line for any unusually slow key event.

## Repository layout

- `Chordl/`: the tap/hold engine
- `Zetl.Core/`: state, persistence, settings, templates, views, and themes
- `Zetl.Contracts/`: versioned Kastn/Zetl wire contracts
- `Zetl.Runtime/`: shortcut routing and workflow orchestration
- `Zetl.App/`: the Avalonia tray application
- `Kastn.App/`: the Avalonia workbench
- `Zetl.Tests/`: behavior and storage tests
- `Zetl.Linux.Spike/`: evdev/uinput safety work

Linux architecture and remaining platform work are in
[`linux-roadmap.md`](linux-roadmap.md). The full documentation map is
[`docs/README.md`](README.md).

## License

Zetl is free software licensed under the
[GNU General Public License version 3](../LICENSE) or any later version
(`GPL-3.0-or-later`).
