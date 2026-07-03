using System.Text.Json;
using Avalonia.Controls;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    // Kastn-local edit history, distinct from Zetl's held-Ctrl+Z coldkey stack.
    // This is the only undo surface: the slip editor's native TextBox undo is
    // disabled, so typing, style toggles, and moves all undo in one ordered run.
    private readonly KastnUndoHistory undoStack = new();
    private readonly KastnUndoHistory redoStack = new();

    // While non-null, slip mutations accumulate into one gesture instead of each
    // becoming its own undo entry. Coalescing by slip id keeps a gesture that
    // touches one slip with several commands (divider insert, drag) revision-correct.
    private GestureAccumulator? gesture;

    private sealed class GestureAccumulator(string description, string projectId)
    {
        public string Description { get; } = description;
        public string ProjectId { get; } = projectId;
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
    // calls join the outer gesture. Dispose finalizes synchronously — the gesture
    // body has already refreshed the project, so no further IPC is needed.
    private IDisposable BeginGesture(string description)
    {
        if (gesture is not null || currentProject is null)
        {
            return NullScope.Instance;
        }

        gesture = new GestureAccumulator(description, currentProject.Id);
        return new GestureScope(this);
    }

    private sealed class GestureScope(MainWindow owner) : IDisposable
    {
        public void Dispose() => owner.EndGesture();
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private void EndGesture()
    {
        var accumulated = gesture;
        gesture = null;
        if (accumulated is null || currentProject is null)
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
    private async Task<ZetlResponseEnvelope> ExecuteMutationAsync(ZetlCommandEnvelope command)
    {
        if (UndoDescription(command.Kind) is not { } description || command.ProjectId is null)
        {
            return await connection.ExecuteAsync(command);
        }

        return IsBucketCommand(command.Kind)
            ? await ExecuteBucketMutationAsync(command, description)
            : await ExecuteSlipMutationAsync(command, description);
    }

    private async Task<ZetlResponseEnvelope> ExecuteSlipMutationAsync(
        ZetlCommandEnvelope command,
        string description)
    {
        var project = currentProject;
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

        var response = await connection.ExecuteAsync(command);
        if (response.Status == ZetlResponseStatus.Success
            && response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options) is { } after)
        {
            if (gesture is not null
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
        var project = currentProject;
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

        var response = await connection.ExecuteAsync(command);
        if (response.Status == ZetlResponseStatus.Success
            && before is not null
            && response.Payload?.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options) is { } after)
        {
            if (gesture is not null
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
        undoStack.Push(entry);
        redoStack.Clear();
    }

    private void ClearUndoHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
    }

    private Task UndoLastAsync() => StepHistoryAsync(undoStack, redoStack, "undo", "Nothing to undo.");

    private Task RedoLastAsync() => StepHistoryAsync(redoStack, undoStack, "redo", "Nothing to redo.");

    private async Task StepHistoryAsync(
        KastnUndoHistory source,
        KastnUndoHistory destination,
        string verb,
        string emptyMessage)
    {
        if (!IsOnline)
        {
            statusText.Text = "Connect to Zetl to edit.";
            return;
        }

        // Flush any pending editor save (and surface its conflict) first, so we act
        // on a settled slip.
        if (!await SaveEditorAsync())
        {
            return;
        }

        if (!source.TryPop(out var entry) || entry is null)
        {
            statusText.Text = emptyMessage;
            return;
        }

        var (opposite, applied, kept) = await ApplyEntryAsync(entry, verb);
        await connection.RefreshAsync();

        if (opposite is not null)
        {
            destination.Push(opposite);
        }

        statusText.Text = kept > 0
            ? $"{Capitalize(verb)}: {entry.Description} — {kept} item{Plural(kept)} kept the newer change."
            : applied == 0
                ? $"Nothing to {verb} — {entry.Description} already changed."
                : $"{Capitalize(verb)}: {entry.Description}";
    }

    // Apply every operation in the entry, returning the entry that reverses it
    // (built from the records' post-apply snapshots), plus how many applied and how
    // many kept the newer change. A conflicted operation asks the user: apply
    // anyway re-issues the inverse against the record's current revision, keep
    // abandons that operation (the entry is consumed either way).
    private async Task<(KastnUndoEntry? Opposite, int Applied, int Kept)> ApplyEntryAsync(
        KastnUndoEntry entry,
        string verb)
    {
        var oppositeOps = new List<KastnUndoOperation>();
        var kept = 0;
        foreach (var op in entry.Operations)
        {
            var current = op;
            while (true)
            {
                var (now, ok, conflict) = await ApplyOperationAsync(entry.ProjectId, current);
                if (ok)
                {
                    oppositeOps.Add(KastnUndoPlanner.Opposite(current, now));
                    // This step advanced the slip's revision; re-thread the stacked
                    // entries that still expect the older one, so a run of undos (or
                    // redos) over the same slip can walk the whole history instead of
                    // conflicting after the first step.
                    undoStack.RethreadRevision(current.SlipId, now.Revision);
                    redoStack.RethreadRevision(current.SlipId, now.Revision);
                    break;
                }

                if (conflict is null)
                {
                    kept++;
                    break;
                }

                var applyAnyway = await KastnDialogs.UndoConflictAsync(
                    this,
                    verb,
                    entry.Description,
                    conflict.Text,
                    current.To.IsDelete
                        ? "(The slip returns to Deleted.)"
                        : current.To.Restore!.Text);
                if (!applyAnyway)
                {
                    kept++;
                    break;
                }

                // Re-issue the inverse against the record's current state; it can
                // conflict again if another change races, re-asking with fresh text.
                current = current with { From = conflict };
            }
        }

        var oppositeBucketOps = new List<KastnBucketUndoOperation>();
        foreach (var op in entry.BucketOperations)
        {
            var current = op;
            while (true)
            {
                var (now, ok, conflict) = await ApplyBucketOperationAsync(entry.ProjectId, current);
                if (ok)
                {
                    oppositeBucketOps.Add(KastnUndoPlanner.Opposite(current, now));
                    undoStack.RethreadBucketRevision(current.BucketId, now.Revision);
                    redoStack.RethreadBucketRevision(current.BucketId, now.Revision);
                    break;
                }

                if (conflict is null)
                {
                    kept++;
                    break;
                }

                var applyAnyway = await KastnDialogs.UndoConflictAsync(
                    this,
                    verb,
                    entry.Description,
                    $"Bucket “{conflict.Name}”",
                    $"Bucket “{current.To.Name}”");
                if (!applyAnyway)
                {
                    kept++;
                    break;
                }

                current = current with { From = conflict };
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
        return (opposite, oppositeOps.Count + oppositeBucketOps.Count, kept);
    }

    // Run one operation's inverse commands, threading the revision between steps.
    // Returns the slip's resulting snapshot and whether the operation applied. A
    // best-effort step (position restore) may fail quietly; a conflict on a real
    // step aborts the operation and carries the record's current snapshot so the
    // caller can offer apply-anyway.
    private async Task<(ZetlSlipSnapshot Now, bool Ok, ZetlSlipSnapshot? Conflict)> ApplyOperationAsync(
        string projectId,
        KastnUndoOperation op)
    {
        var now = op.From;
        foreach (var step in KastnUndoPlanner.BuildSteps(op))
        {
            var response = await ExecuteInverseStepAsync(projectId, op.SlipId, now.Revision, step);
            if (response is null)
            {
                return (now, false, null);
            }

            if (response.Status == ZetlResponseStatus.Success)
            {
                if (response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options) is { } snapshot)
                {
                    now = snapshot;
                }

                continue;
            }

            // A position restore that no longer fits (stale anchor, nothing to move)
            // must not fail the operation; the meaningful change already applied.
            if (step.BestEffort && response.Status != ZetlResponseStatus.Conflict)
            {
                continue;
            }

            var conflict = response.Status == ZetlResponseStatus.Conflict
                ? response.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
                : null;
            return (now, false, conflict);
        }

        return (now, true, null);
    }

    // The bucket twin of ApplyOperationAsync.
    private async Task<(ZetlBucketSnapshot Now, bool Ok, ZetlBucketSnapshot? Conflict)> ApplyBucketOperationAsync(
        string projectId,
        KastnBucketUndoOperation op)
    {
        var now = op.From;
        foreach (var step in KastnUndoPlanner.BuildBucketSteps(op))
        {
            var response = await ExecuteInverseStepAsync(projectId, op.BucketId, now.Revision, step);
            if (response is null)
            {
                return (now, false, null);
            }

            if (response.Status == ZetlResponseStatus.Success)
            {
                if (response.Payload?.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options) is { } snapshot)
                {
                    now = snapshot;
                }

                continue;
            }

            if (step.BestEffort && response.Status != ZetlResponseStatus.Conflict)
            {
                continue;
            }

            var conflict = response.Status == ZetlResponseStatus.Conflict
                ? response.Conflict?.Current.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)
                : null;
            return (now, false, conflict);
        }

        return (now, true, null);
    }

    // One inverse command over the wire; null means a transport failure that was
    // already surfaced in the status line.
    private async Task<ZetlResponseEnvelope?> ExecuteInverseStepAsync(
        string projectId,
        string targetId,
        long expectedRevision,
        KastnInverseStep step)
    {
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
            return await connection.ExecuteAsync(command);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = $"Edit history failed: {ex.Message}";
            return null;
        }
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

    // True when a text field owns focus, so window-level Ctrl+Z/Ctrl+Y defer to
    // that box's own undo. The slip editor never reaches this guard — its tunnel
    // handler claims the keys for Kastn history first.
    private bool IsTextInputFocused() => FocusManager?.GetFocusedElement() is TextBox;

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
