using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal enum KastnMoveStatus { Completed, Interrupted, Failed, Conflict, OutcomeUnknown }
internal sealed record KastnMoveResult(KastnMoveStatus Status, int CompletedItems, int ConfirmedCommands,
    string? Error = null);

// Captures target snapshots and threads only confirmed revisions. The injected
// continuation guard checks workflow/session and live destination validity.
internal sealed class KastnMoveOperation
{
    private sealed record Target(string Id, long Revision, IReadOnlyList<(ZetlCommandKind Kind, object Payload)> Steps);
    private readonly IReadOnlyList<Target> targets;
    public KastnDropPlan Plan { get; }

    public KastnMoveOperation(KastnProjectIndex index, KastnDropPlan plan)
    {
        if (!KastnDropPlanner.IsValid(index, plan)) throw new InvalidOperationException("The drop target changed. Try the move again.");
        Plan = plan with { SlipIds = plan.SlipIds.ToArray() };
        var captured = new List<Target>();
        if (plan.Action == KastnDropAction.SlipMove)
        {
            foreach (var id in Plan.SlipIds)
            {
                var slip = index.Slip(id)!;
                var steps = new List<(ZetlCommandKind, object)>();
                if (slip.BucketId != Plan.DestinationBucketId)
                    steps.Add((ZetlCommandKind.MoveSlip, new MoveSlipCommand { DestinationBucketId = Plan.DestinationBucketId! }));
                var appendReorder = slip.BucketId == Plan.DestinationBucketId && Plan.BeforeSlipId is null
                    && (Plan.SlipIds.Count > 1 || index.Slips(slip.BucketId).LastOrDefault()?.Id != slip.Id);
                if (Plan.BeforeSlipId is not null || appendReorder)
                    steps.Add((ZetlCommandKind.ReorderSlip, new ReorderSlipCommand { BeforeSlipId = Plan.BeforeSlipId }));
                captured.Add(new(id, slip.Revision, steps));
            }
        }
        else
        {
            var bucket = index.Bucket(Plan.SourceId)!;
            var steps = new List<(ZetlCommandKind, object)>();
            if (bucket.ParentBucketId != Plan.NewParentBucketId)
                steps.Add((ZetlCommandKind.UpdateBucket, new UpdateBucketCommand
                {
                    Name = bucket.Name, ParentBucketId = Plan.NewParentBucketId,
                    Settings = bucket.Settings with { }, RenderKind = bucket.RenderKind
                }));
            if (Plan.Action == KastnDropAction.BucketReorder)
                steps.Add((ZetlCommandKind.ReorderBucket, new ReorderBucketCommand { BeforeBucketId = Plan.BeforeBucketId }));
            captured.Add(new(bucket.Id, bucket.Revision, steps));
        }
        targets = captured;
    }

    public async Task<KastnMoveResult> ExecuteAsync(
        Func<ZetlCommandEnvelope, object, Task<ZetlResponseEnvelope>> execute, Func<bool> canContinue)
    {
        var completed = 0;
        var confirmed = 0;
        try
        {
            foreach (var target in targets)
            {
                var revision = target.Revision;
                foreach (var (kind, payload) in target.Steps)
                {
                    if (!canContinue()) return new(KastnMoveStatus.Interrupted, completed, confirmed, "The move was interrupted.");
                    var command = ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), kind, payload,
                        Plan.ProjectId, target.Id, revision);
                    var response = await execute(command, payload);
                    if (response.Status != ZetlResponseStatus.Success)
                        return new(response.Status == ZetlResponseStatus.Conflict ? KastnMoveStatus.Conflict : KastnMoveStatus.Failed,
                            completed, confirmed, response.Error?.Message ?? $"Move failed: {response.Status}.");
                    if (!IsConfirmedResponse(Plan, command, response))
                        return new(KastnMoveStatus.OutcomeUnknown, completed, confirmed, "The move could not be confirmed. Refresh before trying again.");
                    revision++;
                    confirmed++;
                }
                completed++;
            }
            return new(KastnMoveStatus.Completed, completed, confirmed);
        }
        catch (ZetlCommandOutcomeUnknownException ex)
        {
            return new(KastnMoveStatus.OutcomeUnknown, completed, confirmed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            return new(KastnMoveStatus.Interrupted, completed, confirmed, ex.Message);
        }
    }

    public static bool IsConfirmedResponse(KastnDropPlan plan, ZetlCommandEnvelope command, ZetlResponseEnvelope response)
    {
        if (response.Status != ZetlResponseStatus.Success || response.CommandId != command.CommandId
            || response.ProjectId is { } id && id != plan.ProjectId || response.Payload is null) return false;
        try
        {
            if (plan.Action == KastnDropAction.SlipMove)
            {
                var slip = response.Payload.Value.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);
                return slip is not null && slip.Id == command.TargetId
                    && slip.Revision == command.ExpectedTargetRevision + 1 && slip.BucketId == plan.DestinationBucketId;
            }
            var bucket = response.Payload.Value.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options);
            return bucket is not null && bucket.Id == command.TargetId
                && bucket.Revision == command.ExpectedTargetRevision + 1 && bucket.ParentBucketId == plan.NewParentBucketId;
        }
        catch (JsonException) { return false; }
    }
}
