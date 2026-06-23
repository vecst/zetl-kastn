using ZETL.Contracts;

namespace KASTN;

// Batch formatting for a multi-slip / bucket selection (UI roadmap Phase 2). Each
// action applies to every selected text slip through revision-checked UpdateSlip
// commands; a single selected slip falls back to the in-editor formatting path.
internal partial class MainWindow
{
    private bool HasBatchSelection() => SelectedSlips().Count >= 2;

    private async Task AlignSlipsAsync(string align)
    {
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
            WrapEditorSelection("~~", "~~", "strike");
            return;
        }

        await ApplyBatchAsync(
            "struck through",
            (slip, _) => new UpdateSlipCommand { Text = KastnBatchFormat.ToggleStrike(slip.Text) });
    }

    // kind: "bullet" | "ordered" | "task".
    private async Task ListSlipsAsync(string kind)
    {
        if (!HasBatchSelection())
        {
            Func<int, string> marker = kind switch
            {
                "ordered" => index => $"{index + 1}. ",
                "task" => _ => "- [ ] ",
                _ => _ => "- ",
            };
            PrefixSelectedLines(marker);
            return;
        }

        // Ordered lists number per the user's rule: a contiguous run (adjacent
        // selected slips in one bucket) counts up; any break — an unselected slip
        // between, or a bucket boundary — restarts the count at 1.
        var numbers = kind == "ordered" ? ComputeOrderedNumbers() : null;
        await ApplyBatchAsync(
            kind switch { "ordered" => "numbered", "task" => "made a checklist", _ => "bulleted" },
            (slip, _) =>
            {
                var marker = kind switch
                {
                    "ordered" => $"{numbers![slip.Id]}. ",
                    "task" => "- [ ] ",
                    _ => "- ",
                };
                return new UpdateSlipCommand { Text = KastnBatchFormat.ApplyLineMarker(slip.Text, marker) };
            });
    }

    private IReadOnlyDictionary<string, int> ComputeOrderedNumbers()
    {
        if (currentProject is null)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var selectedIds = SelectedSlips().Select(slip => slip.Id).ToHashSet(StringComparer.Ordinal);
        return KastnBatchFormat.OrderedNumbers(
            currentProject.Slips,
            selectedIds,
            slip => slip.Type == ZetlSlipType.Text && !IsSlipInDeleted(slip));
    }

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
                && !IsSlipInDeleted(slip))
            .ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        saving = true;
        SetEditingEnabled();
        var changed = 0;
        var failed = 0;
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
            saving = false;
            SetEditingEnabled();
        }
    }
}
