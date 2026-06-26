using System;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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

    [AvaloniaFact]
    public void WikiLinkClickNavigatesToTarget()
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
                    Id = "slip-source",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-inbox",
                    Title = "Source Note",
                    Text = "See [[slip-target|Target Link]]",
                    Source = "copy",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                },
                new ZETL.Contracts.ZetlSlipSnapshot
                {
                    Id = "slip-target",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-inbox",
                    Title = "Target Note",
                    Text = "Hello from the target note",
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

        // 3. Select "slip-source"
        var findMethod = typeof(MainWindow).GetMethod("FindTreeNode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(findMethod);
        
        var sourceNode = (KastnTreeNode?)findMethod.Invoke(null, new object?[] { window.projectTree.ItemsSource, "slip-source" });
        Assert.NotNull(sourceNode);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            window.projectTree.SelectedItem = sourceNode;
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 4. Find the link TextBlock inside viewerDocumentPanel
        var textBlocks = FindVisualChildren<Avalonia.Controls.TextBlock>(window.viewerDocumentPanel);
        var linkBlock = textBlocks.FirstOrDefault(tb => tb.TextDecorations == Avalonia.Media.TextDecorations.Underline);
        Assert.NotNull(linkBlock);
        Assert.Equal("Target Link", linkBlock.Text);

        // 5. Simulate click on the link (pointer pressed event)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var pointer = new Avalonia.Input.Pointer(0, Avalonia.Input.PointerType.Mouse, true);
            linkBlock.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
                linkBlock,
                pointer,
                linkBlock,
                new Point(),
                0,
                new Avalonia.Input.PointerPointProperties(),
                Avalonia.Input.KeyModifiers.None));
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 6. Assert selection has navigated to target node!
        var selectedNode = window.projectTree.SelectedItem as KastnTreeNode;
        Assert.NotNull(selectedNode);
        Assert.Equal("slip-target", selectedNode.Slip?.Id);

        window.Close();
    }

    [AvaloniaFact]
    public void BacklinkButtonClickNavigatesToSource()
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
                    Id = "slip-source",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-inbox",
                    Title = "Source Note",
                    Text = "See [[slip-target|Target Link]]",
                    Source = "copy",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                },
                new ZETL.Contracts.ZetlSlipSnapshot
                {
                    Id = "slip-target",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-inbox",
                    Title = "Target Note",
                    Text = "Hello from the target note",
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

        // 3. Select "slip-target"
        var findMethod = typeof(MainWindow).GetMethod("FindTreeNode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(findMethod);
        
        var targetNode = (KastnTreeNode?)findMethod.Invoke(null, new object?[] { window.projectTree.ItemsSource, "slip-target" });
        Assert.NotNull(targetNode);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            window.projectTree.SelectedItem = targetNode;
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 4. Find the backlink button in the inspector fields panel
        var buttons = FindVisualChildren<Avalonia.Controls.Button>(window.slipInspectorFieldsPanel);
        var backlinkButton = buttons.FirstOrDefault(b => b.Content as string == "Source Note");
        Assert.NotNull(backlinkButton);

        // 5. Simulate click on the backlink button
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            backlinkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 6. Assert selection has navigated back to source node!
        var selectedNode = window.projectTree.SelectedItem as KastnTreeNode;
        Assert.NotNull(selectedNode);
        Assert.Equal("slip-source", selectedNode.Slip?.Id);

        window.Close();
    }

    private static List<T> FindVisualChildren<T>(Avalonia.Visual parent) where T : Avalonia.Visual
    {
        var list = new List<T>();
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is T typed)
            {
                list.Add(typed);
            }
            list.AddRange(FindVisualChildren<T>(child));
        }
        return list;
    }
}
