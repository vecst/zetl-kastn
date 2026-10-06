# Windows Smoke Checklist

Use this checklist for Windows dogfood and release-candidate smoke passes. The
canonical app is `Zetl.exe`. Follow
[rc-testing-workflow.md](rc-testing-workflow.md) for the test order, disposable
profile setup, evidence record, and rerun rules.

## Current RC Pass

- Baseline build: `927abe4` (`origin/master` at the start of the pass)
- Template recovery follow-up: `58e0510`
- Platform: Windows, tested through the published disposable-profile bundle
- Status: in progress; checked items passed manually and inline TODO entries
  record issues observed during the pass

## Prepare

1. Quit any running Zetl instance.
2. Build and publish both applications from a clean worktree:

   ```powershell
   dotnet build Zetl.slnx
   .\scripts\publish-win-x64.ps1 -RunArtifactChecks
   ```

   The publish script stages a fresh bundle, requires `Zetl.exe` and
   `Kastn.exe` to identify the same Git commit, and leaves the previous bundle
   untouched if either publish or artifact check fails.

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

- [ ] First launch opens the tour (`Take the Tour`); closing it leaves Zetl in the tray.
- [x] A tray-icon double-click opens the normal Board; a single click is inert.
- [ ] **TODO:** Fresh-profile startup should initialize and activate the normal
      and Shift Journal projects. Even after a Shift Journal exists and is saved
      as `shiftDefaultJournalProjectId`, `shiftActiveProjectId` remains blank;
      the tray action and held `Ctrl+Shift+B` then fall back to the normal active
      project instead of opening the Shift Journal. Merely selecting the Shift
      project is not remembered; it must be explicitly activated. With no active
      Shift project, reopen the last selected Shift project (or its Journal)
      rather than falling back across lanes. (The Shift Board now falls back to
      the Shift Journal when one exists, 2de0770; captures were already
      lane-correct. Lane activation on a fresh profile is still open.)
- [x] Every tray menu action opens the expected flow.
- [x] A second launch reports that Zetl is already running and exits.
- [x] `Quit` removes the tray icon and a later launch starts normally.
- [ ] Coordinated Quit coalesces repeated Quit requests (fixed in source, b29ab3c,
      not yet verified on a packaged build): with a dirty Kastn editor's
      confirmation pending, Quit again, then cancel. Neither process exits or
      restarts, and Zetl's toast points to Kastn's dialog. Kastn's confirmation
      must also come to the front when Kastn is behind other windows.
- [x] Ending Zetl from Task Manager does not corrupt state and a later launch
      starts normally.
- [x] With a dirty Kastn editor, test normal Exit and coordinated Zetl Quit both
      online and offline; verify save, keep-recovery, discard, and cancel paths.
- [x] Force-terminate Kastn after typing, relaunch it, and confirm the matching
      project/slip opens with the local draft; change the slip remotely first and
      confirm recovery opens a conflict without losing either version.
- [ ] **TODO:** Keep Kastn's recovered-conflict panel above the status bar.
      Its two version previews currently overflow into the status area even at
      the normal manual-test window size.

## Shortcut Workflows

- [x] Tap `Ctrl+C`, `Ctrl+X`, `Ctrl+V`, `Ctrl+B`, `Ctrl+P`, `Ctrl+R`, and
      `Ctrl+Z`; the foreground application keeps its normal tap behavior.
- [x] Held `Ctrl+C` captures copied text and click-away commits the slip.
- [x] Plain `Ctrl+C` captures clipboard images into the active bucket; verify
      Board thumbnail, full preview, provenance, deduplication, and ZIP export.
- [x] Copy a direct image URL; verify Zetl downloads it into the project's
      `assets` folder, retains the final URL privately, and keeps HTML/non-image
      URLs as text slips.
- [x] Held `Ctrl+C` with an image opens the image capture dialog; verify its
      preview, optional caption, project/bucket routing, and click-away commit.
- [x] Held `Ctrl+X` opens quick note capture and respects the clipboard setting.
- [x] Held `Ctrl+B` opens an auto-hiding Board; held `Ctrl+Shift+B` uses the
      Shift lane.
- [x] Held `Ctrl+V` opens Compile; click-away cancels without changing state.
- [x] Replay pastes in order, archives consumed slips, restores the user's
      clipboard, and can be undone.
- [x] In LibreOffice Calc, copy cells containing bold, underline, and right
      alignment into a Replay bucket; verify each Replay paste preserves those
      styles without adding wrap/alignment rules, and the final clipboard
      restore preserves the original rich cells.
- [ ] Before Replay, copy rich text, RTF, spreadsheet cells, files, and mixed
      text/image content; verify every advertised clipboard format returns
      after each paste. If Zetl reports an unsupported format, verify Replay
      leaves both the clipboard and queued slip untouched. Use the controlled
      [Explorer file-list restoration](rc-testing-workflow.md#explorer-file-list-restoration)
      procedure for the file-list case.
- For a manual Replay clipboard baseline, prefer the application's context-menu
  **Copy** command. It updates the clipboard without routing the copy gesture
  through Zetl's keyboard auto-capture path.
- [ ] During Replay's restore delay, copy new rich content whose visible text
      matches the queued slip; verify Zetl does not overwrite the newer copy.
- [ ] Pass-through sets aside the latest pasted automatic copy (never a held capture) and can be undone.
- [ ] Replay and pass-through image slips; verify image-to-image and image-to-text
      clipboard restoration plus undo.
- [ ] After rapid tap/hold replay stress and after forced Zetl termination,
      type in a plain editor and confirm Ctrl, Shift, Caps Lock, and the
      configured target keys are not inverted or stuck.

## Board, Compile, And Theme

- [ ] Create, rename, select, and delete projects, buckets, nested buckets, and
      slips in both lanes.
- [ ] **TODO:** Record project and bucket creation in Notification History and
      `Zetl Logs`, including the direct tray/Board paths that currently persist
      silently.
- [ ] Edit and clear an image caption from the Board; verify its slip-list label
      updates and the caption survives restart.
- [ ] Compile formatted, unformatted, and TSV output.
- [ ] Save structured and flattened compile results to another bucket.
- [x] Use a Consumable template once as durable and once as Temporary. The
      durable project remains active with its Replay bucket after completion;
      the temporary project uses the selected lane and removes itself when its
      Replay queue is exhausted.
- [ ] Export clean and archive project packages; confirm only the archive keeps
      application/window-title provenance and captured native/HTML source data.
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
