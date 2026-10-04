using System.Diagnostics;
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
        if (lifetime.IsRetired) return;
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
        var settings = this.settings.Current;
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
        var selected = currentProject is not null
            && CurrentSelection() is KastnSelection.Slips { SlipIds: [var id] }
                ? ProjectIndex.Slip(id) : null;
        RenderSlipInspector(selected);
    }

    private void RenderSlipInspector(ZetlSlipSnapshot? slip)
    {
        if (!detailShowingMetadata || boardModeActive)
        {
            inspectorPresenter.Suspend(currentProject?.Id, slip is not null);
            return;
        }
        inspectorPresenter.Render(currentProject is null ? null : ProjectIndex, slip,
            CaptureSlipSelectionAction(), OpenInspectorUrl);
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
        viewPickerBox.SelectedItem as ZetlViewDocument ?? viewCatalog.Select(null);

    private void SelectViewForProject(ZetlProjectSnapshot project) =>
        viewPickerBox.SelectedItem = viewCatalog.SelectDefault(project, this.settings.Current.KastnDefaultViewId);

    private void RefreshViewCatalog(ZetlProjectSnapshot? project, string? selectId = null, bool force = true)
    {
        if (!viewCatalog.Refresh(project, force)) return;
        var wasRefreshing = refreshing;
        refreshing = true;
        try
        {
            viewPickerBox.ItemsSource = viewCatalog.Views;
            viewPickerBox.SelectedItem = viewCatalog.Select(selectId);
        }
        finally { refreshing = wasRefreshing; }
    }

    private KastnViewExportOperation? CaptureViewExportOperation() =>
        currentProject is null ? null : new(currentProject, CurrentViewSlips(), SelectedView, this.settings.Current);

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

}
