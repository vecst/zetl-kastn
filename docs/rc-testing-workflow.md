# Windows RC Testing Workflow

Use this workflow to run a repeatable Zetl/Kastn release-candidate pass. The
individual acceptance cases live in
[windows-parity-checklist.md](windows-parity-checklist.md); this document defines
the order, test environment, and evidence needed to trust those results.

## Principles

- Test one clean Git commit and record its full identity.
- Use a new disposable data directory for the formal pass. Dogfood against the
  normal profile only after disposable-profile behavior is sound.
- Run clipboard-sensitive checks before crash, Task Manager, or elevated-app
  checks can disturb the environment.
- Mark a checklist item complete only when its whole stated behavior passes.
- Preserve a failed profile and logs until the failure is understood.

## 1. Identify The Candidate

Quit Zetl and Kastn, then start from a clean worktree:

```powershell
git status --short
git rev-parse HEAD
```

Record the commit and choose a matching profile directory. For example, for
commit `abcdef1`:

```text
C:\tmp\zetl-rc-manual-abcdef1
```

Do not reuse a profile from another commit for fresh-start tests. A retained
older profile may be used later for an explicit upgrade or recovery check.

## 2. Run The Automated Gate

```powershell
dotnet build Zetl.slnx
dotnet test Zetl.Tests\Zetl.Tests.csproj --no-build
.\scripts\publish-win-x64.ps1 -RunArtifactChecks
```

The gate passes only when:

- the solution builds without errors;
- the test project has no failures;
- the publish script produces both executables from the same commit;
- the staged persistence scenario passes; and
- the Windows clipboard self-test actually runs its live mutation checks.

Read the clipboard output, not just the exit code. If it says
`SKIP live clipboard mutation tests`, close applications that may own the
clipboard and rerun the published executable:

```powershell
$selfTest = Start-Process `
    -FilePath .\artifacts\publish\win-x64\Zetl.exe `
    -ArgumentList '--self-test' `
    -Wait -PassThru -NoNewWindow
