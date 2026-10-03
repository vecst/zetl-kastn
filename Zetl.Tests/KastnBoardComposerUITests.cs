using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KASTN;
using ZETL.Contracts;
using Xunit;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("unchanged")]
    [InlineData("typing")]
    [InlineData("project")]
    [InlineData("focus-away")]
    public async Task BoardComposerIpcCompletionKeepsOriginalProjectAndNewerWriting(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "KastnBoardComposerUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var release = new ManualResetEventSlim();
        var arrived = new TaskCompletionSource<ZetlCommandEnvelope>();
        var commands = new ConcurrentQueue<ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Composer", ["Inbox", "Next"], "Inbox");
            var inbox = project.Buckets.First(bucket => bucket.Name == "Inbox");
            var replacement = store.CreateProject("Other", ["Inbox"], "Inbox");
            replacement.Buckets[0].Id = inbox.Id;
            var pipeName = $"kastn-composer-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.AddSlip)
                    {
                        commands.Enqueue(command);
                        if (arrived.TrySetResult(command)) release.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl already running."), pipeName,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");
            window = new MainWindow(controller, new KastnDraftStore(Path.Combine(directory, "draft.json")));
            window.Show();
            window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var board = WindowField<KastnBoardPresenter>(window, "boardPresenter");
            var composer = BoardComposerControls(board.ColumnCards(inbox.Id)!);
            composer.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            composer.Box.Text = " submitted card ";
            var column = BoardRecords(board, "boardColumns")[inbox.Id]!;
            BoardComposerKey(composer.Box, Key.Enter);
            var command = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var payload = command.Payload!.Value.Deserialize<AddSlipCommand>(ZetlProtocolJson.Options)!;
            Assert.Equal(project.Id, command.ProjectId);
            Assert.Equal(inbox.Id, payload.BucketId);
            Assert.Equal("submitted card", payload.Text);
            Assert.Equal("kastn", payload.Source);
            Task? navigation = null;
            TextBox? otherBox = null;
            if (change == "typing")
            {
                composer.Box.Text = "later writing";
                // An early snapshot of our own add must reuse the column draft.
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(project));
            }
            else if (change == "project")
            {
                navigation = controller.NavigateToProjectAsync(replacement.Id);
                PublishRenderSnapshot(controller, ZetlProjectSnapshotMapper.ToSnapshot(replacement));
                var newComposer = BoardComposerControls(board.ColumnCards(inbox.Id)!);
                newComposer.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                newComposer.Box.Text = "other project draft";
            }
            else if (change == "focus-away")
            {
                var next = project.Buckets.First(bucket => bucket.Name == "Next");
                var other = BoardComposerControls(board.ColumnCards(next.Id)!);
                other.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                otherBox = other.Box;
                Assert.True(otherBox.IsFocused);
            }
            Dispatcher.UIThread.RunJobs();
            release.Set();
            await WaitForConditionAsync(() => !(bool)column.GetType().GetProperty("ComposerBusy")!.GetValue(column)!,
                "Composer submission should settle.");
            if (navigation is not null) await navigation.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(commands);
            Assert.Equal("submitted card", Assert.Single(inbox.Slips).Text);
            Assert.Empty(replacement.Buckets[0].Slips);
            Assert.Equal(change switch { "typing" => "later writing", "project" => " submitted card ", _ => "" }, composer.Box.Text);
            Assert.Equal(change == "project" ? 0 : 1, WindowField<KastnEditHistory>(window, "editHistory").UndoCount);
            if (change == "project")
            {
                Assert.Equal("other project draft", BoardComposerControls(board.ColumnCards(inbox.Id)!).Box.Text);
                Assert.NotEqual("Card added.", window.statusText.Text);
            }
            if (otherBox is not null) Assert.True(otherBox.IsFocused);
        }
        finally
        {
            release.Set();
            if (window is not null) CloseWindow(window);
            Directory.Delete(directory, recursive: true);
        }
    }
}
