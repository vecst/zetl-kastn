using System.Text;
using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyKeepsCapturedInputsAcrossSequentialPictureLoads(bool switchProject)
    {
        var (window, controller, original) = WorkflowWindow();
        try
        {
            var pictures = new[] { ExportPicture("first"), ExportPicture("second") };
            var project = original with { Slips = [.. original.Slips, .. pictures] };
            PublishRenderSnapshot(controller, project);
            window.searchBox.Text = "baseline";
            var view = SelectExportView(window, ZetlViewKinds.Html);
            var first = new TaskCompletionSource<ZetlPictureContent?>();
            var second = new TaskCompletionSource<ZetlPictureContent?>();
            var requests = new List<string>();
            string? copied = null;
            var copying = InvokeWorkflow(window, "CopyRenderedViewAsync",
                (Func<string, Task>)(text => { copied = text; return Task.CompletedTask; }),
                (Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>>)((projectId, slip) =>
                {
                    requests.Add(projectId);
                    return slip.Id == "first" ? first.Task : second.Task;
                }));
            Assert.Single(requests);
            ChangeExportInputs(window, controller, project, view, switchProject);
            first.SetResult(KastnViewExportTests.Content("first"));
            await WaitForConditionAsync(() => requests.Count == 2, "Second captured picture should load.");
            second.SetResult(KastnViewExportTests.Content("second") with { Bytes = [4, 5, 6] });
            await copying;

            Assert.All(requests, id => Assert.Equal(project.Id, id));
            Assert.Contains("Captured title", copied);
            Assert.Contains("baseline", copied);
            Assert.Contains("data:image/png;base64,AQID", copied);
            Assert.Contains("data:image/png;base64,BAUG", copied);
            Assert.DoesNotContain("second text", copied);
            Assert.DoesNotContain("replacement writing", copied);
            Assert.DoesNotContain("Changed title", copied);
            Assert.Equal("Copied the Captured view view to the clipboard.", window.statusText.Text);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(ZetlViewKinds.Plain)]
    [InlineData(ZetlViewKinds.Markdown)]
    [InlineData(ZetlViewKinds.Html)]
    [InlineData(ZetlViewKinds.Pdf)]
    public async Task ExportKeepsCapturedInputsAcrossPickerAndStreamAwaits(string kind)
    {
        var (window, controller, original) = WorkflowWindow();
        try
        {
            var picture = ExportPicture("diagram");
            var project = original with { Slips = [original.Slips[0] with { Text = "baseline café ✨" }, original.Slips[1], picture] };
            PublishRenderSnapshot(controller, project);
            window.searchBox.Text = "baseline";
            var view = SelectExportView(window, kind);
            var picking = new TaskCompletionSource<KastnViewExportDestination?>();
            var opening = new TaskCompletionSource<Stream>();
            FilePickerSaveOptions? options = null;
            var pictureProjects = new List<string>();
            var streamRequested = false;
            var exporting = InvokeWorkflow(window, "ExportRenderedViewAsync",
                (Func<FilePickerSaveOptions, Task<KastnViewExportDestination?>>)(captured => { options = captured; return picking.Task; }),
                (Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>>)((projectId, slip) =>
                {
                    pictureProjects.Add(projectId);
                    return Task.FromResult<ZetlPictureContent?>(KastnViewExportTests.Content(slip.Id) with
                    {
                        Bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
                    });
                }));
            var extension = kind switch { ZetlViewKinds.Html => "html", ZetlViewKinds.Markdown => "md", ZetlViewKinds.Pdf => "pdf", _ => "txt" };
            Assert.Equal($"{project.Name}.{extension}", options!.SuggestedFileName);
            Assert.Equal(extension, options.DefaultExtension);
            Assert.Equal("Export Captured view", options.Title);
            ChangeExportInputs(window, controller, project, view, switchProject: true);
            picking.SetResult(new("captured-output", () => { streamRequested = true; return opening.Task; }));
            await WaitForConditionAsync(() => streamRequested, "Captured document should reach writing.");
            ChangeExportInputs(window, controller, project, view, switchProject: false);
            using var output = new MemoryStream();
            opening.SetResult(output);
            await exporting;
            var bytes = output.ToArray();
            var text = Encoding.UTF8.GetString(bytes);
            if (kind == ZetlViewKinds.Pdf)
            {
                Assert.StartsWith("%PDF-", text);
                Assert.True(text.Contains("/Subtype/Image") || text.Contains("/Subtype /Image"));
            }
            else
            {
                Assert.Contains("baseline café ✨", text);
                Assert.DoesNotContain("replacement writing", text);
                Assert.DoesNotContain("second text", text);
                Assert.DoesNotContain("Changed title", text);
                if (kind != ZetlViewKinds.Plain)
                {
                    Assert.Contains("Captured title", text);
                    Assert.Contains("data:image/png;base64,", text);
                }
            }
            Assert.Equal(kind == ZetlViewKinds.Plain ? 0 : 1, pictureProjects.Count);
            Assert.All(pictureProjects, id => Assert.Equal(project.Id, id));
            Assert.Equal("Exported the Captured view view to captured-output.", window.statusText.Text);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledExportPickerDoesNotLoadPicturesOrOpenOutput()
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            var status = window.statusText.Text;
            await InvokeWorkflow(window, "ExportRenderedViewAsync",
                (Func<FilePickerSaveOptions, Task<KastnViewExportDestination?>>)(_ => Task.FromResult<KastnViewExportDestination?>(null)),
                (Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>>)((_, _) => throw new InvalidOperationException("Cancelled.")));
            Assert.Equal(status, window.statusText.Text);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAndExportReportDestinationFailures(bool copy)
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetch = (_, _) => Task.FromResult<ZetlPictureContent?>(null);
            if (copy)
            {
                await InvokeWorkflow(window, "CopyRenderedViewAsync", (Func<string, Task>)(_ => throw new IOException("Destination unavailable")), fetch);
            }
            else
            {
                await InvokeWorkflow(window, "ExportRenderedViewAsync",
                    (Func<FilePickerSaveOptions, Task<KastnViewExportDestination?>>)(_ => Task.FromResult<KastnViewExportDestination?>(
                        new("output", () => throw new IOException("Destination unavailable")))), fetch);
            }
            Assert.Contains(copy ? "Could not copy" : "Could not export", window.statusText.Text);
            Assert.Contains("Destination unavailable", window.statusText.Text);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopySkipsPdfAndEmptyFilteredOutput(bool pdf)
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            if (pdf) SelectExportView(window, ZetlViewKinds.Pdf);
            else window.searchBox.Text = "nothing matches this filter";
            Dispatcher.UIThread.RunJobs();
            await InvokeWorkflow(window, "CopyRenderedViewAsync",
                (Func<string, Task>)(_ => throw new InvalidOperationException("No copy expected.")),
                (Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>>)((_, _) => throw new InvalidOperationException("No load expected.")));
            Assert.False(window.copyViewButton.IsEnabled);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public async Task CopyFinishesOriginalImageFetchAfterRealIpcNavigation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnExportIpc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var releaseResponse = new ManualResetEventSlim();
        var pictureArrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        var requests = new ConcurrentQueue<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Original export", ["Inbox"], "Inbox");
            var firstPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            var secondPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jk1sAAAAASUVORK5CYII=");
            var first = store.AddImageSlip(project, project.Buckets[0], new(firstPng, 1, 1), "copy", caption: "Original first");
            var second = store.AddImageSlip(project, project.Buckets[0], new(secondPng, 1, 1), "copy", caption: "Original second");
            var replacement = store.CreateProject("Replacement", ["Inbox"], "Inbox");
            store.AddSlip(replacement.Buckets[0], "replacement writing", "copy");
            var pipeName = $"kastn-export-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.GetSlipPicture)
                    {
                        requests.Enqueue(command);
                        if (pictureArrived.TrySetResult(command)) releaseResponse.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."), pipeName,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");
            window = new MainWindow(controller);
            window.Show();
            SelectExportView(window, ZetlViewKinds.Html);
            var copying = InvokeWorkflow(window, "CopyRenderedViewAsync");
            var query = await pictureArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first.Id, query.TargetId);
            var navigating = controller.NavigateToProjectAsync(replacement.Id);
            PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
            releaseResponse.Set();
            await copying.WaitAsync(TimeSpan.FromSeconds(5));
            await navigating.WaitAsync(TimeSpan.FromSeconds(5));

            var copied = await window.Clipboard!.TryGetTextAsync();
            Assert.Contains(Convert.ToBase64String(firstPng), copied);
            Assert.Contains(Convert.ToBase64String(secondPng), copied);
            Assert.Contains("Original first", copied);
            Assert.Contains("Original second", copied);
            Assert.DoesNotContain("replacement writing", copied);
            Assert.All(requests, request => Assert.Equal(project.Id, request.ProjectId));
            Assert.Single(requests.Where(request => request.TargetId == first.Id));
            Assert.Contains(requests, request => request.TargetId == second.Id);
        }
        finally
        {
            releaseResponse.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ZetlSlipSnapshot ExportPicture(string id) => KastnViewExportTests.Picture(id) with
    {
        BucketId = "render-bucket", Text = $"baseline {id}"
    };

    private static ZetlViewDocument SelectExportView(MainWindow window, string kind)
    {
        var view = new ZetlViewDocument { Id = "captured", Name = "Captured view", Kind = kind, Title = "Captured title" };
        window.viewPickerBox.ItemsSource = new[] { view };
        window.viewPickerBox.SelectedItem = view;
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static void ChangeExportInputs(MainWindow window, KastnConnectionController controller,
        ZetlProjectSnapshot project, ZetlViewDocument view, bool switchProject)
    {
        view.Name = "Changed view";
        view.Title = "Changed title";
        view.Kind = ZetlViewKinds.Plain;
        PublishRenderSnapshot(controller, RenderProject(switchProject ? "replacement" : project.Id, "replacement writing"));
        SelectExportView(window, ZetlViewKinds.Plain);
        window.searchBox.Text = "replacement";
        Dispatcher.UIThread.RunJobs();
    }
}
