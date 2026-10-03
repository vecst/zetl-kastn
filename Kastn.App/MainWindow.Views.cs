using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private bool lastBoardModeActive;

    private void RefreshViewer()
    {
        if (currentProject is null)
        {
            viewerSummaryText.Text = "No project selected.";
            lastRenderedViewText = "";
            viewRenderCache.Clear();
            readerPresenter.Clear();
            boardPresenter.Clear();
            RefreshSlipInspector();
            copyViewButton.IsEnabled = false;
            exportViewButton.IsEnabled = false;
            formatFidelityNote.IsVisible = false;
            return;
        }

        var visible = CurrentViewSlips();
        RefreshSlipInspector();
        viewerSummaryText.Text = visible.Count == 0
            ? "No slips match the current filters."
            : $"{visible.Count} of {currentProject.Slips.Count} slips in the current view.";

        // The on-screen View is always the readable per-slip document; the view kind
        // governs only the Copy/Export artifact. Rebuild only when the rendered content
        // could have changed (a project mutation, the filters, or the chosen view) — a
        // pure selection change just re-highlights, so picture blocks never reload.
        var view = SelectedView;
        var settings = CurrentAppSettings();
        var inputs = KastnViewRenderKey.Create(currentProject, visible, view, settings);
        var selectedId = SelectedTreeNode?.Slip?.Id ?? editorState.SlipId;
        if (boardModeActive)
        {
            readerPresenter.Suspend();
            boardPresenter.Render(CaptureBoardInputs(visible, inputs), selectedId);
        }
        else
        {
            boardPresenter.Suspend();
            readerPresenter.Render(CaptureReaderInputs(visible, inputs), selectedId, force: lastBoardModeActive);
        }
        lastBoardModeActive = boardModeActive;

        if (view.Kind == ZetlViewKinds.Pdf)
        {
            // PDF is binary — there is no text to copy; Export writes the .pdf.
            lastRenderedViewText = "";
            copyViewButton.IsEnabled = false;
            exportViewButton.IsEnabled = visible.Count > 0;
        }
        else
        {
            lastRenderedViewText = viewRenderCache.GetText(inputs, () => visible.Count == 0
                ? ""
                : ZetlViewRenderer.Render(
                    currentProject,
                    visible,
                    view,
                    preferSlipKindOverBucketKind: settings.KastnPreferSlipKindOverBucketKind));
            var hasOutput = lastRenderedViewText.Length > 0;
            copyViewButton.IsEnabled = hasOutput;
            exportViewButton.IsEnabled = hasOutput;
        }

        deleteViewMenuItem.IsEnabled = !ZetlViewDefaults.IsBuiltIn(SelectedView.Id);
        UpdateFormatFidelityNote();
    }

    // The on-screen reader always renders slip formatting and alignment; the selected
    // view kind only decides what its Copy/Export artifact carries. Tell the editor
    // when that artifact would drop the formatting the reader is showing, so a user
    // authoring toward, say, a TSV export is not surprised by raw markup in the output.
    private void UpdateFormatFidelityNote()
    {
        var kind = SelectedView.Kind;
        var preservesFormatting = ZetlViewRenderer.ExportPreservesFormatting(kind);
        var preservesAlignment = ZetlViewRenderer.ExportPreservesAlignment(kind);
        var preservesTypography = ZetlViewRenderer.ExportPreservesTypography(kind);
        if (preservesFormatting && preservesAlignment && preservesTypography)
        {
            formatFidelityNote.IsVisible = false;
            return;
        }

        var losses = new List<string>();
        if (!preservesFormatting)
        {
            losses.Add("emphasis and links");
        }
        if (!preservesAlignment)
        {
            losses.Add("block alignment");
        }
        if (!preservesTypography)
        {
            losses.Add("font family, size, and color");
        }

        formatFidelityNote.Text = $"The {kind} view does not preserve "
            + NaturalList(losses)
            + " in Copy/Export; the rich reader still shows them.";
        formatFidelityNote.IsVisible = true;
    }

    private static string NaturalList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "formatting",
        1 => items[0],
        2 => $"{items[0]} or {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, or {items[^1]}"
    };

    private void RefreshSlipInspector()
    {
        // The inspector follows the tree selection, which can be any slip in the
        // project — including a deleted or filtered-out one the whole-project View
        // does not render. Keep it as long as the slip still exists in the project.
        var selected = SelectedTreeNode?.Slip;
        if (selected is not null
            && currentProject?.Slips.All(slip => slip.Id != selected.Id) != false)
        {
            selected = null;
        }

        RenderSlipInspector(selected);
    }

    private void InspectSlip(string slipId)
    {
        RenderSlipInspector(currentProject is null ? null : ProjectIndex.Slip(slipId));
    }

    private string lastInspectorSignature = "";

    private void RenderSlipInspector(ZetlSlipSnapshot? slip)
    {
        // Rebuild the detail fields only when their inputs changed (the slip, its
        // revision, or the project — the change sequence covers backlink edits);
        // the refreshes that follow one action otherwise re-cleared the pane
        // several times, which read as a flash.
        var signature = $"{currentProject?.Id}|{currentProject?.ChangeSequence}|{slip?.Id}|{slip?.Revision}";
        if (string.Equals(signature, lastInspectorSignature, StringComparison.Ordinal))
        {
            return;
        }

        lastInspectorSignature = signature;
        slipInspectorFieldsPanel.Children.Clear();
        if (currentProject is null || slip is null)
        {
            slipInspectorFieldsPanel.Children.Add(new TextBlock
            {
                Text = "No slip is available in the current view.",
                Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var section in KastnSlipInspector.Build(ProjectIndex, slip))
        {
            slipInspectorFieldsPanel.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 8, 0, 0)
            });
            if (string.Equals(section.Heading, "Linked from", StringComparison.Ordinal))
            {
                foreach (var field in section.Fields)
                {
                    var targetSlipId = field.Value;
                    var navigateButton = new Button
                    {
                        Content = field.Label,
                        Padding = new Avalonia.Thickness(9, 3),
                        Margin = new Avalonia.Thickness(0, 2, 0, 2),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    navigateButton.Click += (_, _) =>
                    {
                        var node = treeProjection.Find(targetSlipId);
                        if (node is not null && !ReferenceEquals(projectTree.SelectedItem, node))
                        {
                            projectTree.SelectedItem = node;
                        }
                    };
                    slipInspectorFieldsPanel.Children.Add(navigateButton);
                }
                continue;
            }
            foreach (var field in section.Fields)
            {
                var fieldPanel = new StackPanel { Spacing = 1 };
                fieldPanel.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    Classes = { "muted" },
                    FontSize = 11
                });
                fieldPanel.Children.Add(new TextBlock
                {
                    Text = field.Value,
                    TextWrapping = TextWrapping.Wrap,
                    [ToolTip.TipProperty] = field.Value
                });
                if (string.Equals(field.Label, "Original URL", StringComparison.Ordinal)
                    && Uri.TryCreate(field.Value, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    var openButton = new Button
                    {
                        Content = "Open URL",
                        Padding = new Avalonia.Thickness(9, 3),
                        Margin = new Avalonia.Thickness(0, 4, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    openButton.Click += (_, _) => OpenInspectorUrl(uri);
                    fieldPanel.Children.Add(openButton);
                }

                slipInspectorFieldsPanel.Children.Add(fieldPanel);
            }
        }
    }

    private void OpenInspectorUrl(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            statusText.Text = "Kastn could not open that URL.";
        }
    }

    private IBrush? ThemeBrush(string key) =>
        this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;

    private KastnReaderRenderInputs CaptureReaderInputs(IReadOnlyList<ZetlSlipSnapshot> visible, KastnViewRenderKey key) =>
        new(ProjectIndex, visible, ZetlViewDefaults.Clone(SelectedView), key, showingDeleted,
            CreateSlipContentRenderer(), new(ThemeBrush("ZetlBorderBrush"), ThemeBrush("ZetlAccentBrush"),
                ThemeBrush("ZetlSurfaceBrush")), CaptureSlipSelectionAction());

    private Action<string> CaptureSlipSelectionAction()
    {
        var projectId = currentProject?.Id;
        var generation = editHistory.Generation;
        return slipId =>
        {
            if (projectId is null || currentProject?.Id != projectId || generation != editHistory.Generation) return;
            var node = treeProjection.Find(slipId);
            if (node is not null && !ReferenceEquals(projectTree.SelectedItem, node))
                projectTree.SelectedItem = node;
        };
    }

    private KastnSlipContentRenderer CreateSlipContentRenderer()
    {
        var project = currentProject is null ? null : ProjectIndex;
        var projectId = project?.Project.Id;
        var generation = editHistory.Generation;
        var mono = this.TryFindResource("ZetlMonoFontFamily", out var value) ? value as FontFamily : null;
        return new(project, new(ThemeBrush("ZetlBorderBrush"), ThemeBrush("ZetlAccentBrush"),
            ThemeBrush("ZetlMutedTextBrush"), mono), CaptureSlipSelectionAction(),
            slipId => projectId is not null && currentProject?.Id == projectId && generation == editHistory.Generation
                ? ToggleSlipCheckedAsync(slipId) : Task.CompletedTask);
    }

    private async Task<ZetlPictureContent?> FetchPictureContentAsync(string projectId, ZetlSlipSnapshot slip)
    {
        var response = await connection.QueryAsync(new ZetlCommandEnvelope
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Kind = ZetlCommandKind.GetSlipPicture,
            ProjectId = projectId,
            TargetId = slip.Id
        });
        return response.Status == ZetlResponseStatus.Success
            ? response.Payload?.Deserialize<ZetlPictureContent>(ZetlProtocolJson.Options)
            : null;
    }

    private ZetlViewDocument SelectedView =>
        viewPickerBox.SelectedItem as ZetlViewDocument
        ?? (loadedViews.Count > 0 ? loadedViews[0] : ZetlViewDefaults.CreateAll()[0]);

    private void SelectViewForProject(ZetlProjectSnapshot project)
    {
        var target = string.IsNullOrEmpty(project.DefaultViewId)
            ? null
            : loadedViews.FirstOrDefault(view => view.Id == project.DefaultViewId);
        if (target is null)
        {
            // No project-pinned view: fall back to the user's global default reading
            // view from Zetl Settings, then to the first available view.
            var globalId = CurrentAppSettings().KastnDefaultViewId;
            if (!string.IsNullOrEmpty(globalId))
            {
                target = loadedViews.FirstOrDefault(view => view.Id == globalId);
            }
        }

        viewPickerBox.SelectedItem = target ?? loadedViews.FirstOrDefault();
    }

    // The Kastn workbench preferences live in the shared Zetl settings file.
    // Cache reads for hot render paths; local saves invalidate immediately via
    // the save stamp, while external changes are picked up within a second.
    private static ZetlAppSettings? cachedAppSettings;
    private static DateTime cachedAppSettingsAt;
    private static int cachedAppSettingsStamp;

    private static ZetlAppSettings CurrentAppSettings()
    {
        var now = DateTime.UtcNow;
        if (cachedAppSettings is null
            || cachedAppSettingsStamp != ZetlAppSettingsStore.SaveStamp
            || now - cachedAppSettingsAt > TimeSpan.FromSeconds(1))
        {
            cachedAppSettings = new ZetlAppSettingsStore().Settings;
            cachedAppSettingsAt = now;
            cachedAppSettingsStamp = ZetlAppSettingsStore.SaveStamp;
        }

        return cachedAppSettings;
    }

    private void RefreshViewCatalog(ZetlProjectSnapshot? project, string? selectId = null)
    {
        globalViews = viewStore.LoadAll();
        var projectViews = project?.Views
            .Select(ZetlProjectSnapshotMapper.ToDocument)
            .ToList() ?? [];
        loadedViews = globalViews
            .Where(view => projectViews.All(projectView => projectView.Id != view.Id))
            .Concat(projectViews)
            .ToList();
        viewPickerBox.ItemsSource = loadedViews;
        viewPickerBox.SelectedItem = loadedViews.FirstOrDefault(view => view.Id == selectId)
            ?? loadedViews.FirstOrDefault();
    }

    private bool IsProjectScopedView(string viewId) =>
        currentProject?.Views.Any(view => view.Id == viewId) == true;

    private KastnViewExportOperation? CaptureViewExportOperation() =>
        currentProject is null ? null : new(currentProject, CurrentViewSlips(), SelectedView, CurrentAppSettings());

    private Task CopyRenderedViewAsync() => TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
        ? CopyRenderedViewAsync(clipboard.SetTextAsync, pictureCache.GetCapturedContentAsync)
        : Task.CompletedTask;

    private async Task CopyRenderedViewAsync(
        Func<string, Task> setText,
        Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetchPicture)
    {
        if (lastRenderedViewText.Length == 0 || CaptureViewExportOperation() is not { } operation)
        {
            return;
        }
        try
        {
            var pictures = await operation.LoadPicturesAsync(fetchPicture);
            await setText(operation.RenderText(pictures));
            statusText.Text = $"Copied the {operation.ViewName} view to the clipboard.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = $"Could not copy the view: {ex.Message}";
        }
    }

    private Task ExportRenderedViewAsync() => ExportRenderedViewAsync(async options =>
    {
        var file = await StorageProvider.SaveFilePickerAsync(options);
        return file is null ? null : new KastnViewExportDestination(file.Name, file.OpenWriteAsync);
    }, pictureCache.GetCapturedContentAsync);

    private async Task ExportRenderedViewAsync(
        Func<FilePickerSaveOptions, Task<KastnViewExportDestination?>> pickFile,
        Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetchPicture)
    {
        if (CaptureViewExportOperation() is not { } operation
            || !operation.IsPdf && lastRenderedViewText.Length == 0)
        {
            return;
        }
        try
        {
            var file = await pickFile(new FilePickerSaveOptions
            {
                Title = $"Export {operation.ViewName}",
                SuggestedFileName = operation.SuggestedFileName,
                DefaultExtension = operation.FileExtension
            });
            if (file is null)
            {
                return;
            }
            var pictures = await operation.LoadPicturesAsync(fetchPicture);
            await using var stream = await file.OpenWriteAsync();
            await operation.WriteAsync(stream, pictures);
            statusText.Text = $"Exported the {operation.ViewName} view to {file.Name}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = $"Could not export the view: {ex.Message}";
        }
    }

    // ---- In-window view editor (mirrors the template editor) ----

    private void EditSelectedView()
    {
        var view = SelectedView;
        if (ZetlViewDefaults.IsBuiltIn(view.Id))
        {
            // Built-ins stay immutable: edit a fresh user copy instead.
            OpenViewEditor(ZetlViewDefaults.Duplicate(view), isNew: true);
        }
        else
        {
            OpenViewEditor(ZetlViewDefaults.Clone(view), isNew: false);
        }
    }

    private void OpenViewEditor(ZetlViewDocument working, bool isNew)
    {
        editingView = working;
        editingProjectScopedView = !isNew && IsProjectScopedView(working.Id);
        viewErrorText.IsVisible = false;

        viewEditorUpdating = true;
        viewEditorTitle.Text = isNew ? "New View" : $"Edit View — {working.Name}";
        viewNameBox.Text = working.Name;
        viewDescriptionBox.Text = working.Description;
        viewKindBox.SelectedItem = ViewKindChoices.Contains(working.Kind)
            ? working.Kind
            : ZetlViewKinds.Formatted;
        viewTsvRowBox.Value = Math.Clamp(working.TsvRowLength, 1, 100);
        viewListStyleBox.SelectedItem = ZetlViewListStyles.Normalize(working.ListStyle);
        viewNumberHeadingsCheck.IsChecked = working.NumberHeadings;
        viewShowTitleCheck.IsChecked = working.ShowTitle;
        viewTitleBox.Text = working.Title;
        LoadViewStructureEditor(working.Sections);
        viewEditorUpdating = false;

        ApplyViewKindSettingsVisibility();
        viewBaselineJson = CurrentViewJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = false;
        viewEditorView.IsVisible = true;
        RefreshViewLivePreview();
    }

    private void SetViewEditorKind(string kind)
    {
        viewKindBox.SelectedItem = kind;
        ApplyViewKindSettingsVisibility();
        RefreshViewLivePreview();
    }

    private void ApplyViewKindSettingsVisibility()
    {
        var kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        viewTsvPanel.IsVisible = kind == ZetlViewKinds.Tsv;
        viewDocumentSettingsPanel.IsVisible = kind is ZetlViewKinds.Markdown
            or ZetlViewKinds.Html
            or ZetlViewKinds.Pdf;

        SetViewKindButtonState(viewFormattedKindButton, kind == ZetlViewKinds.Formatted);
        SetViewKindButtonState(viewPlainKindButton, kind == ZetlViewKinds.Plain);
        SetViewKindButtonState(viewTsvKindButton, kind == ZetlViewKinds.Tsv);
        SetViewKindButtonState(viewMarkdownKindButton, kind == ZetlViewKinds.Markdown);
        SetViewKindButtonState(viewHtmlKindButton, kind == ZetlViewKinds.Html);
        SetViewKindButtonState(viewPdfKindButton, kind == ZetlViewKinds.Pdf);
    }

    private static void SetViewKindButtonState(Button button, bool isActive)
    {
        button.Classes.Set("view-format-active", isActive);
    }

    private string CurrentViewJson()
    {
        var doc = CurrentViewDocument();
        return doc is null ? "" : JsonSerializer.Serialize(doc, JsonFile.Options);
    }

    private ZetlViewDocument? CurrentViewDocument()
    {
        if (editingView is null)
        {
            return null;
        }

        var doc = ZetlViewDefaults.Clone(editingView);
        doc.Name = viewNameBox.Text?.Trim() ?? "";
        doc.Description = viewDescriptionBox.Text?.Trim() ?? "";
        doc.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        doc.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        doc.ListStyle = viewListStyleBox.SelectedItem as string ?? ZetlViewListStyles.Bullet;
        doc.NumberHeadings = viewNumberHeadingsCheck.IsChecked == true;
        doc.ShowTitle = viewShowTitleCheck.IsChecked == true;
        doc.Title = viewTitleBox.Text?.Trim() ?? "";
        doc.Sections = CurrentViewSections();
        return doc;
    }

    private bool IsViewDirty() =>
        editingView is not null && CurrentViewJson() != viewBaselineJson;

    private async Task CancelViewEditAsync()
    {
        if (IsViewDirty())
        {
            var discard = await KastnDialogs.ConfirmAsync(
                this,
                "Discard unsaved changes to this view?",
                "Discard");
            if (!discard)
            {
                return;
            }
        }

        CloseViewEditor();
    }

    private async Task SaveViewAsync()
    {
        if (editingView is not { } view)
        {
            return;
        }

        view.Name = viewNameBox.Text?.Trim() ?? "";
        view.Description = viewDescriptionBox.Text?.Trim() ?? "";
        view.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        view.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        view.ListStyle = viewListStyleBox.SelectedItem as string ?? ZetlViewListStyles.Bullet;
        view.NumberHeadings = viewNumberHeadingsCheck.IsChecked == true;
        view.ShowTitle = viewShowTitleCheck.IsChecked == true;
        view.Title = viewTitleBox.Text?.Trim() ?? "";
        view.Sections = CurrentViewSections();
        if (string.IsNullOrEmpty(view.Id))
        {
            view.Id = ZetlViewDefaults.CreateId(view.Name);
        }

        var errors = ZetlViewValidator.Validate(view);
        if (errors.Count > 0)
        {
            viewErrorText.Text = string.Join("\n", errors);
            viewErrorText.IsVisible = true;
            return;
        }

        var saveProjectScoped = view.Sections.Count > 0;
        if (saveProjectScoped)
        {
            if (currentProject is null)
            {
                viewErrorText.Text = "Open a project before saving a structured view.";
                viewErrorText.IsVisible = true;
                return;
            }

            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.SaveProjectView,
                new SaveProjectViewCommand { View = ZetlProjectSnapshotMapper.ToSnapshot(view) },
                currentProject.Id,
                expectedTargetRevision: currentProject.MetadataRevision));
            if (response.Status != ZetlResponseStatus.Success)
            {
                viewErrorText.Text = response.Error?.Message ?? $"Save failed: {response.Status}.";
                viewErrorText.IsVisible = true;
                return;
            }

            // A formerly global structured view becomes project-owned after the
            // authoritative project write succeeds.
            if (!editingProjectScopedView && !ZetlViewDefaults.IsBuiltIn(view.Id))
            {
                try
                {
                    viewStore.Delete(view.Id);
                }
                catch (IOException)
                {
                    // The project copy is durable; a stale global copy is hidden by id.
                }
            }
        }
        else
        {
            try
            {
                viewStore.Save(view);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
            {
                viewErrorText.Text = ex.Message;
                viewErrorText.IsVisible = true;
                return;
            }

            if (editingProjectScopedView && currentProject is not null)
            {
                var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.DeleteProjectView,
                    new DeleteProjectViewCommand { ViewId = view.Id },
                    currentProject.Id,
                    expectedTargetRevision: currentProject.MetadataRevision));
                if (response.Status != ZetlResponseStatus.Success)
                {
                    viewErrorText.Text = response.Error?.Message ?? $"Scope change failed: {response.Status}.";
                    viewErrorText.IsVisible = true;
                    return;
                }
            }
        }

        var savedId = view.Id;
        var savedName = view.Name;
        CloseViewEditor();
        await connection.SynchronizeAsync();
        currentProject = connection.Current.Project;
        RefreshViewCatalog(currentProject, savedId);
        RefreshViewer();
        statusText.Text = $"Saved view '{savedName}'.";
    }

    private void CloseViewEditor()
    {
        editingView = null;
        editingProjectScopedView = false;
        viewErrorText.IsVisible = false;
        viewEditorView.IsVisible = false;
        if (currentProject is not null)
        {
            projectView.IsVisible = true;
            emptyState.IsVisible = false;
        }
        else
        {
            emptyState.IsVisible = true;
        }
    }

    private async Task DeleteSelectedViewAsync()
    {
        var view = SelectedView;
        if (ZetlViewDefaults.IsBuiltIn(view.Id))
        {
            statusText.Text = "Built-in views can't be deleted.";
            return;
        }

        var confirmed = await KastnDialogs.ConfirmAsync(
            this,
            $"Delete the view '{view.Name}'? This cannot be undone.",
            "Delete");
        if (!confirmed)
        {
            return;
        }

        if (IsProjectScopedView(view.Id) && currentProject is not null)
        {
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.DeleteProjectView,
                new DeleteProjectViewCommand { ViewId = view.Id },
                currentProject.Id,
                expectedTargetRevision: currentProject.MetadataRevision));
            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Delete failed: {response.Status}.";
                return;
            }
        }
        else
        {
            try
            {
                viewStore.Delete(view.Id);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                statusText.Text = $"Could not delete view: {ex.Message}";
                return;
            }
        }

        await connection.SynchronizeAsync();
        currentProject = connection.Current.Project;
        RefreshViewCatalog(currentProject);
        RefreshViewer();
        statusText.Text = $"Deleted view '{view.Name}'.";
    }

}
