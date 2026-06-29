using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Batch formatting for multi-slip selections (UI roadmap Phase 2). Each action
// applies to every selected text slip through revision-checked UpdateSlip commands;
// a single selected slip falls back to the in-editor formatting path, and a selected
// bucket uses list buttons as bucket render defaults.
internal partial class MainWindow
{
    private bool HasBatchSelection() => SelectedSlips().Count >= 2;

    private async Task AlignSlipsAsync(string align)
    {
        // A bucket in title mode: the alignment targets the bucket's heading, so
        // pressing Center centers the title rather than its slips.
        if (TitleModeBucket() is { } bucket)
        {
            await SendBucketHeadingAsync(bucket, align, bucket.HeadingBold, bucket.HeadingLevel);
            return;
        }

        if (!HasBatchSelection())
        {
            await SetSlipAlignAsync(align);
            return;
        }

        await ApplyBatchAsync(
            $"aligned {align}",
            (slip, _) => new UpdateSlipCommand { Text = slip.Text, Align = align });
    }

    private async Task StrikeSlipsAsync()
    {
        if (!HasBatchSelection())
        {
            await ToggleInlineStyleAsync(ZetlInlineStyleKinds.Strike);
            return;
        }

        await ApplyBatchAsync(
            "struck through",
            (slip, _) => new UpdateSlipCommand { Text = KastnBatchFormat.ToggleStrike(slip.Text) });
    }

    // kind: bullet | ordered | task | heading | quote | code. The kind is the note's
    // own render property (nothing is written into the body); a single selection toggles
    // it, a multi-selection applies it uniformly.
    private async Task ListSlipsAsync(string kind)
    {
        if (TitleModeBucket() is { } bucket && IsBucketListRenderKind(kind))
        {
            await SetBucketRenderKindAsync(bucket, kind);
            return;
        }

        if (!HasBatchSelection())
        {
            await SetSlipBlockKindAsync(kind);
            return;
        }

        await ApplyBatchAsync(
            NoteKindActionLabel(kind),
            (slip, _) => new UpdateSlipCommand { Text = slip.Text, BlockKind = kind });
    }

    private static bool IsBucketListRenderKind(string kind) =>
        kind is ZetlBucketRenderKinds.Bullet or ZetlBucketRenderKinds.Ordered or ZetlBucketRenderKinds.Task;

    private static string NoteKindActionLabel(string kind) => kind switch
    {
        ZetlBlockKinds.Ordered => "numbered",
        ZetlBlockKinds.Task => "made a checklist",
        ZetlBlockKinds.Heading => "made a heading",
        ZetlBlockKinds.Quote => "made a quote",
        ZetlBlockKinds.Code => "made a code block",
        _ => "bulleted"
    };

    // Loop the selected text slips in document order, sending one UpdateSlip per
    // slip; mirrors the single-slip save path's status/guard handling. In a batch
    // selection no slip is in the editor, so each slip's own snapshot revision is used.
    private async Task ApplyBatchAsync(
        string actionLabel,
        Func<ZetlSlipSnapshot, int, UpdateSlipCommand> build)
    {
        if (!IsOnline || saving || currentProject is null || editorState.ConflictCurrent is not null)
        {
            return;
        }

        var selectedIds = SelectedSlips().Select(slip => slip.Id).ToHashSet(StringComparer.Ordinal);
        var ordered = currentProject.Slips
            .Where(slip => selectedIds.Contains(slip.Id)
                && slip.Type == ZetlSlipType.Text
                && !IsSlipInDeleted(slip)
                && !ZetlViewRenderer.IsStructuralKind(slip.BlockKind))
            .ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        saving = true;
        SetEditingEnabled();
        var changed = 0;
        var failed = 0;
        // Drop the per-mutation snapshot pushes during the loop so the tree/View
        // rebuild once at the end instead of flashing once per slip.
        batching = true;
        try
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                var slip = ordered[index];
                var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.UpdateSlip,
                    build(slip, index),
                    currentProject.Id,
                    slip.Id,
                    slip.Revision));
                if (response.Status == ZetlResponseStatus.Success)
                {
                    changed++;
                }
                else
                {
                    failed++;
                }
            }

            batching = false;
            await connection.RefreshAsync();
            statusText.Text = failed == 0
                ? $"{changed} slip{Plural(changed)} {actionLabel}."
                : $"{changed} {actionLabel}; {failed} failed.";
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = ex.Message;
        }
        finally
        {
            batching = false;
            saving = false;
            SetEditingEnabled();
        }
    }
}
