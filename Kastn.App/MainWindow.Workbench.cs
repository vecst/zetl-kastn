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
{    private async void OnSlipSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (refreshing)
        {
            return;
        }

        var oldId = editorState.SlipId;
        var selectedItems = SelectedSlipItems();
        if (selectedItems.Count != 1)
        {
            if (!await SaveEditorAsync())
            {
                refreshing = true;
                slipList.SelectedItem = slips.FirstOrDefault(item => item.Id == oldId);
                refreshing = false;
                return;
            }

            editorState.Select(null);
            UpdateEditorFromState();
            RefreshDestinationBuckets();
            return;
        }

        var selected = selectedItems[0];
        if (oldId != selected.Id && !await SaveEditorAsync())
        {
            refreshing = true;
            slipList.SelectedItem = slips.FirstOrDefault(item => item.Id == oldId);
            refreshing = false;
            return;
        }

        editorState.Select(selected.Slip);
        UpdateEditorFromState();
        RefreshDestinationBuckets();
    }

    private void OnEditorTextChanged()
    {
        if (editorUpdating || editorState.SlipId is null)
        {
            return;
        }

        editorState.SetDraft(slipEditor.Text ?? "");
        statusText.Text = editorState.IsDirty
            ? "Unsaved changes — saved when you leave the editor."
            : connection.Current.Status;
    }

    private async void OnTreeVisibilityClick(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (visibilityUpdating
            || !IsOnline
            || currentProject is null
            || (sender as Control)?.DataContext is not KastnTreeNode node)
        {
            return;
        }

        var targetIds = TreeSlips(node)
            .Select(slip => slip.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targetIds.Count == 0)
        {
            return;
        }

        visibilityUpdating = true;
        try
        {
            if (!await SaveEditorAsync())
            {
                statusText.Text = "Save or resolve the current slip before changing visibility.";
                return;
            }

            var project = currentProject;
            if (project is null)
            {
                return;
            }

            var targets = targetIds
                .Select(id => project.Slips.FirstOrDefault(slip => slip.Id == id))
                .Where(slip => slip is not null)
                .Cast<ZetlSlipSnapshot>()
                .ToList();
            var exclude = targets.Any(slip => !slip.ExcludedFromViews);
            var changed = 0;
            var failed = 0;
            saving = true;
            SetEditingEnabled();
            foreach (var slip in targets.Where(slip => slip.ExcludedFromViews != exclude))
            {
                var revision = string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal)
                    ? editorState.Revision
                    : slip.Revision;
                var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.UpdateSlip,
                    new UpdateSlipCommand
                    {
                        Text = string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal)
                            ? editorState.DraftText.Trim()
                            : slip.Text,
                        ExcludedFromViews = exclude
                    },
                    project.Id,
                    slip.Id,
                    revision));
                if (response.Status == ZetlResponseStatus.Success)
                {
                    changed++;
                    if (response.Payload?.Deserialize<ZetlSlipSnapshot>(
                            ZetlProtocolJson.Options) is { } saved
                        && string.Equals(saved.Id, editorState.SlipId, StringComparison.Ordinal))
                    {
                        editorState.AcceptSaved(saved);
                    }
                }
                else
                {
                    failed++;
                }
            }

            await connection.RefreshAsync();
            var action = exclude ? "hidden" : "shown";
            statusText.Text = failed == 0
                ? $"{changed} slip{Plural(changed)} {action}."
                : $"{changed} slip{Plural(changed)} {action}; {failed} failed.";
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = ex.Message;
        }
        finally
        {
            saving = false;
            visibilityUpdating = false;
            SetEditingEnabled();
        }
    }

    private static IEnumerable<ZetlSlipSnapshot> TreeSlips(KastnTreeNode node)
    {
        if (node.Slip is { } slip)
        {
            yield return slip;
        }

        foreach (var child in node.Children)
        {
            foreach (var descendant in TreeSlips(child))
            {
                yield return descendant;
            }
        }
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
        if (!editorState.IsDirty)
        {
            return editorState.ConflictCurrent is null;
        }

        if (!IsOnline || saving || editorState.ConflictCurrent is not null
            || currentProject is null || editorState.SlipId is null)
        {
            return false;
        }

        var text = ZetlSlipLinks.RefreshCachedTitles(editorState.DraftText, currentProject).Trim();
        if (text.Length == 0
            && string.IsNullOrWhiteSpace(SelectedSlip?.Title)
            && SelectedSlip?.Type != ZetlSlipType.Picture)
        {
            statusText.Text = "A slip needs a title or note.";
            return false;
        }

        saving = true;
        pendingSaveText = text;
        SetEditingEnabled();
        try
        {
            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.UpdateSlip,
                new UpdateSlipCommand { Text = text },
                currentProject.Id,
                editorState.SlipId,
                editorState.Revision));
            if (response.Status == ZetlResponseStatus.Conflict)
            {
                var current = response.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options);
                if (current is not null)
                {
                    editorState.Reconcile(current);
                    ShowConflict();
                }

                return false;
            }

            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Save failed: {response.Status}.";
                return false;
            }

            var saved = response.Payload?.Deserialize<ZetlSlipSnapshot>(
                ZetlProtocolJson.Options);
            if (saved is not null)
            {
                editorState.AcceptSaved(saved);
                UpdateEditorFromState();
            }

            statusText.Text = "Slip saved.";
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = $"Slip was not saved. {ex.Message}";
            return false;
        }
        finally
        {
            pendingSaveText = null;
            saving = false;
            SetEditingEnabled();
        }
    }

    // Set the selected text slip's block alignment (left/center/right). Carries the
    // current editor draft along like the eye-toggle does, so it also commits any
    // pending text edit; the renderer and on-screen View honor Align.
    private async Task SetSlipAlignAsync(string align)
    {
        if (!IsOnline || saving || currentProject is null
            || editorState.ConflictCurrent is not null)
        {
            return;
        }

        var selected = SelectedSlips();
        if (selected.Count != 1
            || selected[0].Type != ZetlSlipType.Text
            || IsSlipInDeleted(selected[0]))
        {
            return;
        }

        var slip = selected[0];
        var isEditing = string.Equals(slip.Id, editorState.SlipId, StringComparison.Ordinal);
        var revision = isEditing ? editorState.Revision : slip.Revision;
        var text = isEditing ? editorState.DraftText.Trim() : slip.Text;

        saving = true;
        SetEditingEnabled();
        try
        {
            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.UpdateSlip,
                new UpdateSlipCommand { Text = text, Align = align },
                currentProject.Id,
                slip.Id,
                revision));
            if (response.Status == ZetlResponseStatus.Conflict)
            {
                var current = response.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(
                    ZetlProtocolJson.Options);
                if (current is not null)
                {
                    editorState.Reconcile(current);
                    ShowConflict();
                }

                return;
            }

            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Align failed: {response.Status}.";
                return;
            }

            if (response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options) is { } saved
                && isEditing)
            {
                editorState.AcceptSaved(saved);
            }

            await connection.RefreshAsync();
            statusText.Text = $"Slip aligned {align}.";
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

    private void UpdateAlignButtons()
    {
        var selected = SelectedSlips();
        var slip = selected.Count == 1 ? selected[0] : null;
        var canAlign = IsOnline
            && !saving
            && slip is { Type: ZetlSlipType.Text }
            && !IsSlipInDeleted(slip);
        alignLeftButton.IsEnabled = canAlign;
        alignCenterButton.IsEnabled = canAlign;
        alignRightButton.IsEnabled = canAlign;

        var active = slip is null ? "left" : ZetlViewRenderer.SlipAlignment(slip);
        alignLeftButton.FontWeight = canAlign && active == "left" ? FontWeight.Bold : FontWeight.Normal;
        alignCenterButton.FontWeight = canAlign && active == "center" ? FontWeight.Bold : FontWeight.Normal;
        alignRightButton.FontWeight = canAlign && active == "right" ? FontWeight.Bold : FontWeight.Normal;
    }

    // Wrap the editor selection (or insert a placeholder) in Markdown delimiters, then
    // reselect the inner text. Setting Text raises TextChanged, so the draft updates and
    // autosaves like normal typing.
    private void WrapEditorSelection(string prefix, string suffix, string placeholder)
    {
        if (!slipEditor.IsEnabled)
        {
            return;
        }

        var text = slipEditor.Text ?? "";
        var start = Math.Clamp(Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var selected = text[start..end];
        var inner = selected.Length == 0 ? placeholder : selected;

        slipEditor.Text = text[..start] + prefix + inner + suffix + text[end..];
        slipEditor.SelectionStart = start + prefix.Length;
        slipEditor.SelectionEnd = start + prefix.Length + inner.Length;
        slipEditor.Focus();
    }

    // Prefix every line the selection touches with a list marker (incrementing for
    // numbered lists), then reselect the modified block. A collapsed selection prefixes
    // just the current line.
    private void PrefixSelectedLines(Func<int, string> marker)
    {
        if (!slipEditor.IsEnabled)
        {
            return;
        }

        var text = slipEditor.Text ?? "";
        var selStart = Math.Clamp(Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var selEnd = Math.Clamp(Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);

        var blockStart = selStart == 0 ? 0 : text.LastIndexOf('\n', selStart - 1) + 1;
        var nextNewline = text.IndexOf('\n', selEnd);
        var blockEnd = nextNewline < 0 ? text.Length : nextNewline;

        var lines = text[blockStart..blockEnd].Split('\n');
        var rebuilt = string.Join("\n", lines.Select((line, index) => marker(index) + line));

        slipEditor.Text = text[..blockStart] + rebuilt + text[blockEnd..];
        slipEditor.SelectionStart = blockStart;
        slipEditor.SelectionEnd = blockStart + rebuilt.Length;
        slipEditor.Focus();
    }

    private void InsertEditorLink()
    {
        if (!slipEditor.IsEnabled)
        {
            return;
        }

        var text = slipEditor.Text ?? "";
        var start = Math.Clamp(Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var selected = text[start..end];

        if (selected.Length == 0)
        {
            // No selection: drop in [text](url) and select "text" to type the label.
            slipEditor.Text = text[..start] + "[text](url)" + text[end..];
            slipEditor.SelectionStart = start + 1;
            slipEditor.SelectionEnd = start + 5;
        }
        else
        {
            // Selection becomes the link label; select the "url" placeholder.
            slipEditor.Text = text[..start] + $"[{selected}](url)" + text[end..];
            var urlStart = start + 1 + selected.Length + 2;
            slipEditor.SelectionStart = urlStart;
            slipEditor.SelectionEnd = urlStart + 3;
        }

        slipEditor.Focus();
    }

    private async Task InsertSlipLinkAsync()
    {
        if (!slipEditor.IsEnabled || currentProject is null)
        {
            return;
        }

        var text = slipEditor.Text ?? "";
        var selectionStart = Math.Clamp(
            Math.Min(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var selectionEnd = Math.Clamp(
            Math.Max(slipEditor.SelectionStart, slipEditor.SelectionEnd), 0, text.Length);
        var existing = ZetlSlipLinks.FindAt(text, selectionStart);
        var query = existing?.CachedTitle
            ?? (selectionEnd > selectionStart ? text[selectionStart..selectionEnd] : "");
        var candidates = currentProject.Slips
            .Where(slip => slip.Id != editorState.SlipId && !IsSlipInDeleted(slip))
            .ToList();
        var target = await KastnDialogs.PickSlipAsync(this, candidates, query);
        if (target is null)
        {
            slipEditor.Focus();
            return;
        }

        var token = ZetlSlipLinks.Format(target.Id, ZetlSlipLinks.TitleFor(target));
        var replaceStart = existing?.Start ?? selectionStart;
        var replaceEnd = existing is null
            ? selectionEnd
            : existing.Start + existing.Length;
        slipEditor.Text = text[..replaceStart] + token + text[replaceEnd..];
        slipEditor.SelectionStart = replaceStart + token.Length;
        slipEditor.SelectionEnd = replaceStart + token.Length;
        slipEditor.Focus();
    }

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
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
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
            await connection.RefreshAsync();
            statusText.Text = $"Bucket '{name}' created.";
        }
        else
        {
            statusText.Text = response.Error?.Message ?? $"Bucket creation failed: {response.Status}.";
        }
    }

    private async Task AddSlipAsync()
    {
        if (!IsOnline || currentProject is null || addingSlip)
        {
            return;
        }

        var existingDraft = currentProject.Slips.LastOrDefault(slip =>
            slip.Source == "kastn"
            && string.Equals(slip.Title, UntitledSlipTitle, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(slip.Text)
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
            await connection.RefreshAsync();
            SetDetailPaneMode(showDetails: false);
            statusText.Text = "Finish the current untitled slip before creating another.";
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

            var destinationBucketId = SelectedBucketId
                is { } selectedBucketId && !KastnWorkbench.IsDeletedBucket(SelectedBucket)
                    ? selectedBucketId
                    : null;
            destinationBucketId ??= currentProject.ActiveBucketId
                ?? currentProject.Buckets.FirstOrDefault(
                    bucket => !KastnWorkbench.IsDeletedBucket(bucket))?.Id;
            if (destinationBucketId is null)
            {
                statusText.Text = "Create a bucket before adding a slip.";
                return;
            }

            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
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
                    await connection.RefreshAsync();
                    SetDetailPaneMode(showDetails: false);
                    statusText.Text = "Slip created.";
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
        pendingBucketSelectionId = bucket.Id;
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand
            {
                Name = name,
                ParentBucketId = parentId,
                Settings = bucket.Settings
            },
            currentProject.Id,
            bucket.Id,
            bucket.Revision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.RefreshAsync();
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
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
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
            await connection.RefreshAsync();
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
            currentProject.Status));
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

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.RenameProject,
            new RenameProjectCommand { Name = name },
            project.Id,
            project.Id,
            project.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.RefreshAsync();
        }

        HandleSimpleResponse(response, $"Project renamed to '{name}'.");
    }

    private async Task SetProjectStatusAsync(ProjectListItem project, string status)
    {
        if (!IsOnline)
        {
            return;
        }

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
            Guid.NewGuid().ToString("N"),
            ZetlCommandKind.SetProjectStatus,
            new SetProjectStatusCommand { Status = status },
            project.Id,
            project.Id,
            project.MetadataRevision));
        if (response.Status == ZetlResponseStatus.Success)
        {
            await connection.RefreshAsync();
        }

        var verb = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase)
            ? "reactivated"
            : status.ToLowerInvariant();
        HandleSimpleResponse(response, $"Project {verb}.");
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

        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
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

            await connection.RefreshAsync();
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
        foreach (var slip in selected)
        {
            if (slip.BucketId == destination.Id)
            {
                skipped++;
                continue;
            }

            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
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

        await connection.RefreshAsync();
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
        var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
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
        foreach (var slip in selected)
        {
            var response = await connection.ExecuteAsync(ZetlCommandEnvelope.Create(
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
        await connection.RefreshAsync();
        statusText.Text = failed == 0
            ? $"{moved} slip{Plural(moved)} moved to Deleted."
            : $"{moved} slip{Plural(moved)} moved to Deleted; {failed} failed.";
    }

    private void UseZetlVersion()
    {
        editorState.UseCurrent();
        UpdateEditorFromState();
        statusText.Text = "Using the current Zetl version.";
    }

    private async Task KeepMineAsync()
    {
        editorState.PrepareOverwrite();
        UpdateEditorFromState();
        editorState.SetDraft(localConflictText.Text ?? "");
        editorUpdating = true;
        slipEditor.Text = editorState.DraftText;
        editorUpdating = false;
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

    private void UpdateEditorFromState()
    {
        var selectedSlips = SelectedSlips();
        if (selectedSlips.Count > 1)
        {
            editorUpdating = true;
            slipEditor.Text = "";
            editorUpdating = false;
            conflictPanel.IsVisible = false;
            slipMetadataText.Text = $"{selectedSlips.Count} slips selected. "
                + "Choose a destination, then move or delete them together.";
            SetEditingEnabled();
            return;
        }

        editorUpdating = true;
        slipEditor.Text = SelectedSlip is { } editorSlip
            && IsUntitledKastnSlip(editorSlip)
            && !editorState.IsDirty
                ? ""
                : editorState.DraftText;
        editorUpdating = false;
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
            : SlipMetadata(slip);
        SetEditingEnabled();
    }

    private void SetEditingEnabled()
    {
        var selectedSlips = SelectedSlips();
        var hasSelectedSlips = selectedSlips.Count > 0;
        var hasMultipleSelectedSlips = selectedSlips.Count > 1;
        var allSelectedSlipsAreActive = hasSelectedSlips
            && selectedSlips.All(slip => !IsSlipInDeleted(slip));
        var selectedSlipIsDeleted = selectedSlips.Count == 1 && IsSlipInDeleted(selectedSlips[0]);
        var canEdit = IsOnline
            && editorState.SlipId is not null
            && !hasMultipleSelectedSlips
            && !saving;
        var canBatch = IsOnline
            && hasSelectedSlips
            && !saving
            && editorState.ConflictCurrent is null;
        var canCreateSlip = IsOnline
            && currentProject is not null
            && !KastnWorkbench.IsDeletedBucket(SelectedBucket)
            && !addingSlip;
        slipEditor.IsEnabled = canEdit && editorState.ConflictCurrent is null;
        var canFormat = slipEditor.IsEnabled;
        boldButton.IsEnabled = canFormat;
        italicButton.IsEnabled = canFormat;
        strikeButton.IsEnabled = canFormat;
        codeButton.IsEnabled = canFormat;
        linkButton.IsEnabled = canFormat;
        wikiLinkButton.IsEnabled = canFormat;
        bulletListButton.IsEnabled = canFormat;
        numberListButton.IsEnabled = canFormat;
        taskListButton.IsEnabled = canFormat;
        saveSlipButton.IsEnabled = canEdit && editorState.ConflictCurrent is null;
        saveSlipMenuItem.IsEnabled = false;
        deleteSlipButton.IsEnabled = canBatch && allSelectedSlipsAreActive;
        deleteSlipMenuItem.IsEnabled = false;
        restoreSlipButton.IsEnabled = canEdit && selectedSlipIsDeleted && moveBuckets.Count > 0;
        moveSlipButton.IsEnabled = canBatch && allSelectedSlipsAreActive && moveBuckets.Count > 0;
        moveBucketBox.IsEnabled = moveSlipButton.IsEnabled || restoreSlipButton.IsEnabled;
        // The Deleted view is for browsing/restoring; bucket and slip creation are
        // hidden there.
        addBucketButton.IsEnabled = IsOnline && currentProject is not null && !showingDeleted;
        closeProjectButton.IsEnabled = currentProject is not null;
        saveAsTemplateMenuItem.IsEnabled = currentProject is not null;
        newSlipButton.IsEnabled = canCreateSlip && !showingDeleted;
        newSlipMenuItem.IsEnabled = newSlipButton.IsEnabled;
        UpdateAlignButtons();
    }

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

        // The slips the selection acts on: each selected slip node, plus every slip
        // under a selected bucket node (selecting a bucket = batch-edit its slips).
        var slipIds = SelectedTreeSlipIds();
        var singleSlipId = slipIds.Count == 1 ? slipIds[0] : null;

        // Save the current edit before switching away from the edited slip. On a
        // failed save (conflict or offline) revert the selection so it stays put.
        if (editorState.SlipId is { } editingId
            && !string.Equals(editingId, singleSlipId, StringComparison.Ordinal)
            && editorState.IsDirty
            && !await SaveEditorAsync())
        {
            refreshing = true;
            projectTree.SelectedItem = FindTreeNode(
                projectTree.ItemsSource as IEnumerable<KastnTreeNode>, editingId);
            refreshing = false;
            return;
        }

        lastSelectedNodeId = node.Id;
        if (singleSlipId is not null)
        {
            // Exactly one slip: load it into the editor and scope the view to its
            // bucket, then show the Slip pane.
            pendingSlipSelectionId = singleSlipId;
            RefreshBucketEditor();
            RefreshSlipView(force: true);
            InspectSlip(singleSlipId);
            SetDetailPaneMode(showDetails: false);
        }
        else
        {
            // Zero slips (an empty bucket) or several: the batch count comes from the
            // tree via SelectedSlips, so just refresh and clear the single-slip editor.
            pendingSlipSelectionId = null;
            RefreshBucketEditor();
            RefreshSlipView(force: true);
            inspectedSlipId = null;
            RenderSlipInspector(null);
            editorState.Select(null);
            UpdateEditorFromState();
            SetDetailPaneMode(showDetails: false);
        }
    }

    // The distinct slip ids the current tree selection targets: each selected slip
    // node plus every slip beneath a selected bucket node, de-duplicated.
    private IReadOnlyList<string> SelectedTreeSlipIds()
    {
        var nodes = projectTree.SelectedItems?.OfType<KastnTreeNode>().ToList()
            ?? (SelectedTreeNode is { } single ? [single] : []);
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slip in nodes.SelectMany(TreeSlips))
        {
            if (seen.Add(slip.Id))
            {
                ids.Add(slip.Id);
            }
        }

        return ids;
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

    private static KastnTreeNode? FindTreeNode(IEnumerable<KastnTreeNode>? nodes, string? id)
    {
        if (nodes is null || id is null)
        {
            return null;
        }

        foreach (var node in nodes)
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                return node;
            }

            if (FindTreeNode(node.Children, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private IReadOnlyList<ZetlSlipSnapshot> CurrentFilteredSlips()
    {
        return currentProject is null
            ? []
            : KastnWorkbench.FilterSlips(
                currentProject,
                SelectedBucketId,
                (sourceFilterBox.SelectedItem as FilterItem)?.Value,
                (sessionFilterBox.SelectedItem as FilterItem)?.Value,
                (dateFilterBox.SelectedItem as DateFilterItem)?.Value ?? KastnDateFilter.All,
                searchBox.Text,
                DateTimeOffset.Now);
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
                currentProject,
                null,
                (sourceFilterBox.SelectedItem as FilterItem)?.Value,
                (sessionFilterBox.SelectedItem as FilterItem)?.Value,
                (dateFilterBox.SelectedItem as DateFilterItem)?.Value ?? KastnDateFilter.All,
                searchBox.Text,
                DateTimeOffset.Now)
            .Where(slip => !IsSlipInDeleted(slip))
            .ToList();
    }

    private IReadOnlyList<ZetlSlipSnapshot> SelectedSlips()
    {
        if (currentProject is null)
        {
            return [];
        }

        // The tree is the selection surface: resolve its slips project-wide so a
        // batch selection survives crossing buckets, and a bucket selection counts
        // all its slips immediately — independent of the bucket-scoped display list.
        var ids = SelectedTreeSlipIds();
        if (ids.Count > 0)
        {
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            return currentProject.Slips.Where(slip => idSet.Contains(slip.Id)).ToList();
        }

        // Fall back to the single editor slip (e.g. a freshly created/selected one).
        return editorState.SlipId is { } editingId
            && currentProject.Slips.FirstOrDefault(slip => slip.Id == editingId) is { } slip
            ? [slip]
            : [];
    }

    private IReadOnlyList<SlipListItem> SelectedSlipItems()
    {
        if (slipList.SelectedItems is { Count: > 0 } selectedItems)
        {
            return selectedItems
                .OfType<SlipListItem>()
                .ToList();
        }

        return slipList.SelectedItem is SlipListItem selected
            ? [selected]
            : [];
    }

    private bool IsSlipInDeleted(ZetlSlipSnapshot? slip)
    {
        if (currentProject is null || slip is null)
        {
            return false;
        }

        return KastnWorkbench.IsDeletedBucket(currentProject.Buckets.FirstOrDefault(
            bucket => bucket.Id == slip.BucketId));
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
            : $"Current parent: {KastnWorkbench.BucketPathLabel(currentProject, parent)}";
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

    private static bool IsDescendant(
        ZetlProjectSnapshot project,
        string candidateId,
        string ancestorId)
    {
        var current = project.Buckets.FirstOrDefault(bucket => bucket.Id == candidateId);
        while (current?.ParentBucketId is { } parentId)
        {
            if (parentId == ancestorId)
            {
                return true;
            }

            current = project.Buckets.FirstOrDefault(bucket => bucket.Id == parentId);
        }

        return false;
    }

    private static string ShortSession(string? session)
    {
        if (string.IsNullOrWhiteSpace(session))
        {
            return "No session";
        }

        return session.Length <= 12 ? session : session[..12];
    }

    private static string SlipPreviewText(ZetlSlipSnapshot slip)
    {
        var source = string.IsNullOrWhiteSpace(slip.Title) ? slip.Text : slip.Title;
        var words = source
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(5)
            .ToList();
        return words.Count == 0 ? "Untitled" : string.Join(' ', words);
    }

    private static bool IsUntitledKastnSlip(ZetlSlipSnapshot slip)
    {
        return string.Equals(slip.Source, "kastn", StringComparison.Ordinal)
            && string.Equals(slip.Title.Trim(), UntitledSlipTitle, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(slip.Text);
    }
}
