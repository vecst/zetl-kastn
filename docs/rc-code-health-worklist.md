# RC Code-Health Worklist

This is the release-candidate worklist for the P1 and P2 risks found during the
July 2026 code-health audit. It is deliberately separate from the product
roadmap: these items protect data, clipboard integrity, process reliability,
and maintainability of the paths needed to fix them safely.

The current automated baseline is 375 passing tests, 2 skipped, and 0 failed at
commit `adb634d`.

## Working Order

Use three distinct phases so refactors and behavior changes remain reviewable:

1. **Behavior-preserving cleanup.** Establish narrow ownership boundaries and
   characterization tests without changing user-visible behavior.
2. **P1 fixes.** Implement and manually verify every release blocker.
3. **P2 hardening.** Address bounded-load, lifecycle, and recovery risks, then
   complete the Windows RC smoke pass.

Each cleanup or fix should land as a focused commit. Do not combine broad file
movement with a behavioral correction unless the correction cannot be isolated.

## Cleanup Pass

### C1. Clipboard content and transaction boundary

- [x] Move clipboard content identity, read/write selection, and Replay tracking
      out of `ZetlShortcutCoordinator` into a focused runtime component.
- [x] Keep the platform backend responsible only for native clipboard access and
      native resource ownership.
- [x] Give the runtime component explicit outcomes instead of a collection of
      loosely related Boolean results.
- [x] Preserve the current `IClipboard` test seam while the extraction lands;
      narrow or replace it only after callers are centralized.
- [x] Add characterization tests for text, rich text, native Replay formats,
      images, ownership-token mismatch, and failed writes.

This boundary must be capable of supporting an atomic capture and a
stage/inject/conditional-restore transaction. It must not make the existing
multi-read capture behavior part of the permanent contract.

### C2. Project-storage lifecycle boundary

- [x] Separate durable project-file writes from project-directory lifecycle
      operations in `IZetlStateStorage`.
- [x] Model project removal as a reversible prepare/commit/rollback operation.
- [x] Keep workspace pointer mutation in `ZetlStateStore`, which remains the
      transaction coordinator and sole domain-state owner.
- [x] Add a real-filesystem characterization test containing `project.json`, an
      image asset, and an injected late workspace failure.

### C3. IPC connection responsibilities

- [x] Separate handshake, inbound frame processing, response delivery, and
      project-change notification delivery inside `ClientConnection`.
- [x] Make response priority and event coalescing policy explicit rather than
      implicit in one shared channel.
- [x] Centralize connection timeouts so tests can use deterministic short
      deadlines.

### C4. Kastn refresh and activation boundary

- [x] Replace one-task-per-event refresh dispatch with a single owned refresh
      pump that coalesces adjacent invalidations.
- [x] Convert activation to a Task-returning operation with one UI-boundary error
      handler.
- [x] Keep connection-state publication and UI navigation errors observable.

### C5. Coordinator and state-store cohesion

- [x] After C1 and C2, reassess `ZetlShortcutCoordinator` and `ZetlStateStore` by
      responsibility rather than line count.
- [x] Extract only workflows with an independent invariant and test surface.
- [x] Keep persistence commit/rollback ordering centralized; do not distribute a
      single transaction across unrelated services.

`ZetlStatePersistenceCoordinator` now owns durable-baseline advancement, ordered
project/workspace writes, and compensating writes, while `ZetlStateStore` remains
the sole domain-state owner and selects the transaction required by each
mutation. `ZetlPendingShortcutRegistry` owns atomic, lane-specific gesture
claiming. Replay and capture sequencing deliberately remain together in
`ZetlShortcutCoordinator` because they share one keyboard/clipboard/dispatcher
transaction and undo boundary.

## P1 — Release Blockers

### P1-1. Project deletion can lose assets during rollback

**Risk:** `RemoveProject` recursively deletes the project directory before the
workspace commit. A late workspace failure restores live state and rewrites
`project.json`, but deleted assets cannot be reconstructed.

**Implementation status:** C2 now moves the complete project directory behind a
reversible pending-removal handle, writes the workspace, then finalizes deletion.
Rollback moves the same directory back. Interrupted pending-removal recovery and
cleanup policy remains part of this P1 before it can be closed.

