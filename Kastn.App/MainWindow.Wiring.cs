using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    // Initialize picker/binding values before subscribing to their change events.
    // Owners are fully constructed before any native action can reach them.
    private void InitializeControlChoices()
    {
        landingLaneItems.ItemsSource = landing.Lanes;
        landingProjectList.ItemsSource = landing.RecentProjects;
        landingProjectWorkspaceList.ItemsSource = landing.Projects;
        landingTemplateItems.ItemsSource = landing.Templates;
        sourceFilterBox.ItemsSource = sources;
        sessionFilterBox.ItemsSource = sessions;
        dateFilterBox.ItemsSource = dates;
        parentBucketBox.ItemsSource = parentBuckets;
        moveBucketBox.ItemsSource = moveBuckets;
        bucketRenderKindBox.ItemsSource = bucketRenderKinds;
        fontFamilyBox.ItemsSource = FontFamilyChoices;
        fontSizeBox.ItemsSource = FontSizeChoices;
        textColorBox.ItemsSource = TextColorChoices;

        dates.Add(new DateFilterItem(KastnDateFilter.All, "All time"));
        dates.Add(new DateFilterItem(KastnDateFilter.Today, "Today"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last7Days, "Last 7 days"));
        dates.Add(new DateFilterItem(KastnDateFilter.Last30Days, "Last 30 days"));
        dateFilterBox.SelectedIndex = 0;
        typeFilterBox.ItemsSource = new[]
        {
            new TypeFilterItem(null, "All types"),
            new TypeFilterItem(ZetlSlipType.Text, "Text"),
            new TypeFilterItem(ZetlSlipType.Url, "Links"),
            new TypeFilterItem(ZetlSlipType.Picture, "Pictures")
        };
        typeFilterBox.SelectedIndex = 0;
        landingCreationItems.ItemsSource = landing.Creations;
        bucketHeadingSizeBox.ItemsSource = new[] { "Normal size", "Large", "Small" };
        bucketHeadingAlignBox.ItemsSource = new[] { "Left", "Center", "Right" };
        RefreshViewCatalog(null);
        RebuildTemplateCards();
    }

    private void WireControlEvents()
    {
        connection.SnapshotChanged += OnSnapshotChanged;
        landingProjectList.SelectionChanged += OnProjectSelectionChanged;
        landingProjectWorkspaceList.SelectionChanged += OnProjectSelectionChanged;
        projectTree.SelectionChanged += OnTreeSelectionChanged;
        SetupTreeDragDrop();
        detailEditorButton.Click += (_, _) => SetDetailPaneMode(showDetails: false);
        detailDetailsButton.Click += (_, _) => SetDetailPaneMode(showDetails: true);
        viewDeletedButton.IsCheckedChanged += (_, _) => OnViewDeletedToggled();
        bucketHeadingSizeBox.SelectionChanged += async (_, _) => await OnBucketHeadingChangedAsync();
        bucketHeadingAlignBox.SelectionChanged += async (_, _) => await OnBucketHeadingChangedAsync();
        bucketHeadingBoldCheck.IsCheckedChanged += async (_, _) => await OnBucketHeadingChangedAsync();
        searchBox.TextChanged += (_, _) => RefreshSlipView();
        sourceFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        sessionFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        dateFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        typeFilterBox.SelectionChanged += (_, _) => RefreshSlipView();
        slipEditor.TextChanged += (_, _) => OnEditorTextChanged();
        // Save when the editor loses focus rather than on a keystroke timer, so
        // typing is never interrupted by a mid-edit save + refresh. The user can turn
        // this off in Settings; switching slips and explicit Save still commit edits.
        slipEditor.LostFocus += async (_, _) =>
        {
            if (this.settings.Current.KastnAutosave)
            {
                await SaveEditorAsync();
            }
        };
        // Tunnel so Ctrl+Enter saves before the editor's AcceptsReturn turns it into a
        // newline; the explicit save keeps the caret so typing can continue.
        slipEditor.AddHandler(
            InputElement.KeyDownEvent,
            OnSlipEditorPreviewKeyDown,
            RoutingStrategies.Tunnel);
        slipEditor.KeyUp += OnSlipEditorKeyUp;
        slipEditor.PointerReleased += OnSlipEditorPointerReleased;
        // The editor has no undo stack of its own: every action — typing included —
        // undoes through Kastn's single ordered history, so Ctrl+Z walks all
        // interactions in the order they happened. Pending typing enters that
        // history via the save flush at the start of each undo/redo.
        slipEditor.IsUndoEnabled = false;

        refreshMenuItem.Click += async (_, _) => await RefreshAsync();
        journalModeMenuItem.Click += async (_, _) => await ToggleJournalModeAsync();
        closeProjectMenuItem.Click += async (_, _) => await CloseProjectAsync();
        deleteProjectMenuItem.Click += async (_, _) => await DeleteProjectAsync();
        exitMenuItem.Click += (_, _) => Close();
        newSlipMenuItem.Click += async (_, _) => await AddSlipAsync();
        saveSlipMenuItem.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipMenuItem.Click += async (_, _) => await DeleteSlipAsync();
        focusSearchMenuItem.Click += (_, _) => FocusSearch();
        focusProjectsMenuItem.Click += (_, _) => landingProjectsPanel.Focus();
        focusBucketsMenuItem.Click += (_, _) => projectTree.Focus();
        focusSlipsMenuItem.Click += (_, _) => FocusMainView();
        aboutMenuItem.Click += ShowAbout;
        addBucketButton.Click += async (_, _) => await AddBucketAsync();
        saveBucketButton.Click += async (_, _) => await SaveBucketAsync();
        deleteBucketButton.Click += async (_, _) => await DeleteBucketAsync();
        closeProjectButton.Click += async (_, _) => await CloseProjectAsync();
        newSlipButton.Click += async (_, _) => await AddSlipAsync();
        viewModeListButton.Click += (_, _) => SetBoardMode(false);
        viewModeBoardButton.Click += (_, _) => SetBoardMode(true);
        boardModeMenuItem.Click += (_, _) => SetBoardMode(boardModeMenuItem.IsChecked);
        // Alignment, bold/italic/strike, and the list markers are whole-slip render
        // properties: a single selected slip toggles its own, a multi-slip / bucket
        // selection applies the change to every selected slip at once (batch).
        // Inline emphasis within the text is typed Markdown; only the code and link
        // buttons still set style ranges over the editor selection.
        alignLeftButton.Click += async (_, _) => await AlignSlipsAsync("left");
        alignCenterButton.Click += async (_, _) => await AlignSlipsAsync("center");
        alignRightButton.Click += async (_, _) => await AlignSlipsAsync("right");
        fontFamilyBox.SelectionChanged += async (_, _) => await OnFontFamilyChangedAsync();
        fontSizeBox.SelectionChanged += async (_, _) => await OnFontSizeChangedAsync();
        textColorBox.SelectionChanged += async (_, _) => await OnTextColorChangedAsync();
        boldButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Bold);
        italicButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Italic);
        strikeButton.Click += async (_, _) => await ToggleSlipStyleAsync(ZetlInlineStyleKinds.Strike);
        codeButton.Click += async (_, _) => await ToggleInlineStyleAsync(ZetlInlineStyleKinds.Code);
        linkButton.Click += async (_, _) => await SetEditorWebLinkAsync();
        wikiLinkButton.Click += async (_, _) => await InsertSlipLinkAsync();
        ignoreBucketRenderKindCheck.IsCheckedChanged += async (_, _) => await OnIgnoreBucketRenderKindChangedAsync();
        representationToggleButton.Click += async (_, _) => await ToggleSlipRepresentationAsync();
        attachPictureButton.Click += async (_, _) => await AttachSlipPictureAsync();
        removePictureButton.Click += async (_, _) => await RemoveSlipPictureAsync();
        bulletListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Bullet);
        numberListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Ordered);
        taskListButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Task);
        headingButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Heading);
        quoteButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Quote);
        codeBlockButton.Click += async (_, _) => await ListSlipsAsync(ZetlBlockKinds.Code);
        insertDividerButton.Click += async (_, _) => await InsertDividerSlipAsync();
        insertGroupButton.Click += async (_, _) => await InsertGroupBucketAsync();
        viewPickerBox.SelectionChanged += (_, _) =>
        {
            if (!refreshing)
            {
                RefreshViewer();
            }
        };
        copyViewButton.Click += async (_, _) => await CopyRenderedViewAsync();
        exportViewButton.Click += async (_, _) => await ExportRenderedViewAsync();
        saveViewSettingsButton.Click += async (_, _) => await SaveViewAsync();
        cancelViewSettingsButton.Click += async (_, _) => await CancelViewEditAsync();
        newViewMenuItem.Click += (_, _) => OpenViewEditor(
            new ZetlViewDocument { Name = "", Category = "Custom", Kind = ZetlViewKinds.Markdown },
            isNew: true);
        editViewSettingsMenuItem.Click += (_, _) => EditSelectedView();
        deleteViewMenuItem.Click += async (_, _) => await DeleteSelectedViewAsync();
        saveSlipButton.Click += async (_, _) => await SaveEditorAsync();
        deleteSlipButton.Click += async (_, _) => await DeleteSlipAsync();
        moveSlipButton.Click += async (_, _) => await MoveSlipAsync();
        restoreSlipButton.Click += async (_, _) => await RestoreSlipAsync();
        useZetlButton.Click += (_, _) => UseZetlVersion();
        keepMineButton.Click += async (_, _) => await KeepMineAsync();
        landingProjectsButton.Click += (_, _) => ShowLandingSection(KastnLandingSection.Projects);
        landingTemplatesButton.Click += (_, _) => ShowLandingSection(KastnLandingSection.Templates);
        landingCreateButton.Click += (_, _) => ShowLandingSection(KastnLandingSection.Creations);
        landingCurrentProjectsButton.Click += (_, _) => SetArchivedProjectMode(showArchived: false);
        landingArchivedProjectsButton.Click += (_, _) => SetArchivedProjectMode(showArchived: true);
        landingCaptureButton.Click += (_, _) => SetTemplateType(consumable: false);
        landingConsumableButton.Click += (_, _) => SetTemplateType(consumable: true);
        WireTemplateEditor();
        WireCreationEditor();
        SizeChanged += (_, _) => RefreshLandingGridLayout();
        KeyDown += OnKeyDown;
    }

    private void FocusSearch()
    {
        if (lifetime.IsRetired || lifetime.AllowClose) return;
        searchBox.Focus();
        searchBox.SelectAll();
    }
}
