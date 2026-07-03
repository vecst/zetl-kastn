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
    public void SwitchingSlipsMustNotLeaveThePreviousTextNativelyUndoable()
    {
        // Regression: selecting another slip swaps the editor text programmatically,
        // and a programmatic Text set enters an Avalonia TextBox's own undo history —
        // so with native undo enabled, Ctrl+Z in the auto-focused editor resurrected
        // slip A's text under slip B's selection (and autosave then wrote it into
        // slip B). The editor's native undo is permanently disabled; Kastn's single
        // ordered history owns Ctrl+Z instead.
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        window.Show();

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
                    Id = "slip-a",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-inbox",
                    Title = "A",
                    Text = "text of slip A",
                    Source = "copy",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                },
                new ZETL.Contracts.ZetlSlipSnapshot
                {
                    Id = "slip-b",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-inbox",
                    Title = "B",
                    Text = "text of slip B",
                    Source = "copy",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                }
            }
        };
        var snapshot = new KastnSessionSnapshot(
            KastnConnectionState.Online,
            "Connected",
            new[] { new ZETL.Contracts.ZetlProjectSummary { Id = "proj-1", Name = "Test Project", MetadataRevision = 1, ChangeSequence = 1 } },
            project);
        var eventField = typeof(KastnConnectionController)
            .GetField("SnapshotChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var eventDelegate = (EventHandler<KastnSessionSnapshot>?)eventField!.GetValue(connection);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => eventDelegate?.Invoke(connection, snapshot));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var findMethod = typeof(MainWindow).GetMethod(
            "FindTreeNode",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        void Select(string slipId)
        {
            var node = (KastnTreeNode?)findMethod.Invoke(null, new object?[] { window.projectTree.ItemsSource, slipId });
            Assert.NotNull(node);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => window.projectTree.SelectedItem = node);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Select("slip-a");
        Assert.Equal("text of slip A", window.slipEditor.Text);

        Select("slip-b");
        Assert.Equal("text of slip B", window.slipEditor.Text);
        Assert.False(
            window.slipEditor.CanUndo,
            "A slip switch must clear the editor's native undo history.");

        // Even if the box reported undo differently, undoing must not change the text.
        window.slipEditor.Undo();
        Assert.Equal(
            "text of slip B",
            window.slipEditor.Text);

        window.Close();
    }

    [AvaloniaFact]
    public void BoardCardsAndColumnsRenderDropMarkers()
    {
        // The board's drag feedback binds card and column overlays to the same
        // KastnTreeNode drop flags the tree template binds; flipping the flags must
        // light the card insertion lines, the column insertion lines, and the
        // column drop-into outline.
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        window.Show();

        var project = new ZETL.Contracts.ZetlProjectSnapshot
        {
            Id = "proj-1",
            Name = "Board Project",
            MetadataRevision = 1,
            ChangeSequence = 1,
            Buckets = new[]
            {
                new ZETL.Contracts.ZetlBucketSnapshot { Id = "b-1", Revision = 1, Name = "One" },
                new ZETL.Contracts.ZetlBucketSnapshot { Id = "b-2", Revision = 1, Name = "Two" }
            },
            Slips = new[]
            {
                new ZETL.Contracts.ZetlSlipSnapshot
                {
                    Id = "slip-a",
                    Revision = 1,
                    Type = ZETL.Contracts.ZetlSlipType.Text,
                    BucketId = "b-1",
                    Text = "card a",
                    Source = "copy",
                    CapturedAtUtc = DateTimeOffset.UtcNow
                }
            }
        };
        var snapshot = new KastnSessionSnapshot(
            KastnConnectionState.Online,
            "Connected",
            new[] { new ZETL.Contracts.ZetlProjectSummary { Id = "proj-1", Name = "Board Project", MetadataRevision = 1, ChangeSequence = 1 } },
            project);
        var eventField = typeof(KastnConnectionController)
            .GetField("SnapshotChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var eventDelegate = (EventHandler<KastnSessionSnapshot>?)eventField!.GetValue(connection);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => eventDelegate?.Invoke(connection, snapshot));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        window.viewModeBoardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(window.boardColumnsPanel.Children.Count >= 2, "The board should build a column per bucket.");

        var findMethod = typeof(MainWindow).GetMethod(
            "FindTreeNode",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var slipNode = (KastnTreeNode?)findMethod.Invoke(null, new object?[] { window.projectTree.ItemsSource, "slip-a" });
        var bucketNode = (KastnTreeNode?)findMethod.Invoke(null, new object?[] { window.projectTree.ItemsSource, "b-2" });
        Assert.NotNull(slipNode);
        Assert.NotNull(bucketNode);

        static List<Avalonia.Controls.Border> VisibleOverlays(Avalonia.Controls.Panel root)
        {
            var result = new List<Avalonia.Controls.Border>();
            void Walk(Avalonia.Controls.Control control)
            {
                if (control is Avalonia.Controls.Border { IsHitTestVisible: false, IsVisible: true } overlay)
                {
                    result.Add(overlay);
                }

                foreach (var child in ((Avalonia.LogicalTree.ILogical)control).LogicalChildren)
                {
                    if (child is Avalonia.Controls.Control childControl)
                    {
                        Walk(childControl);
                    }
                }
            }

            Walk(root);
            return result;
        }

        Assert.Empty(VisibleOverlays(window.boardColumnsPanel));

        Avalonia.Threading.Dispatcher.UIThread.Post(() => slipNode!.DropEdge = KastnDropEdge.Before);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains(VisibleOverlays(window.boardColumnsPanel), overlay => overlay.Height == 2);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            slipNode!.DropEdge = KastnDropEdge.None;
            bucketNode!.DropEdge = KastnDropEdge.After;
            bucketNode.IsDropTarget = true;
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var overlays = VisibleOverlays(window.boardColumnsPanel);
        Assert.Contains(overlays, overlay => overlay.Width == 3);
        Assert.Contains(overlays, overlay => overlay.BorderThickness.Left == 2);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            bucketNode!.DropEdge = KastnDropEdge.None;
            bucketNode.IsDropTarget = false;
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Empty(VisibleOverlays(window.boardColumnsPanel));

        window.Close();
    }

    [AvaloniaFact]
    public void BoardRefreshReusesUnchangedCardsAndColumns()
    {
        // Incremental board rendering: a refresh must reuse the column and card
        // controls whose inputs are unchanged (keeping layout and scroll state)
        // and rebuild only the affected card.
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        window.Show();

        ZETL.Contracts.ZetlSlipSnapshot Slip(string id, long revision, string bucketId, string text) => new()
        {
            Id = id,
            Revision = revision,
            Type = ZETL.Contracts.ZetlSlipType.Text,
            BucketId = bucketId,
            Text = text,
            Source = "copy",
            CapturedAtUtc = DateTimeOffset.UtcNow
        };

        var eventField = typeof(KastnConnectionController)
            .GetField("SnapshotChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var eventDelegate = (EventHandler<KastnSessionSnapshot>?)eventField!.GetValue(connection);

        void Publish(long sequence, params ZETL.Contracts.ZetlSlipSnapshot[] slips)
        {
            var project = new ZETL.Contracts.ZetlProjectSnapshot
            {
                Id = "proj-1",
                Name = "Board Project",
                MetadataRevision = 1,
                ChangeSequence = sequence,
                Buckets = new[]
                {
                    new ZETL.Contracts.ZetlBucketSnapshot { Id = "b-1", Revision = 1, Name = "One" },
                    new ZETL.Contracts.ZetlBucketSnapshot { Id = "b-2", Revision = 1, Name = "Two" }
                },
                Slips = slips
            };
            var snapshot = new KastnSessionSnapshot(
                KastnConnectionState.Online,
                "Connected",
                new[] { new ZETL.Contracts.ZetlProjectSummary { Id = "proj-1", Name = "Board Project", MetadataRevision = 1, ChangeSequence = sequence } },
                project);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => eventDelegate?.Invoke(connection, snapshot));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        static Avalonia.Controls.StackPanel CardsPanelOf(Avalonia.Controls.Control columnWrapper)
        {
            var border = (Avalonia.Controls.Border)((Avalonia.Controls.Grid)columnWrapper).Children[0];
            var mainGrid = (Avalonia.Controls.Grid)border.Child!;
            var scroll = (Avalonia.Controls.ScrollViewer)mainGrid.Children[1];
            return (Avalonia.Controls.StackPanel)scroll.Content!;
        }

        Publish(1, Slip("s1", 1, "b-1", "one"), Slip("s2", 1, "b-1", "two"));
        window.viewModeBoardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var column0 = window.boardColumnsPanel.Children[0];
        var column1 = window.boardColumnsPanel.Children[1];
        var cards0 = CardsPanelOf(column0);
        Assert.Equal(2, cards0.Children.Count);
        var card1 = cards0.Children[0];
        var card2 = cards0.Children[1];

        // Edit s2: its card rebuilds; everything else keeps its control instance.
        Publish(2, Slip("s1", 1, "b-1", "one"), Slip("s2", 2, "b-1", "two edited"));
        Assert.Same(column0, window.boardColumnsPanel.Children[0]);
        Assert.Same(column1, window.boardColumnsPanel.Children[1]);
        Assert.Same(card1, cards0.Children[0]);
        Assert.NotSame(card2, cards0.Children[1]);

        // Move s2 to the other column: its rebuilt card lands there; s1's card and
        // both columns are still the same instances.
        Publish(3, Slip("s1", 1, "b-1", "one"), Slip("s2", 3, "b-2", "two edited"));
        Assert.Same(column0, window.boardColumnsPanel.Children[0]);
        Assert.Same(card1, cards0.Children[0]);
        Assert.Single(cards0.Children);
        Assert.Single(CardsPanelOf(column1).Children);

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

    [AvaloniaFact]
    public void MainWindowMinimizationRespectsMinimizeToTraySetting()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + ".json");
        try
        {
            // 1. Arrange settings with override
            ZETL.ZetlAppSettingsStore.DefaultSettingsPathOverride = tempFile;
            var settingsStore = new ZETL.ZetlAppSettingsStore();
            settingsStore.Settings.KastnMinimizeToTray = true;
            settingsStore.Save();

            var connection = new KastnConnectionController(_ => Task.CompletedTask);
            var window = new MainWindow(connection);
            window.Show();

            // 2. Act - Minimize window when setting is true (default)
            window.WindowState = Avalonia.Controls.WindowState.Minimized;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // 3. Assert - it should be hidden (IsVisible is false, and ShowInTaskbar is false)
            Assert.False(window.IsVisible);
            Assert.False(window.ShowInTaskbar);

            // 4. Arrange - restore window and change setting to false
            window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Show();
            Assert.True(window.IsVisible);

            settingsStore.Settings.KastnMinimizeToTray = false;
            settingsStore.Save();

            // 5. Act - Minimize window when setting is false
            window.WindowState = Avalonia.Controls.WindowState.Minimized;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // 6. Assert - it should NOT be hidden (IsVisible is true, but WindowState is Minimized)
            Assert.True(window.IsVisible);
            Assert.Equal(Avalonia.Controls.WindowState.Minimized, window.WindowState);

            window.Close();
        }
        finally
        {
            ZETL.ZetlAppSettingsStore.DefaultSettingsPathOverride = null;
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
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
