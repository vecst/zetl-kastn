using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("bucket", "unchanged")]
    [InlineData("group", "unchanged")]
    [InlineData("rename", "unchanged")]
    [InlineData("delete-project", "unchanged")]
    [InlineData("delete-bucket", "unchanged")]
    [InlineData("bucket", "selection")]
    [InlineData("group", "selection")]
    [InlineData("rename", "selection")]
    [InlineData("delete-project", "selection")]
    [InlineData("delete-bucket", "selection")]
    [InlineData("bucket", "typing")]
    [InlineData("rename", "typing")]
    [InlineData("delete-project", "typing")]
    [InlineData("delete-bucket", "typing")]
    [InlineData("bucket", "revision")]
    [InlineData("rename", "revision")]
    [InlineData("delete-project", "revision")]
    [InlineData("delete-bucket", "revision")]
    [InlineData("bucket", "project")]
    [InlineData("rename", "project")]
    [InlineData("delete-project", "retire")]
    public async Task NativePromptsKeepCapturedTargetsAndRejectLaterInteractions(string route, string change)
    {
        var commands = new List<ZetlCommandEnvelope>();
        await using var h = await NativeActionHarness.CreateAsync(command => { lock (commands) commands.Add(command); });
        var window = h.Window;
        var landing = WindowField<KastnLandingPage>(window, "landing");
        var card = landing.RecentProjects.Single(card => card.Id == h.Source.Id);
        var named = new TaskCompletionSource<string?>();
        var confirmed = new TaskCompletionSource<bool>();
        var pending = route switch
        {
            "bucket" => InvokeWorkflow(window, "AddBucketAsync", (Func<Task<string?>>)(() => named.Task)),
            "group" => InvokeWorkflow(window, "InsertGroupBucketAsync", (Func<Task<string?>>)(() => named.Task)),
            "rename" => InvokeWorkflow(window, "RenameProjectAsync", card, (Func<Task<string?>>)(() => named.Task)),
            "delete-project" => InvokeWorkflow(window, "DeleteProjectAsync", card, (Func<Task<bool>>)(() => confirmed.Task)),
            _ => InvokeWorkflow(window, "DeleteBucketAsync", (Func<ZetlBucketSnapshot, Task<bool>>)(_ => confirmed.Task))
        };
        Assert.False(pending.IsCompleted);
        if (change == "selection") window.projectTree.SelectedItem = window.treeProjection.Find(h.OtherSlip.Id);
        else if (change == "typing") window.slipEditor.Text = "Later writing";
        else if (change == "retire") CloseWindow(window);
        else if (change == "project") await window.ActivateRequestAsync(h.Destination.Id);
        else if (change == "revision")
        {
            var remote = route is "bucket" or "delete-bucket"
                ? ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.UpdateBucket,
                    new UpdateBucketCommand { Name = "Remote bucket" }, h.Source.Id, h.Source.Buckets[0].Id, h.Source.Buckets[0].Revision)
                : ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.RenameProject,
                    new RenameProjectCommand { Name = "Remote project" }, h.Source.Id, h.Source.Id, h.Source.MetadataRevision);
            Assert.Equal(ZetlResponseStatus.Success, (await h.Controller.ExecuteAsync(remote)).Status);
            await h.Controller.SynchronizeAsync();
            lock (commands) commands.Clear();
        }
        Dispatcher.UIThread.RunJobs();
        named.SetResult("New name");
        confirmed.SetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        var kind = route switch
        {
            "bucket" or "group" => ZetlCommandKind.AddBucket,
            "rename" => ZetlCommandKind.RenameProject,
            "delete-project" => ZetlCommandKind.DeleteProject,
            _ => ZetlCommandKind.DeleteBucket
        };
        ZetlCommandEnvelope[] writes;
        lock (commands) writes = commands.Where(command => command.Kind == kind).ToArray();
        if (change == "unchanged")
        {
            Assert.Equal(h.Source.Id, Assert.Single(writes).ProjectId);
            if (route is "bucket" or "group")
            {
                var created = h.Source.Buckets.Single(bucket => bucket.Name == "New name");
                Assert.Equal(h.Source.Buckets[0].Id, created.ParentBucketId);
                Assert.Equal(route == "group" ? ZetlBucketRenderKinds.Group : "", created.RenderKind);
            }
            else if (route == "rename") Assert.Equal("New name", h.Source.Name);
            else if (route == "delete-project")
            {
                Assert.DoesNotContain(h.Store.State.Projects, project => project.Id == h.Source.Id);
                Assert.Null(h.Controller.Current.Project);
                Assert.Null(WindowField<ZetlProjectSnapshot?>(window, "currentProject"));
            }
            else Assert.DoesNotContain(h.Source.Buckets, bucket => bucket.Id == writes[0].TargetId);
        }
        else Assert.Empty(writes);
        if (change == "typing")
        {
            Assert.Equal("Later writing", window.slipEditor.Text);
            Assert.True(window.editorState.IsDirty);
        }
        if (change == "selection") Assert.Equal(h.OtherSlip.Id, window.editorState.SlipId);
    }

    [AvaloniaTheory]
    [InlineData("template", "unchanged")]
    [InlineData("creation", "unchanged")]
    [InlineData("template", "replacement")]
    [InlineData("creation", "replacement")]
    [InlineData("template", "editor")]
    [InlineData("creation", "editor")]
    [InlineData("template", "retire")]
    [InlineData("creation", "retire")]
    public async Task NativeCatalogDeleteProtectsReplacedFilesAndNewEditorSessions(string kind, string change)
    {
        var directory = Path.Combine(defaultDraftDirectory, $"native-catalog-{kind}-{change}");
        var templates = new ZetlTemplateStore(directory);
        var creations = new ZetlCreationTypeStore(directory);
        var template = KastnProjectCreationTests.Template();
        var creation = new ZetlCreationTypeDocument { Id = "native-creation", Name = "Original", TemplateId = "blank" };
        if (kind == "template") templates.Save(template); else creations.Save(creation);
        var window = CatalogEditorWindow(directory, directory);
        try
        {
            var confirmed = new TaskCompletionSource<bool>();
            var pending = kind == "template"
                ? InvokeWorkflow(window, "DeleteTemplateAsync", new KastnTemplateCard("", template.Name, "", template, true), (Func<Task<bool>>)(() => confirmed.Task))
                : InvokeWorkflow(window, "DeleteCreationAsync", new KastnCreationCard("", creation.Name, "", creation, true), (Func<Task<bool>>)(() => confirmed.Task));
            Assert.False(pending.IsCompleted);
            if (change == "replacement")
            {
                if (kind == "template") { var replacement = ZetlTemplateDefaults.Clone(template); replacement.Name = "Replacement"; templates.Save(replacement); }
                else { var replacement = ZetlCreationTypeDefaults.Clone(creation); replacement.Name = "Replacement"; creations.Save(replacement); }
            }
            else if (change == "editor") { OpenCatalogEditor(window, kind); CatalogName(window, kind).Text = "New draft"; }
            else if (change == "retire") CloseWindow(window);
            confirmed.SetResult(true);
            await pending;
            var name = kind == "template" ? templates.LoadAll().FirstOrDefault(doc => doc.Id == template.Id)?.Name
                : creations.LoadAll().FirstOrDefault(doc => doc.Id == creation.Id)?.Name;
            if (change == "unchanged") Assert.Null(name);
            else Assert.Equal(change == "replacement" ? "Replacement" : kind == "template" ? template.Name : creation.Name, name);
            if (change == "editor") Assert.Equal("New draft", CatalogName(window, kind).Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task NativeSaveMenuSharesAvailabilityAndDoesNotRestoreAnOldCaret()
    {
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource();
        await using var h = await NativeActionHarness.CreateAsync(command =>
        {
            if (command.Kind == ZetlCommandKind.UpdateSlip && arrived.TrySetResult()) release.Wait(TimeSpan.FromSeconds(5));
        });
        try
        {
            var window = h.Window;
            Assert.Equal(window.saveSlipButton.IsEnabled, window.saveSlipMenuItem.IsEnabled);
            Assert.True(window.deleteSlipMenuItem.IsEnabled);
            window.slipEditor.Focus();
            window.slipEditor.Text = "Saved menu edit";
            window.slipEditor.CaretIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.editorState.IsDirty);
            window.saveSlipMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.slipEditor.CaretIndex = 7;
            release.Set();
            await WaitForConditionAsync(() => WindowField<Task<bool>>(window, "inflightSave").IsCompleted, "Menu save should finish.");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Saved menu edit", h.EditedSlip.Text);
            Assert.Equal(7, window.slipEditor.CaretIndex);
            Assert.True(window.slipEditor.IsFocused);
            Assert.Equal(window.saveSlipButton.IsEnabled, window.saveSlipMenuItem.IsEnabled);
            CloseWindow(window);
            Assert.False(window.saveSlipMenuItem.IsEnabled);
            Assert.False(window.deleteSlipMenuItem.IsEnabled);
        }
        finally { release.Set(); }
    }

    [AvaloniaTheory]
    [InlineData("lane", "unchanged")]
    [InlineData("lane", "typing")]
    [InlineData("lane", "project")]
    [InlineData("name", "typing")]
    [InlineData("name", "selection")]
    [InlineData("name", "project")]
    public async Task NativeReplayPromptsPreserveTheLatestNavigationAndDraft(string stage, string change)
    {
        await using var h = await NativeActionHarness.CreateAsync(_ => { }, replay: true);
        var window = h.Window;
        var landing = WindowField<KastnLandingPage>(window, "landing");
        landing.SetArchived(true);
        var card = landing.Projects.Single(card => card.Id == h.Source.Id);
        var lane = new TaskCompletionSource<string?>();
        var name = new TaskCompletionSource<string?>();
        var nameOpened = false;
        var pending = InvokeWorkflow(window, "CreateTemporaryProjectFromReplayAsync", card,
            (Func<Task<string?>>)(() => lane.Task), (Func<Task<string?>>)(() => { nameOpened = true; return name.Task; }));
        Assert.False(pending.IsCompleted);
        if (stage == "name")
        {
            lane.SetResult(ZetlStateRules.NormalLane);
            await WaitForConditionAsync(() => nameOpened, "Second replay prompt should open.");
        }
        if (change == "typing") window.slipEditor.Text = "Later replay draft";
        else if (change == "selection") window.projectTree.SelectedItem = window.treeProjection.Find(h.OtherSlip.Id);
        else if (change == "project") await window.ActivateRequestAsync(h.Destination.Id);
        Dispatcher.UIThread.RunJobs();
        lane.TrySetResult(ZetlStateRules.NormalLane);
        name.SetResult("Temporary replay");
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        var temporary = h.Store.State.Projects.Where(project => project.Kind == ZetlStateRules.TemporaryConsumableProjectKind).ToArray();
        if (change == "unchanged")
        {
            Assert.Equal(Assert.Single(temporary).Id, h.Controller.Current.Project?.Id);
            Assert.Equal("Temporary replay", h.Controller.Current.Project?.Name);
        }
        else Assert.Empty(temporary);
        if (change == "typing") Assert.Equal("Later replay draft", window.slipEditor.Text);
        if (change == "selection") Assert.Equal(h.OtherSlip.Id, window.editorState.SlipId);
        if (change == "project") Assert.Equal(h.Destination.Id, h.Controller.Current.Project?.Id);
        if (stage == "lane" && change != "unchanged") Assert.False(nameOpened);
    }

    [AvaloniaTheory]
    [InlineData("heading", "typing")]
    [InlineData("bucket", "selection")]
    [InlineData("rename", "typing")]
    public async Task SentNativeCommandsFinishWithoutReplacingLaterWritingOrSelection(string route, string change)
    {
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource();
        var kind = route == "heading" ? ZetlCommandKind.SetBucketHeading
            : route == "bucket" ? ZetlCommandKind.AddBucket : ZetlCommandKind.RenameProject;
        await using var h = await NativeActionHarness.CreateAsync(command =>
        {
            if (command.Kind == kind && arrived.TrySetResult()) release.Wait(TimeSpan.FromSeconds(5));
        });
        try
        {
            var window = h.Window;
            var card = WindowField<KastnLandingPage>(window, "landing").RecentProjects.Single(card => card.Id == h.Source.Id);
            var bucket = h.Controller.Current.Project!.Buckets.First(bucket => bucket.Id == h.Source.Buckets[0].Id);
            var pending = route switch
            {
                "heading" => InvokeWorkflow(window, "SendBucketHeadingAsync", bucket, "center", true, 1),
                "bucket" => InvokeWorkflow(window, "AddBucketAsync", (Func<Task<string?>>)(() => Task.FromResult<string?>("Added bucket"))),
                _ => InvokeWorkflow(window, "RenameProjectAsync", card, (Func<Task<string?>>)(() => Task.FromResult<string?>("Renamed project")))
            };
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Other native writes cannot queue captured revisions behind this write.
            await InvokeWorkflow(window, "ToggleJournalModeAsync");
            Assert.False(h.Source.JournalMode);
            if (change == "typing") window.slipEditor.Text = "Later native draft";
            else window.projectTree.SelectedItem = window.treeProjection.Find(h.OtherSlip.Id);
            Dispatcher.UIThread.RunJobs();
            release.Set();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            if (route == "heading") Assert.Equal("center", h.Source.Buckets[0].HeadingAlign);
            else if (route == "bucket") Assert.Contains(h.Source.Buckets, bucket => bucket.Name == "Added bucket");
            else Assert.Equal("Renamed project", h.Source.Name);
            if (change == "typing")
            {
                Assert.Equal("Later native draft", window.slipEditor.Text);
                Assert.True(window.editorState.IsDirty);
                Assert.Contains("Unsaved", window.statusText.Text!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("renamed", window.statusText.Text!, StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Equal(h.OtherSlip.Id, window.editorState.SlipId);
            Assert.False(WindowField<KastnMutationCoordinator>(window, "mutations").IsBusy);
        }
        finally { release.Set(); }
    }

    private sealed class NativeActionHarness(string directory, ZetlStateStore store, ZetlProject source,
        ZetlProject destination, ZetlSlip editedSlip, ZetlSlip otherSlip, ZetlIpcServer server,
        KastnConnectionController controller, MainWindow window) : IAsyncDisposable
    {
        public ZetlStateStore Store => store;
        public ZetlProject Source => source;
        public ZetlProject Destination => destination;
        public ZetlSlip EditedSlip => editedSlip;
        public ZetlSlip OtherSlip => otherSlip;
        public KastnConnectionController Controller => controller;
        public MainWindow Window => window;

        public static async Task<NativeActionHarness> CreateAsync(Action<ZetlCommandEnvelope> observe, bool replay = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), "KastnNativeAction", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var source = store.CreateProject("Source", ["Inbox", "Next"], "Inbox");
            var edited = store.AddSlip(source.Buckets[0], "baseline", "copy");
            var other = store.AddSlip(source.Buckets[1], "other", "copy");
            var destination = store.CreateProject("Destination", ["Inbox"], "Inbox");
            if (replay)
            {
                store.SetBucketKind(source.Buckets[0], "Replay");
                store.SetProjectStatus(source, ZetlStateRules.ArchivedStatus);
            }
            var pipeName = $"kastn-native-{Guid.NewGuid():N}";
            var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command => { observe(command); return false; });
            server.Start();
            var controller = new KastnConnectionController(_ => throw new InvalidOperationException("Already running."),
                pipeName, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(source.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == source.Id, "Source should load.");
            var window = new MainWindow(controller, new KastnDraftStore(Path.Combine(directory, "draft.json")),
                viewStore: new ZetlViewStore(Path.Combine(directory, "views")),
                templateStore: new ZetlTemplateStore(Path.Combine(directory, "templates")),
                creationStore: new ZetlCreationTypeStore(Path.Combine(directory, "creations")),
                stateStore: new KastnStateStore(Path.Combine(directory, "kastn.json")));
            window.Show();
            window.projectTree.SelectedItem = window.treeProjection.Find(edited.Id);
            Dispatcher.UIThread.RunJobs();
            return new(directory, store, source, destination, edited, other, server, controller, window);
        }

        public async ValueTask DisposeAsync()
        {
            CloseWindow(window);
            await controller.DisposeAsync();
            server.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
