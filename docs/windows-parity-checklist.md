# Windows Smoke Checklist

Use this checklist for Windows dogfood and release-candidate smoke passes. The
canonical app is `Zetl.exe`.

## Prepare

1. Quit any running Zetl instance.
2. Build and publish:

   ```powershell
   dotnet build Zetl.slnx
   dotnet publish Zetl.App\Zetl.App.csproj -p:PublishProfile=win-x64
   ```

3. Start `artifacts\publish\win-x64\Zetl.exe`.
4. Keep `%AppData%\Zetl` backed up while testing state-changing workflows.

For automated shortcut smoke, launch against disposable state and explicitly
allow injected input:

```powershell
artifacts\publish\win-x64\Zetl.exe --data-dir=C:\tmp\zetl-hotkey-smoke --allow-injected-input-for-testing
```

The flag is ignored unless `--data-dir` is also present. Do not use it with a
normal profile. Zetl's own replayed pass-through keys remain filtered to avoid
recursive shortcut handling.

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
- [ ] Held `Ctrl+C` captures copied text and click-away commits the slip.
- [ ] Plain `Ctrl+C` captures clipboard images into the active bucket; verify
      Board thumbnail, full preview, provenance, deduplication, and ZIP export.
- [ ] Copy a direct image URL; verify Zetl downloads it into the project's
      `assets` folder, retains the final URL privately, and keeps HTML/non-image
      URLs as text slips.
- [ ] Held `Ctrl+C` with an image opens the image capture dialog; verify its
      preview, optional caption, project/bucket routing, and click-away commit.
- [ ] Held `Ctrl+X` opens quick note capture and respects the clipboard setting.
- [ ] Held `Ctrl+B` opens an auto-hiding Board; held `Ctrl+Shift+B` uses the
      Shift lane.
- [ ] Held `Ctrl+V` opens Compile; click-away cancels without changing state.
- [ ] Replay pastes in order, archives consumed slips, restores the user's
      clipboard, and can be undone.
- [ ] Pop removes the matching pasted slip and can be undone.
- [ ] Replay and Pop image slips; verify image-to-image and image-to-text
      clipboard restoration plus undo.
- [ ] After rapid tap/hold replay stress and after forced Zetl termination,
      type in a plain editor and confirm Ctrl, Shift, Caps Lock, and the
      configured target keys are not inverted or stuck.

## Board, Compile, And Theme

- [ ] Create, rename, select, and delete projects, buckets, nested buckets, and
      slips in both lanes.
- [ ] Edit and clear an image caption from the Board; verify its slip-list label
      updates and the caption survives restart.
- [ ] Compile formatted, unformatted, and TSV output.
- [ ] Save structured and flattened compile results to another bucket.
- [ ] Export clean and archive project packages; confirm only the archive keeps
      application and window-title provenance.
- [ ] Open a project in Kastn; apply font family, font size, and text color to one
      slip and a multi-selection, then verify undo/redo and restart persistence.
- [ ] Export the styled project as HTML and PDF and confirm typography matches the
      reader. Confirm Markdown, Formatted, Plain, and TSV show the fidelity warning
      and remain free of unsupported font/color markup.
- [ ] Render and export `https`, `mailto`, and internal-fragment links; confirm
      `javascript:`, `data:`, `file:`, and relative targets stay visibly inert.
- [ ] Insert a Kastn Group named `Callouts`; confirm reader and document exports use
      `Callouts` and never a generic `Group` placeholder.
- [ ] At Kastn's minimum supported width, confirm the editor formatting toolbar
      wraps without clipping its typography, style, list, or alignment controls.
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
