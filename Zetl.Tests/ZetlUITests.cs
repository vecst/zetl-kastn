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

public class ZetlUITests : IDisposable
{
    private readonly string defaultDraftDirectory;

    public ZetlUITests()
    {
        defaultDraftDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "KastnUiDrafts",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(defaultDraftDirectory);
        KastnDraftStore.DefaultPathOverride = System.IO.Path.Combine(
            defaultDraftDirectory,
            "draft.json");
    }

    public void Dispose()
    {
        KastnDraftStore.DefaultPathOverride = null;
        if (System.IO.Directory.Exists(defaultDraftDirectory))
        {
            System.IO.Directory.Delete(defaultDraftDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void MainWindowLoadsWithoutCrashing()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        
        Assert.NotNull(window);
        
        window.Show();
        
        Assert.True(window.IsVisible);
        
        CloseWindow(window);
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
        
        CloseWindow(window);
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
                    FontFamily = "Georgia",
                    FontSize = 18,
                    TextColor = "#2563EB",
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
        var node = window.treeProjection.Find("slip-1");
        Assert.NotNull(node);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            window.projectTree.SelectedItem = node;
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 5. Assert editor binds to the selected slip
        Assert.Equal("Hello from the test note", window.slipEditor.Text);
        Assert.False(window.editorState.IsDirty);
        Assert.Equal("Georgia", Assert.IsType<KastnFontFamilyItem>(window.fontFamilyBox.SelectedItem).Value);
        Assert.Equal(18, Assert.IsType<KastnFontSizeItem>(window.fontSizeBox.SelectedItem).Value);
        Assert.Equal("#2563EB", Assert.IsType<KastnTextColorItem>(window.textColorBox.SelectedItem).Value);
        Assert.Equal(18, window.slipEditor.FontSize);
        Assert.Equal(
            Avalonia.Media.Color.FromRgb(0x25, 0x63, 0xEB),
            Assert.IsType<Avalonia.Media.SolidColorBrush>(window.slipEditor.Foreground).Color);

        // 6. Modify editor text and assert it marks the state as dirty
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            window.slipEditor.Text = "Hello from the test note - EDITED";
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(window.editorState.IsDirty);
        Assert.Equal("Hello from the test note - EDITED", window.editorState.DraftText);

        CloseWindow(window);
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


        void Select(string slipId)
        {
            var node = window.treeProjection.Find(slipId);
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

        CloseWindow(window);
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

        var slipNode = window.treeProjection.Find("slip-a");
        var bucketNode = window.treeProjection.Find("b-2");
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

        CloseWindow(window);
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

        CloseWindow(window);
    }

    [AvaloniaFact]
    public void BoardComposerOpensCommitsAndDiscards()
    {
        // The inline card composer: + opens and focuses it, Escape discards and
        // closes, and a commit that fails (offline here) keeps the composer open
        // with the typed text so capture is never silently lost.
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
                new ZETL.Contracts.ZetlBucketSnapshot { Id = "b-1", Revision = 1, Name = "One" }
            },
            Slips = Array.Empty<ZETL.Contracts.ZetlSlipSnapshot>()
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

        var columnWrapper = (Avalonia.Controls.Grid)window.boardColumnsPanel.Children[0];
        var mainGrid = (Avalonia.Controls.Grid)((Avalonia.Controls.Border)columnWrapper.Children[0]).Child!;
        var headerGrid = (Avalonia.Controls.Grid)mainGrid.Children[0];
        var addButton = (Avalonia.Controls.Button)headerGrid.Children[2];
        var composer = (Avalonia.Controls.Border)mainGrid.Children[2];
        var composerBox = (Avalonia.Controls.TextBox)composer.Child!;
        Assert.False(composer.IsVisible);

        addButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(composer.IsVisible, "The + button opens the composer.");

        // Enter with a dead connection: the add fails, so the text must survive.
        composerBox.Text = "typed card";
        composerBox.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.Enter
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(composer.IsVisible, "A failed commit keeps the composer open.");
        Assert.Equal("typed card", composerBox.Text);

        // Escape is the explicit discard.
        composerBox.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.Escape
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(composer.IsVisible, "Escape closes the composer.");
        Assert.True(string.IsNullOrEmpty(composerBox.Text), "Escape discards the draft.");

        CloseWindow(window);
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
        var sourceNode = window.treeProjection.Find("slip-source");
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

        CloseWindow(window);
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
        var targetNode = window.treeProjection.Find("slip-target");
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

        CloseWindow(window);
    }

    [AvaloniaFact]
    public void MainWindowMinimizesNormallyAndCloseBehaviorFollowsSetting()
    {
        var tempFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            System.IO.Path.GetRandomFileName() + ".json");
        try
        {
            ZetlAppSettingsStore.DefaultSettingsPathOverride = tempFile;
            var settingsStore = new ZetlAppSettingsStore();
            settingsStore.Settings.KastnCloseToTray = true;
            settingsStore.Save();

            var connection = new KastnConnectionController(_ => Task.CompletedTask);
            var window = new MainWindow(connection);
            window.Show();

            window.WindowState = Avalonia.Controls.WindowState.Minimized;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible);
            Assert.True(window.ShowInTaskbar);
            Assert.Equal(Avalonia.Controls.WindowState.Minimized, window.WindowState);

            window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.False(window.IsVisible);
            Assert.False(window.ShowInTaskbar);

            CloseWindow(window);

            settingsStore.Settings.KastnCloseToTray = false;
            settingsStore.Save();

            var exitWindow = new MainWindow(new KastnConnectionController(_ => Task.CompletedTask));
            var closed = false;
            exitWindow.Closed += (_, _) => closed = true;
            exitWindow.Show();
            exitWindow.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(closed);
        }
        finally
        {
            ZetlAppSettingsStore.DefaultSettingsPathOverride = null;
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }

    [AvaloniaFact]
    public async Task MainWindowRestoresPersistedDraftIntoTheMatchingSlip()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "KastnUiTests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        MainWindow? window = null;
        try
        {
            var store = new ZetlStateStore(
                System.IO.Path.Combine(directory, "state.json"),
                "kastn-ui");
            var project = store.CreateProject("Recovery", ["Inbox"], "Inbox");
            var slip = store.AddSlip(project.Buckets[0], "baseline", "copy");
            var pipeName = $"kastn-ui-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName);
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."),
                pipeName,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(
                () => controller.Current.Project?.Id == project.Id,
                "Kastn should load the recovery project.");

            var draftStore = new KastnDraftStore(System.IO.Path.Combine(directory, "draft.json"));
            Assert.True(draftStore.Save(new KastnDraftDocument
            {
                ProjectId = project.Id,
                SlipId = slip.Id,
                BaselineRevision = slip.Revision,
                BaselineText = slip.Text,
                DraftText = "recovered local writing",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }));

            window = new MainWindow(controller, draftStore);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(slip.Id, window.editorState.SlipId);
            Assert.Equal("recovered local writing", window.editorState.DraftText);
            Assert.True(window.editorState.IsDirty);
            Assert.Null(window.editorState.ConflictCurrent);
        }
        finally
        {
            if (window is not null)
            {
                CloseWindow(window);
            }
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task ClosingAnOnlineDirtyWindowSavesBeforeExit()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "KastnUiTests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var settingsPath = System.IO.Path.Combine(directory, "settings.json");
        MainWindow? window = null;
        try
        {
            ZetlAppSettingsStore.DefaultSettingsPathOverride = settingsPath;
            var settings = new ZetlAppSettingsStore();
            settings.Settings.KastnCloseToTray = false;
            settings.Save();

            var store = new ZetlStateStore(
                System.IO.Path.Combine(directory, "state.json"),
                "kastn-ui");
            var project = store.CreateProject("Close Save", ["Inbox"], "Inbox");
            var slip = store.AddSlip(project.Buckets[0], "before close", "copy");
            var pipeName = $"kastn-ui-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName);
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."),
                pipeName,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(
                () => controller.Current.Project?.Id == project.Id,
                "Kastn should load the close-save project.");

            var draftStore = new KastnDraftStore(System.IO.Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, draftStore);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            window.slipEditor.Text = "saved during close";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(window.editorState.IsDirty);

            window.Close();
            await WaitForConditionAsync(() => closed, "The window should close after saving its editor.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal("saved during close", slip.Text);
            Assert.Null(draftStore.Draft);
            window = null;
        }
        finally
        {
            ZetlAppSettingsStore.DefaultSettingsPathOverride = null;
            if (window is not null)
            {
                CloseWindow(window);
            }
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task ActivateRequestWithoutProjectKeepsOpenProject()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "KastnUiTests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var pipeName = $"kastn-ui-{Guid.NewGuid():N}";
        MainWindow? window = null;
        try
        {
            var store = new ZetlStateStore(
                System.IO.Path.Combine(directory, "state.json"),
                "kastn-ui");
            var project = store.CreateProject("Open Project", ["Inbox"], "Inbox");
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName);
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."),
                pipeName,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(
                () => controller.Current.Project?.Id == project.Id,
                "Kastn should open the requested project before activation.");

            window = new MainWindow(controller);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            await window.ActivateRequestAsync(null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(project.Id, controller.Current.Project?.Id);
        }
        finally
        {
            if (window is not null)
            {
                CloseWindow(window);
            }

            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void CloseWindow(MainWindow window)
    {
        window.CloseForShutdown();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void SelectionChangesReuseDocumentAndExportText()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        window.Show();
        try
        {
            PublishRenderSnapshot(connection, RenderProject("render-project", "original text"));
            var generation = WindowField<int>(window, "pictureRenderGeneration");
            var export = WindowField<string>(window, "lastRenderedViewText");
            var second = window.treeProjection.Find("render-two");
            Assert.NotNull(second);

            window.projectTree.SelectedItem = second;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal("second text", window.slipEditor.Text);
            Assert.Equal(generation, WindowField<int>(window, "pictureRenderGeneration"));
            Assert.Same(export, WindowField<string>(window, "lastRenderedViewText"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void SwitchingProjectsWithMatchingRevisionsRebuildsTheDocument()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        window.Show();
        try
        {
            PublishRenderSnapshot(connection, RenderProject("first-project", "original text"));
            var generation = WindowField<int>(window, "pictureRenderGeneration");

            // Same slip ids, revisions, view id, and change sequence; only the
            // project identity and content differ.
            PublishRenderSnapshot(connection, RenderProject("second-project", "replacement text"));

            Assert.True(WindowField<int>(window, "pictureRenderGeneration") > generation);
            Assert.Contains("replacement text", WindowField<string>(window, "lastRenderedViewText"));
            Assert.DoesNotContain("original text", WindowField<string>(window, "lastRenderedViewText"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public async Task SavingPreservesLaterTypingAndRebasesItsRecoveryJournal()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KastnUiTests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        MainWindow? window = null;
        using var releaseResponse = new System.Threading.ManualResetEventSlim();
        var commandArrived = new TaskCompletionSource<ZETL.Contracts.ZetlCommandEnvelope>();
        try
        {
            var store = new ZetlStateStore(System.IO.Path.Combine(directory, "state.json"), "kastn-ui");
            var project = store.CreateProject("Save race", ["Inbox"], "Inbox");
            var slip = store.AddSlip(project.Buckets[0], "baseline", "copy");
            var pipeName = $"kastn-ui-{Guid.NewGuid():N}";
            using var server = new ZetlIpcServer(new ZetlProjectService(store), pipeName, log: null,
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZETL.Contracts.ZetlCommandKind.UpdateSlip
                        && commandArrived.TrySetResult(command))
                    {
                        releaseResponse.Wait(TimeSpan.FromSeconds(5));
                    }
                    return false;
                });
            server.Start();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."), pipeName,
                TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(25));
            controller.Start(project.Id);
            await WaitForConditionAsync(() => controller.Current.Project?.Id == project.Id, "Project should load.");

            var drafts = new KastnDraftStore(System.IO.Path.Combine(directory, "draft.json"));
            window = new MainWindow(controller, drafts);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var saveMethod = typeof(MainWindow).GetMethod("SaveEditorAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var flushMethod = typeof(MainWindow).GetMethod("FlushDraftJournal",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            window.slipEditor.Text = "sent draft";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True((bool)flushMethod.Invoke(window, null)!);
            var saving = (Task<bool>)saveMethod.Invoke(window, null)!;
            Assert.Same(saving, saveMethod.Invoke(window, null));
            try
            {
                await commandArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.slipEditor.Text = "sent draft plus later typing";
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var snapshot = controller.Current.Project!;
                PublishRenderSnapshot(controller, snapshot with
                {
                    ChangeSequence = project.ChangeSequence,
                    Slips = [snapshot.Slips[0] with { Text = "sent draft", Revision = slip.Revision }]
                });
                Assert.Equal("sent draft plus later typing", window.slipEditor.Text);
                Assert.Null(window.editorState.ConflictCurrent);
            }
            finally
            {
                releaseResponse.Set();
            }

            Assert.False(await saving.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("sent draft plus later typing", window.slipEditor.Text);
            Assert.Equal(slip.Revision, drafts.Draft?.BaselineRevision);
            Assert.Equal("sent draft", drafts.Draft?.BaselineText);
            Assert.Equal("sent draft plus later typing", drafts.Draft?.DraftText);
            Assert.True(window.editorState.IsDirty);

            Assert.True(await ((Task<bool>)saveMethod.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("sent draft plus later typing", slip.Text);
            Assert.Null(drafts.Draft);
            Assert.False(window.editorState.IsDirty);
        }
        finally
        {
            releaseResponse.Set();
            if (window is not null)
            {
                CloseWindow(window);
            }
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void ToolbarFollowsSingleBatchAndBucketSelection()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var project = RenderProject("selection-project", "first text");
        project = project with
        {
            Buckets = [.. project.Buckets, new() { Id = "destination", Name = "Other", Revision = 1 }],
            Slips = [project.Slips[0], project.Slips[1] with { BucketId = "destination" }]
        };
        typeof(KastnConnectionController).GetProperty(nameof(KastnConnectionController.Current))!
            .SetValue(connection, new KastnSessionSnapshot(KastnConnectionState.Online, "Connected", [], project));
        var window = new MainWindow(connection);
        window.Show();
        try
        {
            var first = window.treeProjection.Find("render-one")!;
            var second = window.treeProjection.Find("render-two")!;
            window.projectTree.SelectedItem = first;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(window.slipEditor.IsEnabled);
            Assert.True(window.codeButton.IsEnabled);
            Assert.True(window.alignLeftButton.IsEnabled);
            Assert.True(window.moveSlipButton.IsEnabled);

            window.projectTree.SelectedItems!.Add(second);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(window.editorState.SlipId);
            Assert.False(window.slipEditor.IsEnabled);
            Assert.False(window.codeButton.IsEnabled);
            Assert.True(window.fontFamilyBox.IsEnabled);
            Assert.True(window.alignLeftButton.IsEnabled);
            Assert.True(window.deleteSlipButton.IsEnabled);

            window.projectTree.SelectedItem = window.treeProjection.Find("render-bucket");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(window.slipEditor.IsEnabled);
            Assert.False(window.fontFamilyBox.IsEnabled);
            Assert.True(window.boldButton.IsEnabled);
            Assert.True(window.bulletListButton.IsEnabled);
            Assert.True(window.alignLeftButton.IsEnabled);
            Assert.False(window.deleteSlipButton.IsEnabled);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [AvaloniaFact]
    public void FailedOfflineSaveRestoresSelectionWithoutDiscardingDraft()
    {
        var connection = new KastnConnectionController(_ => Task.CompletedTask);
        var window = new MainWindow(connection);
        window.Show();
        try
        {
            PublishRenderSnapshot(connection, RenderProject("selection-project", "first text"));
            window.projectTree.SelectedItem = window.treeProjection.Find("render-one");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.slipEditor.Text = "unsaved local writing";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // Publishing a snapshot does not connect the test controller. Leaving
            // this dirty editor must fail the save and restore the original row.
            window.projectTree.SelectedItem = window.treeProjection.Find("render-two");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal("render-one", Assert.IsType<KastnTreeNode>(window.projectTree.SelectedItem).Id);
            Assert.Equal("render-one", window.editorState.SlipId);
            Assert.Equal("unsaved local writing", window.slipEditor.Text);
            Assert.True(window.editorState.IsDirty);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    private static T WindowField<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;

    private static ZETL.Contracts.ZetlProjectSnapshot RenderProject(string id, string text) => new()
    {
        Id = id, Name = id, MetadataRevision = 1, ChangeSequence = 1,
        Buckets = [new() { Id = "render-bucket", Name = "Bucket", Revision = 1 }],
        Slips = new[] { ("render-one", text), ("render-two", "second text") }
            .Select(item => new ZETL.Contracts.ZetlSlipSnapshot
            {
                Id = item.Item1, Revision = 1, Type = ZETL.Contracts.ZetlSlipType.Text,
                BucketId = "render-bucket", Text = item.Item2, Source = "copy",
                CapturedAtUtc = DateTimeOffset.UtcNow
            }).ToArray()
    };

    private static void PublishRenderSnapshot(KastnConnectionController connection, ZETL.Contracts.ZetlProjectSnapshot project)
    {
        var snapshot = new KastnSessionSnapshot(KastnConnectionState.Online, "Connected",
        [
            new() { Id = project.Id, Name = project.Name, MetadataRevision = project.MetadataRevision, ChangeSequence = project.ChangeSequence }
        ], project);
        var callback = (EventHandler<KastnSessionSnapshot>?)typeof(KastnConnectionController)
            .GetField("SnapshotChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(connection);
        callback?.Invoke(connection, snapshot);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, string failureMessage)
    {
        var stopAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < stopAt)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), failureMessage);
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
