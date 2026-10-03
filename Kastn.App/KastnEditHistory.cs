using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal enum KastnHistoryStepStatus { Completed, Empty, Busy, Interrupted, OutcomeUnknown, Invalidated, ConcurrentChange }
internal sealed record KastnHistoryConflict(string Verb, string Description, string CurrentText, string TargetText);
internal sealed record KastnHistoryStepResult(KastnHistoryStepStatus Status, KastnUndoEntry? Entry = null,
    int Applied = 0, int Kept = 0, bool HasRepair = false);

// Owns Kastn's recorded mutations, gesture transactions, and inverse execution.
// Transport, snapshot supply, and conflict decisions are injected; no UI controls.
internal sealed class KastnEditHistory(
    Func<ZetlProjectSnapshot?> getProject,
    Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute,
    Func<Task<KastnSessionSnapshot?>> refresh,
    Func<IAsyncDisposable> deferRefresh)
{
    private readonly KastnUndoHistory undoStack = new();
    private readonly KastnUndoHistory redoStack = new();
    private string? projectId;
    private string? serverInstanceId;
    private long generation;
    private ZetlProjectSnapshot? currentProject => getProject();
    public bool IsStepping { get; private set; }
    public int UndoCount => undoStack.Count;
    public int RedoCount => redoStack.Count;
    public long Generation => generation;
    public KastnUndoEntry? Peek(bool redo = false) =>
        (redo ? redoStack : undoStack).TryPeek(out var entry) ? entry : null;

    // A transient outage can retain revision-checked entries. Changing project
    // or server retires both stacks and every still-running gesture/command.
    public void ObserveSession(KastnSessionSnapshot session)
    {
        var serverChanged = session.ConnectionState == KastnConnectionState.Online
            && serverInstanceId is not null && session.ServerInstanceId is not null
            && serverInstanceId != session.ServerInstanceId;
        if (projectId != session.Project?.Id || serverChanged)
        {
            generation++;
            ClearUndoHistory();
        }
        projectId = session.Project?.Id;
        if (session.ConnectionState == KastnConnectionState.Online && session.ServerInstanceId is not null)
            serverInstanceId = session.ServerInstanceId;
    }

    private bool IsCurrent(long capturedGeneration, string capturedProjectId) =>
        generation == capturedGeneration && projectId == capturedProjectId
        && currentProject?.Id == capturedProjectId;

    // While non-null, mutations accumulate into one gesture instead of each
    // becoming its own undo entry. Coalescing by id keeps a gesture that touches
    // one record with several commands (divider insert, drag) revision-correct.
    //
    // AsyncLocal, not a field: a gesture's commands await IPC round-trips, and the
    // UI stays live during those awaits — a plain field would let an unrelated
    // user action (clicking another slip, its autosave) join the open gesture and
    // get reverted with it by one Ctrl+Z. AsyncLocal scopes the gesture to the
    // async flow that opened it; input-driven flows start without one.
    private readonly AsyncLocal<GestureAccumulator?> activeGesture = new();

    private GestureAccumulator? gesture => activeGesture.Value;

    private sealed class GestureAccumulator(string description, string projectId, long generation)
    {
        public string Description { get; } = description;
        public string ProjectId { get; } = projectId;
        public long Generation { get; } = generation;
        public bool Completed { get; set; }
        public Dictionary<string, GestureSlip> Slips { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, GestureBucket> Buckets { get; } = new(StringComparer.Ordinal);
    }

    private sealed class GestureSlip
    {
        // The slip's state before the gesture first touched it (null = created
        // during the gesture), and its neighbour then.
        public ZetlSlipSnapshot? Pre;
        public string? PreFollowing;
        // The latest snapshot the gesture has produced for this slip.
        public required ZetlSlipSnapshot Latest;
    }

    private sealed class GestureBucket
    {
        // The bucket's state before the gesture first touched it, and its following
        // same-parent sibling then. Bucket creation is not recorded, so Pre is
        // always a real snapshot.
        public required ZetlBucketSnapshot Pre;
        public string? PreFollowing;
        public required ZetlBucketSnapshot Latest;
    }

    // Group the mutations issued inside the scope into a single undo entry. Nested
    // calls within the same flow join the outer gesture. Dispose finalizes
    // synchronously — the gesture body has already refreshed the project, so no
    // further IPC is needed.
    public IDisposable BeginGesture(string description)
    {
        if (activeGesture.Value is not null || currentProject is null)
        {
            return NullScope.Instance;
        }

        activeGesture.Value = new GestureAccumulator(description, currentProject.Id, generation);
        return new GestureScope(this, activeGesture.Value);
    }

    private sealed class GestureScope(KastnEditHistory owner, GestureAccumulator accumulator) : IDisposable
    {
        public void Dispose() => owner.EndGesture(accumulator);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private void EndGesture(GestureAccumulator accumulated)
    {
        if (accumulated.Completed) return;
        accumulated.Completed = true;
        if (ReferenceEquals(activeGesture.Value, accumulated)) activeGesture.Value = null;
        if (!IsCurrent(accumulated.Generation, accumulated.ProjectId))
        {
            return;
        }

        var operations = new List<KastnUndoOperation>();
        foreach (var (slipId, slip) in accumulated.Slips)
        {
            var op = OperationFor(slipId, slip.Pre, slip.PreFollowing, slip.Latest);
            if (!KastnUndoPlanner.IsNoOp(op))
            {
                operations.Add(op);
            }
        }

        var bucketOperations = new List<KastnBucketUndoOperation>();
        foreach (var (bucketId, bucket) in accumulated.Buckets)
        {
            var op = BucketOperationFor(bucketId, bucket.Pre, bucket.PreFollowing, bucket.Latest);
            if (!KastnUndoPlanner.IsNoOp(op))
            {
                bucketOperations.Add(op);
            }
        }

        if (operations.Count > 0 || bucketOperations.Count > 0)
        {
            RecordEntry(new KastnUndoEntry(accumulated.Description, accumulated.ProjectId, operations)
            {
                BucketOperations = bucketOperations
            });
        }
    }

    // The single choke point for Kastn-issued mutations. On a successful slip or
    // bucket command it records the inverse (into the active gesture, or as its own
    // entry) so in-app Ctrl+Z can reverse it; every other command passes straight
    // through. The response is returned unchanged so existing conflict and status
    // handling is untouched.
    public async Task<ZetlResponseEnvelope> ExecuteMutationAsync(ZetlCommandEnvelope command)
    {
        if (IsStepping)
        {
            return new ZetlResponseEnvelope
            {
                CommandId = command.CommandId,
                Status = ZetlResponseStatus.Failure,
                ProjectId = command.ProjectId,
                Error = new ZetlProtocolError
                {
                    Code = "edit_history_busy",
                    Message = "Wait for the current undo or redo to finish."
                }
            };
        }

        if (UndoDescription(command.Kind) is not { } description || command.ProjectId is null)
        {
            return await execute(command);
        }

        return IsBucketCommand(command.Kind)
            ? await ExecuteBucketMutationAsync(command, description)
            : await ExecuteSlipMutationAsync(command, description);
    }

    private async Task<ZetlResponseEnvelope> ExecuteSlipMutationAsync(
        ZetlCommandEnvelope command,
        string description)
    {
        var capturedGeneration = generation;
        var project = currentProject?.Id == command.ProjectId ? currentProject : null;
        ZetlSlipSnapshot? before = null;
        string? beforeFollowing = null;
        if (project is not null && command.TargetId is not null)
        {
            before = project.Slips.FirstOrDefault(slip =>
                string.Equals(slip.Id, command.TargetId, StringComparison.Ordinal));
            if (before is not null)
            {
                beforeFollowing = KastnUndoPlanner.FollowingSlipId(project, before.BucketId, before.Id);
            }
        }

        var inheritedGesture = gesture;
        var response = await execute(command);
        if (inheritedGesture is not null
            && (inheritedGesture.Completed || inheritedGesture.Generation != capturedGeneration)) return response;
        if (IsCurrent(capturedGeneration, command.ProjectId!)
            && response.CommandId == command.CommandId
            && (response.ProjectId is null || response.ProjectId == command.ProjectId)
            && response.Status == ZetlResponseStatus.Success
            && ReadSnapshot<ZetlSlipSnapshot>(response.Payload) is { } after
            && (command.TargetId is null || after.Id == command.TargetId)
            && (command.ExpectedTargetRevision is null || after.Revision > command.ExpectedTargetRevision))
        {
            if (gesture is not null
                && !gesture.Completed && gesture.Generation == capturedGeneration
                && string.Equals(gesture.ProjectId, command.ProjectId, StringComparison.Ordinal))
            {
                Accumulate(after.Id, before, beforeFollowing, after);
            }
            else
            {
                var op = OperationFor(after.Id, before, beforeFollowing, after);
                if (!KastnUndoPlanner.IsNoOp(op))
                {
                    RecordEntry(new KastnUndoEntry(description, command.ProjectId!, [op]));
                }
            }
        }

        return response;
    }

    // The bucket twin of the slip path. Recording requires the pre-change bucket
    // snapshot, so bucket creation (no before) is never recorded — matching the
    // documented v1 scope of rename/reparent/settings/heading/reorder.
    private async Task<ZetlResponseEnvelope> ExecuteBucketMutationAsync(
        ZetlCommandEnvelope command,
        string description)
    {
        var capturedGeneration = generation;
        var project = currentProject?.Id == command.ProjectId ? currentProject : null;
        ZetlBucketSnapshot? before = null;
        string? beforeFollowing = null;
        if (project is not null && command.TargetId is not null)
        {
            before = project.Buckets.FirstOrDefault(bucket =>
                string.Equals(bucket.Id, command.TargetId, StringComparison.Ordinal));
            if (before is not null)
            {
                beforeFollowing = KastnUndoPlanner.FollowingBucketId(project, before.Id);
            }
        }

        var inheritedGesture = gesture;
        var response = await execute(command);
        if (inheritedGesture is not null
            && (inheritedGesture.Completed || inheritedGesture.Generation != capturedGeneration)) return response;
        if (IsCurrent(capturedGeneration, command.ProjectId!)
            && response.CommandId == command.CommandId
            && (response.ProjectId is null || response.ProjectId == command.ProjectId)
            && response.Status == ZetlResponseStatus.Success
            && before is not null
            && ReadSnapshot<ZetlBucketSnapshot>(response.Payload) is { } after
            && (command.TargetId is null || after.Id == command.TargetId)
            && (command.ExpectedTargetRevision is null || after.Revision > command.ExpectedTargetRevision))
        {
            if (gesture is not null
                && !gesture.Completed && gesture.Generation == capturedGeneration
                && string.Equals(gesture.ProjectId, command.ProjectId, StringComparison.Ordinal))
            {
                AccumulateBucket(after.Id, before, beforeFollowing, after);
            }
            else
            {
                var op = BucketOperationFor(after.Id, before, beforeFollowing, after);
                if (!KastnUndoPlanner.IsNoOp(op))
                {
                    RecordEntry(new KastnUndoEntry(description, command.ProjectId!, [])
                    {
                        BucketOperations = [op]
                    });
                }
            }
        }

        return response;
    }

    private void Accumulate(string slipId, ZetlSlipSnapshot? before, string? beforeFollowing, ZetlSlipSnapshot after)
    {
        if (gesture!.Slips.TryGetValue(slipId, out var existing))
        {
            existing.Latest = after;
            return;
        }

        gesture.Slips[slipId] = new GestureSlip
        {
            Pre = before,
            PreFollowing = beforeFollowing,
            Latest = after
        };
    }

    private void AccumulateBucket(string bucketId, ZetlBucketSnapshot before, string? beforeFollowing, ZetlBucketSnapshot after)
    {
        if (gesture!.Buckets.TryGetValue(bucketId, out var existing))
        {
            existing.Latest = after;
            return;
        }

        gesture.Buckets[bucketId] = new GestureBucket
        {
            Pre = before,
            PreFollowing = beforeFollowing,
            Latest = after
        };
    }

    // Build the operation that returns a slip from its post-change state (latest) to
    // its pre-change state (pre, or a delete when the slip was created).
    private KastnUndoOperation OperationFor(
        string slipId,
        ZetlSlipSnapshot? pre,
        string? preFollowing,
        ZetlSlipSnapshot latest)
    {
        var fromFollowing = currentProject is null
            ? null
            : KastnUndoPlanner.FollowingSlipId(currentProject, latest.BucketId, slipId);
        var to = pre is null ? KastnSlipMemento.Delete : KastnSlipMemento.To(pre);
        return new KastnUndoOperation(slipId, latest, fromFollowing, to, preFollowing);
    }

    private KastnBucketUndoOperation BucketOperationFor(
        string bucketId,
        ZetlBucketSnapshot pre,
        string? preFollowing,
        ZetlBucketSnapshot latest)
    {
        var fromFollowing = currentProject is null
            ? null
            : KastnUndoPlanner.FollowingBucketId(currentProject, bucketId);
        return new KastnBucketUndoOperation(bucketId, latest, fromFollowing, pre, preFollowing);
    }

    // A fresh user action invalidates the redo stack.
    private void RecordEntry(KastnUndoEntry entry)
    {
        // Project navigation clears history. A late old-project response must
        // not repopulate the new project's stack or discard its redo entries.
        if (entry.ProjectId != currentProject?.Id) return;
        undoStack.Push(entry);
        redoStack.Clear();
    }

    private void ClearUndoHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
    }

    private enum HistoryApplyStatus
    {
        Applied,
        Conflict,
        NotApplicable,
        Interrupted,
        OutcomeUnknown
    }

    private sealed record SlipApplyResult(
        ZetlSlipSnapshot Now,
        HistoryApplyStatus Status,
        ZetlSlipSnapshot? Conflict = null);

    private sealed record BucketApplyResult(
        ZetlBucketSnapshot Now,
        HistoryApplyStatus Status,
        ZetlBucketSnapshot? Conflict = null);

    private sealed record EntryApplyResult(
        KastnUndoEntry? Opposite,
        int Applied,
        int Kept,
        HistoryApplyStatus Status);

    private readonly record struct InverseStepResult(
        ZetlResponseEnvelope? Response,
        HistoryApplyStatus Status);

    public async Task<KastnHistoryStepResult> StepAsync(bool redo,
        Func<KastnHistoryConflict, Task<bool>> resolveConflict)
    {
        if (IsStepping) return new(KastnHistoryStepStatus.Busy);
        var source = redo ? redoStack : undoStack;
        var destination = redo ? undoStack : redoStack;
        var verb = redo ? "redo" : "undo";
        var entry = Peek(redo);
        if (entry is null) return new(KastnHistoryStepStatus.Empty);
        var capturedGeneration = generation;
        if (!IsCurrent(capturedGeneration, entry.ProjectId)) return new(KastnHistoryStepStatus.Invalidated);

        IsStepping = true;
        try
        {
            await using var refreshBatch = deferRefresh();
            var result = await ApplyEntryAsync(entry, verb, capturedGeneration, resolveConflict);
            if (!IsCurrent(capturedGeneration, entry.ProjectId)) return new(KastnHistoryStepStatus.Invalidated);
            if (result.Status is HistoryApplyStatus.Interrupted or HistoryApplyStatus.OutcomeUnknown)
            {
                var refreshed = await TryRefreshHistorySnapshotAsync(entry.ProjectId);
                if (!IsCurrent(capturedGeneration, entry.ProjectId)) return new(KastnHistoryStepStatus.Invalidated);
                if (!source.TryPeek(out var retained) || retained?.EntryId != entry.EntryId)
                    return new(KastnHistoryStepStatus.ConcurrentChange);
                var repair = refreshed is null ? null : KastnUndoPlanner.BuildRepairEntry(entry, refreshed, verb);
                if (repair is not null) source.Push(repair);
                return new(result.Status == HistoryApplyStatus.OutcomeUnknown
                    ? KastnHistoryStepStatus.OutcomeUnknown : KastnHistoryStepStatus.Interrupted,
                    entry, result.Applied, result.Kept, repair is not null);
            }
            if (!source.TryPop(entry)) return new(KastnHistoryStepStatus.ConcurrentChange);
            if (!entry.IsRepair && result.Opposite is not null) destination.Push(result.Opposite);
            _ = await TryRefreshHistorySnapshotAsync(entry.ProjectId);
            if (!IsCurrent(capturedGeneration, entry.ProjectId)) return new(KastnHistoryStepStatus.Invalidated);
            return new(KastnHistoryStepStatus.Completed, entry, result.Applied, result.Kept);
        }
        finally
        {
            IsStepping = false;
        }
    }

    private async Task<ZetlProjectSnapshot?> TryRefreshHistorySnapshotAsync(string expectedProjectId)
    {
        try
        {
            var session = await refresh();
            if (session is null) return null;
            ObserveSession(session);
            return session.ConnectionState == KastnConnectionState.Online
                && session.Project?.Id == expectedProjectId ? session.Project : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    // Apply every operation in the entry, returning the entry that reverses it
    // (built from the records' post-apply snapshots), plus how many applied and how
    // many kept the newer change. A conflicted operation asks the user: apply
    // anyway re-issues the inverse against the record's current revision, keep
    // abandons that operation (the entry is consumed either way).
    private async Task<EntryApplyResult> ApplyEntryAsync(
        KastnUndoEntry entry,
        string verb, long capturedGeneration,
        Func<KastnHistoryConflict, Task<bool>> resolveConflict)
    {
        var oppositeOps = new List<KastnUndoOperation>();
        var kept = 0;
        foreach (var op in entry.Operations)
        {
            var current = op;
            while (true)
            {
                var result = await ApplyOperationAsync(entry.ProjectId, current, capturedGeneration);
                if (result.Status == HistoryApplyStatus.Applied)
                {
                    oppositeOps.Add(KastnUndoPlanner.Opposite(current, result.Now));
                    // This step advanced the slip's revision; re-thread the stacked
                    // entries that still expect the older one, so a run of undos (or
                    // redos) over the same slip can walk the whole history instead of
                    // conflicting after the first step.
                    undoStack.RethreadRevision(current.SlipId, result.Now.Revision);
                    redoStack.RethreadRevision(current.SlipId, result.Now.Revision);
                    break;
                }

                if (result.Status == HistoryApplyStatus.NotApplicable)
                {
                    kept++;
                    break;
                }

                if (result.Status is HistoryApplyStatus.Interrupted or HistoryApplyStatus.OutcomeUnknown)
                {
                    return new EntryApplyResult(
                        Opposite: null,
                        Applied: oppositeOps.Count,
                        Kept: kept,
                        Status: result.Status);
                }

                var applyAnyway = await resolveConflict(new KastnHistoryConflict(
                    verb,
                    entry.Description,
                    result.Conflict!.Text,
                    current.To.IsDelete
                        ? "(The slip returns to Deleted.)"
                        : current.To.Restore!.Text));
                if (!IsCurrent(capturedGeneration, entry.ProjectId))
                    return new EntryApplyResult(null, oppositeOps.Count, kept, HistoryApplyStatus.Interrupted);
                if (!applyAnyway)
                {
                    kept++;
                    break;
                }

                // Re-issue the inverse against the record's current state; it can
                // conflict again if another change races, re-asking with fresh text.
                current = current with { From = result.Conflict! };
            }
        }

        var oppositeBucketOps = new List<KastnBucketUndoOperation>();
        foreach (var op in entry.BucketOperations)
        {
            var current = op;
            while (true)
            {
                var result = await ApplyBucketOperationAsync(entry.ProjectId, current, capturedGeneration);
                if (result.Status == HistoryApplyStatus.Applied)
                {
                    oppositeBucketOps.Add(KastnUndoPlanner.Opposite(current, result.Now));
                    undoStack.RethreadBucketRevision(current.BucketId, result.Now.Revision);
                    redoStack.RethreadBucketRevision(current.BucketId, result.Now.Revision);
                    break;
                }

                if (result.Status == HistoryApplyStatus.NotApplicable)
                {
                    kept++;
                    break;
                }

                if (result.Status is HistoryApplyStatus.Interrupted or HistoryApplyStatus.OutcomeUnknown)
                {
                    return new EntryApplyResult(
                        Opposite: null,
                        Applied: oppositeOps.Count + oppositeBucketOps.Count,
                        Kept: kept,
                        Status: result.Status);
                }

                var applyAnyway = await resolveConflict(new KastnHistoryConflict(
                    verb,
                    entry.Description,
                    $"Bucket “{result.Conflict!.Name}”",
                    $"Bucket “{current.To.Name}”"));
                if (!IsCurrent(capturedGeneration, entry.ProjectId))
                    return new EntryApplyResult(null, oppositeOps.Count + oppositeBucketOps.Count, kept, HistoryApplyStatus.Interrupted);
                if (!applyAnyway)
                {
                    kept++;
                    break;
                }

                current = current with { From = result.Conflict! };
            }
        }

        // Reverse so a later undo replays the opposite operations in the mirror order.
        oppositeOps.Reverse();
        oppositeBucketOps.Reverse();
        var opposite = oppositeOps.Count > 0 || oppositeBucketOps.Count > 0
            ? new KastnUndoEntry(entry.Description, entry.ProjectId, oppositeOps)
            {
                BucketOperations = oppositeBucketOps
            }
            : null;
        return new EntryApplyResult(
            opposite,
            oppositeOps.Count + oppositeBucketOps.Count,
            kept,
            HistoryApplyStatus.Applied);
    }

    // Run one operation's inverse commands, threading the revision between steps.
    // Returns the slip's resulting snapshot and whether the operation applied. A
    // best-effort step (position restore) may fail quietly; a conflict on a real
    // step aborts the operation and carries the record's current snapshot so the
    // caller can offer apply-anyway.
    private async Task<SlipApplyResult> ApplyOperationAsync(
        string projectId,
        KastnUndoOperation op, long capturedGeneration)
    {
        var now = op.From;
        foreach (var step in KastnUndoPlanner.BuildSteps(op))
        {
            var execution = await ExecuteInverseStepAsync(projectId, op.SlipId, now.Revision, step, capturedGeneration);
            if (execution.Status is HistoryApplyStatus.Interrupted or HistoryApplyStatus.OutcomeUnknown)
            {
                return new SlipApplyResult(now, execution.Status);
            }
            var response = execution.Response!;

            if (response.Status == ZetlResponseStatus.Success)
            {
                if (ReadSnapshot<ZetlSlipSnapshot>(response.Payload) is not { } snapshot
                    || snapshot.Id != op.SlipId || snapshot.Revision <= now.Revision)
                    return new SlipApplyResult(now, HistoryApplyStatus.OutcomeUnknown);
                now = snapshot;
                continue;
            }

            // A position restore that no longer fits (stale anchor, nothing to move)
            // must not fail the operation; the meaningful change already applied.
            if (step.BestEffort && response.Status != ZetlResponseStatus.Conflict)
            {
                continue;
            }

            if (response.Status == ZetlResponseStatus.Conflict
                && response.Conflict is { TargetKind: ZetlEntityKind.Slip }
                && response.Conflict.TargetId == op.SlipId
                && ReadSnapshot<ZetlSlipSnapshot>(response.Conflict.Current) is { } conflict
                && conflict.Id == op.SlipId)
            {
                return new SlipApplyResult(now, HistoryApplyStatus.Conflict, conflict);
            }

            var notApplicable = response.Status is
                ZetlResponseStatus.NotFound or ZetlResponseStatus.ValidationError;
            return notApplicable && now.Revision == op.From.Revision
                ? new SlipApplyResult(now, HistoryApplyStatus.NotApplicable)
                : new SlipApplyResult(now, HistoryApplyStatus.Interrupted);
        }

        return new SlipApplyResult(now, HistoryApplyStatus.Applied);
    }

    // The bucket twin of ApplyOperationAsync.
    private async Task<BucketApplyResult> ApplyBucketOperationAsync(
        string projectId,
        KastnBucketUndoOperation op, long capturedGeneration)
    {
        var now = op.From;
        foreach (var step in KastnUndoPlanner.BuildBucketSteps(op))
        {
            var execution = await ExecuteInverseStepAsync(projectId, op.BucketId, now.Revision, step, capturedGeneration);
            if (execution.Status is HistoryApplyStatus.Interrupted or HistoryApplyStatus.OutcomeUnknown)
            {
                return new BucketApplyResult(now, execution.Status);
            }
            var response = execution.Response!;

            if (response.Status == ZetlResponseStatus.Success)
            {
                if (ReadSnapshot<ZetlBucketSnapshot>(response.Payload) is not { } snapshot
                    || snapshot.Id != op.BucketId || snapshot.Revision <= now.Revision)
                    return new BucketApplyResult(now, HistoryApplyStatus.OutcomeUnknown);
                now = snapshot;
                continue;
            }

            if (step.BestEffort && response.Status != ZetlResponseStatus.Conflict)
            {
                continue;
            }

            if (response.Status == ZetlResponseStatus.Conflict
                && response.Conflict is { TargetKind: ZetlEntityKind.Bucket }
                && response.Conflict.TargetId == op.BucketId
                && ReadSnapshot<ZetlBucketSnapshot>(response.Conflict.Current) is { } conflict
                && conflict.Id == op.BucketId)
            {
                return new BucketApplyResult(now, HistoryApplyStatus.Conflict, conflict);
            }

            var notApplicable = response.Status is
                ZetlResponseStatus.NotFound or ZetlResponseStatus.ValidationError;
            return notApplicable && now.Revision == op.From.Revision
                ? new BucketApplyResult(now, HistoryApplyStatus.NotApplicable)
                : new BucketApplyResult(now, HistoryApplyStatus.Interrupted);
        }

        return new BucketApplyResult(now, HistoryApplyStatus.Applied);
    }

    // One inverse command over the wire, preserving the distinction between a
    // confirmed response, a command that was not completed, and a sent command
    // whose result is still unknown.
    private async Task<InverseStepResult> ExecuteInverseStepAsync(
        string projectId,
        string targetId,
        long expectedRevision,
        KastnInverseStep step, long capturedGeneration)
    {
        if (!IsCurrent(capturedGeneration, projectId))
            return new InverseStepResult(null, HistoryApplyStatus.Interrupted);
        var command = new ZetlCommandEnvelope
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Kind = step.Kind,
            ProjectId = projectId,
            TargetId = targetId,
            ExpectedTargetRevision = expectedRevision,
            Payload = step.Payload
        };

        try
        {
            var response = await execute(command);
            if (!IsCurrent(capturedGeneration, projectId))
                return new InverseStepResult(null, HistoryApplyStatus.Interrupted);
            if (response.CommandId != command.CommandId
                || response.ProjectId is { } responseProjectId && responseProjectId != projectId)
                return new InverseStepResult(null, HistoryApplyStatus.OutcomeUnknown);
            return new InverseStepResult(response, HistoryApplyStatus.Applied);
        }
        catch (ZetlCommandOutcomeUnknownException ex)
        {
            if (ex.ServerInstanceChanged)
            {
                generation++;
                ClearUndoHistory();
            }
            return new InverseStepResult(null, HistoryApplyStatus.OutcomeUnknown);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            return new InverseStepResult(null, HistoryApplyStatus.Interrupted);
        }
    }

    private static T? ReadSnapshot<T>(JsonElement? payload) where T : class
    {
        try { return payload?.Deserialize<T>(ZetlProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    // The user-facing label for an undoable command, or null when the command is
    // not recorded. Bucket creation and deletion stay unrecorded — their inverses
    // need re-creation semantics that v1 bucket undo deliberately excludes.
    private static string? UndoDescription(ZetlCommandKind kind) => kind switch
    {
        ZetlCommandKind.AddSlip => "Add slip",
        ZetlCommandKind.UpdateSlip => "Edit slip",
        ZetlCommandKind.MoveSlip => "Move slip",
        ZetlCommandKind.ReorderSlip => "Reorder slip",
        ZetlCommandKind.DeleteSlip => "Delete slip",
        ZetlCommandKind.UpdateBucket => "Edit bucket",
        ZetlCommandKind.SetBucketHeading => "Edit bucket heading",
        ZetlCommandKind.ReorderBucket => "Reorder bucket",
        _ => null
    };

    private static bool IsBucketCommand(ZetlCommandKind kind) =>
        kind is ZetlCommandKind.UpdateBucket
            or ZetlCommandKind.SetBucketHeading
            or ZetlCommandKind.ReorderBucket;

}
