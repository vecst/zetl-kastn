using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("unchanged")]
    [InlineData("typing")]
    [InlineData("section")]
    [InlineData("new-editor")]
    [InlineData("navigation")]
    [InlineData("away-back")]
    [InlineData("conflict")]
    public async Task ViewSavesPreserveLaterDraftsAndNeverCompleteIntoAnotherProjectSession(string interruption)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnViewSaveUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        var commands = 0;
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Authoring", ["Inbox", "Next"], "Inbox");
            store.AddSlip(project.Buckets[0], "baseline", "copy");
            var view = ViewForUi("authoring", "Original", true);
            store.SaveProjectView(project, view);
            project.DefaultViewId = view.Id;
            var replacement = store.CreateProject("Replacement", ["Inbox"], "Inbox");
            store.SaveProjectView(replacement, ViewForUi(view.Id, "Replacement view", true));
            var pipe = $"kastn-view-save-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipe, log: null, dropResponseForTesting: command =>
            {
                if (command.Kind == ZetlCommandKind.SaveProjectView)
                {
                    Interlocked.Increment(ref commands);
                    if (arrived.TrySetResult(command)) release.Wait(TimeSpan.FromSeconds(8));
                }
                return false;
            });
            server.Start();
            await using var controller = new KastnConnectionController(_ => throw new InvalidOperationException("Already running."),
                pipe, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");
            window = new MainWindow(controller, new KastnDraftStore(Path.Combine(directory, "draft.json")),
                new ZetlViewStore(Path.Combine(directory, "views")));
            window.Show();
            OpenViewForUi(window, view);
            var presenter = WindowField<KastnViewEditorPresenter>(window, "viewEditor");
            var originalSession = presenter.State;
            window.viewNameBox.Text = "Submitted";
            Dispatcher.UIThread.RunJobs();
            if (interruption == "conflict") project.MetadataRevision++;
            var saving = InvokeWorkflow(window, "SaveViewAsync");
            var command = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(project.Id, command.ProjectId);
            Assert.False(window.saveViewSettingsButton.IsEnabled);
            if (interruption == "typing") window.viewNameBox.Text = "Later writing";
            else if (interruption == "section")
            {
                presenter.State!.Sections[0].Title = "Later heading";
                presenter.RefreshPreview();
            }
            else if (interruption == "new-editor") OpenViewForUi(window, ViewForUi("replacement-editor", "New draft", false));
            // Exercise an authoritative snapshot arriving ahead of its response.
            PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
            Assert.True(window.viewEditorView.IsVisible);
            Task? navigation = null;
            if (interruption is "navigation" or "away-back")
            {
                navigation = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                Assert.Empty(window.viewLivePreviewPanel.Children);
                if (interruption == "away-back")
                {
                    PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                    // IDs match again, but this is a retired project session.
                    Assert.Empty(window.viewLivePreviewPanel.Children);
                }
            }
            await InvokeWorkflow(window, "SaveViewAsync");
            release.Set();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, commands);
            Assert.True(window.saveViewSettingsButton.IsEnabled);
            Assert.Equal(interruption == "conflict" ? "Original" : "Submitted", project.Views.Single().Name);
            Assert.Equal("Replacement view", replacement.Views.Single().Name);
            if (interruption == "unchanged")
            {
                Assert.Null(presenter.State);
                Assert.False(window.viewEditorView.IsVisible);
                Assert.Equal(view.Id, Assert.IsType<ZetlViewDocument>(window.viewPickerBox.SelectedItem).Id);
            }
            else
            {
                Assert.NotNull(presenter.State);
                Assert.True(window.viewEditorView.IsVisible);
                Assert.Equal(interruption == "typing" ? "Later writing" : interruption == "new-editor" ? "New draft" : "Submitted", presenter.Capture()!.Name);
                Assert.Equal(interruption != "new-editor", presenter.State!.IsDirty);
                if (interruption == "section") Assert.Equal("Later heading", presenter.Capture()!.Sections[0].Title);
                if (interruption == "conflict") Assert.True(window.viewErrorText.IsVisible);
                if (interruption is "typing" or "section")
                {
                    Assert.Same(originalSession, presenter.State);
                    Assert.True(presenter.State!.ProjectScoped);
                }
            }
        }
        finally
        {
            release.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData("unchanged")]
    [InlineData("typing")]
    [InlineData("new-editor")]
    public async Task ViewDiscardConfirmationOnlyClosesTheDraftItConfirmed(string interruption)
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            OpenViewForUi(window, ViewForUi("draft", "Original", false));
            window.viewNameBox.Text = "Unsaved";
            Dispatcher.UIThread.RunJobs();
            var confirm = new TaskCompletionSource<bool>();
            var cancelling = InvokeWorkflow(window, "CancelViewEditAsync", (Func<Task<bool>>)(() => confirm.Task));
            if (interruption == "typing") window.viewNameBox.Text = "Later writing";
            else if (interruption == "new-editor") OpenViewForUi(window, ViewForUi("another", "New draft", false));
            Dispatcher.UIThread.RunJobs();
            confirm.SetResult(true);
            await cancelling;
            var presenter = WindowField<KastnViewEditorPresenter>(window, "viewEditor");
            if (interruption == "unchanged") Assert.Null(presenter.State);
            else Assert.Equal(interruption == "typing" ? "Later writing" : "New draft", presenter.Capture()!.Name);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void ViewEditorOwnsStructureAndPreviewAndRetiresRebuiltSectionControls()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            var view = ViewForUi("draft", "Original", true);
            var bucket = project.Buckets.First(bucket => !KastnWorkbench.IsDeletedBucket(bucket)).Name;
            view.Sections[0].Buckets = [bucket];
            view.Sections.Add(new() { Title = "Second", Buckets = [bucket], HeadingLevel = 3, HeadingAlign = "right", HeadingBold = true });
            OpenViewForUi(window, view);
            var presenter = WindowField<KastnViewEditorPresenter>(window, "viewEditor");
            Assert.False(presenter.State!.IsDirty);
            var preview = window.viewLivePreviewPanel.Children.ToArray();
            presenter.RefreshPreview();
            Assert.Equal(preview, window.viewLivePreviewPanel.Children);
            var firstCard = window.viewSectionEditorPanel.Children[0];
            var retiredTitle = firstCard.GetVisualDescendants().OfType<TextBox>().Single(box => Equals(box.Watermark, "Section heading"));
            var down = firstCard.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "↓"));
            down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Second", presenter.Capture()!.Sections[0].Title);
            retiredTitle.Text = "Retired title";
            down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "Second", "Inbox" }, presenter.Capture()!.Sections.Select(section => section.Title));
            window.viewAllBucketsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(presenter.Capture()!.Sections);
            window.viewCustomSectionsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, presenter.Capture()!.Sections[0].HeadingLevel);
            window.viewTsvKindButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.viewTsvPanel.IsVisible);
            Assert.False(window.viewDocumentSettingsPanel.IsVisible);
            window.viewPdfKindButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.viewTsvPanel.IsVisible);
            Assert.True(window.viewDocumentSettingsPanel.IsVisible);
            PublishRenderSnapshot(controller, project with { ChangeSequence = project.ChangeSequence + 1, Slips = [project.Slips[0] with { Text = "Live snapshot content" }] });
            window.UpdateLayout();
            Assert.Contains(window.viewLivePreviewPanel.GetVisualDescendants().OfType<TextBlock>(), text => ContentText(text) == "Live snapshot content");
            Assert.Equal("Original", view.Name);
            Assert.Equal("Inbox", view.Sections[0].Title);
            OpenViewForUi(window, ViewForUi("next", "Next editor", false));
            retiredTitle.Text = "Another stale event";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Next editor", presenter.Capture()!.Name);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewDeleteConfirmationCannotDeleteAReusedIdInAnotherProject(bool returnToOriginal)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            var view = ViewForUi("same-view", "Original", true);
            project = project with { MetadataRevision = project.MetadataRevision + 1, Views = [ZetlProjectSnapshotMapper.ToSnapshot(view)] };
            PublishRenderSnapshot(controller, project);
            window.viewPickerBox.SelectedItem = WindowField<KastnViewCatalog>(window, "viewCatalog").Find(view.Id);
            var confirmed = new TaskCompletionSource<bool>();
            var deleting = InvokeWorkflow(window, "DeleteSelectedViewAsync", (Func<ZetlViewDocument, Task<bool>>)(_ => confirmed.Task));
            var replacement = project with { Id = "replacement", Views = [ZetlProjectSnapshotMapper.ToSnapshot(ViewForUi(view.Id, "Replacement", true))] };
            PublishRenderSnapshot(controller, replacement);
            if (returnToOriginal) PublishRenderSnapshot(controller, project);
            confirmed.SetResult(true);
            await deleting;
            Assert.Equal(returnToOriginal ? "Original" : "Replacement", WindowField<KastnViewCatalog>(window, "viewCatalog").Find(view.Id)!.Name);
            Assert.False(WindowField<bool>(window, "viewWriteInProgress"));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ViewDraftCannotOverwriteARemovedOrRemotelyChangedProjectView(bool removed)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            var view = ViewForUi("project-view", "Original", true);
            project = project with { MetadataRevision = 2, Views = [ZetlProjectSnapshotMapper.ToSnapshot(view)] };
            PublishRenderSnapshot(controller, project);
            OpenViewForUi(window, view);
            window.viewNameBox.Text = "Local draft";
            Dispatcher.UIThread.RunJobs();
            var changed = project with
            {
                MetadataRevision = 3,
                Views = removed ? [] : [ZetlProjectSnapshotMapper.ToSnapshot(ViewForUi(view.Id, "Remote draft", true))]
            };
            PublishRenderSnapshot(controller, changed);
            await InvokeWorkflow(window, "SaveViewAsync");
            Assert.True(window.viewErrorText.IsVisible);
            Assert.Contains("changed or was removed", window.viewErrorText.Text);
            Assert.Equal("Local draft", WindowField<KastnViewEditorPresenter>(window, "viewEditor").Capture()!.Name);
            Assert.Equal(removed ? null : "Remote draft", WindowField<KastnViewCatalog>(window, "viewCatalog").Find(view.Id)?.Name);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingBuiltInViewsSavesOnlyAnIndependentValidatedGlobalCopy(bool invalid)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnGlobalViewUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        try
        {
            var project = RenderProject("global-views", "baseline");
            var controller = new KastnConnectionController(_ => Task.CompletedTask);
            typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!
                .SetValue(controller, new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], project));
            var store = new ZetlViewStore(Path.Combine(directory, "views"));
            window = new MainWindow(controller, new KastnDraftStore(Path.Combine(directory, "draft.json")), store);
            window.Show();
            typeof(MainWindow).GetMethod("EditSelectedView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var editor = WindowField<KastnViewEditorPresenter>(window, "viewEditor");
            Assert.False(ZetlViewDefaults.IsBuiltIn(editor.Capture()!.Id));
            Assert.False(editor.State!.IsDirty);
            window.viewNameBox.Text = invalid ? "" : "My universal view";
            Dispatcher.UIThread.RunJobs();
            var id = editor.Capture()!.Id;
            await InvokeWorkflow(window, "SaveViewAsync");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Formatted", store.LoadAll().Single(view => view.Id == "formatted").Name);
            if (invalid)
            {
                Assert.DoesNotContain(store.LoadAll(), view => view.Id == id);
                Assert.True(window.viewErrorText.IsVisible);
                Assert.True(editor.State!.IsDirty);
            }
            else
            {
                Assert.Equal("My universal view", store.LoadAll().Single(view => view.Id == id).Name);
                Assert.Null(editor.State);
                Assert.Equal(id, Assert.IsType<ZetlViewDocument>(window.viewPickerBox.SelectedItem).Id);
            }
            Assert.Empty(project.Views);
            Assert.False(WindowField<bool>(window, "viewWriteInProgress"));
        }
        finally
        {
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalViewDeletionPreservesLaterSelectionAndRejectsChangedFiles(bool changedFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnViewDeleteUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        try
        {
            var project = RenderProject("global-delete", "baseline");
            var controller = new KastnConnectionController(_ => Task.CompletedTask);
            typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!
                .SetValue(controller, new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], project));
            var store = new ZetlViewStore(Path.Combine(directory, "views"));
            var original = ViewForUi("original", "Original view", false);
            store.Save(original);
            store.Save(ViewForUi("another", "Another view", false));
            window = new MainWindow(controller, new KastnDraftStore(Path.Combine(directory, "draft.json")), store);
            window.Show();
            var catalog = WindowField<KastnViewCatalog>(window, "viewCatalog");
            window.viewPickerBox.SelectedItem = catalog.Find(original.Id);
            var confirm = new TaskCompletionSource<bool>();
            var deleting = InvokeWorkflow(window, "DeleteSelectedViewAsync", (Func<ZetlViewDocument, Task<bool>>)(_ => confirm.Task));
            window.viewPickerBox.SelectedItem = catalog.Find("another");
            if (changedFile) store.Save(ViewForUi(original.Id, "Changed in another window", false));
            confirm.SetResult(true);
            await deleting;
            Assert.Equal("another", Assert.IsType<ZetlViewDocument>(window.viewPickerBox.SelectedItem).Id);
            Assert.Equal(changedFile, store.LoadAll().Any(view => view.Id == original.Id));
            Assert.Contains(store.LoadAll(), view => view.Id == "another");
            Assert.Empty(project.Views);
        }
        finally
        {
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaTheory]
    [InlineData("rebuild")]
    [InlineData("close")]
    [InlineData("new-editor")]
    public void RetiredViewPreviewLinksCannotActInTheSameProject(string interruption)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            project = project with { ChangeSequence = 2, Slips = [project.Slips[0] with { Text = "[[render-two|Target]]" }, project.Slips[1]] };
            PublishRenderSnapshot(controller, project);
            OpenViewForUi(window, ViewForUi("draft", "Original", false));
            var action = ContentControls(window.viewLivePreviewPanel).OfType<TextBlock>().Single(text => text.Text == "Target");
            if (interruption == "rebuild") window.viewNameBox.Text = "Changed preview";
            else if (interruption == "new-editor") OpenViewForUi(window, ViewForUi("new-draft", "New draft", false));
            else typeof(MainWindow).GetMethod("CloseViewEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Dispatcher.UIThread.RunJobs();
            window.projectTree.SelectedItem = window.treeProjection.Find(project.Slips[0].Id);
            Dispatcher.UIThread.RunJobs();
            var version = window.editorState.SelectionVersion;
            Assert.True(PressContent(action).Handled);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(version, window.editorState.SelectionVersion);
            Assert.Equal(project.Slips[0].Id, window.editorState.SlipId);
        }
        finally { CloseWindow(window); }
    }

    private static ZetlViewDocument ViewForUi(string id, string name, bool structured) => new()
    {
        Id = id, Name = name, Kind = ZetlViewKinds.Html,
        Sections = structured ? [new() { Title = "Inbox", Buckets = ["Inbox"] }] : []
    };

    private static void OpenViewForUi(MainWindow window, ZetlViewDocument view)
    {
        typeof(MainWindow).GetMethod("OpenViewEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [view, false]);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
