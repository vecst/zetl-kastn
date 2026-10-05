using System.Text.Json;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnSlipBatchResult(int Changed, int Skipped, int Failed, bool Interrupted);

// A batch owns stable source IDs/revisions and document order, independently of
// controls. Revision failures count per item; session invalidation stops unsent
// commands. Undo gestures and refresh deferral are supplied by the caller.
internal sealed class KastnSlipMutationBatch(string projectId, IReadOnlyList<ZetlSlipSnapshot> targets)
{
    private readonly ZetlSlipSnapshot[] captured = targets.ToArray();
    public async Task<KastnSlipBatchResult> ExecuteAsync(
        Func<ZetlSlipSnapshot, int, ZetlCommandEnvelope?> build,
        Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute,
        Func<bool> isCurrent,
        Action<ZetlSlipSnapshot>? accepted = null)
    {
        var changed = 0;
        var skipped = 0;
        var failed = 0;
        for (var index = 0; index < captured.Length; index++)
        {
            if (!isCurrent()) return new(changed, skipped, failed, true);
            var target = captured[index];
            var command = build(target, index);
            if (command is null) { skipped++; continue; }
            if (command.ProjectId != projectId || command.TargetId != target.Id
                || command.ExpectedTargetRevision != target.Revision)
                throw new InvalidOperationException("Batch command changed its captured target.");
            var response = await execute(command);
            if (response.Status == ZetlResponseStatus.Success && response.CommandId == command.CommandId
                && (response.ProjectId is null || response.ProjectId == projectId)
                && response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options) is { } saved
                && saved.Id == target.Id && saved.Revision > target.Revision)
            {
                changed++;
                if (isCurrent()) accepted?.Invoke(saved);
            }
            else failed++;
        }
        return new(changed, skipped, failed, !isCurrent());
    }
}
