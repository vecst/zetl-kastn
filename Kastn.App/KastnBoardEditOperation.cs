using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Owns the dialog's immutable intent and revision chain. A confirmed move is
// required before issuing the content update; the window owns interruption/UI.
internal sealed class KastnBoardEditOperation
{
    private readonly UpdateSlipCommand update;
    private readonly string? destinationBucketId;
    private readonly string sourceBucketId;

    public KastnBoardEditOperation(string projectId, ZetlSlipSnapshot slip,
        EditSlipResult result, string? deletedBucketId)
    {
        ProjectId = projectId;
        IsDelete = result.Delete;
        sourceBucketId = slip.BucketId;
        destinationBucketId = IsDelete ? deletedBucketId : result.DestinationBucketId ?? slip.BucketId;
        update = new UpdateSlipCommand
        {
            Text = result.Text.Trim(), BlockKind = result.BlockKind,
            IgnoreBucketRenderKind = result.IgnoreBucketRenderKind
        };
        if (IsDelete)
            SetCommand(ZetlCommandKind.DeleteSlip, new DeleteSlipCommand(), slip);
        else if (destinationBucketId != slip.BucketId)
            SetCommand(ZetlCommandKind.MoveSlip,
                new MoveSlipCommand { DestinationBucketId = destinationBucketId! }, slip);
        else
            SetCommand(ZetlCommandKind.UpdateSlip, update, slip);
    }

    public string ProjectId { get; }
    public bool IsDelete { get; }
    public bool IsComplete { get; private set; }
    public ZetlCommandEnvelope Command { get; private set; } = null!;
    public object Payload { get; private set; } = null!;

    public bool TryAcceptResponse(ZetlResponseEnvelope response, out ZetlSlipSnapshot saved)
    {
        saved = null!;
        if (IsComplete || response.Status != ZetlResponseStatus.Success
            || response.CommandId != Command.CommandId
            || response.ProjectId is { } id && id != ProjectId
            || response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options) is not { } slip
            || slip.Id != Command.TargetId || slip.Revision != Command.ExpectedTargetRevision + 1
            || destinationBucketId is not null && slip.BucketId != destinationBucketId
            || IsDelete && (slip.DeletedAtUtc is null || slip.DeletedFromBucketId != sourceBucketId)
            || Command.Kind == ZetlCommandKind.UpdateSlip
                && (slip.Text != update.Text || slip.BlockKind != ZetlBlockKinds.Normalize(update.BlockKind)
                    || slip.IgnoreBucketRenderKind != update.IgnoreBucketRenderKind))
            return false;

        saved = slip;
        if (Command.Kind == ZetlCommandKind.MoveSlip)
            SetCommand(ZetlCommandKind.UpdateSlip, update, saved);
        else
            IsComplete = true;
        return true;
    }

    private void SetCommand(ZetlCommandKind kind, object payload, ZetlSlipSnapshot slip)
    {
        Payload = payload;
        Command = ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), kind, payload,
            ProjectId, slip.Id, slip.Revision);
    }
}
