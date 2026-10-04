# Zetl first tray-menu render

Measured October 4, 2026, on Windows with Avalonia 11.3.17 and a Debug build.

Zetl creates its `NativeMenu` items during host startup. On Windows, Avalonia
creates a new `TrayPopupRoot` window and `MenuFlyoutPresenter` on each right-click.
The first use also pays for templates, text layout, and rendering initialization.
There are no note-save or project-load callbacks on that menu-open path.
Framework source: [TrayIconImpl at 11.3.17](https://github.com/AvaloniaUI/Avalonia/blob/11.3.17/src/Windows/Avalonia.Win32/TrayIconImpl.cs).

`ZetlTrayMenuWarmup` prepares inert menu-item copies in an unshown window. Host
startup schedules it at background dispatcher priority after normal startup work.
The window is closed in `finally`. Warm-up does not attach commands/click handlers
or show/activate the window, and errors leave normal tray-menu opening available.
The host logs the preparation time. Only Windows schedules this optimization.

The checked-in diagnostic probe runs the exact framework right-click path in a
separate process with Zetl's application styles. It creates no normal app host
and does not load profiles, notes, or clipboard contents. Its test popup is
cloaked before its first frame and closed after two animation callbacks. These
callbacks approximate completed drawing; they are not a measurement of visible
pixels or a universal timing guarantee.

Final representative run (milliseconds):

| Measurement | Cold process | Process with startup warm-up |
| --- | ---: | ---: |
| Startup layout preparation | — | 489 |
| First menu `Show` returns | 892 | 126 |
| First menu, two frame callbacks | 1,194 | 304 |
| Second menu, two frame callbacks | 88 | 137 |
| Third menu, two frame callbacks | 80 | 71 |

Earlier cold runs reached 1,743 ms. Warm-up shifts about half a second of layout
work to startup and substantially reduces first-click latency. Some first-frame
rendering cost remains. Measurements vary with runtime/JIT, graphics initialization,
and machine load; they do not establish whether taking a note first changes the
delay in a particular session.

To reproduce, build once into an isolated output directory, then run each mode in
a fresh Windows process:

```powershell
dotnet build tools/TrayMenuProbe/TrayMenuProbe.csproj -p:UsedAvaloniaProducts= -p:BaseOutputPath="$PWD/artifacts/tray-menu-probe/bin/"
& "$PWD/artifacts/tray-menu-probe/bin/Debug/TrayMenuProbe.exe"
& "$PWD/artifacts/tray-menu-probe/bin/Debug/TrayMenuProbe.exe" --warmup
```

The probe uses reflection only to invoke Avalonia's private right-click method
and Zetl's internal helper. Review that diagnostic code when upgrading Avalonia.
Production warm-up uses public controls and layout APIs.
