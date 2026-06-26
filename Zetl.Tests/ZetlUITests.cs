using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using KASTN;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ZETL.Tests.TestAppBuilder))]

namespace ZETL.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<KASTN.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true
            });
    }
}

public class ZetlUITests
{
    [AvaloniaFact]
    public void MainWindowLoadsWithoutCrashing()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        
        Assert.NotNull(window);
        
        window.Show();
        
        Assert.True(window.IsVisible);
        
        window.Close();
    }

    [AvaloniaFact]
    public void BoardModeToggleChangesUIState()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        
        window.Show();
        
        // Assert initial state is List Mode (not Board Mode)
        Assert.False(window.boardModeMenuItem.IsChecked);
        Assert.DoesNotContain("view-format-active", window.viewModeBoardButton.Classes);
        Assert.Contains("view-format-active", window.viewModeListButton.Classes);
        
        // Simulate clicking the "Board Mode" button
        window.viewModeBoardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        
        // Assert state updated to Board Mode
        Assert.True(window.boardModeMenuItem.IsChecked);
        Assert.Contains("view-format-active", window.viewModeBoardButton.Classes);
        Assert.DoesNotContain("view-format-active", window.viewModeListButton.Classes);
        
        // Simulate clicking the "List Mode" button
        window.viewModeListButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        
        // Assert state reverted to List Mode
        Assert.False(window.boardModeMenuItem.IsChecked);
        Assert.DoesNotContain("view-format-active", window.viewModeBoardButton.Classes);
        Assert.Contains("view-format-active", window.viewModeListButton.Classes);
        
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectLoadsAndEditorBindsAndEditsText()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        window.Show();

        // 1. Build a dummy project
        var project = new ZETL.Contracts.ZetlProjectSnapshot
        {
            Id = "proj-1",
            Name = "Test Project",
            MetadataRevision = 1,
            ChangeSequence = 1,
            Buckets = new[]
            {
                new ZETL.Contracts.ZetlBucketSnapshot { Id = "b-inbox", Revision = 1, Name = "Inbox" }
            },
            Slips = new[]
            {
                new ZETL.Contracts.ZetlSlipSnapshot
                {
                    Id = "slip-1",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-inbox",
                    Title = "First Note",
                    Text = "Hello from the test note",
                    Source = "copy",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                }
            }
        };

        // 2. Build session snapshot and publish it
        var snapshot = new KastnSessionSnapshot(
            KastnConnectionState.Online,
            "Connected",
            new[] { new ZETL.Contracts.ZetlProjectSummary { Id = "proj-1", Name = "Test Project", MetadataRevision = 1, ChangeSequence = 1 } },
            project);

        var eventField = typeof(KastnConnectionController)
            .GetField("SnapshotChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(eventField);
        var eventDelegate = (EventHandler<KastnSessionSnapshot>?)eventField.GetValue(connection);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            eventDelegate?.Invoke(connection, snapshot);
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 3. Assert window elements update
        Assert.True(window.projectView.IsVisible);
        Assert.False(window.emptyState.IsVisible);
        Assert.Equal("Test Project", window.projectTitle.Text);

        // 4. Find the node representing "slip-1" and select it
        var findMethod = typeof(MainWindow).GetMethod("FindTreeNode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(findMethod);
        
        var node = (KastnTreeNode?)findMethod.Invoke(null, new object?[] { window.projectTree.ItemsSource, "slip-1" });
        Assert.NotNull(node);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            window.projectTree.SelectedItem = node;
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 5. Assert editor binds to the selected slip
        Assert.Equal("Hello from the test note", window.slipEditor.Text);
        Assert.False(window.editorState.IsDirty);

        // 6. Modify editor text and assert it marks the state as dirty
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            window.slipEditor.Text = "Hello from the test note - EDITED";
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(window.editorState.IsDirty);
        Assert.Equal("Hello from the test note - EDITED", window.editorState.DraftText);

        window.Close();
    }
}
