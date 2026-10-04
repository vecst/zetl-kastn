using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("project")]
    [InlineData("server")]
    [InlineData("typing")]
    [InlineData("reselection")]
    [InlineData("navigation-pending")]
    [InlineData("retire")]
    public async Task TemplatePromptCannotCreateAfterTheInitiatingContextChanges(string interruption)
    {
        var (window, controller, project) = WorkflowWindow();
        var answer = new TaskCompletionSource<KastnProjectCreationChoice?>();
        try
        {
            var running = CreateForUi(window, KastnProjectCreationTests.Template(), null, _ => answer.Task);
            Assert.True(WindowField<KastnProjectCreationWorkflow>(window, "projectCreation").IsBusy);
            if (interruption == "project") PublishRenderSnapshot(controller, project with { Id = "replacement" });
            else if (interruption == "server")
            {
                var snapshot = controller.Current with { ServerInstanceId = "replacement-server" };
                typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!.SetValue(controller, snapshot);
                typeof(MainWindow).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [snapshot]);
            }
            else if (interruption == "typing") { window.slipEditor.Text = "Later writing"; Dispatcher.UIThread.RunJobs(); }
            else if (interruption == "reselection")
            {
                window.projectTree.SelectedItem = window.treeProjection.Find("render-two");
                Dispatcher.UIThread.RunJobs();
                window.projectTree.SelectedItem = window.treeProjection.Find("render-one");
                Dispatcher.UIThread.RunJobs();
            }
            else if (interruption == "navigation-pending") await controller.NavigateToProjectAsync("replacement");
            else window.RetireLifetime();
            var message = window.statusText.Text;
            answer.SetResult(new("New project", false));
            await running;
            Assert.False(WindowField<KastnProjectCreationWorkflow>(window, "projectCreation").IsBusy);
            Assert.Equal(message, window.statusText.Text);
            Assert.NotEqual(WindowState.Minimized, window.WindowState);
            if (interruption == "typing") Assert.Equal("Later writing", window.editorState.DraftText);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task StaleTemporaryLaneAnswerCannotRememberAPreference()
    {
        var (window, controller, project) = WorkflowWindow();
        var answer = new TaskCompletionSource<KastnTemplateLaneChoice?>();
        var path = Path.Combine(defaultDraftDirectory, "settings.json");
        MainWindow? isolated = null;
        try
        {
            var owner = new KastnSettings(path);
            isolated = new MainWindow(controller, settings: owner);
            isolated.Show();
            var running = CreateForUi(isolated, KastnProjectCreationTests.Template(), null,
                _ => Task.FromResult<KastnProjectCreationChoice?>(new("Temporary", true)), _ => answer.Task);
            PublishRenderSnapshot(controller, project with { Id = "replacement" });
            answer.SetResult(new("Shift", true));
            await running;
            Assert.Equal("", owner.Current.KastnTemporaryTemplateLaneDefault);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (isolated is not null) CloseWindow(isolated);
            CloseWindow(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationFromLandingSurvivesItsOwnRefreshesAndReportsViewConflicts(bool viewConflict)
    {
        var directory = Path.Combine(defaultDraftDirectory, "creation");
        Directory.CreateDirectory(directory);
        var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "creation-ui");
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        var pipe = $"kastn-creation-{Guid.NewGuid():N}";
        using var server = new ZetlIpcServer(new ZetlProjectService(store), pipe, log: null, dropResponseForTesting: command =>
        {
            commands.Enqueue(command);
            // Mimic a separate project-metadata edit between seeds and view assignment.
            if (viewConflict && command.Kind == ZetlCommandKind.AddSlip
                && command.Payload!.Value.GetProperty("title").GetString() == "Title only")
                store.State.Projects.Single().MetadataRevision++;
            return false;
        });
        server.Start();
        await using var controller = new KastnConnectionController(_ => throw new InvalidOperationException("Already running."),
            pipe, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
        controller.Start();
        await WaitForConditionAsync(() => controller.Current.ConnectionState == KastnConnectionState.Online, "Should connect.");
        var owner = new KastnSettings(Path.Combine(directory, "settings.json"));
        var window = new MainWindow(controller, settings: owner);
        window.Show();
        try
        {
            var answer = new TaskCompletionSource<KastnProjectCreationChoice?>();
            var running = CreateForUi(window, KastnProjectCreationTests.Template(), "chosen-view", _ => answer.Task);
            await controller.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(controller.Current.Project);
            answer.SetResult(new("Created project", false));
            await running.WaitAsync(TimeSpan.FromSeconds(8));
            Dispatcher.UIThread.RunJobs();
            var project = Assert.Single(store.State.Projects);
            Assert.Equal(project.Id, controller.Current.Project?.Id);
            Assert.Equal(project.Id, WindowField<ZetlProjectSnapshot>(window, "currentProject").Id);
            Assert.Equal(4, ZetlProjectSnapshotMapper.ToSnapshot(project).Slips.Count);
            Assert.Equal(viewConflict ? null : "chosen-view", project.DefaultViewId);
            Assert.Single(commands.Where(command => command.Kind == ZetlCommandKind.CreateProject));
            Assert.Equal(4, commands.Count(command => command.Kind == ZetlCommandKind.AddSlip));
            Assert.Single(commands.Where(command => command.Kind == ZetlCommandKind.SetProjectView));
            if (viewConflict)
            {
                Assert.Contains("default view could not be assigned", window.statusText.Text);
                Assert.NotEqual(WindowState.Minimized, window.WindowState);
            }
            else
            {
                Assert.Contains("Created 'Created project' from the Starter template", window.statusText.Text);
                Assert.Equal(WindowState.Minimized, window.WindowState);
            }
            var message = window.statusText.Text;
            await controller.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(message, window.statusText.Text);
            await controller.NavigateToProjectAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("from the Starter template", window.statusText.Text);
            Assert.DoesNotContain("default view could not be assigned", window.statusText.Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("create")]
    [InlineData("seed")]
    [InlineData("create-retire")]
    [InlineData("seed-retire")]
    public async Task LateCreationRepliesCannotNavigateOrContinueSeedingAfterProjectSwitch(string at)
    {
        var directory = Path.Combine(defaultDraftDirectory, "delayed-creation");
        Directory.CreateDirectory(directory);
        var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "creation-ui");
        var original = store.CreateProject("Original", ["Inbox"], "Inbox");
        var replacement = store.CreateProject("Replacement", ["Inbox"], "Inbox");
        store.AddSlip(replacement.Buckets[0], "Replacement writing", "copy");
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        var arrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        using var release = new ManualResetEventSlim();
        var pipe = $"kastn-delayed-creation-{Guid.NewGuid():N}";
        using var server = new ZetlIpcServer(new ZetlProjectService(store), pipe, log: null, dropResponseForTesting: command =>
        {
            commands.Enqueue(command);
            if (command.Kind == (at.StartsWith("create", StringComparison.Ordinal) ? ZetlCommandKind.CreateProject : ZetlCommandKind.AddSlip)
                && arrived.TrySetResult(command)) release.Wait(TimeSpan.FromSeconds(8));
            return false;
        });
        server.Start();
        await using var controller = new KastnConnectionController(_ => throw new InvalidOperationException("Already running."),
            pipe, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
        controller.Start(original.Id);
        await WaitForConditionAsync(() => controller.Current.Project?.Id == original.Id, "Should open original project.");
        var window = new MainWindow(controller, settings: new KastnSettings(Path.Combine(directory, "settings.json")));
        window.Show();
        try
        {
            var running = CreateForUi(window, KastnProjectCreationTests.Template(), "view",
                _ => Task.FromResult<KastnProjectCreationChoice?>(new("New project", false)));
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task? navigating = null;
            if (at.EndsWith("retire", StringComparison.Ordinal)) window.RetireLifetime();
            else
            {
                navigating = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
            }
            var message = window.statusText.Text;
            release.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(8));
            if (navigating is not null) await navigating.WaitAsync(TimeSpan.FromSeconds(8));
            Dispatcher.UIThread.RunJobs();
            if (navigating is not null)
            {
                Assert.Equal(replacement.Id, controller.Current.Project?.Id);
                Assert.Equal(replacement.Id, WindowField<ZetlProjectSnapshot>(window, "currentProject").Id);
            }
            else Assert.Equal(message, window.statusText.Text);
            Assert.Single(commands.Where(command => command.Kind == ZetlCommandKind.CreateProject));
            Assert.Equal(at.StartsWith("create", StringComparison.Ordinal) ? 0 : 1,
                commands.Count(command => command.Kind == ZetlCommandKind.AddSlip));
            Assert.DoesNotContain(commands, command => command.Kind == ZetlCommandKind.SetProjectView);
            Assert.NotEqual(WindowState.Minimized, window.WindowState);
            Assert.False(WindowField<KastnProjectCreationWorkflow>(window, "projectCreation").IsBusy);
        }
        finally { release.Set(); CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("stable")]
    [InlineData("typing")]
    [InlineData("project")]
    public async Task TemplateCreationWaitsForTheEditorSaveAndPreservesInterruptions(string interruption)
    {
        var directory = Path.Combine(defaultDraftDirectory, "creation-save");
        Directory.CreateDirectory(directory);
        var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "creation-ui");
        var original = store.CreateProject("Original", ["Inbox"], "Inbox");
        var edited = store.AddSlip(original.Buckets[0], "Baseline", "kastn");
        var replacement = store.CreateProject("Replacement", ["Inbox"], "Inbox");
        var arrived = new TaskCompletionSource();
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        using var release = new ManualResetEventSlim();
        var pipe = $"kastn-creation-save-{Guid.NewGuid():N}";
        using var server = new ZetlIpcServer(new ZetlProjectService(store), pipe, log: null, dropResponseForTesting: command =>
        {
            commands.Enqueue(command);
            if (command.Kind == ZetlCommandKind.UpdateSlip && arrived.TrySetResult()) release.Wait(TimeSpan.FromSeconds(8));
            return false;
        });
        server.Start();
        await using var controller = new KastnConnectionController(_ => throw new InvalidOperationException("Already running."),
            pipe, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
        controller.Start(original.Id);
        await WaitForConditionAsync(() => controller.Current.Project?.Id == original.Id, "Should open the editor project.");
        var window = new MainWindow(controller, settings: new KastnSettings(Path.Combine(directory, "settings.json")));
        window.Show();
        try
        {
            window.projectTree.SelectedItem = window.treeProjection.Find(edited.Id);
            Dispatcher.UIThread.RunJobs();
            window.slipEditor.Text = "Submitted writing";
            Dispatcher.UIThread.RunJobs();
            var prompts = 0;
            var running = CreateForUi(window, KastnProjectCreationTests.Template(), null, _ =>
            {
                prompts++;
                return Task.FromResult<KastnProjectCreationChoice?>(new("New project", false));
            });
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, prompts);
            Task? navigating = null;
            if (interruption == "typing") { window.slipEditor.Text = "Later writing"; Dispatcher.UIThread.RunJobs(); }
            else if (interruption == "project")
            {
                navigating = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
            }
            release.Set();
            await running.WaitAsync(TimeSpan.FromSeconds(8));
            if (navigating is not null) await navigating.WaitAsync(TimeSpan.FromSeconds(8));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Submitted writing", edited.Text);
            Assert.Equal(interruption == "stable" ? 1 : 0, prompts);
            Assert.Equal(interruption == "stable" ? 1 : 0, commands.Count(command => command.Kind == ZetlCommandKind.CreateProject));
            if (interruption == "typing")
            {
                Assert.Equal("Later writing", window.editorState.DraftText);
                Assert.True(window.editorState.IsDirty);
                Assert.Equal(original.Id, controller.Current.Project?.Id);
            }
            else if (interruption == "project") Assert.Equal(replacement.Id, controller.Current.Project?.Id);
        }
        finally { release.Set(); CloseWindow(window); }
    }

    private static Task CreateForUi(MainWindow window, ZetlTemplateDocument template, string? view,
        Func<KastnProjectCreationPrompt, Task<KastnProjectCreationChoice?>> prompt,
        Func<KastnTemplateLanePrompt, Task<KastnTemplateLaneChoice?>>? lane = null) =>
        InvokeWorkflow(window, "CreateProjectFromTemplateAsync", template, view!, prompt,
            lane ?? (_ => throw new Exception("Unexpected lane prompt.")));
}
