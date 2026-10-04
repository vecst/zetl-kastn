using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private void OnEditorTextChanged()
    {
        if (lifetime.IsRetired || editorUpdating || editorState.SlipId is null)
        {
            return;
        }

        editorState.ApplyTextEdit(slipEditor.Text ?? "");
        ScheduleDraftJournal();
        UpdateInlineFormatButtons();
        statusText.Text = editorState.IsDirty
            ? "Unsaved changes — saved when you leave the editor."
            : connection.Current.Status;
    }

    private async void OnTreeVisibilityClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as Control)?.DataContext is KastnTreeNode node)
        {
            await ToggleTreeVisibilityAsync(node);
        }
    }

    private async Task ToggleTreeVisibilityAsync(KastnTreeNode node)
    {
        if (visibilityUpdating || !IsOnline || currentProject is null
            || saving && inflightSave is not { IsCompleted: false })
        {
            return;
        }

        var context = new KastnEditorWorkflowContext(currentProject.Id, editorState);
        var clickedSlips = node.TreeSlips().ToArray();
        var targetIds = clickedSlips.Select(slip => slip.Id).Distinct(StringComparer.Ordinal).ToArray();
        if (targetIds.Length == 0)
        {
            return;
        }
        // Preserve the clicked intent even if a refresh arrives during the save.
        var exclude = clickedSlips.Any(slip => !slip.ExcludedFromViews);
        var ownsBusy = false;
        visibilityUpdating = true;
        try
        {
            var savedEditor = await SaveEditorAsync();
            if (!context.IsSameSession(currentProject?.Id, editorState))
            {
                return;
            }
            if (!savedEditor)
            {
                statusText.Text = "Save or resolve the current slip before changing visibility.";
                return;
            }

            if (!IsOnline || saving)
            {
                return;
            }
            var targets = targetIds.Select(id => ProjectIndex.Slip(id))
                .OfType<ZetlSlipSnapshot>().ToArray();
            var changed = 0;
            var failed = 0;
            ownsBusy = true;
            saving = true;
            SetEditingEnabled();
            await using var refreshBatch = connection.DeferRefresh();
            foreach (var slip in targets.Where(slip => slip.ExcludedFromViews != exclude))
            {
                // Already-sent commands finish in their original project. Stop
                // issuing further commands after navigation or editor reselection.
                if (!IsOnline || !context.IsSameSession(currentProject?.Id, editorState))
                {
                    break;
                }
                if (slip.Id == editorState.SlipId && editorState.ConflictCurrent is not null)
                {
                    failed++;
                    continue;
                }
                var mutation = KastnEditorMutationAcceptance.Create(
                    editorState, context.ProjectId, slip, ZetlCommandKind.UpdateSlip,
                    text => new UpdateSlipCommand { Text = text, ExcludedFromViews = exclude });
                pendingEditorMutation = mutation;
                try
                {
                    var response = await ExecuteMutationAsync(mutation.Command);
                    if (response.Status == ZetlResponseStatus.Success && mutation.TryAcceptResponse(response))
                    {
                        changed++;
                        if (mutation.IsCurrentEditor)
                        {
                            PersistEditorAfterSave(context.ProjectId);
                            UpdateEditorFromState();
                        }
                    }
                    else
                    {
                        failed++;
                        if (response.Status == ZetlResponseStatus.Conflict && mutation.ReconcileConflict(response))
                        {
                            ShowConflict();
                        }
                    }
                }
                finally
                {
                    pendingEditorMutation = null;
                }
            }

            if (!context.IsSameSession(currentProject?.Id, editorState))
            {
                return;
            }
            await connection.SynchronizeAsync();
            if (context.IsSameSession(currentProject?.Id, editorState))
            {
                var action = exclude ? "hidden" : "shown";
                statusText.Text = editorState.ConflictCurrent is not null
                    ? "Resolve the slip conflict before continuing."
                    : failed == 0 ? $"{changed} slip{Plural(changed)} {action}."
                        : $"{changed} slip{Plural(changed)} {action}; {failed} failed.";
            }
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (context.IsSameSession(currentProject?.Id, editorState))
            {
                statusText.Text = ex.Message;
            }
        }
        finally
        {
            if (ownsBusy)
            {
                saving = false;
            }
            visibilityUpdating = false;
            SetEditingEnabled();
        }
    }

    // A save response may arrive before its posted UI projection. Settle the
    // saved baseline before a workflow captures authoritative target snapshots.
    private bool TrySettleSavedEditorSnapshot(string projectId)
    {
        if (editorState.SlipId is not { } id) return true;
        if (ProjectIndex.Slip(id) is { } displayed && displayed.Revision >= editorState.Revision) return true;
        var latest = connection.Current;
        if (latest.Project?.Id != projectId || latest.Project.Slips.FirstOrDefault(slip => slip.Id == id) is not { } saved
            || saved.Revision < editorState.Revision) return false;
        ApplySnapshot(latest);
        return currentProject?.Id == projectId;
    }

    private Task<bool> SaveEditorAsync()
    {
        // Coalesce concurrent callers (editor focus-loss racing a slip selection)
        // onto one in-flight save, so the second caller awaits the same result
        // instead of seeing a false "save failed" from the `saving` guard.
        if (inflightSave is { } pending && !pending.IsCompleted)
        {
            return pending;
        }

        inflightSave = SaveEditorCoreAsync();
        return inflightSave;
    }

    private async Task<bool> SaveEditorCoreAsync()
    {
        // Native close can lose editor focus before Closed retires the window.
        // That focus-loss autosave must not undo an explicit discard decision.
        if (lifetime.AllowClose) return false;
        if (!editorState.IsDirty)
        {
            return editorState.ConflictCurrent is null;
        }

        if (!IsOnline || saving || editorState.ConflictCurrent is not null
            || currentProject is null || editorState.SlipId is null)
        {
            return false;
        }

        var operation = KastnEditorSaveOperation.Create(editorState, currentProject);
        if (operation is null)
        {
            statusText.Text = "A slip needs a title or body.";
            return false;
        }

        saving = true;
        pendingEditorSave = operation;
        SetEditingEnabled();
        try
        {
            var result = await operation.ExecuteAsync(ExecuteMutationAsync);
            if (lifetime.IsRetired) return false;
            if (result.Saved && operation.IsCurrentEditor)
            {
                PersistEditorAfterSave(operation.ProjectId);
                UpdateEditorFromState();
            }

            if (editorState.ConflictCurrent is not null)
            {
                ShowConflict();
            }
            if (result.Message is { } message)
            {
                statusText.Text = message;
            }
            return result.CanLeaveEditor;
        }
        finally
        {
            pendingEditorSave = null;
            saving = false;
            if (!lifetime.IsRetired) SetEditingEnabled();
        }
    }

    // Set the selected text slip's block alignment (left/center/right). Carries the
    // current editor draft along like the eye-toggle does, so it also commits any
    // pending text edit; the renderer and on-screen View honor Align.
    private Task SetSlipAlignAsync(string align) =>
        SelectedSlips() is [var slip]
            ? UpdateSlipPropertyAsync(
                slip,
                text => new UpdateSlipCommand { Text = text, Align = align },
                $"Slip aligned {align}.")
            : Task.CompletedTask;

    private void UpdateAlignButtons(KastnSelectionContext context, bool canAlign)
    {
        var slip = context.SingleSlip;
        var titleBucket = context.TitleBucket;
        alignLeftButton.IsEnabled = canAlign;
        alignCenterButton.IsEnabled = canAlign;
        alignRightButton.IsEnabled = canAlign;

        var active = titleBucket is not null
            ? ZetlViewRenderer.NormalizeHeadingAlign(titleBucket.HeadingAlign)
            : slip is null ? null : ZetlViewRenderer.SlipAlignment(slip);
        alignLeftButton.FontWeight = active == "left" ? FontWeight.Bold : FontWeight.Normal;
        alignCenterButton.FontWeight = active == "center" ? FontWeight.Bold : FontWeight.Normal;
        alignRightButton.FontWeight = active == "right" ? FontWeight.Bold : FontWeight.Normal;
    }

    // Highlight the list button matching either the selected bucket's default list
    // render kind or the single selected note's own kind, so the buttons read as
    // toggles (pressed when that kind is active) like alignment.
    private void UpdateListButtons(KastnSelectionContext context)
    {
        var slip = context.SingleSlip;
        var bucket = context.TitleBucket;
        var active = bucket is not null
            ? ZetlViewRenderer.BucketRenderKind(bucket)
            : slip is { Type: ZetlSlipType.Text } && !IsSlipInDeleted(slip)
                ? ZetlViewRenderer.SlipBlockKind(slip)
                : "";
        bulletListButton.FontWeight = active == ZetlBlockKinds.Bullet ? FontWeight.Bold : FontWeight.Normal;
        numberListButton.FontWeight = active == ZetlBlockKinds.Ordered ? FontWeight.Bold : FontWeight.Normal;
        taskListButton.FontWeight = active == ZetlBlockKinds.Task ? FontWeight.Bold : FontWeight.Normal;
        headingButton.FontWeight = active == ZetlBlockKinds.Heading ? FontWeight.Bold : FontWeight.Normal;
        quoteButton.FontWeight = active == ZetlBlockKinds.Quote ? FontWeight.Bold : FontWeight.Normal;
        codeBlockButton.FontWeight = active == ZetlBlockKinds.Code ? FontWeight.Bold : FontWeight.Normal;
    }

    private void UpdateSlipTypographyControls(IReadOnlyList<ZetlSlipSnapshot> selected)
    {
        slipTypographyUpdating = true;
        try
        {
            var commonFamily = CommonValue(selected.Select(slip => slip.FontFamily), StringComparer.OrdinalIgnoreCase);
            var commonSize = CommonValue(selected.Select(slip => slip.FontSize));
            var commonColor = CommonValue(selected.Select(slip => slip.TextColor), StringComparer.OrdinalIgnoreCase);

            fontFamilyBox.SelectedItem = commonFamily is null
                ? null
                : FontFamilyChoices.FirstOrDefault(choice =>
                    string.Equals(choice.Value, commonFamily, StringComparison.OrdinalIgnoreCase));
            fontSizeBox.SelectedItem = commonSize is null
                ? null
                : FontSizeChoices.FirstOrDefault(choice => choice.Value == commonSize.Value);
            textColorBox.SelectedItem = commonColor is null
                ? null
                : TextColorChoices.FirstOrDefault(choice =>
                    string.Equals(choice.Value, commonColor, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            slipTypographyUpdating = false;
        }

        ApplyEditorTypography(selected.Count == 1 ? selected[0] : null);
    }

    private static string? CommonValue(
        IEnumerable<string> values,
        IEqualityComparer<string> comparer)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var first = enumerator.Current;
        while (enumerator.MoveNext())
        {
            if (!comparer.Equals(first, enumerator.Current))
            {
                return null;
            }
        }

        return first;
    }

    private static int? CommonValue(IEnumerable<int> values)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var first = enumerator.Current;
        while (enumerator.MoveNext())
        {
            if (first != enumerator.Current)
            {
                return null;
            }
        }

        return first;
    }

    private void ApplyEditorTypography(ZetlSlipSnapshot? slip)
    {
        slipEditor.ClearValue(TextBox.FontFamilyProperty);
        slipEditor.ClearValue(TextBox.FontSizeProperty);
        slipEditor.ClearValue(TextBox.ForegroundProperty);
        if (slip is null)
        {
            return;
        }

        if (KastnSlipTypography.FamilyOf(slip) is { } fontFamily)
        {
            slipEditor.FontFamily = fontFamily;
        }

        if (KastnSlipTypography.SizeOf(slip) is { } fontSize)
        {
            slipEditor.FontSize = fontSize;
        }

        if (KastnSlipTypography.ForegroundOf(slip) is { } foreground)
        {
            slipEditor.Foreground = foreground;
        }
    }

    private void UpdateInlineFormatButtons(KastnSelectionContext? context = null)
    {
        context ??= CaptureSelectionContext();
        if (!CanReadInlineStyleState(context, out var slip))
        {
            SetInlineFormatButtonActive(ZetlInlineStyleKinds.Bold, false);
            SetInlineFormatButtonActive(ZetlInlineStyleKinds.Italic, false);
            SetInlineFormatButtonActive(ZetlInlineStyleKinds.Strike, false);
            SetInlineFormatButtonActive(ZetlInlineStyleKinds.Code, false);
            SetInlineFormatButtonActive(ZetlInlineStyleKinds.Link, false);
            SetInlineFormatButtonActive(ZetlInlineStyleKinds.WikiLink, false);
            return;
        }

        var text = slipEditor.Text ?? "";
        var start = Math.Clamp(Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), start, text.Length);
        var styles = CurrentInlineStyles(slip);
        var emptyTarget = KastnInlineStyleEditing.StyleTargetForSelection(text, start, end).Length <= 0;
        // Bold/italic/strike read the slip's own whole-slip flags; code and the links
        // remain selection-scoped ranges.
        SetInlineFormatButtonActive(ZetlInlineStyleKinds.Bold, slip.Bold);
        SetInlineFormatButtonActive(ZetlInlineStyleKinds.Italic, slip.Italic);
        SetInlineFormatButtonActive(ZetlInlineStyleKinds.Strike, slip.Strike);
        SetInlineFormatButtonActive(
            ZetlInlineStyleKinds.Code,
            emptyTarget
                ? editorState.HasPendingInlineStyle(ZetlInlineStyleKinds.Code)
                : KastnInlineStyleEditing.IsStyleActiveAtSelection(text, styles, start, end - start, ZetlInlineStyleKinds.Code));
        SetInlineFormatButtonActive(
            ZetlInlineStyleKinds.Link,
            KastnInlineStyleEditing.IsStyleActiveAtSelection(text, styles, start, end - start, ZetlInlineStyleKinds.Link));
        SetInlineFormatButtonActive(
            ZetlInlineStyleKinds.WikiLink,
            KastnInlineStyleEditing.IsStyleActiveAtSelection(text, styles, start, end - start, ZetlInlineStyleKinds.WikiLink));
    }

    private void SetInlineFormatButtonActive(string kind, bool active)
    {
        var button = ZetlInlineStyleKinds.Normalize(kind) switch
        {
            ZetlInlineStyleKinds.Italic => italicButton,
            ZetlInlineStyleKinds.Strike => strikeButton,
            ZetlInlineStyleKinds.Code => codeButton,
            ZetlInlineStyleKinds.Link => linkButton,
            ZetlInlineStyleKinds.WikiLink => wikiLinkButton,
            _ => boldButton
        };
        button.Classes.Set("view-format-active", active);
    }

    // Set the selected note's kind, toggling it off when already that kind. The kind is
    // the note's render property — nothing is written into its body text.
    private Task SetSlipBlockKindAsync(string kind)
    {
        var selected = SelectedSlips();
        if (selected.Count != 1)
        {
            return Task.CompletedTask;
        }

        var slip = selected[0];
        var target = ZetlViewRenderer.SlipBlockKind(slip) == kind ? "" : kind;
        var label = target.Length == 0
            ? "Note kind cleared."
            : $"Note {NoteKindActionLabel(target)}.";
        return UpdateSlipPropertyAsync(
            slip, text => new UpdateSlipCommand { Text = text, BlockKind = target }, label);
    }

    // A dual slip carries both text content and an attached picture; its Type is
    // the preferred representation and every renderer, export, and Zetl text-first
    // flow follows it.
    private static bool IsDualRepresentationSlip(ZetlSlipSnapshot slip) =>
        slip.Picture is not null && !string.IsNullOrWhiteSpace(slip.Text);

    private Task ToggleSlipRepresentationAsync()
    {
        if (SelectedSlips() is not [var slip] || !IsDualRepresentationSlip(slip))
        {
            return Task.CompletedTask;
        }

        var toPicture = slip.Type != ZetlSlipType.Picture;
        return UpdateSlipPropertyAsync(
            slip,
            text => new UpdateSlipCommand
            {
                Text = text,
                Type = toPicture ? ZetlSlipType.Picture : ZetlSlipType.Text
            },
            toPicture ? "Presenting as a picture." : "Presenting as text.",
            requireTextType: false);
    }

    private Task AttachSlipPictureAsync() => AttachSlipPictureAsync(PickSlipPictureAsync);

    private async Task AttachSlipPictureAsync(Func<Task<SetSlipPictureCommand?>> pickPicture)
    {
        if (!IsOnline || saving || currentProject is null
            || SelectedSlips() is not [var slip]
            || IsSlipInDeleted(slip)
            || ZetlBlockKinds.IsStructural(slip.BlockKind))
        {
            return;
        }

        var projectId = currentProject.Id;
        var selectionVersion = editorState.SelectionVersion;
        var picture = await pickPicture();
        if (picture is null)
        {
            return;
        }

        // The picker and file stream can outlive the selected editor/project.
        // Re-resolve the original target in its original editor session before IPC.
        if (currentProject?.Id != projectId || editorState.SelectionVersion != selectionVersion
            || SelectedSlips() is not [var selected] || selected.Id != slip.Id
            || ProjectIndex.Slip(slip.Id) is not { } currentSlip
            || ZetlBlockKinds.IsStructural(currentSlip.BlockKind))
        {
            return;
        }
        await SendSlipCommandAsync(
            currentSlip,
            ZetlCommandKind.SetSlipPicture,
            _ => picture,
            currentSlip.Picture is null ? "Picture attached." : "Picture replaced.");
    }

    private async Task<SetSlipPictureCommand?> PickSlipPictureAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add picture",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        if (files.Count != 1)
        {
            return null;
        }

        byte[] pngBytes;
        int width;
        int height;
        try
        {
            // Decode whatever format was picked and normalize it to PNG locally,
            // so the wire carries the same normalized shape captures produce.
            await using var stream = await files[0].OpenReadAsync();
            using var source = new Bitmap(stream);
            width = source.PixelSize.Width;
            height = source.PixelSize.Height;
            using var encoded = new MemoryStream();
            source.Save(encoded);
            pngBytes = encoded.ToArray();
        }
        catch (Exception ex) when (
            ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            statusText.Text = $"Could not read the picture: {ex.Message}";
            return null;
        }

        if (pngBytes.Length > 25 * 1024 * 1024)
        {
            statusText.Text = "The picture exceeds the 25 MB limit.";
            return null;
        }

        return new SetSlipPictureCommand { Bytes = pngBytes, Width = width, Height = height };
    }

    private Task RemoveSlipPictureAsync()
    {
        if (SelectedSlips() is not [var slip] || slip.Picture is null)
        {
            return Task.CompletedTask;
        }

        return SendSlipCommandAsync(
            slip,
            ZetlCommandKind.RemoveSlipPicture,
            _ => new RemoveSlipPictureCommand(),
            "Picture removed.");
    }

    private async Task OnIgnoreBucketRenderKindChangedAsync()
    {
        if (slipRenderOptionUpdating || SelectedSlips() is not [var slip])
        {
            return;
        }

        var ignore = ignoreBucketRenderKindCheck.IsChecked == true;
        await UpdateSlipPropertyAsync(
            slip,
            text => new UpdateSlipCommand
            {
                Text = text,
                IgnoreBucketRenderKind = ignore
            },
            ignore ? "Bucket style ignored for this slip." : "Bucket style applies to this slip.");
    }

    // Toggle a task note's checked state from a checkbox click in the View. Targets the
    // clicked note by id rather than the editor selection.
    private Task ToggleSlipCheckedAsync(string slipId)
    {
        var slip = currentProject?.Slips.FirstOrDefault(item => item.Id == slipId);
        if (slip is null)
        {
            return Task.CompletedTask;
        }

        var toggled = !slip.Checked;
        return UpdateSlipPropertyAsync(
            slip,
            text => new UpdateSlipCommand { Text = text, Checked = toggled },
            toggled ? "Checked." : "Unchecked.");
    }

    // Send one UpdateSlip for a single text note. The representation toggle passes
    // requireTextType: false because it legitimately targets Picture and Url slips.
    private Task UpdateSlipPropertyAsync(
        ZetlSlipSnapshot slip,
        Func<string, UpdateSlipCommand> buildWithText,
        string successText,
        bool requireTextType = true) =>
        requireTextType && slip.Type != ZetlSlipType.Text
            ? Task.CompletedTask
            : SendSlipCommandAsync(slip, ZetlCommandKind.UpdateSlip, buildWithText, successText);

    // Send one revision-checked command for a single slip with the editor-aware
    // handling every slip action shares: skip while the slip has an unresolved
    // conflict, send the editor's revision when the slip is open, surface a
    // conflict in the editor, and refresh. A command that carries the slip's text
    // takes it from the editor draft, so it also commits pending typing; one that
    // does not (a picture change) keeps the draft.
    private async Task SendSlipCommandAsync<TPayload>(
        ZetlSlipSnapshot slip,
        ZetlCommandKind kind,
        Func<string, TPayload> buildWithText,
        string successText)
    {
        if (!IsOnline || saving || currentProject is null || IsSlipInDeleted(slip))
        {
            return;
        }

        var isEditing = string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal);
        if (isEditing && editorState.ConflictCurrent is not null)
        {
            return;
        }

        var acceptance = KastnEditorMutationAcceptance.Create(
            editorState, currentProject.Id, slip, kind, buildWithText);
        var command = acceptance.Command;

        saving = true;
        pendingEditorMutation = acceptance;
        SetEditingEnabled();
        try
        {
            var response = await ExecuteMutationAsync(command);
            if (response.Status == ZetlResponseStatus.Conflict)
            {
                if (acceptance.ReconcileConflict(response))
                {
                    ShowConflict();
                }
                return;
            }

            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Update failed: {response.Status}.";
                return;
            }

            if (!acceptance.TryAcceptResponse(response))
            {
                statusText.Text = "The update response did not confirm the updated slip.";
                return;
            }
            if (acceptance.IsCurrentEditor)
            {
                PersistEditorAfterSave(command.ProjectId!);
                UpdateEditorFromState();
                if (editorState.ConflictCurrent is not null)
                {
                    ShowConflict();
                }
            }

            await connection.SynchronizeAsync();
            if (currentProject?.Id == command.ProjectId
                && (!isEditing || acceptance.IsCurrentEditor))
            {
                statusText.Text = editorState.ConflictCurrent is not null
                    ? "Resolve the slip conflict before continuing."
                    : acceptance.IsCurrentEditor && editorState.IsDirty
                        ? $"{successText} Newer edits are still unsaved." : successText;
            }
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = ex.Message;
        }
        finally
        {
            pendingEditorMutation = null;
            saving = false;
            SetEditingEnabled();
        }
    }

    private async Task ToggleInlineStyleAsync(string kind)
    {
        if (!CanEditInlineStyle(out var slip))
        {
            return;
        }

        var (text, start, length, styles) = PrepareInlineStyleTarget(slip);
        if (length <= 0)
        {
            var enabled = editorState.TogglePendingInlineStyle(kind);
            statusText.Text = enabled
                ? $"{InlineStyleLabel(kind)} set for new text."
                : $"{InlineStyleLabel(kind)} cleared for new text.";
            UpdateInlineFormatButtons();
            slipEditor.Focus();
            return;
        }

        var updated = KastnInlineStyleEditing.ToggleTextStyle(
            text,
            styles,
            start,
            length,
            kind);
        await SaveInlineStylesAsync(slip, text, updated, $"{InlineStyleLabel(kind)} toggled.");
        slipEditor.Focus();
    }

    private Task SetEditorWebLinkAsync() => SetEditorWebLinkAsync(
        (initial, allowRemove) => KastnDialogs.EditWebLinkAsync(this, initial, allowRemove));

    private async Task SetEditorWebLinkAsync(Func<string, bool, Task<KastnDialogs.LinkEditResult?>> editLink)
    {
        if (currentProject is null || !CanEditInlineStyle(out var slip))
        {
            return;
        }

        var context = new KastnEditorWorkflowContext(currentProject.Id, editorState);
        var text = slipEditor.Text ?? "";
        var start = Math.Clamp(Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var styles = CurrentInlineStyles(slip);
        var existing = KastnInlineStyleEditing.CoveringRangeAtSelection(
            text,
            styles,
            start,
            end - start,
            ZetlInlineStyleKinds.Link);
        var linkEdit = await editLink(
            existing?.Href ?? "https://",
            existing is not null);
        if (!CanApplyLinkEdit(context, out slip))
        {
            return;
        }
        if (linkEdit is null)
        {
            slipEditor.Focus();
            return;
        }

        if (existing is not null)
        {
            var changed = linkEdit.Remove
                ? KastnInlineStyleEditing.RemoveRange(text, styles, existing)
                : KastnInlineStyleEditing.SetWebLink(
                    text,
                    styles,
                    existing.Start,
                    existing.Length,
                    linkEdit.Href ?? "");
            await SaveInlineStylesAsync(
                slip,
                text,
                changed,
                linkEdit.Remove ? "Link removed." : "Link updated.");
            slipEditor.Focus();
            return;
        }

        var (preparedText, preparedStart, preparedLength, preparedStyles) = PrepareInlineSelection(
            "link",
            text,
            start,
            end);
        var updated = KastnInlineStyleEditing.SetWebLink(
            preparedText,
            preparedStyles,
            preparedStart,
            preparedLength,
            linkEdit.Href ?? "");
        await SaveInlineStylesAsync(slip, preparedText, updated, "Link set.");

        slipEditor.Focus();
    }

    private Task InsertSlipLinkAsync() => InsertSlipLinkAsync(
        title => KastnDialogs.PickLinkRangeActionAsync(this, "Slip Link", title),
        (candidates, query) => KastnDialogs.PickSlipAsync(this, candidates, query));

    private async Task InsertSlipLinkAsync(
        Func<string, Task<KastnDialogs.LinkRangeAction?>> pickAction,
        Func<IReadOnlyList<ZetlSlipSnapshot>, string, Task<ZetlSlipSnapshot?>> pickSlip)
    {
        if (currentProject is null || !CanEditInlineStyle(out var slip))
        {
            return;
        }

        var context = new KastnEditorWorkflowContext(currentProject.Id, editorState);
        var text = slipEditor.Text ?? "";
        var selectionStart = Math.Clamp(
            Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var selectionEnd = Math.Clamp(
            Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var styles = CurrentInlineStyles(slip);
        var existing = KastnInlineStyleEditing.CoveringRangeAtSelection(
            text,
            styles,
            selectionStart,
            selectionEnd - selectionStart,
            ZetlInlineStyleKinds.WikiLink);
        if (existing is not null)
        {
            var action = await pickAction(
                $"Change or remove the link to '{existing.CachedTitle ?? "this slip"}'?");
            if (!CanApplyLinkEdit(context, out slip))
            {
                return;
            }
            if (action is null)
            {
                slipEditor.Focus();
                return;
            }

            if (action == KastnDialogs.LinkRangeAction.Remove)
            {
                var withoutLink = KastnInlineStyleEditing.RemoveRange(text, styles, existing);
                await SaveInlineStylesAsync(slip, text, withoutLink, "Slip link removed.");
                slipEditor.Focus();
                return;
            }
        }

        var query = selectionEnd > selectionStart ? text[selectionStart..selectionEnd] : "";
        var candidates = currentProject.Slips
            .Where(slip => slip.Id != editorState.SlipId && !IsSlipInDeleted(slip))
            .ToList();
        var target = await pickSlip(candidates, query);
        if (!CanApplyLinkEdit(context, out slip))
        {
            return;
        }
        if (target is null)
        {
            slipEditor.Focus();
            return;
        }

        target = ProjectIndex.Slip(target.Id);
        if (target is null || target.Id == slip.Id || IsSlipInDeleted(target))
        {
            return;
        }
        var label = selectionEnd > selectionStart ? text[selectionStart..selectionEnd] : ZetlSlipLinks.TitleFor(target);
        if (existing is not null)
        {
            var changed = KastnInlineStyleEditing.SetWikiLink(
                text,
                styles,
                existing.Start,
                existing.Length,
                target.Id,
                ZetlSlipLinks.TitleFor(target));
            await SaveInlineStylesAsync(slip, text, changed, "Slip link updated.");
        }
        else
        {
            var (preparedText, preparedStart, preparedLength, preparedStyles) = PrepareInlineSelection(
                label,
                text,
                selectionStart,
                selectionEnd);
            var updated = KastnInlineStyleEditing.SetWikiLink(
                preparedText,
                preparedStyles,
                preparedStart,
                preparedLength,
                target.Id,
                ZetlSlipLinks.TitleFor(target));
            await SaveInlineStylesAsync(slip, preparedText, updated, "Slip link set.");
        }

        slipEditor.Focus();
    }

    private bool CanApplyLinkEdit(KastnEditorWorkflowContext context, out ZetlSlipSnapshot slip)
    {
        slip = null!;
        return !saving && context.IsSameDraft(currentProject?.Id, editorState)
            && CanEditInlineStyle(out slip);
    }

    private bool CanEditInlineStyle(out ZetlSlipSnapshot slip)
    {
        slip = null!;
        var selected = SelectedSlips();
        if (selected.Count != 1
            || selected[0].Type != ZetlSlipType.Text
            || ZetlBlockKinds.IsStructural(selected[0].BlockKind)
            || IsSlipInDeleted(selected[0])
            || !slipEditor.IsEnabled)
        {
            return false;
        }

        slip = selected[0];
        return true;
    }

    private bool CanReadInlineStyleState(KastnSelectionContext context, out ZetlSlipSnapshot slip)
    {
        slip = null!;
        var selected = context.Slips;
        if (selected.Count != 1
            || selected[0].Type != ZetlSlipType.Text
            || ZetlBlockKinds.IsStructural(selected[0].BlockKind)
            || context.IsDeleted(selected[0])
            || context.EditorSlipId is null)
        {
            return false;
        }

        slip = selected[0];
        return true;
    }

    private IReadOnlyList<ZetlInlineStyleRange> CurrentInlineStyles(ZetlSlipSnapshot slip) =>
        string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal)
            ? editorState.DraftInlineStyles
            : slip.InlineStyles;

    private (string Text, int Start, int Length, IReadOnlyList<ZetlInlineStyleRange> Styles)
        PrepareInlineStyleTarget(ZetlSlipSnapshot slip)
    {
        var text = slipEditor.Text ?? "";
        var (start, length) = KastnInlineStyleEditing.StyleTargetForSelection(
            text,
            slipEditor.SelectionStart,
            slipEditor.SelectionEnd);
        var styles = CurrentInlineStyles(slip);
        return (text, start, length, styles);
    }

    private (string Text, int Start, int Length, IReadOnlyList<ZetlInlineStyleRange> Styles)
        PrepareInlineSelection(
            string placeholder,
            string? capturedText = null,
            int? capturedStart = null,
            int? capturedEnd = null)
    {
        var text = capturedText ?? slipEditor.Text ?? "";
        var start = capturedStart is { } savedStart
            ? Math.Clamp(savedStart, 0, text.Length)
            : Math.Clamp(Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var end = capturedEnd is { } savedEnd
            ? Math.Clamp(savedEnd, start, text.Length)
            : Math.Clamp(Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), start, text.Length);
        var styles = CurrentInlineStyles(SelectedSlip!);
        if (end > start)
        {
            return (text, start, end - start, styles);
        }

        styles = KastnInlineStyleEditing.ShiftForReplacement(
            text,
            styles,
            start,
            replacedLength: 0,
            replacementLength: placeholder.Length);
        text = text[..start] + placeholder + text[start..];
        LoadEditorText(text);
        slipEditor.SelectionStart = start;
        slipEditor.SelectionEnd = start + placeholder.Length;
        editorState.SetDraft(text);
        return (text, start, placeholder.Length, styles);
    }

    private async Task SaveInlineStylesAsync(
        ZetlSlipSnapshot slip,
        string text,
        IReadOnlyList<ZetlInlineStyleRange> styles,
        string successText)
    {
        if (saving || slip.Id != editorState.SlipId || !CanEditInlineStyle(out var current) || current.Id != slip.Id)
        {
            return;
        }
        var commandText = text.Trim();
        var commandStyles = KastnInlineStyleEditing.ForTrimmedCommand(text, styles);
        editorState.SetDraft(text);
        editorState.SetInlineStyles(commandStyles);
        FlushDraftJournal();
        await UpdateSlipPropertyAsync(
            slip,
            _ => new UpdateSlipCommand
            {
                Text = commandText,
                InlineStyles = commandStyles
            },
            successText);
    }

    private static string InlineStyleLabel(string kind) => ZetlInlineStyleKinds.Normalize(kind) switch
    {
        ZetlInlineStyleKinds.Italic => "Italic",
        ZetlInlineStyleKinds.Strike => "Strikethrough",
        ZetlInlineStyleKinds.Code => "Inline code",
        _ => "Bold"
    };

    private async Task AddBucketAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        var name = await KastnDialogs.PromptAsync(this, "New Bucket", "Bucket name");
        if (name is null)
        {
            return;
        }

        var parentId = SelectedBucketId is not null && !KastnWorkbench.IsDeletedBucket(SelectedBucket)
            ? SelectedBucketId
            : null;
        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.AddBucket,
            new AddBucketCommand
            {
                Name = name,
                ParentBucketId = parentId
            },
            currentProject.Id));
        if (response.Status == ZetlResponseStatus.Success)
        {
            pendingBucketSelectionId = response.Payload?.Deserialize<ZetlBucketSnapshot>(
                ZetlProtocolJson.Options)?.Id;
            await connection.SynchronizeAsync();
            statusText.Text = $"Bucket '{name}' created.";
        }
        else
        {
            statusText.Text = response.Error?.Message ?? $"Bucket creation failed: {response.Status}.";
        }
    }

    private async Task AddSlipAsync(string? targetBucketId = null)
    {
        if (!IsOnline || currentProject is null || addingSlip)
        {
            return;
        }

        var existingDraft = currentProject.Slips.LastOrDefault(slip =>
            slip.Source == "kastn"
            && string.Equals(slip.Title, UntitledSlipTitle, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(slip.Text)
            && (targetBucketId is null || slip.BucketId == targetBucketId)
            && !KastnWorkbench.IsDeletedBucket(currentProject.Buckets.FirstOrDefault(
                bucket => bucket.Id == slip.BucketId)));
        if (existingDraft is not null)
        {
            // Reuse the existing untitled draft rather than stacking another:
            // select it in the tree and open it in the slip editor.
            ResetSlipFilters();
            pendingBucketSelectionId = existingDraft.BucketId;
            pendingSlipSelectionId = existingDraft.Id;
            pendingSlipFocus = true;
            await connection.SynchronizeAsync();
            SetDetailPaneMode(showDetails: false);
            statusText.Text = "Finish the current untitled slip before creating another.";

            if (boardModeActive)
            {
                await EditBoardSlipAsync(existingDraft);
            }
            return;
        }

        addingSlip = true;
        SetEditingEnabled();
        try
        {
            if (!await SaveEditorAsync())
            {
                statusText.Text = "Save or resolve the current slip before creating a new one.";
                return;
            }

            var destinationBucketId = targetBucketId
                ?? (SelectedBucketId is { } selectedBucketId && !KastnWorkbench.IsDeletedBucket(SelectedBucket)
                    ? selectedBucketId
                    : null);
            destinationBucketId ??= currentProject.ActiveBucketId
                ?? currentProject.Buckets.FirstOrDefault(
                    bucket => !KastnWorkbench.IsDeletedBucket(bucket))?.Id;
            if (destinationBucketId is null)
            {
                statusText.Text = "Create a bucket before adding a slip.";
                return;
            }

            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.AddSlip,
                new AddSlipCommand
                {
                    BucketId = destinationBucketId,
                    Title = UntitledSlipTitle,
                    Text = "",
                    Source = "kastn"
                },
                currentProject.Id));
            if (response.Status == ZetlResponseStatus.Success)
            {
                var created = response.Payload?.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options);
                if (created is not null)
                {
                    pendingBucketSelectionId = created.BucketId;
                    pendingSlipSelectionId = created.Id;
                    pendingSlipFocus = true;
                    ResetSlipFilters();
                    await connection.SynchronizeAsync();
                    SetDetailPaneMode(showDetails: false);
                    statusText.Text = "Slip created.";
                    if (boardModeActive)
                    {
                        await EditBoardSlipAsync(created);
                    }
                    return;
                }
            }

            HandleSimpleResponse(response, "Slip created.");
        }
        finally
        {
            addingSlip = false;
            SetEditingEnabled();
        }
    }

    // Insert a Group container — a bucket Kastn renders as a boxed group. Slips and whole
    // buckets are dragged into it through the existing tree drag-and-drop.
    private async Task InsertGroupBucketAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        // A group is a labelled document section. Ask for that authored label now
        // instead of persisting the implementation placeholder "Group", which every
        // renderer would then correctly (but noisily) expose in Copy/Export output.
        var name = await KastnDialogs.PromptAsync(this, "New Group", "Section label");
        if (name is null)
        {
            return;
        }

        var parentId = SelectedBucketId is not null && !KastnWorkbench.IsDeletedBucket(SelectedBucket)
            ? SelectedBucketId
            : null;
        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.AddBucket,
            new AddBucketCommand { Name = name, ParentBucketId = parentId, RenderKind = ZetlBucketRenderKinds.Group },
            currentProject.Id));
        if (response.Status == ZetlResponseStatus.Success)
        {
            pendingBucketSelectionId = response.Payload?.Deserialize<ZetlBucketSnapshot>(
                ZetlProtocolJson.Options)?.Id;
            await connection.SynchronizeAsync();
            statusText.Text = $"Group '{name}' added — drag slips or buckets into it.";
        }
        else
        {
            statusText.Text = response.Error?.Message ?? $"Group creation failed: {response.Status}.";
        }
    }

    // Insert a divider as its own structural note (rendered as a rule) after the selected
    // note, rather than editing a note to "become" a divider. A divider carries no text.
    private async Task InsertDividerSlipAsync()
    {
        if (!IsOnline || currentProject is null || addingSlip)
        {
            return;
        }

        var selected = SelectedSlips();
        var anchor = selected.Count == 1 && !IsSlipInDeleted(selected[0]) ? selected[0] : null;
        var bucketId = anchor?.BucketId
            ?? (SelectedBucketId is { } selectedBucketId
                && !KastnWorkbench.IsDeletedBucket(SelectedBucket) ? selectedBucketId : null)
            ?? currentProject.ActiveBucketId
            ?? currentProject.Buckets.FirstOrDefault(
                bucket => !KastnWorkbench.IsDeletedBucket(bucket))?.Id;
        if (bucketId is null)
        {
            statusText.Text = "Create a bucket before adding a divider.";
            return;
        }

        addingSlip = true;
        SetEditingEnabled();
        try
        {
            if (!await SaveEditorAsync())
            {
                statusText.Text = "Save or resolve the current slip before adding a divider.";
                return;
            }

            // The add and its follow-up reorder are one undo step.
            using var undoGesture = BeginGesture("Insert divider");
            await using var refreshBatch = connection.DeferRefresh();

            // The note that follows the anchor in document order — the reorder target so
            // the new divider lands just after the anchor (null = keep it last).
            var nextSlipId = anchor is null ? null : NextSlipInBucket(bucketId, anchor.Id);

            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.AddSlip,
                new AddSlipCommand
                {
                    BucketId = bucketId,
                    Text = "",
                    Source = "kastn",
                    BlockKind = ZetlBlockKinds.Divider
                },
                currentProject.Id));
            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Divider failed: {response.Status}.";
                return;
            }

            var created = response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);
            if (created is not null && anchor is not null)
            {
                // The divider lands at the end of the bucket; move it before the note that
                // followed the anchor (no-op when the anchor was already last).
                await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.ReorderSlip,
                    new ReorderSlipCommand { BeforeSlipId = nextSlipId },
                    currentProject.Id,
                    created.Id,
                    created.Revision));
            }

            if (created is not null)
            {
                pendingBucketSelectionId = created.BucketId;
                pendingSlipSelectionId = created.Id;
            }

            await connection.SynchronizeAsync();
            statusText.Text = "Divider added.";
        }
        finally
        {
            addingSlip = false;
            SetEditingEnabled();
        }
    }

    // The id of the note immediately after anchorId within the bucket (document order),
    // or null when the anchor is last — used as a reorder anchor for divider insertion.
    private string? NextSlipInBucket(string bucketId, string anchorId)
    {
        if (currentProject is null)
        {
            return null;
        }

        var bucketSlips = currentProject.Slips.Where(slip => slip.BucketId == bucketId).ToList();
        var index = bucketSlips.FindIndex(slip => slip.Id == anchorId);
        return index >= 0 && index + 1 < bucketSlips.Count ? bucketSlips[index + 1].Id : null;
    }

    private async Task SetBucketRenderKindAsync(ZetlBucketSnapshot bucket, string kind)
    {
        if (!IsOnline || saving || currentProject is null || editorState.ConflictCurrent is not null
            || KastnWorkbench.IsDeletedBucket(bucket))
        {
            return;
        }

        var normalized = ZetlBucketRenderKinds.Normalize(kind);
        if (!IsBucketListRenderKind(normalized))
        {
            return;
        }

        var current = ZetlViewRenderer.BucketRenderKind(bucket);
        var target = current == normalized ? ZetlBucketRenderKinds.None : normalized;
        saving = true;
        SetEditingEnabled();
        try
        {
            pendingBucketSelectionId = bucket.Id;
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.UpdateBucket,
                new UpdateBucketCommand
                {
                    Name = bucket.Name,
                    ParentBucketId = bucket.ParentBucketId,
                    Settings = bucket.Settings,
                    RenderKind = target
                },
                currentProject.Id,
                bucket.Id,
                bucket.Revision));
            if (response.Status == ZetlResponseStatus.Success)
            {
                await connection.SynchronizeAsync();
            }

            HandleSimpleResponse(response, BucketRenderKindStatus(target));
        }
        finally
        {
            saving = false;
            SetEditingEnabled();
        }
    }

    private static string BucketRenderKindStatus(string kind) => kind switch
    {
        ZetlBucketRenderKinds.Task => "Bucket now creates checklist-style notes.",
        ZetlBucketRenderKinds.Ordered => "Bucket now creates numbered notes.",
        ZetlBucketRenderKinds.Bullet => "Bucket now creates bulleted notes.",
        _ => "Bucket note style cleared."
    };

    private async Task SaveBucketAsync()
    {
        if (!IsOnline || currentProject is null || SelectedBucket is not { } bucket)
        {
            return;
        }

        var name = bucketNameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            statusText.Text = "A bucket name is required.";
            return;
        }

        var parentId = (parentBucketBox.SelectedItem as KastnBucketItem)?.Id;
        var renderKind = (bucketRenderKindBox.SelectedItem as KastnRenderKindItem)?.Value ?? "";
        pendingBucketSelectionId = bucket.Id;
        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand
            {
                Name = name,
                ParentBucketId = parentId,
                Settings = bucket.Settings,
                RenderKind = renderKind
            },
            currentProject.Id,
            bucket.Id,
            bucket.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.SynchronizeAsync();
        }

        HandleSimpleResponse(response, "Bucket saved.");
    }

    private async Task DeleteBucketAsync()
    {
        if (!IsOnline || currentProject is null || SelectedBucket is not { } bucket)
        {
            return;
        }

        if (!await KastnDialogs.ConfirmAsync(
                this,
                $"Delete '{bucket.Name}' and all of its child buckets and slips?",
                "Delete Bucket"))
        {
            return;
        }

        pendingBucketSelectionId = bucket.ParentBucketId;
        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.DeleteBucket,
            new DeleteBucketCommand(),
            currentProject.Id,
            bucket.Id,
            bucket.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            editorState.Select(null);
            UpdateEditorFromState();
            await connection.SynchronizeAsync();
        }

        HandleSimpleResponse(response, "Bucket deleted.");
    }

    private async Task DeleteProjectAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        await DeleteProjectAsync(new ProjectListItem(
            currentProject.Id,
            currentProject.Name,
            currentProject.MetadataRevision,
            "",
            "",
            "",
            null,
            currentProject.Status,
            currentProject.Slips.Count,
            "",
            "",
            false,
            string.Equals(
                currentProject.Kind,
                ZetlStateRules.TemporaryConsumableProjectKind,
                StringComparison.Ordinal)));
    }

    private async Task RenameProjectAsync(ProjectListItem project)
    {
        if (!IsOnline)
        {
            return;
        }

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before renaming the project.";
            return;
        }

        var name = await KastnDialogs.PromptAsync(
            this,
            "Rename Project",
            "Project name",
            project.Name);
        if (name is null || string.Equals(name, project.Name, StringComparison.Ordinal))
        {
            return;
        }

        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.RenameProject,
            new RenameProjectCommand { Name = name },
            project.Id,
            project.Id,
            project.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.SynchronizeAsync();
        }

        HandleSimpleResponse(response, $"Project renamed to '{name}'.");
    }

    private async Task ToggleJournalModeAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        var turningOn = !currentProject.JournalMode;
        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.SetJournalMode,
            new SetJournalModeCommand { JournalMode = turningOn },
            currentProject.Id,
            currentProject.Id,
            currentProject.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.SynchronizeAsync();
        }

        HandleSimpleResponse(
            response,
            turningOn
                ? "Journal mode on — capture rolls into a dated bucket each day."
                : "Journal mode off.");
    }

    private async Task SetProjectStatusAsync(ProjectListItem project, string status)
    {
        if (!IsOnline)
        {
            return;
        }

        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.SetProjectStatus,
            new SetProjectStatusCommand { Status = status },
            project.Id,
            project.Id,
            project.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.SynchronizeAsync();
        }

        var verb = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase)
            ? "reactivated"
            : status.ToLowerInvariant();
        HandleSimpleResponse(response, $"Project {verb}.");
    }

    private async Task SetActiveProjectAsync(ProjectListItem project, bool shifted)
    {
        if (!IsOnline)
        {
            return;
        }

        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.SetActiveProject,
            new SetActiveProjectCommand { ActivateShifted = shifted },
            project.Id));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.SynchronizeAsync();
        }

        HandleSimpleResponse(
            response,
            shifted ? "Project set as Shift." : "Project set as Main.");
    }

    private async Task CreateTemporaryProjectFromReplayAsync(ProjectListItem project)
    {
        if (!IsOnline)
        {
            return;
        }

        if (!project.CanUseTemporarily)
        {
            statusText.Text = "Only archived projects with replay material can be used temporarily.";
            return;
        }

        var laneChoice = await KastnDialogs.PickTemporaryTemplateLaneAsync(
            this,
            project.Name,
            LaneLabel(ZetlStateRules.NormalLane),
            LaneLabel(ZetlStateRules.ShiftLane),
            title: "Use Temporarily",
            prompt: $"Create a temporary project from '{project.Name}' in which lane?",
            confirmText: "Create Temporary",
            allowRemember: false);
        if (laneChoice is null)
        {
            return;
        }

        var lane = ZetlStateRules.CanonicalTemporaryLane(laneChoice.Lane)
            ?? ZetlStateRules.NormalLane;
        var name = await KastnDialogs.PromptAsync(
            this,
            "Temporary Project",
            "Project name",
            $"{project.Name} Temporary",
            candidate => lastProjectSummaries.Any(summary =>
                string.Equals(summary.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    ? "A project with that name already exists."
                    : null);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.CreateTemporaryProjectFromReplay,
            new CreateTemporaryProjectFromReplayCommand
            {
                Name = name.Trim(),
                TemporaryLane = lane,
                ActivateShifted = string.Equals(lane, ZetlStateRules.ShiftLane, StringComparison.Ordinal)
            },
            project.Id));
        if (response.Status == ZetlResponseStatus.Success)
        {
            var created = response.Payload?.Deserialize<ZetlProjectSnapshot>(
                ZetlProtocolJson.Options);
            await connection.SynchronizeAsync();
            if (created is not null)
            {
                await connection.NavigateToProjectAsync(created.Id);
                statusText.Text = $"Created temporary project '{created.Name}'.";
                return;
            }
        }

        HandleSimpleResponse(response, "Created temporary project.");
    }

    private async Task DeleteProjectAsync(ProjectListItem project)
    {
        if (!IsOnline)
        {
            return;
        }

        if (!await SaveEditorAsync())
        {
            statusText.Text = "Save or resolve the current slip before deleting the project.";
            return;
        }

        if (!await KastnDialogs.ConfirmAsync(
                this,
                $"Delete project '{project.Name}' and all of its buckets and slips? This cannot be undone.",
                "Delete Project"))
        {
            return;
        }

        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.DeleteProject,
            new DeleteProjectCommand(),
            project.Id,
            project.Id,
            project.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            if (string.Equals(currentProject?.Id, project.Id, StringComparison.Ordinal))
            {
                editorState.Select(null);
                UpdateEditorFromState();
                await connection.NavigateToProjectAsync(null);
            }

            await connection.SynchronizeAsync();
        }

        HandleSimpleResponse(response, "Project deleted.");
    }

    private async Task MoveSlipAsync()
    {
        if (!await SaveEditorAsync()
            || !IsOnline
            || currentProject is null
            || moveBucketBox.SelectedItem is not KastnBucketItem destination
            || destination.Id is null)
        {
            return;
        }

        var selected = SelectedSlips()
            .Where(slip => !IsSlipInDeleted(slip))
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var moved = 0;
        var skipped = 0;
        var failed = 0;
        var projectId = currentProject.Id;
        // A single moved slip stays selected (re-driven through the tree so the
        // editor/inspector/View re-sync), matching drag-drop. Batch moves clear.
        var reselectSlipId = selected.Count == 1 ? selected[0].Id : null;
        pendingBucketSelectionId = destination.Id;
        using var undoGesture = BeginGesture(selected.Count == 1 ? "Move slip" : "Move slips");
        await using var refreshBatch = connection.DeferRefresh();
        foreach (var slip in selected)
        {
            if (slip.BucketId == destination.Id)
            {
                skipped++;
                continue;
            }

            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.MoveSlip,
                new MoveSlipCommand { DestinationBucketId = destination.Id },
                projectId,
                slip.Id,
                slip.Revision));
            if (response.Status == ZetlResponseStatus.Success)
            {
                moved++;
            }
            else
            {
                failed++;
            }
        }

        await connection.SynchronizeAsync();
        if (reselectSlipId is not null)
        {
            ReselectSlipNode(reselectSlipId);
        }
        else
        {
            editorState.Select(null);
            UpdateEditorFromState();
        }

        statusText.Text = BatchStatus(
            moved > 0 ? $"{moved} slip{Plural(moved)} moved to {destination.Bucket?.Name}" : null,
            skipped > 0 ? $"{skipped} already there" : null,
            failed > 0 ? $"{failed} failed" : null);
    }

    private async Task RestoreSlipAsync()
    {
        if (!await SaveEditorAsync()
            || !IsOnline
            || currentProject is null
            || SelectedSlip is not { } slip
            || !IsSlipInDeleted(slip))
        {
            return;
        }

        var destination = moveBucketBox.SelectedItem as KastnBucketItem
            ?? moveBuckets.FirstOrDefault();
        if (destination?.Id is null)
        {
            statusText.Text = "Create a regular bucket before restoring this slip.";
            return;
        }

        pendingBucketSelectionId = destination.Id;
        pendingSlipSelectionId = slip.Id;
        pendingSlipFocus = false;
        var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.MoveSlip,
            new MoveSlipCommand { DestinationBucketId = destination.Id },
            currentProject.Id,
            slip.Id,
            editorState.Revision));
        HandleSimpleResponse(response, $"Slip restored to {destination.Bucket?.Name}.");
    }

    private async Task DeleteSlipAsync()
    {
        if (!await SaveEditorAsync()
            || !IsOnline
            || currentProject is null
            || SelectedSlips().Count == 0)
        {
            return;
        }

        var selected = SelectedSlips()
            .Where(slip => !IsSlipInDeleted(slip))
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (!await KastnDialogs.ConfirmAsync(
                this,
                selected.Count == 1
                    ? "Move the selected slip to Deleted?"
                    : $"Move {selected.Count} selected slips to Deleted?",
                "Delete Slip"))
        {
            return;
        }

        var moved = 0;
        var failed = 0;
        var projectId = currentProject.Id;
        using var undoGesture = BeginGesture(selected.Count == 1 ? "Delete slip" : "Delete slips");
        await using var refreshBatch = connection.DeferRefresh();
        foreach (var slip in selected)
        {
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.DeleteSlip,
                new DeleteSlipCommand(),
                projectId,
                slip.Id,
                slip.Revision));
            if (response.Status == ZetlResponseStatus.Success)
            {
                moved++;
                var deleted = response.Payload?.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options);
                pendingBucketSelectionId ??= deleted?.BucketId;
            }
            else
            {
                failed++;
            }
        }

        editorState.Select(null);
        ResetSlipFilters();
        UpdateEditorFromState();
        await connection.SynchronizeAsync();
        statusText.Text = failed == 0
            ? $"{moved} slip{Plural(moved)} moved to Deleted."
            : $"{moved} slip{Plural(moved)} moved to Deleted; {failed} failed.";
    }

    private void UseZetlVersion()
    {
        var projectId = currentProject?.Id;
        var slipId = editorState.SlipId;
        editorState.UseCurrent();
        recoveredDraftActive = false;
        ClearDraftJournal(projectId, slipId);
        UpdateEditorFromState();
        statusText.Text = "Using the current Zetl version.";
    }

    private async Task KeepMineAsync()
    {
        editorState.PrepareOverwrite();
        UpdateEditorFromState();
        editorState.SetDraft(localConflictText.Text ?? "");
        LoadEditorText(editorState.DraftText);
        FlushDraftJournal();
        await SaveEditorAsync();
    }

    private void ShowConflict()
    {
        conflictPanel.IsVisible = editorState.ConflictCurrent is not null;
        localConflictText.Text = editorState.DraftText;
        remoteConflictText.Text = editorState.ConflictCurrent?.Text ?? "";
        statusText.Text = "Resolve the slip conflict before continuing.";
        SetEditingEnabled();
    }

    private void FocusMainView()
    {
        viewerDocumentScroll.Focus();
    }

    // Load text into the editor as fresh context rather than as an edit: the guard
    // keeps the load out of dirty-tracking, and the box's own undo is permanently
    // disabled, so a load can never be "undone" into resurrecting another slip's
    // text under the current selection.
    private void LoadEditorText(string text)
    {
        editorUpdating = true;
        slipEditor.Text = text;
        editorUpdating = false;
    }

    private void UpdateEditorFromState()
    {
        var selectedSlips = SelectedSlips();
        UpdateSlipTypographyControls(selectedSlips);
        if (selectedSlips.Count > 1)
        {
            LoadEditorText("");
            slipRenderOptionUpdating = true;
            ignoreBucketRenderKindCheck.IsChecked = false;
            ignoreBucketRenderKindCheck.IsEnabled = false;
            slipRenderOptionUpdating = false;
            representationToggleButton.IsVisible = false;
            attachPictureButton.IsVisible = false;
            removePictureButton.IsVisible = false;
            conflictPanel.IsVisible = false;
            slipMetadataText.Text = $"{selectedSlips.Count} slips selected. "
                + "Choose a destination, then move or delete them together.";
            SetEditingEnabled();
            return;
        }

        LoadEditorText(SelectedSlip is { } editorSlip
            && IsUntitledKastnSlip(editorSlip)
            && !editorState.IsDirty
                ? ""
                : editorState.DraftText);
        conflictPanel.IsVisible = editorState.ConflictCurrent is not null;
        if (editorState.ConflictCurrent is { } conflict)
        {
            localConflictText.Text = editorState.DraftText;
            remoteConflictText.Text = conflict.Text;
        }

        var slip = SelectedSlip
            ?? currentProject?.Slips.FirstOrDefault(item => item.Id == editorState.SlipId);
        slipMetadataText.Text = slip is null
            ? "Select a slip to read or edit it."
            : ZetlBlockKinds.IsStructural(slip.BlockKind)
                ? "Structural element — a divider Kastn renders and Zetl ignores. It has no text to edit; use Delete to remove it."
                : SlipMetadata(slip);
        slipRenderOptionUpdating = true;
        ignoreBucketRenderKindCheck.IsChecked = slip?.IgnoreBucketRenderKind == true;
        slipRenderOptionUpdating = false;
        representationToggleButton.IsVisible = slip is not null
            && IsDualRepresentationSlip(slip)
            && !IsSlipInDeleted(slip);
        representationToggleButton.Content = slip?.Type == ZetlSlipType.Picture
            ? "Show as text"
            : "Show as picture";
        var canHavePicture = slip is not null
            && !IsSlipInDeleted(slip)
            && !ZetlBlockKinds.IsStructural(slip.BlockKind);
        attachPictureButton.IsVisible = canHavePicture;
        attachPictureButton.Content = slip?.Picture is null ? "Add picture…" : "Replace picture…";
        // Removing must leave the slip standing on text or a title.
        removePictureButton.IsVisible = canHavePicture
            && slip?.Picture is not null
            && (!string.IsNullOrWhiteSpace(slip.Text) || !string.IsNullOrWhiteSpace(slip.Title));
        SetEditingEnabled();
    }

    private void SetEditingEnabled()
    {
        var context = CaptureSelectionContext();
        var enabled = KastnCommandAvailability.Compute(
            context, online: IsOnline, busy: savingVisual,
            hasConflict: editorState.ConflictCurrent is not null, addingSlip: addingSlip);
        slipEditor.IsEnabled = enabled.EditorEnabled;
        // Bold/italic/strike are whole-slip properties, so they batch like the
        // list buttons; bold on a bucket title toggles the heading's bold.
        boldButton.IsEnabled = enabled.FormatSelectionEnabled || enabled.FormatBucketEnabled;
        fontFamilyBox.IsEnabled = enabled.FormatSelectionEnabled;
        fontSizeBox.IsEnabled = enabled.FormatSelectionEnabled;
        textColorBox.IsEnabled = enabled.FormatSelectionEnabled;
        italicButton.IsEnabled = enabled.FormatSelectionEnabled;
        strikeButton.IsEnabled = enabled.FormatSelectionEnabled;
        codeButton.IsEnabled = enabled.EditorEnabled;
        linkButton.IsEnabled = enabled.EditorEnabled;
        wikiLinkButton.IsEnabled = enabled.EditorEnabled;
        headingButton.IsEnabled = enabled.FormatSelectionEnabled;
        quoteButton.IsEnabled = enabled.FormatSelectionEnabled;
        codeBlockButton.IsEnabled = enabled.FormatSelectionEnabled;
        // The divider insert lives in the tree-side insert bar and creates a new
        // structural note, so it follows the new-slip rule rather than needing a note
        // in the editor.
        insertDividerButton.IsEnabled = enabled.CreateSlipEnabled && !showingDeleted;
        // A group is a bucket, so it follows the add-bucket rule.
        insertGroupButton.IsEnabled = IsOnline && currentProject is not null && !showingDeleted;
        bulletListButton.IsEnabled = enabled.FormatSelectionEnabled || enabled.FormatBucketEnabled;
        numberListButton.IsEnabled = enabled.FormatSelectionEnabled || enabled.FormatBucketEnabled;
        taskListButton.IsEnabled = enabled.FormatSelectionEnabled || enabled.FormatBucketEnabled;
        ignoreBucketRenderKindCheck.IsEnabled = enabled.SlipPropertiesEnabled;
        representationToggleButton.IsEnabled = ignoreBucketRenderKindCheck.IsEnabled;
        attachPictureButton.IsEnabled = ignoreBucketRenderKindCheck.IsEnabled;
        removePictureButton.IsEnabled = ignoreBucketRenderKindCheck.IsEnabled;
        saveSlipButton.IsEnabled = enabled.EditorEnabled;
        saveSlipMenuItem.IsEnabled = false;
        deleteSlipButton.IsEnabled = enabled.MoveOrDeleteEnabled;
        deleteSlipMenuItem.IsEnabled = false;
        restoreSlipButton.IsEnabled = enabled.RestoreSlipEnabled && moveBuckets.Count > 0;
        moveSlipButton.IsEnabled = enabled.MoveOrDeleteEnabled && moveBuckets.Count > 0;
        moveBucketBox.IsEnabled = moveSlipButton.IsEnabled || restoreSlipButton.IsEnabled;
        // The Deleted view is for browsing/restoring; bucket and slip creation are
        // hidden there.
        addBucketButton.IsEnabled = IsOnline && currentProject is not null && !showingDeleted;
        closeProjectButton.IsEnabled = currentProject is not null;
        saveAsTemplateMenuItem.IsEnabled = currentProject is not null;
        newSlipButton.IsEnabled = enabled.CreateSlipEnabled && !showingDeleted;
        newSlipMenuItem.IsEnabled = newSlipButton.IsEnabled;
        UpdateAlignButtons(context, enabled.AlignEnabled);
        UpdateListButtons(context);
        UpdateInlineFormatButtons(context);
    }

    // A clicked destination is provisional until autosave settles. Snapshots may
    // acknowledge the old draft, but must not bind the editor from the new bucket.
    private sealed record TreeSelectionSaveRequest(string? ProjectId, long Generation, long EditorVersion);
    private TreeSelectionSaveRequest? pendingTreeSelectionSave;

    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshing)
        {
            return;
        }

        var node = SelectedTreeNode;
        if (node is null)
        {
            return;
        }

        var slipIds = SelectedTreeSlipIds();
        var singleSlipId = slipIds.Count == 1 ? slipIds[0] : null;
        pendingTreeSelectionSave = null; // A later user selection supersedes an earlier wait.

        // Save the current edit before switching away from the edited slip. On a
        // failed save (conflict or offline) revert the selection so it stays put.
        if (editorState.SlipId is { } editingId
            && !string.Equals(editingId, singleSlipId, StringComparison.Ordinal)
            && editorState.IsDirty)
        {
            var request = new TreeSelectionSaveRequest(currentProject?.Id, editHistory.Generation, editorState.SelectionVersion);
            pendingTreeSelectionSave = request;
            var requestedIds = projectTree.SelectedItems?.OfType<KastnTreeNode>().Select(item => item.Id).ToArray()
                ?? [node.Id];
            try
            {
                var saved = await SaveEditorAsync();
                if (!ReferenceEquals(pendingTreeSelectionSave, request) || currentProject?.Id != request.ProjectId
                    || request.Generation != editHistory.Generation || request.EditorVersion != editorState.SelectionVersion)
                    return;
                pendingTreeSelectionSave = null;
                var wasRefreshing = refreshing;
                refreshing = true;
                try
                {
                    ApplyTreeNodeSelection(saved ? requestedIds : [editingId]);
                }
                finally { refreshing = wasRefreshing; }
                if (saved) UpdateTreeSelectionUi();
                else
                {
                    RefreshBucketEditor();
                    RefreshViewer();
                    RefreshDestinationBuckets();
                }
            }
            finally
            {
                if (ReferenceEquals(pendingTreeSelectionSave, request)) pendingTreeSelectionSave = null;
            }
            return;
        }

        UpdateTreeSelectionUi();
    }

    // The selection -> editor/batch logic, shared by fresh selections and refreshes.
    // Branches on the one explicit selection value.
    private void UpdateTreeSelectionUi()
    {
        var node = SelectedTreeNode;
        if (node is null)
        {
            return;
        }

        if (CurrentSelection() is KastnSelection.Slips { SlipIds: [var onlySlipId] })
        {
            // Exactly one slip: bind the editor to it.
            pendingSlipSelectionId = onlySlipId;
            RefreshBucketEditor();
            RefreshSlipView(force: true);
            InspectSlip(onlySlipId);
            SetDetailPaneMode(showDetails: false);
        }
        else
        {
            // A batch of slips, a bucket title, or nothing: the batch count comes from
            // SelectedSlips; clear the single-slip editor either way.
            pendingSlipSelectionId = null;
            RefreshBucketEditor();
            RefreshSlipView(force: true);
            RenderSlipInspector(null);
            editorState.Select(null);
            UpdateEditorFromState();
            SetDetailPaneMode(showDetails: false);
        }
    }

    // The center View is persistent; this toggle changes only the right Detail pane.
    private void SetDetailPaneMode(bool showDetails)
    {
        detailShowingMetadata = showDetails;
        editorPanel.IsVisible = !showDetails;
        inspectorPanel.IsVisible = showDetails;
        var hasProject = currentProject is not null;
        detailEditorButton.IsEnabled = hasProject && showDetails;
        detailDetailsButton.IsEnabled = hasProject && !showDetails;
        SetEditingEnabled();
    }

    private IReadOnlyList<ZetlSlipSnapshot> CurrentFilteredSlips()
    {
        return currentProject is null
            ? []
            : KastnWorkbench.FilterSlips(
                ProjectIndex,
                SelectedBucketId,
                (sourceFilterBox.SelectedItem as FilterItem)?.Value,
                (sessionFilterBox.SelectedItem as FilterItem)?.Value,
                (dateFilterBox.SelectedItem as DateFilterItem)?.Value ?? KastnDateFilter.All,
                searchBox.Text,
                DateTimeOffset.Now,
                (typeFilterBox.SelectedItem as TypeFilterItem)?.Value);
    }

    // The center View is the constant whole-project document: it never scopes to the
    // tree's selected bucket (selecting a bucket only moves the highlight). Source /
    // session / date / search still narrow the rendered set, and the protected Deleted
    // bucket is kept out of the document. Excluded-from-views slips are dropped later,
    // by the renderer's BuildGroups, so they still resolve in the slip inspector.
    private IReadOnlyList<ZetlSlipSnapshot> CurrentViewSlips()
    {
        if (currentProject is null)
        {
            return [];
        }

        return KastnWorkbench.FilterSlips(
                ProjectIndex,
                null,
                (sourceFilterBox.SelectedItem as FilterItem)?.Value,
                (sessionFilterBox.SelectedItem as FilterItem)?.Value,
                (dateFilterBox.SelectedItem as DateFilterItem)?.Value ?? KastnDateFilter.All,
                searchBox.Text,
                DateTimeOffset.Now,
                (typeFilterBox.SelectedItem as TypeFilterItem)?.Value)
            .Where(slip => !IsSlipInDeleted(slip))
            .ToList();
    }

    private bool IsSlipInDeleted(ZetlSlipSnapshot? slip)
    {
        if (currentProject is null || slip is null)
        {
            return false;
        }

        return KastnWorkbench.IsDeletedBucket(ProjectIndex.Bucket(slip.BucketId));
    }

    private string SlipMetadata(ZetlSlipSnapshot slip)
    {
        var metadata = $"{slip.Source} | {ShortSession(slip.SessionId)} | "
            + $"{slip.CapturedAtUtc.LocalDateTime:F} | revision {editorState.Revision}";
        if (slip.CaptureOrigin is { } captureOrigin)
        {
            var application = string.IsNullOrWhiteSpace(captureOrigin.ApplicationName)
                ? captureOrigin.ProcessName
                : captureOrigin.ApplicationName;
            var location = string.IsNullOrWhiteSpace(captureOrigin.WindowTitle)
                ? application
                : $"{application} — {captureOrigin.WindowTitle}";
            if (!string.IsNullOrWhiteSpace(location))
            {
                metadata += $" | Captured in {location}";
            }
        }

        if (!IsSlipInDeleted(slip))
        {
            return metadata;
        }

        var origin = currentProject?.Buckets.FirstOrDefault(
            bucket => bucket.Id == slip.DeletedFromBucketId)?.Name ?? "Unknown bucket";
        var deletedAt = slip.DeletedAtUtc is null
            ? "unknown time"
            : slip.DeletedAtUtc.Value.LocalDateTime.ToString("g");
        return $"{metadata} | Deleted from {origin} at {deletedAt}";
    }

    private void RefreshParentBucketHint(ZetlBucketSnapshot? selected)
    {
        if (selected is null || currentProject is null)
        {
            parentBucketHintText.Text = "Choose a bucket to edit its placement.";
            return;
        }

        if (KastnWorkbench.IsDeletedBucket(selected))
        {
            parentBucketHintText.Text = "Deleted is protected and always top level.";
            return;
        }

        if (selected.ParentBucketId is null)
        {
            parentBucketHintText.Text = "Current parent: top level";
            return;
        }

        var parent = currentProject.Buckets.FirstOrDefault(
            bucket => bucket.Id == selected.ParentBucketId);
        parentBucketHintText.Text = parent is null
            ? "Current parent: missing"
            : $"Current parent: {ProjectIndex.BucketPathLabel(parent)}";
    }

    private static string BatchStatus(params string?[] parts)
    {
        var message = string.Join("; ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return message.Length == 0 ? "No slips changed." : $"{message}.";
    }

    private static string Plural(int count)
    {
        return count == 1 ? "" : "s";
    }

    private static string ShortSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session))
        {
            return "No session";
        }

        return session.Length <= 12 ? session : session[..12];
    }

    private static bool IsUntitledKastnSlip(ZetlSlipSnapshot slip)
    {
        var title = new ZETL.ZetlAppSettingsStore().Settings.UntitledSlipTitle;
        return string.Equals(slip.Source, "kastn", StringComparison.Ordinal)
            && string.Equals(slip.Title.Trim(), title, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(slip.Text);
    }
}
