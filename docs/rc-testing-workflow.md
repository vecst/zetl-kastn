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
5. unsupported formats pause Replay without changing the clipboard or queue;
6. temporary consumables self-dispose after completion while durable
   consumables remain.

### Phase D: Pop, Compile, And Output

1. Verify Pop removal and undo with text and image slips.
2. Exercise Compile selection, output modes, Copy, Paste Now, and destination
   saves.
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