**Acceptance:**

- [x] A workspace-write failure after deletion preparation restores the original
      project directory byte-for-byte, including image assets and view data.
- [x] A project-removal preparation failure leaves live and durable state
      unchanged.
- [ ] Successful deletion leaves no live project directory.
- [ ] Interrupted pending-trash entries have a documented startup recovery or
      cleanup policy.

### P1-2. Clipboard replacement can leave partial content

**Risk:** after `EmptyClipboard`, any later `SetClipboardData` failure can leave
the clipboard empty or partially populated. Plain text, rich text, images,
native Replay payloads, compile, and restoration share this exposure.

**Implementation after C1:** use one clipboard transaction that captures the
original, prepares every native allocation before mutation, writes the complete
target, and attempts rollback after any post-empty failure. Return a structured
outcome that distinguishes staging failure, ownership loss, and restore failure.

**Acceptance:**

- [ ] Fault injection can fail each individual native format write.
- [ ] A failed permanent write either preserves or restores the original
      clipboard and never reports success for a partial target.
- [ ] A failed temporary Replay/compile stage does not inject a paste.
- [ ] Restoration failure is logged and surfaced without lying about clipboard
      ownership.
- [ ] Windows self-tests cover plain, rich, native spreadsheet, image, mixed,
      and exactly empty clipboards.

### P1-3. Replay completes before final clipboard restoration

**Risk:** the bucket returns to Standard and completion is announced while the
last Replay item is still staged during the restore delay. A fast additional
Ctrl+V can pass through and paste it again.

**Implementation after C1:** keep a lane in an explicit restoring state until
conditional restoration settles. While restoring, suppress or serialize another
paste instead of allowing ordinary pass-through.

**Acceptance:**

- [ ] A Ctrl+V during the final restore delay cannot paste the last Replay item
      again.
- [ ] Replay reports completion only after restoration succeeds, is skipped
      because the user changed the clipboard, or fails with a visible/logged
      outcome.
- [ ] Main and Alternate lanes retain independent queue semantics while sharing
      safe clipboard ownership.
- [ ] Existing rapid-tap and newer-matching-clipboard tests remain green.

## P2 — Hardening Before Public Release

### P2-1. Clipboard capture can combine different clipboard generations

- [ ] Capture text, HTML, image, native formats, and the observed sequence as one
      backend operation, or retry when the sequence changes during capture.
- [ ] Add a fake that changes generation between format reads and verify no torn
      slip is committed.

### P2-2. IPC reads have no progress deadline

- [x] Apply a short handshake deadline and an incomplete-frame progress
      deadline without imposing an idle timeout on healthy established clients.
- [x] Test partial headers, partial payloads, and a client that never sends
      `hello`.

### P2-3. IPC events can crowd out command responses

- [x] Give responses a delivery path that project-change events cannot exhaust.
- [x] Coalesce project events as invalidation hints while preserving the newest
      project sequence.
- [x] Saturation tests must prove a completed mutation response is delivered or
      remains safely recoverable by command ID.

### P2-4. Kastn refresh events create an unbounded task backlog

- [x] Keep at most one running refresh and one dirty/pending signal.
- [x] A burst of change events should converge on the newest snapshot with a
      bounded number of list/snapshot requests.

### P2-5. Kastn activation can surface an unhandled async-void exception

- [x] Make activation Task-returning and catch/report failure at the dispatcher
      or control-pipe boundary.
- [ ] Test a disconnect during project navigation and verify Kastn remains
      running with an accurate connection status.

### P2-6. Pop recovery is session-only

- [ ] Choose a durable recovery design: move popped slips to a review bucket or
      persist a bounded Pop history.
- [ ] Include the source bucket/recovery destination in the notification.
- [ ] Verify restart recovery for text, image, and mixed slips.

## Final RC Gates

- [ ] Full automated suite passes with no new skips.
- [ ] Focused fault-injection tests cover every P1 failure point.
- [ ] Windows clipboard self-tests pass outside the sandbox.
- [ ] `windows-parity-checklist.md` passes on the published artifact.
- [ ] No P1 item remains open; any deferred P2 has an explicit release decision
      and documented user impact.
