using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public partial class ZetlUITests
{
    [AvaloniaTheory]
    [InlineData("button")]
    [InlineData("menu")]
    public void RepeatedBoardActionsKeepPaneWidthsAndComposerDrafts(string route)
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            var widths = new[] { new GridLength(247), new GridLength(5), new GridLength(7), new GridLength(341) };
            var indices = new[] { 0, 1, 3, 4 };
            for (var i = 0; i < indices.Length; i++) window.mainColumnsGrid.ColumnDefinitions[indices[i]].Width = widths[i];
            window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var board = WindowField<KastnBoardPresenter>(window, "boardPresenter");
            var card = board.Card("render-one");
            var composer = BoardComposerControls(board.ColumnCards("render-bucket")!);
            composer.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            composer.Box.Text = "Keep this board draft";
            if (route == "button") window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else window.boardModeMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(card, board.Card("render-one"));
            Assert.Equal("Keep this board draft", composer.Box.Text);
            window.viewModeListButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.viewModeListButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < indices.Length; i++) Assert.Equal(widths[i], window.mainColumnsGrid.ColumnDefinitions[indices[i]].Width);
            Assert.True(window.treePaneBorder.IsVisible);
            Assert.True(window.rightPaneBorder.IsVisible);
            Assert.False(window.boardModeMenuItem.IsChecked);
            window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(composer.Box, BoardComposerControls(board.ColumnCards("render-bucket")!).Box);
            Assert.Equal("Keep this board draft", composer.Box.Text);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public void AdvertisedBoardShortcutTogglesOnceAndLeavesOtherModifiersUnclaimed()
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            window.slipEditor.Focus();
            var widths = window.mainColumnsGrid.ColumnDefinitions.Select(column => column.Width).ToArray();
            var first = WindowShortcut(window.slipEditor, Key.B);
            Assert.True(first.Handled);
            Assert.True(window.boardPanel.IsVisible);
            Assert.True(window.boardModeMenuItem.IsChecked);
            Assert.False(WindowShortcut(window, Key.B, KeyModifiers.Control | KeyModifiers.Shift).Handled);
            Assert.True(window.boardPanel.IsVisible);
            Assert.True(WindowShortcut(window, Key.B).Handled);
            Assert.False(window.boardPanel.IsVisible);
            Assert.Equal(widths, window.mainColumnsGrid.ColumnDefinitions.Select(column => column.Width));
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaTheory]
    [InlineData("menu")]
    [InlineData("keyboard")]
    public async Task AdvertisedNewSlipRoutesSaveTheDraftAndCreateExactlyOneSlip(string route)
    {
        var writes = new List<ZetlCommandKind>();
        await using var h = await NativeActionHarness.CreateAsync(command => { lock (writes) writes.Add(command.Kind); });
        var window = h.Window;
        var count = h.Source.Buckets.Sum(bucket => bucket.Slips.Count);
        window.slipEditor.Text = "Draft saved before creating";
        Dispatcher.UIThread.RunJobs();
        if (route == "menu") window.newSlipMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        else Assert.True(WindowShortcut(window.slipEditor, Key.N).Handled);
        await WaitForConditionAsync(() => h.Source.Buckets.Sum(bucket => bucket.Slips.Count) == count + 1
            && !WindowField<KastnMutationCoordinator>(window, "mutations").IsBusy, "New-slip route should complete.");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Draft saved before creating", h.EditedSlip.Text);
        var created = h.Source.Buckets.SelectMany(bucket => bucket.Slips).Single(slip => slip.Source == "kastn");
        Assert.Contains(created, h.Source.Buckets[0].Slips);
        Assert.Equal(created.Id, window.editorState.SlipId);
        lock (writes)
        {
            Assert.Single(writes.Where(kind => kind == ZetlCommandKind.UpdateSlip));
            Assert.Single(writes.Where(kind => kind == ZetlCommandKind.AddSlip));
        }
    }

    [AvaloniaTheory]
    [InlineData("menu")]
    [InlineData("keyboard")]
    public async Task NewSlipClearsTheTypeFilterAndBindsTheCreatedTextSlip(string route)
    {
        await using var h = await NativeActionHarness.CreateAsync(_ => { });
        var window = h.Window;
        window.typeFilterBox.SelectedIndex = 3; // Pictures excludes the text slips.
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Filters (1) ▾", window.filtersButton.Content);
        Assert.Null(window.editorState.SlipId);
        if (route == "menu") window.newSlipMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        else Assert.True(WindowShortcut(window, Key.N).Handled);
        await WaitForConditionAsync(() => h.Source.Buckets.SelectMany(bucket => bucket.Slips).Any(slip => slip.Source == "kastn")
            && !WindowField<KastnMutationCoordinator>(window, "mutations").IsBusy, "Filtered creation should settle.");
        Dispatcher.UIThread.RunJobs();
        var created = h.Source.Buckets.SelectMany(bucket => bucket.Slips).Single(slip => slip.Source == "kastn");
        Assert.Equal(0, window.typeFilterBox.SelectedIndex);
        Assert.Equal("Filters ▾", window.filtersButton.Content);
        Assert.Equal(created.Id, window.editorState.SlipId);
    }

    [AvaloniaTheory]
    [InlineData("menu")]
    [InlineData("keyboard")]
    public void FindRoutesFocusAndSelectTheExistingSearch(string route)
    {
        var (window, _, _) = WorkflowWindow();
        try
        {
            window.searchBox.Text = "baseline";
            Dispatcher.UIThread.RunJobs();
            window.projectTree.Focus();
            if (route == "menu") window.focusSearchMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else Assert.True(WindowShortcut(window.projectTree, Key.F).Handled);
            Assert.True(window.searchBox.IsFocused);
            Assert.Equal("baseline", window.searchBox.SelectedText);
        }
        finally { CloseWindow(window); }
    }

    [AvaloniaFact]
    public async Task RetiredWindowRejectsKeyboardAndBoardRoutesWithoutTransportWork()
    {
        var calls = 0;
        await using var h = await NativeActionHarness.CreateAsync(_ => Interlocked.Increment(ref calls));
        var window = h.Window;
        CloseWindow(window);
        var baseline = Volatile.Read(ref calls);
        Assert.False(WindowShortcut(window, Key.N).Handled);
        Assert.False(WindowShortcut(window, Key.B).Handled);
        Assert.False(WindowShortcut(window, Key.F5, KeyModifiers.None).Handled);
        window.viewModeBoardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.refreshMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await InvokeWorkflow(window, "RefreshAsync");
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.boardPanel.IsVisible);
        Assert.Equal(baseline, Volatile.Read(ref calls));
    }

    [AvaloniaFact]
    public void ToolingConstructorCreatesOwnersWithoutRuntimeInputOrSubscription()
    {
        var window = new MainWindow();
        try
        {
            window.Show();
            Assert.Throws<ArgumentNullException>(() => new MainWindow(null!));
            Assert.NotNull(WindowField<KastnLandingPage>(window, "landing"));
            Assert.NotNull(WindowField<KastnMutationCoordinator>(window, "mutations"));
            Assert.Null(window.landingLaneItems.ItemsSource);
            Assert.False(WindowShortcut(window, Key.B).Handled);
            Assert.False(WindowShortcut(window, Key.F5, KeyModifiers.None).Handled);
            CloseWindow(window);
            window.RetireLifetime();
            Assert.False(window.IsVisible);
            Assert.False(window.saveSlipMenuItem.IsEnabled);
        }
        finally { CloseWindow(window); }
    }

    private static KeyEventArgs WindowShortcut(Control target, Key key, KeyModifiers modifiers = KeyModifiers.Control)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        target.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
        return args;
    }
}
