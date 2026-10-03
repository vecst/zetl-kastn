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
    private bool lastBoardModeActive;
    private bool lastBoardDeletedOnly;

    private void RefreshViewer()
    {
        if (currentProject is null)
        {
            viewerSummaryText.Text = "No project selected.";
            lastRenderedViewText = "";
            viewRenderCache.Clear();
            readerPresenter.Clear();
            lastBoardRenderKey = null;
            ClearBoard();
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
            var rebuilt = inputs != lastBoardRenderKey || !lastBoardModeActive
                || showingDeleted != lastBoardDeletedOnly;
            if (rebuilt)
            {
                BuildBoardView(visible);
                lastBoardRenderKey = inputs;
                lastBoardDeletedOnly = showingDeleted;
            }
            UpdateBoardSelectionHighlight(scrollIntoView: !rebuilt);
        }
        else
        {
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

    private Task<ZetlPictureContent?> GetPictureContentAsync(ZetlSlipSnapshot slip) =>
        currentProject is null
            ? Task.FromResult<ZetlPictureContent?>(null)
            : pictureCache.GetContentAsync(currentProject.Id, slip);

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

    // A cached decoded bitmap for the slip's picture at the given decode width,
    // or null when it has not been decoded yet. A hit lets the caller assign the
    // image synchronously, so a rebuild neither re-decodes nor blinks "Loading…".
    private Bitmap? CachedDecodedPicture(ZetlSlipSnapshot slip, int width) =>
        slip.Picture is { } picture ? pictureCache.FindDecoded(picture.Sha256, width) : null;

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

    // Each board column's cards panel by bucket id, so the drag hit-test can pick
    // the precise insertion slot from the pointer's place among the cards.
    private readonly Dictionary<string, StackPanel> boardColumnCardPanels = new(StringComparer.Ordinal);

    // Reconciled board state, keyed by bucket/slip id, so a refresh updates only
    // what changed instead of rebuilding the whole board (which flashed and reset
    // every scroll position). A card is reused while its render inputs are
    // unchanged; decoded picture bitmaps belong to the shared picture cache.
    private sealed class BoardColumnUi
    {
        public required Grid Wrapper { get; init; }
        public required TextBlock TitleText { get; init; }
        public required TextBlock CountText { get; init; }
        public required StackPanel CardsPanel { get; init; }

        // The inline "type a card in place" composer under the cards; it lives
        // outside the reconciled cards panel so refreshes never disturb it.
        public required Border Composer { get; init; }
        public required TextBox ComposerBox { get; init; }
        public bool ComposerBusy { get; set; }
    }

    private sealed class BoardCardUi
    {
        public required Grid Wrapper { get; init; }
        public required Border CardBorder { get; init; }
        public required string RenderKey { get; init; }

        // The thumbnail targets of a picture card, waiting for the async load
        // (the decoded bitmap lives in the shared picture cache). The
        // reconcile starts the load after the card is registered, so a load that
        // completes synchronously still sees itself as the current card. A dual
        // card also feeds its click-to-peek expanded image from the same bitmap.
        public (Image Image, Image? ExpandedImage, TextBlock Status)? PendingPictureLoad { get; set; }
    }

    private readonly Dictionary<string, BoardColumnUi> boardColumns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoardCardUi> boardCards = new(StringComparer.Ordinal);

    // Dual (text + picture) cards the user expanded to peek at the picture.
    // Keyed by slip id rather than stored on the card so a peek survives the
    // card rebuilds a revision bump causes; pruned with stale cards and cleared
    // with the board.
    private readonly HashSet<string> expandedBoardPictures = new(StringComparer.Ordinal);

    private void BuildBoardView(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        if (currentProject is null)
        {
            ClearBoard();
            return;
        }

        // Traverse bucket snapshots directly: board columns need canonical bucket
        // order, not a second projection of every slip into temporary tree nodes.
        var buckets = ProjectIndex.OrderedBuckets();
        var visibleSlipsByBucket = visible
            .Where(slip => !IsSlipInDeleted(slip))
            .GroupBy(slip => slip.BucketId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ZetlSlipSnapshot>)g.ToList(), StringComparer.Ordinal);

        var desiredColumns = new List<Control>();
        var liveBucketIds = new HashSet<string>(StringComparer.Ordinal);
        var liveSlipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            liveBucketIds.Add(bucket.Id);
            var slips = visibleSlipsByBucket.TryGetValue(bucket.Id, out var found) ? found : [];
            if (!boardColumns.TryGetValue(bucket.Id, out var column))
            {
                column = CreateBoardColumn(bucket);
                boardColumns[bucket.Id] = column;
            }

            // Drag markers bind live node flags, so reused columns must adopt the
            // current projection's context (including changes to Deleted mode).
            column.TitleText.Text = bucket.Name;
            column.CountText.Text = $"({slips.Count})";
            column.Wrapper.DataContext =
                treeProjection.Find(bucket.Id);

            ReconcileColumnCards(column, bucket, slips, liveSlipIds);
            desiredColumns.Add(column.Wrapper);
        }

        foreach (var staleId in boardColumns.Keys.Where(id => !liveBucketIds.Contains(id)).ToList())
        {
            boardColumns.Remove(staleId);
            boardColumnCardPanels.Remove(staleId);
        }

        foreach (var staleId in boardCards.Keys.Where(id => !liveSlipIds.Contains(id)).ToList())
        {
            RemoveBoardCard(staleId);
            expandedBoardPictures.Remove(staleId);
        }

        KastnPanelReconciler.SyncChildren(boardColumnsPanel.Children, desiredColumns);
    }

    private void ClearBoard()
    {
        foreach (var id in boardCards.Keys.ToList())
        {
            RemoveBoardCard(id);
        }

        boardColumns.Clear();
        boardColumnCardPanels.Clear();
        boardColumnsPanel.Children.Clear();
        boardSlipCards.Clear();
        expandedBoardPictures.Clear();
        highlightedBoardSlipId = null;
    }

    private void ReconcileColumnCards(
        BoardColumnUi column,
        ZetlBucketSnapshot bucket,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        HashSet<string> liveSlipIds)
    {
        var desiredCards = new List<Control>();
        foreach (var slip in slips)
        {
            liveSlipIds.Add(slip.Id);
            var renderKey = BoardCardRenderKey(slip, bucket);
            if (boardCards.TryGetValue(slip.Id, out var card)
                && !string.Equals(card.RenderKey, renderKey, StringComparison.Ordinal))
            {
                // Content changed: rebuild this one card.
                RemoveBoardCard(slip.Id);
                card = null;
            }

            if (card is null)
            {
                card = CreateBoardCard(slip, renderKey);
                boardCards[slip.Id] = card;
                boardSlipCards[slip.Id] = card.CardBorder;
                if (card.PendingPictureLoad is { } load)
                {
                    card.PendingPictureLoad = null;
                    _ = LoadBoardCardPictureAsync(slip, card, load.Image, load.ExpandedImage, load.Status);
                }
            }

            card.Wrapper.DataContext =
                treeProjection.Find(slip.Id);
            desiredCards.Add(card.Wrapper);
        }

        KastnPanelReconciler.SyncChildren(column.CardsPanel.Children, desiredCards);
    }

    // The inputs a rendered card depends on beyond its position: the slip's own
    // revision plus the bucket/setting context that shapes its footer markers.
    private string BoardCardRenderKey(ZetlSlipSnapshot slip, ZetlBucketSnapshot bucket) =>
        $"{slip.Revision}|{bucket.RenderKind}|{CurrentAppSettings().KastnPreferSlipKindOverBucketKind}|{UntitledSlipTitle}";

    private void RemoveBoardCard(string slipId)
    {
        if (!boardCards.TryGetValue(slipId, out var card))
        {
            return;
        }

        boardCards.Remove(slipId);
        boardSlipCards.Remove(slipId);
        if (card.Wrapper.Parent is Panel parent)
        {
            parent.Children.Remove(card.Wrapper);
        }
    }

    // The reusable column shell: header, cards panel, drag wiring, and drop
    // overlays. The reconcile pass owns the changing parts — header text, node
    // DataContext, and the card list.
    private BoardColumnUi CreateBoardColumn(ZetlBucketSnapshot bucket)
    {
        var bucketId = bucket.Id;

        // Header elements
        var titleText = new TextBlock
        {
            Text = bucket.Name,
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var countText = new TextBlock
        {
            Text = "(0)",
            Classes = { "muted" },
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };

        var addCardButton = new Button
        {
            Content = "+",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(addCardButton, "Add a card (Enter adds, Esc closes)");

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
            // Hit-testable everywhere so the whole header (not just its text) can
            // start a column drag; the + button keeps its click.
            Background = Brushes.Transparent
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(countText, 1);
        Grid.SetColumn(addCardButton, 2);
        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(countText);
        headerGrid.Children.Add(addCardButton);

        // Column-header drag reorders columns via ReorderBucket.
        headerGrid.PointerPressed += OnBoardColumnHeaderPointerPressed;
        headerGrid.PointerMoved += OnBoardColumnHeaderPointerMoved;
        headerGrid.PointerReleased += OnBoardColumnHeaderPointerReleased;

        // Cards list; the reconcile pass fills and maintains it.
        var cardsPanel = new StackPanel
        {
            Spacing = 6
        };

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = cardsPanel
        };

        // The inline composer: type a card in place instead of a modal round-trip.
        // Enter adds and keeps composing, Shift+Enter inserts a newline, Esc
        // discards, and leaving the box commits any typed text so it is never lost.
        var composerBox = new TextBox
        {
            Watermark = "New card",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        var composer = new Border
        {
            Background = ThemeBrush("ZetlSurfaceAltBrush"),
            BorderBrush = ThemeBrush("ZetlAccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 8, 0, 0),
            Child = composerBox,
            IsVisible = false
        };

        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        Grid.SetRow(headerGrid, 0);
        Grid.SetRow(scrollViewer, 1);
        Grid.SetRow(composer, 2);
        mainGrid.Children.Add(headerGrid);
        mainGrid.Children.Add(scrollViewer);
        mainGrid.Children.Add(composer);

        // The border inherits its KastnTreeNode DataContext from the wrapper, which
        // the reconcile pass repoints at the fresh tree node on every refresh.
        var columnBorder = new Border
        {
            Tag = "dragRow",
            Background = ThemeBrush("ZetlSurfaceBrush"),
            BorderBrush = ThemeBrush("ZetlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Width = 280,
            Padding = new Thickness(8),
            Child = mainGrid,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Hook up drop events on the column border so users can drop cards onto empty column space
        DragDrop.SetAllowDrop(columnBorder, true);
        columnBorder.AddHandler(DragDrop.DragOverEvent, OnBoardDragOver);
        columnBorder.AddHandler(DragDrop.DragLeaveEvent, OnBoardDragLeave);
        columnBorder.AddHandler(DragDrop.DropEvent, OnBoardDrop);

        boardColumnCardPanels[bucketId] = cardsPanel;

        // Drag feedback overlays, bound to the same node flags the tree rows bind:
        // an accent outline while cards would drop into this column, and vertical
        // insertion lines at the left/right edges while a dragged column would land
        // before/after it.
        var columnWrapper = new Grid();
        columnWrapper.Children.Add(columnBorder);
        var intoOverlay = new Border
        {
            BorderBrush = ThemeBrush("ZetlAccentBrush"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
            IsVisible = false
        };
        intoOverlay.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(KastnTreeNode.IsDropTarget)));
        columnWrapper.Children.Add(intoOverlay);
        columnWrapper.Children.Add(CreateBoardDropLine(horizontal: false, atStart: true, nameof(KastnTreeNode.ShowDropBefore)));
        columnWrapper.Children.Add(CreateBoardDropLine(horizontal: false, atStart: false, nameof(KastnTreeNode.ShowDropAfter)));

        var column = new BoardColumnUi
        {
            Wrapper = columnWrapper,
            TitleText = titleText,
            CountText = countText,
            CardsPanel = cardsPanel,
            Composer = composer,
            ComposerBox = composerBox
        };

        addCardButton.Click += (_, _) =>
        {
            composer.IsVisible = true;
            composerBox.Focus();
        };
        composerBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseBoardComposer(column);
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                await CommitBoardComposerAsync(bucketId, column, keepOpen: true);
            }
        }, RoutingStrategies.Tunnel);
        composerBox.LostFocus += async (_, _) =>
        {
            // Leaving the box commits typed text (capture must never silently drop
            // it) and closes; an empty composer just closes. A failed commit keeps
            // the composer open so the text stays recoverable.
            if (string.IsNullOrWhiteSpace(composerBox.Text))
            {
                CloseBoardComposer(column);
                return;
            }

            if (await CommitBoardComposerAsync(bucketId, column, keepOpen: false))
            {
                CloseBoardComposer(column);
            }
        };

        return column;
    }

    private static void CloseBoardComposer(BoardColumnUi column)
    {
        column.ComposerBox.Text = "";
        column.Composer.IsVisible = false;
    }

    // Send the composer text as a new slip at the end of the bucket. Returns true
    // when the card was added (or there was nothing to add); the composer clears
    // and, for Enter-to-add, stays focused for the next card.
    private async Task<bool> CommitBoardComposerAsync(string bucketId, BoardColumnUi column, bool keepOpen)
    {
        if (column.ComposerBusy)
        {
            return false;
        }

        var text = (column.ComposerBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return true;
        }

        if (!IsOnline || currentProject is null)
        {
            statusText.Text = "Connect to Zetl to edit.";
            return false;
        }

        column.ComposerBusy = true;
        try
        {
            var response = await ExecuteMutationAsync(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.AddSlip,
                new AddSlipCommand { BucketId = bucketId, Text = text, Source = "kastn" },
                currentProject.Id));
            if (response.Status != ZetlResponseStatus.Success)
            {
                statusText.Text = response.Error?.Message ?? $"Add failed: {response.Status}.";
                return false;
            }

            column.ComposerBox.Text = "";
            await connection.SynchronizeAsync();
            statusText.Text = "Card added.";
            if (keepOpen)
            {
                column.ComposerBox.Focus();
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            statusText.Text = ex.Message;
            return false;
        }
        finally
        {
            column.ComposerBusy = false;
        }
    }

    // An accent insertion line overlaying one edge of a board card or column,
    // visibility-bound to the node's drop-edge state: horizontal lines mark card
    // slots (top/bottom), vertical lines mark column slots (left/right).
    private Control CreateBoardDropLine(bool horizontal, bool atStart, string visibilityProperty)
    {
        var line = new Border
        {
            Background = ThemeBrush("ZetlAccentBrush"),
            IsHitTestVisible = false,
            IsVisible = false
        };
        if (horizontal)
        {
            line.Height = 2;
            line.VerticalAlignment = atStart ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        }
        else
        {
            line.Width = 3;
            line.HorizontalAlignment = atStart ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        line.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(visibilityProperty));
        return line;
    }

    // The reusable card: rebuilt only when its render key changes, so the closures
    // below always capture a snapshot equivalent to what is displayed. The node
    // DataContext lives on the wrapper and is repointed by the reconcile pass.
    private BoardCardUi CreateBoardCard(ZetlSlipSnapshot slip, string renderKey)
    {
        var contentRenderer = CreateSlipContentRenderer();
        var previewText = KastnSlipContentRenderer.CreateBoardPreview(slip, UntitledSlipTitle);

        var listKinds = currentProject is null
            ? new ZetlSlipListKinds("", "", "")
            : ZetlViewRenderer.ResolveListKinds(
                currentProject, slip, CurrentAppSettings().KastnPreferSlipKindOverBucketKind);

        // Footer layout (contains tags/markers/checkbox)
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 4, 0, 0)
        };

        void AddFooterMarker(string kind)
        {
            if (contentRenderer.CreateBoardListMarker(kind, slip) is { } marker)
                footer.Children.Add(marker);
        }

        AddFooterMarker(listKinds.Outer);
        AddFooterMarker(listKinds.Inner);

        // Capture origin icon/text if present
        if (slip.CaptureOrigin is { } origin)
        {
            var appName = !string.IsNullOrWhiteSpace(origin.ApplicationName) ? origin.ApplicationName : origin.ProcessName;
            if (!string.IsNullOrWhiteSpace(appName))
            {
                footer.Children.Add(new TextBlock
                {
                    Text = $"[{appName}]",
                    Classes = { "muted" },
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
        }

        var mainPanel = new StackPanel { Spacing = 4 };

        // A text-presenting dual slip keeps its text as the card content and adds
        // a small thumbnail on the right; clicking the thumbnail peeks the
        // attached picture below the text. The peek is view state only — the
        // slip's preferred representation is unchanged.
        (Image Image, Image? ExpandedImage, TextBlock Status)? pendingPictureLoad = null;
        if (slip.Type != ZetlSlipType.Picture && slip.Picture is not null)
        {
            var thumbnail = new Image
            {
                MaxWidth = 44,
                MaxHeight = 44,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            ToolTip.SetTip(thumbnail, "Show the attached picture");
            previewText.Margin = new Thickness(0, 0, 6, 4);
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(previewText, 0);
            Grid.SetColumn(thumbnail, 1);
            header.Children.Add(previewText);
            header.Children.Add(thumbnail);
            mainPanel.Children.Add(header);

            var expandedImage = new Image
            {
                MaxWidth = 240,
                MaxHeight = 160,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsVisible = expandedBoardPictures.Contains(slip.Id)
            };
            var pictureStatus = new TextBlock
            {
                Text = "Loading...",
                Classes = { "muted" },
                FontSize = 11,
                IsVisible = false
            };
            mainPanel.Children.Add(expandedImage);
            mainPanel.Children.Add(pictureStatus);

            thumbnail.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                var expanded = !expandedBoardPictures.Remove(slip.Id);
                if (expanded)
                {
                    expandedBoardPictures.Add(slip.Id);
                }

                expandedImage.IsVisible = expanded;
            };

            if (CachedDecodedPicture(slip, 260) is { } cachedDual)
            {
                thumbnail.Source = cachedDual;
                expandedImage.Source = cachedDual;
            }
            else
            {
                pendingPictureLoad = (thumbnail, expandedImage, pictureStatus);
            }
        }
        else
        {
            mainPanel.Children.Add(previewText);
        }

        // Start loading once the card exists so retired-card guards can reject
        // stale assignments. Decoded bitmaps remain owned by KastnPictureCache.
        if (slip.Type == ZetlSlipType.Picture && slip.Picture is not null)
        {
            var image = new Image
            {
                MaxWidth = 240,
                MaxHeight = 120,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var statusText = new TextBlock
            {
                Text = "Loading...",
                Classes = { "muted" },
                FontSize = 11
            };
            mainPanel.Children.Add(image);
            mainPanel.Children.Add(statusText);
            if (CachedDecodedPicture(slip, 260) is { } cachedThumbnail)
            {
                image.Source = cachedThumbnail;
                statusText.IsVisible = false;
            }
            else
            {
                pendingPictureLoad = (image, null, statusText);
            }
        }

        if (footer.Children.Count > 0)
        {
            mainPanel.Children.Add(footer);
        }

        // The border inherits its KastnTreeNode DataContext from the wrapper, which
        // the reconcile pass repoints at the fresh tree node on every refresh.
        var cardBorder = new Border
        {
            Tag = "dragRow",
            Background = ThemeBrush("ZetlSurfaceAltBrush"),
            BorderBrush = ThemeBrush("ZetlBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = mainPanel,
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        // Selection click
        cardBorder.PointerPressed += (sender, args) =>
        {
            ReselectSlipNode(slip.Id);
        };

        // Setup dragging for the card
        cardBorder.PointerPressed += OnBoardCardPointerPressed;
        cardBorder.PointerMoved += OnBoardCardPointerMoved;
        cardBorder.PointerReleased += OnBoardCardPointerReleased;

        // Double click editor
        cardBorder.DoubleTapped += async (sender, args) =>
        {
            await EditBoardSlipAsync(slip);
        };

        // Setup drop target on the card (to reorder slips)
        DragDrop.SetAllowDrop(cardBorder, true);
        cardBorder.AddHandler(DragDrop.DragOverEvent, OnBoardDragOver);
        cardBorder.AddHandler(DragDrop.DragLeaveEvent, OnBoardDragLeave);
        cardBorder.AddHandler(DragDrop.DropEvent, OnBoardDrop);

        // Insertion lines at the card's top/bottom edges, bound to the same node
        // flags the tree rows bind, so a drag shows exactly where the card lands.
        var cardWrapper = new Grid();
        cardWrapper.Children.Add(cardBorder);
        cardWrapper.Children.Add(CreateBoardDropLine(horizontal: true, atStart: true, nameof(KastnTreeNode.ShowDropBefore)));
        cardWrapper.Children.Add(CreateBoardDropLine(horizontal: true, atStart: false, nameof(KastnTreeNode.ShowDropAfter)));

        return new BoardCardUi
        {
            Wrapper = cardWrapper,
            CardBorder = cardBorder,
            RenderKey = renderKey,
            PendingPictureLoad = pendingPictureLoad
        };
    }

    private void UpdateBoardSelectionHighlight(bool scrollIntoView = true)
    {
        if (highlightedBoardSlipId is not null
            && boardSlipCards.TryGetValue(highlightedBoardSlipId, out var previous))
        {
            previous.BorderBrush = ThemeBrush("ZetlBorderBrush");
        }

        highlightedBoardSlipId = null;
        var selectedId = SelectedTreeNode?.Slip?.Id ?? editorState.SlipId;
        if (selectedId is null || !boardSlipCards.TryGetValue(selectedId, out var card))
        {
            return;
        }

        card.BorderBrush = ThemeBrush("ZetlAccentBrush");
        highlightedBoardSlipId = selectedId;

        if (scrollIntoView)
        {
            card.BringIntoView();
            Dispatcher.UIThread.Post(card.BringIntoView, DispatcherPriority.Background);
        }
    }

    // Load a board card's thumbnail into the shared decoded-picture cache. A load
    // that outlives its card — the card was replaced while the bytes were in
    // flight — drops its assignment; the decoded bitmap stays cached either way,
    // so the replacement card picks it up synchronously.
    private async Task LoadBoardCardPictureAsync(
        ZetlSlipSnapshot slip,
        BoardCardUi card,
        Avalonia.Controls.Image image,
        Avalonia.Controls.Image? expandedImage,
        TextBlock status)
    {
        bool IsCurrent() =>
            boardCards.TryGetValue(slip.Id, out var current) && ReferenceEquals(current, card);

        // A dual card's status starts hidden (its thumbnail is a side affordance,
        // not the card content), so a failure must reveal it to be seen.
        void ReportUnavailable()
        {
            status.Text = "Picture unavailable.";
            status.IsVisible = true;
        }

        try
        {
            var content = await GetPictureContentAsync(slip);
            if (!IsCurrent())
            {
                return;
            }

            if (content is null)
            {
                ReportUnavailable();
                return;
            }

            // 260: smaller decode width for board card thumbnails.
            var bitmap = pictureCache.Decode(content, 260);
            if (!IsCurrent())
            {
                return;
            }

            image.Source = bitmap;
            if (expandedImage is not null)
            {
                expandedImage.Source = bitmap;
            }

            status.IsVisible = false;
        }
        catch (Exception ex) when (
            KastnPictureCache.IsLoadFailure(ex))
        {
            if (IsCurrent())
            {
                ReportUnavailable();
            }
        }
    }

    // ---- Creation types (template + default view) ----
}
