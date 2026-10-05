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

    private async Task OnFontFamilyChangedAsync()
    {
        if (slipTypographyUpdating || fontFamilyBox.SelectedItem is not KastnFontFamilyItem choice)
        {
            return;
        }

        var singleStatus = choice.Value.Length == 0
            ? "Font reset to the view default."
            : $"Font set to {choice.Label}.";
        var batchStatus = choice.Value.Length == 0
            ? "reset to the default font"
            : $"set in {choice.Label}";
        await ApplySlipTypographyPropertyAsync(
            singleStatus,
            batchStatus,
            text => new UpdateSlipCommand { Text = text, FontFamily = choice.Value });
    }

    private async Task OnFontSizeChangedAsync()
    {
        if (slipTypographyUpdating || fontSizeBox.SelectedItem is not KastnFontSizeItem choice)
        {
            return;
        }

        var singleStatus = choice.Value == 0
            ? "Font size reset to the view default."
            : $"Font size set to {choice.Value} pt.";
        var batchStatus = choice.Value == 0
            ? "reset to the default font size"
            : $"set to {choice.Value} pt";
        await ApplySlipTypographyPropertyAsync(
            singleStatus,
            batchStatus,
            text => new UpdateSlipCommand { Text = text, FontSize = choice.Value });
    }

    private async Task OnTextColorChangedAsync()
    {
        if (slipTypographyUpdating || textColorBox.SelectedItem is not KastnTextColorItem choice)
        {
            return;
        }

        var singleStatus = choice.Value.Length == 0
            ? "Text color reset to the view default."
            : $"Text color set to {choice.Label}.";
        var batchStatus = choice.Value.Length == 0
            ? "reset to the default text color"
            : $"colored {choice.Label.ToLowerInvariant()}";
        await ApplySlipTypographyPropertyAsync(
            singleStatus,
            batchStatus,
            text => new UpdateSlipCommand { Text = text, TextColor = choice.Value });
    }

    private async Task ApplySlipTypographyPropertyAsync(
        string singleStatus,
        string batchStatus,
        Func<string, UpdateSlipCommand> build)
    {
        if (!HasBatchSelection())
        {
            if (SelectedSlips() is [var slip])
            {
                await UpdateSlipPropertyAsync(slip, build, singleStatus);
            }

            return;
        }

        await ApplyBatchAsync(batchStatus, (slip, _) => build(slip.Text));
    }

    // Bold/italic/strike are whole-slip render properties (like alignment and the
    // note kind) — nothing is written into the body text, and inline emphasis
    // stays typed Markdown. A single selection toggles the flag; a multi-selection
    // applies uniformly (styled when any selected slip is still unstyled). Bold on
    // a bucket title toggles the heading's own bold instead.
    private async Task ToggleSlipStyleAsync(string kind)
    {
        if (kind == ZetlInlineStyleKinds.Bold && TitleModeBucket() is { } bucket)
        {
            await SendBucketHeadingAsync(bucket, bucket.HeadingAlign, !bucket.HeadingBold, bucket.HeadingLevel);
            return;
        }

        if (!HasBatchSelection())
        {
            if (SelectedSlips() is not [var slip])
            {
                return;
            }

            var enabled = !SlipStyleFlag(slip, kind);
            await UpdateSlipPropertyAsync(
                slip,
                text => SlipStyleCommand(text, kind, enabled),
                $"{InlineStyleLabel(kind)} {(enabled ? "on" : "off")}.");
            return;
        }

        var enable = SelectedSlips().Any(slip => !SlipStyleFlag(slip, kind));
        await ApplyBatchAsync(
            SlipStyleActionLabel(kind, enable),
            (slip, _) => SlipStyleCommand(slip.Text, kind, enable));
    }

    private static bool SlipStyleFlag(ZetlSlipSnapshot slip, string kind) => kind switch
    {
        ZetlInlineStyleKinds.Italic => slip.Italic,
        ZetlInlineStyleKinds.Strike => slip.Strike,
        _ => slip.Bold
    };

    private static UpdateSlipCommand SlipStyleCommand(string text, string kind, bool enabled) => kind switch
    {
        ZetlInlineStyleKinds.Italic => new UpdateSlipCommand { Text = text, Italic = enabled },
        ZetlInlineStyleKinds.Strike => new UpdateSlipCommand { Text = text, Strike = enabled },
        _ => new UpdateSlipCommand { Text = text, Bold = enabled }
    };

    private static string SlipStyleActionLabel(string kind, bool enable) => kind switch
    {
        ZetlInlineStyleKinds.Italic => enable ? "italicized" : "unitalicized",
        ZetlInlineStyleKinds.Strike => enable ? "struck through" : "unstruck",
        _ => enable ? "bolded" : "unbolded"
    };

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
                && !ZetlBlockKinds.IsStructural(slip.BlockKind))
            .ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        var context = CaptureMutationContext();
        using var busy = mutations.TryBeginWrite();
        if (busy is null) return;
        using var gesture = BeginGesture(ordered.Count == 1 ? $"{Capitalize(actionLabel)} slip" : $"{Capitalize(actionLabel)} slips");
        await using var refresh = connection.DeferRefresh();
        try
        {
            var result = await new KastnSlipMutationBatch(context.ProjectId, ordered).ExecuteAsync(
                (slip, index) => ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.UpdateSlip,
                    build(slip, index), context.ProjectId, slip.Id, slip.Revision),
                command => ExecuteSlipBatchCommandAsync(command, context), () => IsCurrentMutation(context));
            if (result.Interrupted || !IsCurrentMutation(context)) return;
            await connection.SynchronizeAsync();
            if (IsCurrentMutationScope(context)) ShowMutationCompletion(context, result.Failed == 0
                ? $"{result.Changed} slip{Plural(result.Changed)} {actionLabel}."
                : $"{result.Changed} {actionLabel}; {result.Failed} failed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { if (IsCurrentMutation(context)) statusText.Text = ex.Message; }
    }
}
