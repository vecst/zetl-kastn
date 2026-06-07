# Windows Avalonia Dogfood Checklist

Use this checklist for the A7 cutover pass. The primary app is the Avalonia
artifact, `Zetl.exe`. `Zetl.Legacy.exe` remains buildable as a fallback until
this pass is complete.

## Prepare

1. Quit any running Zetl instance.
2. Build and publish:

   ```powershell
   dotnet build Zetl.slnx
   dotnet publish Zetl.App\Zetl.App.csproj -p:PublishProfile=win-x64
   ```

3. Start `artifacts\publish\win-x64\Zetl.exe`.
4. Keep `%AppData%\Zetl` backed up while testing state-changing workflows.

## Startup And Tray

- [ ] First launch shows `How Zetl Works`; closing it leaves Zetl in the tray.
- [ ] A tray-icon click opens the normal Board.
- [ ] Every tray menu action opens the expected flow.
- [ ] A second launch reports that Zetl is already running and exits.
- [ ] `Quit` removes the tray icon and a later launch starts normally.
- [ ] Ending Zetl from Task Manager does not corrupt state and a later launch
      starts normally.

## Shortcut Workflows

- [ ] Tap `Ctrl+C`, `Ctrl+X`, `Ctrl+V`, `Ctrl+B`, `Ctrl+P`, `Ctrl+R`, and
      `Ctrl+Z`; the foreground application keeps its normal tap behavior.
- [ ] Held `Ctrl+C` captures copied text and click-away commits the note.
- [ ] Held `Ctrl+X` opens quick note capture and respects the clipboard setting.
- [ ] Held `Ctrl+B` opens an auto-hiding Board; held `Ctrl+Shift+B` uses the
      Shift lane.
- [ ] Held `Ctrl+V` opens Compile; click-away cancels without changing state.
- [ ] Replay pastes in order, archives consumed notes, restores the user's
      clipboard, and can be undone.
- [ ] Pop removes the matching pasted note and can be undone.

## Board, Compile, And Theme

- [ ] Create, rename, select, and delete projects, buckets, nested buckets, and
      notes in both lanes.
- [ ] Compile formatted, unformatted, and TSV output.
- [ ] Save structured and flattened compile results to another bucket.
- [ ] Copy and Paste Now compile actions restore the original target.
- [ ] Change settings and create a custom theme; restart and confirm both
      persisted.
- [ ] Notification history and the `Zetl Logs` project contain expected events.

## Elevated Applications

- [ ] With normal Zetl, try shortcuts in an application launched with
      `Run as administrator`; confirm Windows blocks cross-integrity input.
- [ ] When a synthetic paste is rejected, confirm Zetl says the compiled text
      remains on the clipboard and mentions running Zetl elevated.
- [ ] Run Zetl at the same elevation as the target and confirm shortcuts work.
- [ ] Confirm no expectation is set for the Windows secure desktop, where
      global hooks are unavailable.

## Report

For a failure, record:

- workflow and shortcut
- normal or Shift lane
- foreground application and whether either process was elevated
- expected and actual result
- relevant entries from Notification History or `Zetl Logs`

The legacy fallback can be launched explicitly with:

```powershell
dotnet run --project ZetlHotkeys.csproj
```