$selfTest.ExitCode
```

Do not accept the clipboard gate until that rerun exits successfully without a
skip message.

Record the bundle version and both SHA-256 values printed by the publish script.
The bundle under `artifacts\publish\win-x64` is the only bundle used for the
manual pass.

## 3. Start A Disposable Manual Pass

Launch the published Zetl with the candidate-specific directory:

```powershell
.\artifacts\publish\win-x64\Zetl.exe --data-dir=C:\tmp\zetl-rc-manual-abcdef1
```

Do not add `--allow-injected-input-for-testing` to a normal manual pass. That
switch is reserved for automated shortcut input against disposable state.

Record:

```text
Commit:
Bundle version:
Zetl.exe SHA-256:
Kastn.exe SHA-256:
Profile directory:
Windows version:
Important target applications and versions:
```

### Upgrade In Place (Required)

Testers upgrade over their existing data, so every candidate also runs on a copy
of a real profile from the previous release. Never point a candidate at the
original profile for this check.

1. Quit Zetl and Kastn. Copy the previous release's profile (normally
   `%AppData%\Zetl`) to a candidate-specific folder, for example
   `C:\tmp\zetl-rc-upgrade-abcdef1`.
2. Launch the published candidate with `--data-dir` pointing at the copy.
3. Confirm every project, bucket, nested bucket, slip, and picture is present;
   the Journal and Shift Journal still route captures; settings and themes kept
   their values; Replay review and older Pop Review buckets are intact; and Kastn
   opens the same projects.
4. The tour opens once for a profile that never finished or skipped it (an
   RC 1 profile that only saw the old help window), and not for one whose tour
   was Completed or Skipped.
5. Make a capture, an edit in Kastn, and a restart, and confirm all three
   persisted.

Preserve the copy if anything is missing or changed; it is the evidence.

## 4. Test In Phases

Work through the matching checklist sections in this order. Keep the same
profile unless a case explicitly requires a fresh one.

### Phase A: Fresh Start, Tray, And Lanes

1. Verify first-launch help, tray behavior, and single-instance handling.
2. Inspect the normal and Shift lane defaults before creating deliberate
   projects.
3. Create one normal-lane project and one Shift-lane project.
4. Restart Zetl and confirm active, last-selected, Journal, and Shift Journal
   routing.

Record routing defects immediately; later tests can hide them by explicitly
activating projects.

### Phase B: Ordinary Capture

1. Test tap behavior in a plain editor before testing held shortcuts.
2. Capture plain text, formatted text, an image, mixed text and image, a direct
   image URL, and a non-image URL.
3. Exercise held-copy routing and quick notes, including click-away,
   `Ctrl+Enter`, and clipboard-preservation settings.
4. Confirm the resulting slips in both Zetl Board and Kastn.

Use recognizable sentinel text and non-sensitive sample content.

### Phase C: Replay Clipboard Integrity

Create a Replay queue with known items such as `REPLAY-01`, `REPLAY-02`, and
`REPLAY-03`. Before each subtest, establish a separate clipboard baseline.

Prefer an application's context-menu **Copy** command when establishing the
baseline. It updates the clipboard without sending Zetl's `Ctrl+C` capture
gesture and accidentally adding another queue item.

Run the restoration matrix:

| Baseline source | Verify after Replay |
| --- | --- |
| Plain editor | Exact sentinel text returns |
| Rich-text editor | Text and formatting return |
| LibreOffice Calc | Values, font styles, color, alignment, and native cell behavior return |
| File Explorer | The same file-list clipboard can still be pasted |
| Mixed text/image source | Every advertised representation remains usable |

#### How Replay Handles Your Clipboard

Since RC 2.1, Replay leaves its own item on the clipboard between pastes, so an
app that is slow to read a paste never gets the wrong thing. Your clipboard comes
back when the queue runs out (after a 500 ms settle), when Replay is turned off,
or, if Replay ended some other way, just before your next tapped `Ctrl+V`. Judge
every restoration check at those moments, never between two Replay pastes.

Each Replay run ends in exactly one of three outcomes. Record which one you saw:

1. **Backup succeeded.** Mid-queue, the clipboard holds the Replay item. After
   the queue ends or Replay is turned off, the original clipboard returns exactly,
   every advertised representation included. A newer copy you make during Replay
   is never overwritten.
2. **Backup unavailable.** Replay still pastes, warns once that your previous
   clipboard can't be restored afterwards, and never claims a restore later. This
   passes when the warning is honest; it is not a restoration pass.
3. **Staging failed.** No paste is sent, the item stays first in the queue, and
   Zetl says so.

#### Explorer File-List Restoration

This test shows whether Replay can restore a multi-item Windows shell clipboard,
not just its visible text fallback. Zetl backs up every memory-held clipboard
format, but Explorer may also offer formats it renders on demand, which can make
a full backup impossible. Either outcome 1 or outcome 2 can be correct here; the
test establishes which, and `docs/guide.md` must then match it (it currently says
a file copied in File Explorer can't be backed up).

Prepare an isolated source and a verification destination in PowerShell:

```powershell
$root = Join-Path $env:TEMP ("zetl-rc-file-list-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$source = New-Item -ItemType Directory -Path (Join-Path $root "source")
$after = New-Item -ItemType Directory -Path (Join-Path $root "after-replay")
Set-Content -LiteralPath (Join-Path $source "alpha.txt") -Value "FILELIST-ALPHA"
Set-Content -LiteralPath (Join-Path $source "beta.txt") -Value "FILELIST-BETA"
New-Item -ItemType Directory -Path (Join-Path $source "empty-folder")
Invoke-Item $root
```

Then run the test:

1. In a dedicated active project and bucket, create two plain-text slips named
   `FILELIST-REPLAY-01` and `FILELIST-REPLAY-02`. Create them directly through
   Zetl Board or Kastn so setup does not depend on clipboard capture.
2. Set that bucket to Replay and confirm both slips are queued in that order.
3. In Explorer's `source` folder, select `alpha.txt`, `beta.txt`, and
   `empty-folder`. Right-click and choose **Copy**. Do not press `Ctrl+C`; the
   context-menu command avoids Zetl's auto-capture path.
4. Without copying anything else, switch to a plain editor and tap `Ctrl+V`.
   Confirm `FILELIST-REPLAY-01` appears, and note whether Zetl warned that the
   previous clipboard can't be restored.
5. Tap `Ctrl+V` again. Confirm `FILELIST-REPLAY-02` appears and the bucket
   returns to Standard. Wait at least a second for the final restore.
6. Open `after-replay` in Explorer, right-click its empty background, and
   choose **Paste**.

Pass with outcome 1 when no warning appeared, all three entries paste into
`after-replay`, both text files keep their exact contents, and the source entries
still exist (a copy, not a move). Pass with outcome 2 when Zetl warned at step 4,
both items still landed in order, and nothing claimed a restore afterwards; then
the original file list is gone, as the warning said. Either way the Replay review
bucket holds exactly the two consumed slips. If Replay proceeded without a
warning but the paste in step 6 fails or is damaged, that is a failure: stop and
preserve the disposable profile and notification/log evidence.

For Calc fidelity, distinguish the two values deliberately:

- the Replay slip is the content Zetl will stage;
- the clipboard baseline is what Zetl must restore afterward.

Create plain Replay slips in a plain editor or directly in Zetl/Kastn. Create a
formatted Replay slip by copying the formatted cells into its bucket. Formatting
already active at the destination can style otherwise plain text and is not
evidence that Replay preserved source formatting.

Also verify:

1. queue order and automatic return to Standard mode;
2. review-bucket archival without duplicates;
3. Zetl undo restoring the consumed item and Replay state;
4. a newer clipboard change during the restore delay is never overwritten;
5. a clipboard that can't be backed up gives outcome 2 (paste proceeds with an honest
   warning), and a staging failure gives outcome 3 (no paste, item kept);
6. temporary consumables self-dispose after completion while durable
   consumables remain.

### Phase D: Pass-Through, Compose, And Output

1. Pass-through, with a project active and capturing copies:
   - With the setting off, a copy pasted straight away stays in the project.
   - Hold `Ctrl+P`: the tray turns orange and the toast says pass-through is on
     for now. Copy and paste straight away: the copy moves to the project's
     **Passed Through** bucket, and holding `Ctrl+Z` brings it back.
   - A held `Ctrl+C` capture and a quick note never pass through, and pasting an
     older copy (with a newer one captured since) moves nothing.
   - Each lane flips separately (`Ctrl+Shift+P` for the Shift lane), and the flip
     ends after 10 minutes without a copy or paste, or on a project switch.
   - Repeat with a picture and with spreadsheet cells.
2. Exercise Compose selection, output modes (Formatted, Plain, TSV), Copy, Paste
   Now, `Ctrl+Enter`, and destination saves. TSV into a spreadsheet lands as cells.
3. Confirm the original foreground target and clipboard behavior.
4. Export clean and archival project packages and compare their privacy data.

### Phase E: Kastn Editing And Publishing

1. Exercise ordinary and multi-select editing, undo/redo, and restart
   persistence.
2. Verify font family, font size, text color, links, Groups, and minimum-width
   toolbar layout.
3. Export each applicable view and inspect HTML/PDF visual fidelity as well as
   plain-format warnings.
4. Confirm notifications and `Zetl Logs` contain the expected operational
   events without exposing private capture content unexpectedly.

### Phase F: Shutdown, Crash, And Recovery

Run these after clipboard and authoring checks because they intentionally
disturb process state:

1. coordinated Quit with Kastn foregrounded, backgrounded, minimized, clean,
   and dirty;
2. save, keep-recovery, discard, and cancel paths online and offline;
3. forced Kastn termination with a dirty draft;
4. remote mutation followed by draft recovery and conflict resolution;
5. forced Zetl termination followed by persistence and keyboard-state checks.

### Phase G: Integrity-Level Boundaries

Finish with the elevated-application cases. Verify the expected Windows block,
the user-facing explanation, and success when Zetl and the target run at the
same integrity level. Secure-desktop input remains outside the supported scope.

## 5. Record Results

Update [windows-parity-checklist.md](windows-parity-checklist.md) as the pass
proceeds:

- `[x]` means the entire item passed on the recorded candidate;
- `[ ]` means it has not been completed;
- `TODO` records an accepted, reproducible issue with enough detail to find it
  again.

For a failure, capture:

```text
Commit and bundle version:
Profile directory:
Workflow and shortcut:
Normal or Shift lane:
Foreground application and elevation:
Expected result:
Actual result:
Repeatable from a fresh profile: yes/no
Relevant Notification History or Zetl Logs entry:
```

Avoid screenshots or logs containing private captured material unless they are
necessary and deliberately sanitized.

## 6. Handle A Failure

1. Stop that test path before performing cleanup that could erase evidence.
2. Quit both applications normally when possible.
3. Preserve the disposable profile under a name containing the commit and a
   short failure label.
4. Repeat once from a new disposable profile to separate persistent-state
   damage from a deterministic defect.
5. Add a checklist TODO or release blocker before changing code.
6. After a fix, rerun the focused case, its neighboring workflow, the automated
   gate, and any affected destructive/recovery path.

Do not silently turn a failed item into a narrower passing claim.

## 7. Close The Candidate

An RC pass is complete when:

- the automated gate passes on the final clean commit;
- every required checklist item is checked or explicitly triaged;
- no open TODO violates capture durability, clipboard integrity, IPC/undo
  integrity, crash recovery, or packaging identity;
- any fix made during the pass has received its focused regression run; and
- a final published bundle is regenerated from the exact commit being released.

The normal user profile may then be used for a short dogfood confirmation. It
does not replace the disposable-profile pass.
