using System.Reflection;
using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    public static IEnumerable<object[]> LinkWorkflowInterruptions =>
        from dialog in new[] { "web", "wiki", "wiki-action", "wiki-change" }
        from interruption in new[] { "typing", "styles", "pending-style", "reselection", "project", "remote", "offline" }
        select new object[] { dialog, interruption };

    [AvaloniaTheory]
    [MemberData(nameof(LinkWorkflowInterruptions))]
    public async Task LinkDialogCannotApplyToChangedDraftOrSession(string dialog, string interruption)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            if (dialog is "wiki-action" or "wiki-change")
            {
                var linked = project.Slips[0] with
                {
                    Revision = 2,
                    InlineStyles = [new() { Start = 0, Length = 4, Kind = ZetlInlineStyleKinds.WikiLink,
                        TargetSlipId = "render-two", CachedTitle = "Second" }]
                };
                PublishRenderSnapshot(controller, project with { Slips = [linked, project.Slips[1]] });
                Assert.Equal(ZetlInlineStyleKinds.WikiLink, Assert.Single(window.editorState.DraftInlineStyles).Kind);
            }
            window.slipEditor.SelectionStart = 0;
            window.slipEditor.SelectionEnd = 4;
            var response = new TaskCompletionSource();
            var picked = false;
            Task editing;
            if (dialog == "web")
            {
                editing = InvokeWorkflow(window, "SetEditorWebLinkAsync",
                    (Func<string, bool, Task<KastnDialogs.LinkEditResult?>>)(async (_, _) =>
                    {
                        await response.Task;
                        return new("https://example.com", false);
                    }));
            }
            else
            {
                editing = InvokeWorkflow(window, "InsertSlipLinkAsync",
                    (Func<string, Task<KastnDialogs.LinkRangeAction?>>)(async _ =>
                    {
                        if (dialog == "wiki-action") await response.Task;
                        return dialog == "wiki-action" ? KastnDialogs.LinkRangeAction.Remove : KastnDialogs.LinkRangeAction.Change;
                    }),
                    (Func<IReadOnlyList<ZetlSlipSnapshot>, string, Task<ZetlSlipSnapshot?>>)(async (_, _) =>
                    {
                        picked = true;
                        await response.Task;
                        return project.Slips[1];
                    }));
            }
            InterruptLinkWorkflow(window, controller, project, interruption);
            var text = window.slipEditor.Text;
            var styles = window.editorState.DraftInlineStyles.ToArray();
            var pendingStyles = window.editorState.PendingInlineStyleKinds.ToHashSet(StringComparer.Ordinal);
            var revision = window.editorState.Revision;
            var version = window.editorState.SelectionVersion;
            var status = window.statusText.Text;
            response.SetResult();
            await editing;

            Assert.Equal(text, window.slipEditor.Text);
            Assert.Equal(styles, window.editorState.DraftInlineStyles);
            Assert.True(pendingStyles.SetEquals(window.editorState.PendingInlineStyleKinds));
            Assert.Equal(revision, window.editorState.Revision);
            Assert.Equal(version, window.editorState.SelectionVersion);
            Assert.Equal(status, window.statusText.Text);
            if (dialog == "wiki-action") Assert.False(picked);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedLinkDialogAppliesCapturedRange(bool wiki)
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            window.slipEditor.SelectionStart = 0;
            window.slipEditor.SelectionEnd = 4;
            if (wiki)
            {
                await InvokeWorkflow(window, "InsertSlipLinkAsync",
                    (Func<string, Task<KastnDialogs.LinkRangeAction?>>)(_ => throw new InvalidOperationException("No existing link.")),
                    (Func<IReadOnlyList<ZetlSlipSnapshot>, string, Task<ZetlSlipSnapshot?>>)((_, _) => Task.FromResult<ZetlSlipSnapshot?>(project.Slips[1])));
            }
            else
            {
                await InvokeWorkflow(window, "SetEditorWebLinkAsync",
                    (Func<string, bool, Task<KastnDialogs.LinkEditResult?>>)((_, _) => Task.FromResult<KastnDialogs.LinkEditResult?>(new("https://example.com", false))));
            }
            var link = Assert.Single(window.editorState.DraftInlineStyles);
            Assert.Equal(0, link.Start);
            Assert.Equal(4, link.Length);
            Assert.Equal(wiki ? ZetlInlineStyleKinds.WikiLink : ZetlInlineStyleKinds.Link, link.Kind);
            Assert.Equal(wiki ? "render-two" : null, link.TargetSlipId);
            Assert.True(window.editorState.IsDirty);
            // Fake online state has no IPC transport. A failed command must retain
            // the applied local intent in the recovery journal.
            Assert.NotNull(WindowField<KastnDraftStore>(window, "draftStore").Draft);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public async Task SlipLinkPickerRechecksRemovedDestination()
    {
        var (window, controller, project) = WorkflowWindow();
        try
        {
            var selected = new TaskCompletionSource<ZetlSlipSnapshot?>();
            var editing = InvokeWorkflow(window, "InsertSlipLinkAsync",
                (Func<string, Task<KastnDialogs.LinkRangeAction?>>)(_ => throw new InvalidOperationException("No existing link.")),
                (Func<IReadOnlyList<ZetlSlipSnapshot>, string, Task<ZetlSlipSnapshot?>>)((_, _) => selected.Task));
            PublishRenderSnapshot(controller, project with { Slips = [project.Slips[0]] });
            selected.SetResult(project.Slips[1]);
            await editing;
            Assert.Empty(window.editorState.DraftInlineStyles);
            Assert.Equal("baseline", window.slipEditor.Text);
            Assert.False(window.editorState.IsDirty);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData("typing")]
    [InlineData("reselection")]
    [InlineData("project")]
    [InlineData("during-save")]
    [InlineData("focus-save")]
    [InlineData("conflict")]
    public async Task VisibilityWorkflowPreservesEditorAndStopsAfterNavigation(string interruption)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnVisibilityUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var releaseResponse = new ManualResetEventSlim();
        var commandArrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        var updates = new ConcurrentQueue<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Visibility race", ["Inbox"], "Inbox");
            var first = store.AddSlip(project.Buckets[0], "baseline", "copy");
            var second = store.AddSlip(project.Buckets[0], "second baseline", "copy");
            var replacement = store.CreateProject("Other", ["Inbox"], "Inbox");
            var replacementSlip = store.AddSlip(replacement.Buckets[0], "replacement baseline", "copy");
            replacementSlip.Id = first.Id;
            store.UpdateSlip(replacementSlip, replacementSlip.Text);
            var pipeName = $"kastn-visibility-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.UpdateSlip)
                    {
                        updates.Enqueue(command);
                        if (commandArrived.TrySetResult(command)) releaseResponse.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."), pipeName,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");
            var drafts = new KastnDraftStore(Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, drafts);
            window.Show();
            window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
            Dispatcher.UIThread.RunJobs();
            if (interruption is "during-save" or "focus-save")
            {
                window.slipEditor.Text = "initial draft";
                Dispatcher.UIThread.RunJobs();
            }
            if (interruption == "focus-save")
            {
                // Focus loss can start autosave before the eye button's Click event.
                _ = InvokeWorkflow(window, "SaveEditorAsync");
            }
            var changing = InvokeWorkflow(window, "ToggleTreeVisibilityAsync", window.treeProjection.Find(project.Buckets[0].Id)!);
            var command = await commandArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(project.Id, command.ProjectId);
            Assert.Equal(first.Id, command.TargetId);
            if (interruption == "focus-save")
            {
                releaseResponse.Set();
                await changing.WaitAsync(TimeSpan.FromSeconds(5));
                Dispatcher.UIThread.RunJobs();
                Assert.True(first.ExcludedFromViews);
                Assert.True(second.ExcludedFromViews);
                Assert.False(window.editorState.IsDirty);
                Assert.Equal("initial draft", window.slipEditor.Text);
                Assert.Null(drafts.Draft);
                Assert.Equal(3, updates.Count);
                return;
            }
            Task? navigation = null;
            if (interruption is "project" or "during-save")
            {
                navigation = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                window.projectTree.SelectedItem = window.treeProjection.Find(first.Id);
                Dispatcher.UIThread.RunJobs();
                window.slipEditor.Text = "replacement draft";
            }
            else
            {
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                if (interruption == "reselection")
                {
                    window.projectTree.SelectedItem = window.treeProjection.Find(second.Id);
                    Dispatcher.UIThread.RunJobs();
                }
                window.slipEditor.Text = "later writing";
                Dispatcher.UIThread.RunJobs();
                window.editorState.SetInlineStyles([new() { Start = 0, Length = 5, Kind = ZetlInlineStyleKinds.Italic }]);
                if (interruption == "conflict")
                {
                    store.UpdateSlip(first, "remote writing");
                    PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
                }
            }
            Dispatcher.UIThread.RunJobs();
            releaseResponse.Set();
            await changing.WaitAsync(TimeSpan.FromSeconds(5));
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(interruption is "project" or "during-save" ? "replacement draft" : "later writing", window.slipEditor.Text);
            Assert.True(window.editorState.IsDirty);
            Assert.Equal(interruption == "conflict", window.editorState.ConflictCurrent is not null);
            Assert.Equal(interruption != "during-save", first.ExcludedFromViews);
            Assert.Equal(interruption is "typing" or "conflict", second.ExcludedFromViews);
            Assert.False(replacementSlip.ExcludedFromViews);
            Assert.All(updates, update => Assert.Equal(project.Id, update.ProjectId));
            Assert.Equal(interruption is "typing" or "conflict" ? 2 : 1, updates.Count);
            if (interruption is "typing" or "conflict")
            {
                Assert.Equal(2, drafts.Draft?.BaselineRevision);
                Assert.Equal("baseline", drafts.Draft?.BaselineText);
                Assert.Equal("later writing", drafts.Draft?.DraftText);
                Assert.Equal(ZetlInlineStyleKinds.Italic, Assert.Single(drafts.Draft!.DraftInlineStyles).Kind);
            }
            if (interruption is "reselection" or "project" or "during-save")
            {
                Assert.StartsWith("Unsaved changes", window.statusText.Text);
            }
        }
        finally
        {
            releaseResponse.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (MainWindow Window, KastnConnectionController Controller, ZetlProjectSnapshot Project) WorkflowWindow()
    {
        var project = RenderProject("workflow-project", "baseline");
        var controller = new KastnConnectionController(_ => Task.CompletedTask);
        typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!
            .SetValue(controller, new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], project));
        var window = new MainWindow(controller);
        window.Show();
        window.projectTree.SelectedItem = window.treeProjection.Find("render-one");
        Dispatcher.UIThread.RunJobs();
        return (window, controller, project);
    }

    private static Task InvokeWorkflow(MainWindow window, string name, params object[] arguments) =>
        (Task)typeof(MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(window, arguments)!;

    private static void InterruptLinkWorkflow(MainWindow window, KastnConnectionController controller,
        ZetlProjectSnapshot project, string interruption)
    {
        switch (interruption)
        {
            case "typing": window.slipEditor.Text = "new writing"; break;
            case "styles": window.editorState.SetInlineStyles([new() { Start = 0, Length = 4, Kind = ZetlInlineStyleKinds.Bold }]); break;
            case "pending-style": window.editorState.TogglePendingInlineStyle(ZetlInlineStyleKinds.Italic); break;
            case "reselection":
                window.projectTree.SelectedItem = window.treeProjection.Find("render-two");
                Dispatcher.UIThread.RunJobs();
                window.projectTree.SelectedItem = window.treeProjection.Find("render-one");
                break;
            case "project": PublishRenderSnapshot(controller, project with { Id = "other-project" }); break;
            case "remote": PublishRenderSnapshot(controller, project with
                { Slips = [project.Slips[0] with { Revision = 3, Text = "remote writing" }, project.Slips[1]] }); break;
            case "offline":
                typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!
                    .SetValue(controller, new KastnSessionSnapshot(KastnConnectionState.Offline, "Disconnected", [], project));
                typeof(MainWindow).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new KastnSessionSnapshot(KastnConnectionState.Offline, "Disconnected", [], project)]);
                break;
        }
        Dispatcher.UIThread.RunJobs();
    }
}
