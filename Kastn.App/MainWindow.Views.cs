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
    private void RefreshViewer()
    {
        if (currentProject is null)
        {
            viewerSummaryText.Text = "No project selected.";
            viewerTextBox.Text = "";
            ClearPictureDocument();
            RefreshSlipInspector([]);
            return;
        }

        var visible = CurrentFilteredSlips();
        RefreshSlipInspector(visible);
        var hasPictures = visible.Any(slip => slip.Type == ZetlSlipType.Picture);
        viewerSummaryText.Text = visible.Count == 0
            ? "No slips match the current filters."
            : $"{visible.Count} of {currentProject.Slips.Count} slips in the current view.";
        viewerTextBox.IsVisible = !hasPictures;
        viewerDocumentScroll.IsVisible = hasPictures;
        if (hasPictures)
        {
            BuildPictureDocument(visible);
        }
        else
        {
            ClearPictureDocument();
        }

        if (SelectedView.Kind == ZetlViewKinds.Pdf)
        {
            // PDF is binary — there is no inline text to show or copy; the preview
            // explains how to get it, and Export writes the .pdf.
            lastRenderedViewText = "";
            viewerTextBox.Text = visible.Count == 0
                ? ""
                : "PDF view — use Export to save a .pdf of the current slips.";
            copyViewButton.IsEnabled = false;
            exportViewButton.IsEnabled = visible.Count > 0;
        }
        else
        {
            lastRenderedViewText = visible.Count == 0
                ? ""
                : ZetlViewRenderer.Render(currentProject, visible, SelectedView);
            viewerTextBox.Text = lastRenderedViewText;
            var hasOutput = lastRenderedViewText.Length > 0;
            copyViewButton.IsEnabled = hasOutput;
            exportViewButton.IsEnabled = hasOutput;
        }

        deleteViewMenuItem.IsEnabled = !ZetlViewDefaults.IsBuiltIn(SelectedView.Id);
    }

    private void RefreshSlipInspector(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        var selected = SelectedTreeNode?.Slip;
        if (selected is not null && visible.All(slip => slip.Id != selected.Id))
        {
            selected = null;
        }

        inspectedSlipId = selected?.Id;
        RenderSlipInspector(selected);
    }

    private void InspectSlip(string slipId)
    {
        inspectedSlipId = slipId;
        RenderSlipInspector(currentProject?.Slips.FirstOrDefault(slip => slip.Id == slipId));
    }

    private void RenderSlipInspector(ZetlSlipSnapshot? slip)
    {
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

        foreach (var section in KastnSlipInspector.Build(currentProject, slip))
        {
            slipInspectorFieldsPanel.Children.Add(new TextBlock
            {
                Text = section.Heading,
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 8, 0, 0)
            });
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

    private void BuildPictureDocument(IReadOnlyList<ZetlSlipSnapshot> visible)
    {
        var generation = ++pictureRenderGeneration;
        DisposeDisplayedPictures();
        viewerDocumentPanel.Children.Clear();
        if (currentProject is null)
        {
            return;
        }

        foreach (var group in ZetlViewRenderer.BuildGroups(currentProject, visible, SelectedView))
        {
            viewerDocumentPanel.Children.Add(new TextBlock
            {
                Text = group.Heading,
                FontSize = Math.Max(15, 21 - group.Depth),
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(group.Depth * 14, 8, 0, 2)
            });

            foreach (var slip in group.Slips)
            {
                if (slip.Type != ZetlSlipType.Picture)
                {
                    viewerDocumentPanel.Children.Add(new TextBlock
                    {
                        Text = $"• {(string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text)}",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness((group.Depth + 1) * 14, 0, 0, 0)
                    });
                    continue;
                }

                var image = new Avalonia.Controls.Image
                {
                    Stretch = Stretch.Uniform,
                    MaxHeight = 520,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var loading = new TextBlock
                {
                    Text = "Loading picture…",
                    Classes = { "muted" },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var preview = new Grid
                {
                    MinHeight = 150,
                    Children = { image, loading }
                };
                var picturePanel = new StackPanel
                {
                    Spacing = 5,
                    Margin = new Avalonia.Thickness((group.Depth + 1) * 14, 0, 0, 6),
                    Children =
                    {
                        new Border
                        {
                            Classes = { "surface" },
                            Padding = new Avalonia.Thickness(8),
                            Child = preview
                        }
                    }
                };
                var pictureLabel = string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text;
                if (!string.IsNullOrWhiteSpace(pictureLabel))
                {
                    picturePanel.Children.Add(new TextBlock
                    {
                        Text = pictureLabel.Trim(),
                        Classes = { "muted" },
                        FontStyle = FontStyle.Italic,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                viewerDocumentPanel.Children.Add(picturePanel);
                _ = LoadPicturePreviewAsync(slip, image, loading, generation);
            }
        }
    }

    private async Task LoadPicturePreviewAsync(
        ZetlSlipSnapshot slip,
        Avalonia.Controls.Image image,
        TextBlock status,
        int generation)
    {
        try
        {
            var content = await GetPictureContentAsync(slip);
            if (generation != pictureRenderGeneration || content is null)
            {
                if (generation == pictureRenderGeneration)
                {
                    status.Text = "Picture unavailable.";
                }
                return;
            }

            using var stream = new MemoryStream(content.Bytes, writable: false);
            var bitmap = Bitmap.DecodeToWidth(stream, 1100);
            if (generation != pictureRenderGeneration)
            {
                bitmap.Dispose();
                return;
            }

            displayedPictureBitmaps.Add(bitmap);
            image.Source = bitmap;
            status.IsVisible = false;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (generation == pictureRenderGeneration)
            {
                status.Text = "Picture unavailable.";
            }
        }
    }

    private async Task<ZetlPictureContent?> GetPictureContentAsync(ZetlSlipSnapshot slip)
    {
        if (currentProject is null || slip.Picture is null)
        {
            return null;
        }

        var cacheKey = slip.Picture.Sha256;
        if (pictureCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (!pictureLoads.TryGetValue(cacheKey, out var loading))
        {
            loading = FetchPictureContentAsync(currentProject.Id, slip, cacheKey);
            pictureLoads[cacheKey] = loading;
        }

        try
        {
            return await loading;
        }
        finally
        {
            pictureLoads.Remove(cacheKey);
        }
    }

    private async Task<ZetlPictureContent?> FetchPictureContentAsync(
        string projectId,
        ZetlSlipSnapshot slip,
        string cacheKey)
    {
        var response = await connection.QueryAsync(new ZetlCommandEnvelope
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Kind = ZetlCommandKind.GetSlipPicture,
            ProjectId = projectId,
            TargetId = slip.Id
        });
        var content = response.Status == ZetlResponseStatus.Success
            ? response.Payload?.Deserialize<ZetlPictureContent>(ZetlProtocolJson.Options)
            : null;
        if (content is null
            || content.Bytes.Length == 0
            || !string.Equals(content.Sha256, cacheKey, StringComparison.Ordinal))
        {
            return null;
        }

        CachePicture(cacheKey, content);
        return content;
    }

    private void CachePicture(string cacheKey, ZetlPictureContent content)
    {
        if (content.Bytes.LongLength > MaximumPictureCacheBytes)
        {
            return;
        }

        while (pictureCacheBytes + content.Bytes.LongLength > MaximumPictureCacheBytes
            && pictureCacheOrder.TryDequeue(out var expired))
        {
            if (pictureCache.Remove(expired, out var removed))
            {
                pictureCacheBytes -= removed.Bytes.LongLength;
            }
        }

        if (pictureCache.TryAdd(cacheKey, content))
        {
            pictureCacheOrder.Enqueue(cacheKey);
            pictureCacheBytes += content.Bytes.LongLength;
        }
    }

    private async Task<IReadOnlyDictionary<string, ZetlPictureContent>> LoadPictureContentsAsync(
        IReadOnlyList<ZetlSlipSnapshot> slips)
    {
        var result = new Dictionary<string, ZetlPictureContent>(StringComparer.Ordinal);
        foreach (var slip in slips.Where(slip => slip.Type == ZetlSlipType.Picture))
        {
            try
            {
                if (await GetPictureContentAsync(slip) is { } content)
                {
                    result[slip.Id] = content;
                }
            }
            catch (Exception ex) when (
                ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                // Keep rendering the rest; unavailable pictures get a readable placeholder.
            }
        }

        return result;
    }

    private void ClearPictureDocument()
    {
        pictureRenderGeneration++;
        viewerDocumentPanel.Children.Clear();
        viewerDocumentScroll.IsVisible = false;
        DisposeDisplayedPictures();
    }

    private void DisposeDisplayedPictures()
    {
        foreach (var bitmap in displayedPictureBitmaps)
        {
            bitmap.Dispose();
        }
        displayedPictureBitmaps.Clear();
    }

    private ZetlViewDocument SelectedView =>
        viewPickerBox.SelectedItem as ZetlViewDocument
        ?? (loadedViews.Count > 0 ? loadedViews[0] : ZetlViewDefaults.CreateAll()[0]);

    private void SelectViewForProject(ZetlProjectSnapshot project)
    {
        var target = string.IsNullOrEmpty(project.DefaultViewId)
            ? null
            : loadedViews.FirstOrDefault(view => view.Id == project.DefaultViewId);
        viewPickerBox.SelectedItem = target ?? loadedViews.FirstOrDefault();
    }

    private async Task CopyRenderedViewAsync()
    {
        if (currentProject is null
            || lastRenderedViewText.Length == 0
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var visible = CurrentFilteredSlips();
        var pictures = await LoadPictureContentsAsync(visible);
        var rendered = ZetlViewRenderer.Render(currentProject, visible, SelectedView, pictures);
        await clipboard.SetTextAsync(rendered);
        statusText.Text = $"Copied the {SelectedView.Name} view to the clipboard.";
    }

    private async Task ExportRenderedViewAsync()
    {
        if (currentProject is null)
        {
            return;
        }

        var view = SelectedView;
        var isPdf = view.Kind == ZetlViewKinds.Pdf;
        if (!isPdf && lastRenderedViewText.Length == 0)
        {
            return;
        }

        var extension = ViewFileExtension(view.Kind);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {view.Name}",
            SuggestedFileName = $"{SafeFileName(currentProject.Name)}.{extension}",
            DefaultExtension = extension
        });
        if (file is null)
        {
            return;
        }

        try
        {
            var visible = CurrentFilteredSlips();
            var pictures = await LoadPictureContentsAsync(visible);
            await using var stream = await file.OpenWriteAsync();
            if (isPdf)
            {
                var pdf = KastnPdfRenderer.Render(currentProject, visible, view, pictures);
                await stream.WriteAsync(pdf);
            }
            else
            {
                var rendered = ZetlViewRenderer.Render(currentProject, visible, view, pictures);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(rendered);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            statusText.Text = $"Could not export the view: {ex.Message}";
            return;
        }

        statusText.Text = $"Exported the {view.Name} view to {file.Name}.";
    }

    private static string ViewFileExtension(string kind) => kind switch
    {
        ZetlViewKinds.Markdown => "md",
        ZetlViewKinds.Html => "html",
        ZetlViewKinds.Pdf => "pdf",
        ZetlViewKinds.Tsv => "tsv",
        _ => "txt"
    };

    private static string SafeFileName(string name)
    {
        var cleaned = string.Concat(name.Trim().Select(character =>
            Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '-' : character));
        return string.IsNullOrWhiteSpace(cleaned) ? "project" : cleaned;
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
        viewErrorText.IsVisible = false;

        viewEditorUpdating = true;
        viewEditorTitle.Text = isNew ? "New View" : $"Edit View — {working.Name}";
        viewNameBox.Text = working.Name;
        viewCategoryBox.Text = working.Category;
        viewDescriptionBox.Text = working.Description;
        viewKindBox.SelectedItem = ViewKindChoices.Contains(working.Kind)
            ? working.Kind
            : ZetlViewKinds.Formatted;
        viewTsvRowBox.Value = Math.Clamp(working.TsvRowLength, 1, 100);
        viewSectionsBox.Text = SectionsToText(working.Sections);
        viewEditorUpdating = false;

        ApplyViewKindTsvVisibility();
        viewBaselineJson = CurrentViewJson();

        emptyState.IsVisible = false;
        projectView.IsVisible = false;
        templateEditorView.IsVisible = false;
        viewEditorView.IsVisible = true;
    }

    private void ApplyViewKindTsvVisibility()
    {
        viewTsvPanel.IsVisible = (viewKindBox.SelectedItem as string) == ZetlViewKinds.Tsv;
    }

    private string CurrentViewJson()
    {
        if (editingView is null)
        {
            return "";
        }

        var doc = ZetlViewDefaults.Clone(editingView);
        doc.Name = viewNameBox.Text?.Trim() ?? "";
        doc.Category = string.IsNullOrWhiteSpace(viewCategoryBox.Text)
            ? "Custom"
            : viewCategoryBox.Text.Trim();
        doc.Description = viewDescriptionBox.Text?.Trim() ?? "";
        doc.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        doc.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        doc.Sections = ParseSections(viewSectionsBox.Text);
        return JsonSerializer.Serialize(doc, JsonFile.Options);
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

    private void SaveView()
    {
        if (editingView is not { } view)
        {
            return;
        }

        view.Name = viewNameBox.Text?.Trim() ?? "";
        view.Category = string.IsNullOrWhiteSpace(viewCategoryBox.Text)
            ? "Custom"
            : viewCategoryBox.Text.Trim();
        view.Description = viewDescriptionBox.Text?.Trim() ?? "";
        view.Kind = viewKindBox.SelectedItem as string ?? ZetlViewKinds.Formatted;
        view.TsvRowLength = (int)(viewTsvRowBox.Value ?? 5);
        view.Sections = ParseSections(viewSectionsBox.Text);
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

        var savedId = view.Id;
        var savedName = view.Name;
        CloseViewEditor();
        ReloadViews(savedId);
        statusText.Text = $"Saved view '{savedName}'.";
    }

    private void CloseViewEditor()
    {
        editingView = null;
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

    // Parse the compact section spec: one section per line, "Heading = Bucket, Bucket".
    // A line without '=' is shorthand for a section named after a single bucket.
    private static List<ZetlViewSection> ParseSections(string? text)
    {
        var sections = new List<ZetlViewSection>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            string title;
            List<string> buckets;
            if (equals >= 0)
            {
                title = line[..equals].Trim();
                buckets = line[(equals + 1)..]
                    .Split(',')
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0)
                    .ToList();
            }
            else
            {
                title = line;
                buckets = [line];
            }

            if (title.Length > 0 && buckets.Count > 0)
            {
                sections.Add(new ZetlViewSection { Title = title, Buckets = buckets });
            }
        }

        return sections;
    }

    private static string SectionsToText(IReadOnlyList<ZetlViewSection> sections)
    {
        return string.Join("\n", sections.Select(section =>
            section.Buckets.Count == 1
                && string.Equals(section.Buckets[0], section.Title, StringComparison.OrdinalIgnoreCase)
                ? section.Title
                : $"{section.Title} = {string.Join(", ", section.Buckets)}"));
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

        try
        {
            viewStore.Delete(view.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            statusText.Text = $"Could not delete view: {ex.Message}";
            return;
        }

        ReloadViews(null);
        statusText.Text = $"Deleted view '{view.Name}'.";
    }

    // Re-read the view catalog from disk, restoring the selection (by id when given,
    // otherwise the first view) and re-rendering the read view.
    private void ReloadViews(string? selectId)
    {
        loadedViews = viewStore.LoadAll();
        viewPickerBox.ItemsSource = loadedViews;
        var target = selectId is null
            ? null
            : loadedViews.FirstOrDefault(view => view.Id == selectId);
        viewPickerBox.SelectedItem = target ?? loadedViews.FirstOrDefault();
        RefreshViewer();
    }

    // ---- Creation types (template + default view) ----
}
