using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace ZETL;

// Settings window. It edits a snapshot and exposes the chosen
// values; persistence remains the controller's responsibility.
internal partial class ZetlSettingsWindow : Window
{
    private readonly ZetlThemeManager? themeManager;
    private readonly ZetlThemeStore? themeStore;
    private readonly ZetlAppSettingsStore? settingsStore;
    private List<TextBox> colorBoxes = [];
    private List<Border> colorSwatches = [];
    private ZetlThemeDocument? workingTheme;
    private ZetlThemeDocument? baselineTheme;
    private string baselineVariant = "System";
    private bool refreshing;
    private bool dirty;

    public ZetlSettingsWindow()
        : this(new ZetlAppSettings())
    {
    }

    internal ZetlSettingsWindow(
        ZetlAppSettings settings,
        ZetlThemeManager? themeManager = null,
        ZetlThemeStore? themeStore = null,
        ZetlAppSettingsStore? settingsStore = null,
        ZetlViewStore? viewStore = null,
        string? defaultTab = null)
    {
        InitializeComponent();
        ZetlWindowPlacement.Track(this);

        // Sidebar Navigation Tab Switcher
        settingsTabList.SelectionChanged += (s, e) =>
        {
            var index = settingsTabList.SelectedIndex;
            zetlSettingsPanel.IsVisible = index == 0;
            kastnSettingsPanel.IsVisible = index == 1;
            chordlSettingsPanel.IsVisible = index == 2;
            holdActionsPanel.IsVisible = index == 3;
            themeEditorPanel.IsVisible = index == 4;
        };

        // Zetl General Settings binding
        toastMsBox.Value = Clamp(settings.ToastDisplayMs, 200, 5000);
        autoCaptureBox.IsChecked = settings.AutoCaptureOnCopy;
        quickNoteClipboardBox.IsChecked = settings.QuickNoteToClipboard;
        replayResumeClipboardBox.IsChecked = settings.ReplayResumeClipboard;
        captureOriginBox.ItemsSource = new[]
        {
            "Off",
            "Application only",
            "Application and window title"
        };
        captureOriginBox.SelectedIndex = ZetlCaptureOriginDetail.Normalize(
            settings.CaptureOriginDetail) switch
        {
            ZetlCaptureOriginDetail.Off => 0,
            ZetlCaptureOriginDetail.ApplicationOnly => 1,
            _ => 2
        };
        defaultBucketsBox.Text = string.Join(Environment.NewLine, settings.DefaultProjectBuckets);
        compileModeBox.ItemsSource = new[] { "Formatted", "Plain", "TSV" };
        compileModeBox.SelectedItem = settings.DefaultCompileMode is "Plain" or "TSV"
            ? settings.DefaultCompileMode
            : "Formatted";
        tsvRowLengthBox.Value = Clamp(settings.DefaultTsvRowLength, 1, 50);
        dayStartHourBox.Value = Clamp(settings.DayStartHour, 0, 23);
        autoReturnHoursBox.Value = Clamp(settings.JournalAutoReturnHours, 0, 168);
        BuildHoldActions(settings);
        idleCopyCaptureBox.ItemsSource = new[]
        {
            "Don't capture copies",
            "Capture copies to the Journal"
        };
        idleCopyCaptureBox.SelectedIndex =
            ZetlIdleCopyCapture.CapturesToJournal(settings.IdleCopyCapture) ? 1 : 0;
        journalIntervalBox.ItemsSource = new[] { "Daily", "Weekly", "Monthly" };
        journalIntervalBox.SelectedItem = ZetlJournalInterval.Normalize(settings.JournalInterval);

        // Zetl Advanced Settings binding
        logRetentionDaysBox.Value = Clamp(settings.LogRetentionDays, 1, 365);
        logMaxNotesBox.Value = Clamp(settings.LogMaxNotesPerDay, 100, 50000);
        logFlushMsBox.Value = Clamp(settings.LogFlushIntervalMs, 500, 60000);
        maxUndoBox.Value = Clamp(settings.MaxUndoActions, 5, 1000);
        clipPollMsBox.Value = Clamp(settings.ClipboardPollIntervalMs, 5, 200);
        clipObsTimeoutMsBox.Value = Clamp(settings.ClipboardObservationTimeoutMs, 50, 2000);
        autoCaptureTimeoutMsBox.Value = Clamp(settings.AutoCaptureClipboardTimeoutMs, 10, 1000);
        popClipDelayMsBox.Value = Clamp(settings.PopClipboardDelayMs, 10, 1000);
        replayRestoreDelayMsBox.Value = Clamp(settings.ReplayClipboardRestoreDelayMs, 10, 1000);
        downloadTimeoutBox.Value = Clamp(settings.DownloadTimeoutSeconds, 1, 120);

        // Kastn General Settings binding
        kastnAutosaveBox.IsChecked = settings.KastnAutosave;
        kastnStartupBox.ItemsSource = new[] { "Landing page", "Last opened project" };
        kastnStartupBox.SelectedIndex =
            ZetlKastnStartup.Normalize(settings.KastnStartup) == ZetlKastnStartup.LastProject ? 1 : 0;
        kastnMainLaneLabelBox.Text = settings.KastnMainLaneLabel?.Trim() ?? "";
        kastnAlternateLaneLabelBox.Text = settings.KastnAlternateLaneLabel?.Trim() ?? "";

        var viewChoices = new List<ViewChoice> { new("", "Project default") };
        viewChoices.AddRange((viewStore ?? new ZetlViewStore()).LoadAll()
            .OrderBy(view => view.Name, StringComparer.OrdinalIgnoreCase)
            .Select(view => new ViewChoice(view.Id, view.Name)));
        kastnDefaultViewBox.ItemsSource = viewChoices;
        kastnDefaultViewBox.SelectedItem =
            viewChoices.FirstOrDefault(choice => choice.Id == settings.KastnDefaultViewId)
            ?? viewChoices[0];

        kastnMinimizeAfterTemplateBox.IsChecked = settings.KastnMinimizeAfterTemplate;
        RefreshKastnLaneChoices();
        kastnTemporaryTemplateLaneBox.SelectedIndex =
            ZetlKastnTemplateLaneDefault.Normalize(settings.KastnTemporaryTemplateLaneDefault) switch
            {
                ZetlStateStore.NormalLane => 1,
                ZetlStateStore.ShiftLane => 2,
                _ => 0
            };
        kastnMainLaneLabelBox.TextChanged += (_, _) => RefreshKastnLaneChoices();
        kastnAlternateLaneLabelBox.TextChanged += (_, _) => RefreshKastnLaneChoices();
        kastnCloseToTrayBox.IsChecked = settings.KastnCloseToTray;
        kastnPreferSlipKindOverBucketKindBox.IsChecked = settings.KastnPreferSlipKindOverBucketKind;

        // Kastn Advanced Settings binding
        untitledSlipTitleBox.Text = string.IsNullOrWhiteSpace(settings.UntitledSlipTitle) ? "Untitled" : settings.UntitledSlipTitle;
        maxSlipLabelLengthBox.Value = Clamp(settings.MaxSlipLabelLength, 5, 100);
        pdfPageFormatBox.SelectedIndex = string.Equals(settings.PdfPageFormat, "A4", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        pdfFontSizeBox.Value = Clamp(settings.PdfFontSize, 6, 24);

        // Chordl Settings binding
        holdDelayMsBox.Value = Clamp(settings.HoldDelayMs, 100, 2000);
        repeatSuppressionDelayMsBox.Value = Clamp(settings.RepeatSuppressionDelayMs, 5, 500);

        // Main Save/Cancel buttons wiring
        saveButton.Click += (_, _) =>
        {
            Saved = true;
            Close();
        };
        cancelButton.Click += (_, _) => Close();

        // Theme Editor Wiring
        this.themeManager = themeManager;
        this.themeStore = themeStore;
        this.settingsStore = settingsStore;

        if (themeManager is not null && themeStore is not null && settingsStore is not null)
        {
            baselineTheme = ZetlThemeDefaults.Clone(themeManager.CurrentTheme);
            baselineVariant = themeManager.CurrentVariant;
            variantBox.ItemsSource = new[] { "System", "Light", "Dark" };
            colorBoxes =
            [
                themeWindowBackgroundBox,
                themeSurfaceBox,
                themeSurfaceAltBox,
                themeTextBox,
                themeMutedTextBox,
                themeAccentBox,
                themeAccentTextBox,
                themeBorderBox,
                themeErrorBox
            ];
            colorSwatches =
            [
                themeWindowBackgroundSwatch,
                themeSurfaceSwatch,
                themeSurfaceAltSwatch,
                themeTextSwatch,
                themeMutedTextSwatch,
                themeAccentSwatch,
                themeAccentTextSwatch,
                themeBorderSwatch,
                themeErrorSwatch
            ];

            themeBox.SelectionChanged += (_, _) => SelectTheme();
            variantBox.SelectionChanged += (_, _) =>
            {
                if (!refreshing)
                {
                    dirty = true;
                    RefreshPaletteFields();
                    ApplyLivePreview();
                }
            };
            foreach (var textBox in colorBoxes
                .Append(themeNameBox)
                .Append(themeFontFamilyBox)
                .Append(themeMonoFontFamilyBox))
            {
                textBox.TextChanged += (_, _) => OnFieldChanged();
            }

            foreach (var numberBox in NumericFields())
            {
                numberBox.ValueChanged += (_, _) => OnFieldChanged();
            }

            themeSaveButton.Click += async (_, _) => await SaveThemeAsync();
            themeDuplicateButton.Click += async (_, _) => await SaveThemeAsAsync();
            themeResetButton.Click += (_, _) => ResetThemeValues();
            themeImportButton.Click += async (_, _) => await ImportThemeAsync();
            themeExportButton.Click += async (_, _) => await ExportThemeAsync();

            ReloadThemes(themeManager.CurrentTheme.Id);
        }
        else
        {
            // If theme resources are missing, remove the Themes tab item
            settingsTabList.Items.RemoveAt(3);
        }

        // Apply default navigation tab
        if (defaultTab == "theme")
        {
            settingsTabList.SelectedItem = themeTab;
        }
        else if (defaultTab == "hold-actions")
        {
            settingsTabList.SelectedItem = holdActionsTab;
        }
        else
        {
            settingsTabList.SelectedIndex = 0;
        }

        // Restore baseline theme on close if modified but not saved
        Closed += (_, _) =>
        {
            if (dirty && themeManager is not null && baselineTheme is not null)
            {
                themeManager.Apply(baselineTheme, baselineVariant);
            }
        };
    }

    public bool Saved { get; private set; }

    // Zetl General getters
    public int ToastDisplayMs => (int)(toastMsBox.Value ?? 950);
    public bool AutoCaptureOnCopy => autoCaptureBox.IsChecked == true;
    public bool QuickNoteToClipboard => quickNoteClipboardBox.IsChecked == true;
    public bool ReplayResumeClipboard => replayResumeClipboardBox.IsChecked == true;
    public string CaptureOriginDetail => captureOriginBox.SelectedIndex switch
    {
        0 => ZetlCaptureOriginDetail.Off,
        1 => ZetlCaptureOriginDetail.ApplicationOnly,
        _ => ZetlCaptureOriginDetail.ApplicationAndWindowTitle
    };
    public List<string> DefaultProjectBuckets => (defaultBucketsBox.Text ?? "")
        .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();
    public string DefaultCompileMode => compileModeBox.SelectedItem as string ?? "Formatted";
    public int DefaultTsvRowLength => (int)(tsvRowLengthBox.Value ?? 5);
    public int DayStartHour => (int)(dayStartHourBox.Value ?? 0);
    public int JournalAutoReturnHours => (int)(autoReturnHoursBox.Value ?? 0);
    // The Hold Actions page as saved settings: only rules changed from the default.
    public List<ZetlGestureRuleSetting> HoldActionRules =>
        ZetlGestureRules.Overrides(holdActionRows.Select(row => row.Rule with
        {
            ActionId = row.Choices[Math.Max(row.Box.SelectedIndex, 0)].Id
        }));

    private readonly List<HoldActionRow> holdActionRows = [];

    private sealed record HoldActionRow(
        ZetlGestureRule Rule,
        IReadOnlyList<ZetlGestureActions.Choice> Choices,
        ComboBox Box);

    // One row per editable rule: the gesture, where it applies, and a menu of
    // the actions that gesture can run.
    private void BuildHoldActions(ZetlAppSettings settings)
    {
        holdActionsIntro.Text =
            $"Choose what each tap and hold does. Every rule applies with and without Shift "
            + $"(the {settings.LaneLabel(false)} and {settings.LaneLabel(true)} lanes). "
            + "When a key has a rule for file lists, it wins there over the rule for anywhere.";

        var rules = ZetlGestureRules.Apply(settings.HoldActionRules)
            .Where(ZetlGestureRules.IsEditable)
            .ToList();
        foreach (var rule in rules)
        {
            var row = holdActionsGrid.RowDefinitions.Count;
            holdActionsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var gesture = new TextBlock { Text = ZetlGestureRules.GestureLabel(rule), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var focus = new TextBlock { Text = ZetlGestureRules.FocusLabel(rule.Focus), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            var choices = ZetlGestureActions.ChoicesFor(rule.Kind);
            var box = new ComboBox
            {
                ItemsSource = choices.Select(choice => choice.Label).ToList(),
                Width = 260,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
            };
            SelectAction(box, choices, rule.ActionId);

            Grid.SetRow(gesture, row);
            Grid.SetRow(focus, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(focus, 1);
            Grid.SetColumn(box, 2);
            holdActionsGrid.Children.Add(gesture);
            holdActionsGrid.Children.Add(focus);
            holdActionsGrid.Children.Add(box);
            holdActionRows.Add(new HoldActionRow(rule, choices, box));
        }

        holdActionsResetButton.Click += (_, _) =>
        {
            foreach (var row in holdActionRows)
            {
                var standard = ZetlGestureRules.Defaults.First(rule =>
                    rule.Kind == row.Rule.Kind
                    && rule.KeyCode == row.Rule.KeyCode
                    && rule.Focus == row.Rule.Focus);
                SelectAction(row.Box, row.Choices, standard.ActionId);
            }
        };
    }

    private static void SelectAction(
        ComboBox box,
        IReadOnlyList<ZetlGestureActions.Choice> choices,
        string actionId)
    {
        var index = choices.ToList().FindIndex(choice => choice.Id == actionId);
        box.SelectedIndex = Math.Max(index, 0);
    }

    public string IdleCopyCapture => idleCopyCaptureBox.SelectedIndex == 1
        ? ZetlIdleCopyCapture.Journal
        : ZetlIdleCopyCapture.Off;
    public string JournalInterval => journalIntervalBox.SelectedItem as string ?? "Weekly";

    // Zetl Advanced getters
    public int LogRetentionDays => (int)(logRetentionDaysBox.Value ?? 14);
    public int LogMaxNotesPerDay => (int)(logMaxNotesBox.Value ?? 2000);
    public int LogFlushIntervalMs => (int)(logFlushMsBox.Value ?? 5000);
    public int MaxUndoActions => (int)(maxUndoBox.Value ?? 100);
    public int ClipboardPollIntervalMs => (int)(clipPollMsBox.Value ?? 20);
    public int ClipboardObservationTimeoutMs => (int)(clipObsTimeoutMsBox.Value ?? 500);
    public int AutoCaptureClipboardTimeoutMs => (int)(autoCaptureTimeoutMsBox.Value ?? 75);
    public int PopClipboardDelayMs => (int)(popClipDelayMsBox.Value ?? 75);
    public int ReplayClipboardRestoreDelayMs => (int)(replayRestoreDelayMsBox.Value ?? 150);
    public int DownloadTimeoutSeconds => (int)(downloadTimeoutBox.Value ?? 10);

    // Kastn General getters
    public bool KastnAutosave => kastnAutosaveBox.IsChecked == true;
    public string KastnStartup => kastnStartupBox.SelectedIndex == 1
        ? ZetlKastnStartup.LastProject
        : ZetlKastnStartup.Landing;
    public string KastnDefaultViewId => (kastnDefaultViewBox.SelectedItem as ViewChoice)?.Id ?? "";
    public string KastnMainLaneLabel => ZetlLaneLabels.Trim(kastnMainLaneLabelBox.Text);
    public string KastnAlternateLaneLabel => ZetlLaneLabels.Trim(kastnAlternateLaneLabelBox.Text);
    public bool KastnMinimizeAfterTemplate => kastnMinimizeAfterTemplateBox.IsChecked == true;
    public bool KastnCloseToTray => kastnCloseToTrayBox.IsChecked == true;
    public string KastnTemporaryTemplateLaneDefault => kastnTemporaryTemplateLaneBox.SelectedIndex switch
    {
        1 => ZetlStateStore.NormalLane,
        2 => ZetlStateStore.ShiftLane,
        _ => ZetlKastnTemplateLaneDefault.Ask
    };
    public bool KastnPreferSlipKindOverBucketKind => kastnPreferSlipKindOverBucketKindBox.IsChecked == true;

    // Kastn Advanced getters
    public string UntitledSlipTitle => string.IsNullOrWhiteSpace(untitledSlipTitleBox.Text) ? "Untitled" : untitledSlipTitleBox.Text;
    public int MaxSlipLabelLength => (int)(maxSlipLabelLengthBox.Value ?? 24);
    public string PdfPageFormat => pdfPageFormatBox.SelectedIndex == 1 ? "A4" : "Letter";
    public int PdfFontSize => (int)(pdfFontSizeBox.Value ?? 11);

    // Chordl getters
    public int HoldDelayMs => (int)(holdDelayMsBox.Value ?? 353);
    public int RepeatSuppressionDelayMs => (int)(repeatSuppressionDelayMsBox.Value ?? 33);

    private static decimal Clamp(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }

    private void RefreshKastnLaneChoices()
    {
        var selectedIndex = kastnTemporaryTemplateLaneBox.SelectedIndex;
        kastnTemporaryTemplateLaneBox.ItemsSource = new[]
        {
            "Ask every time",
            ZetlLaneLabels.Resolve(kastnMainLaneLabelBox.Text, shifted: false),
            ZetlLaneLabels.Resolve(kastnAlternateLaneLabelBox.Text, shifted: true)
        };
        kastnTemporaryTemplateLaneBox.SelectedIndex = Math.Clamp(selectedIndex, 0, 2);
    }

    private sealed record ViewChoice(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    // ==========================================
    // THEME EDITOR CORE LOGIC (Integrated)
    // ==========================================
    private ZetlThemePalette? ActivePalette =>
        workingTheme is null ? null :
        string.Equals(
            SelectedVariant == "System" ? themeManager!.EffectiveVariant : SelectedVariant,
            "Light",
            StringComparison.Ordinal)
            ? workingTheme.Light
            : workingTheme.Dark;

    private string SelectedVariant =>
        variantBox.SelectedItem as string ?? "System";

    private void ReloadThemes(string selectedId)
    {
        if (themeStore is null || themeBox is null || variantBox is null || themeManager is null)
        {
            return;
        }
        refreshing = true;
        var themes = themeStore.LoadAll().ToList();
        themeBox.ItemsSource = themes;
        themeBox.SelectedItem = themes.FirstOrDefault(theme => theme.Id == selectedId)
            ?? themes[0];
        variantBox.SelectedItem = themeManager.CurrentVariant;
        refreshing = false;
        SelectTheme();
    }

    private void SelectTheme()
    {
        if (themeBox.SelectedItem is not ZetlThemeDocument selected)
        {
            return;
        }

        workingTheme = ZetlThemeDefaults.Clone(selected);
        RefreshAllThemeFields();
        dirty = true;
        ApplyLivePreview();
    }

    private void RefreshAllThemeFields()
    {
        if (workingTheme is null)
        {
            return;
        }
        refreshing = true;
        themeNameBox.Text = workingTheme.Name;
        themeIdBox.Text = workingTheme.Id;
        themeFontFamilyBox.Text = workingTheme.Typography.FontFamily;
        themeMonoFontFamilyBox.Text = workingTheme.Typography.MonoFontFamily;
        themeBodyFontSizeBox.Value = (decimal)workingTheme.Typography.BodyFontSize;
        themeHeadingFontSizeBox.Value = (decimal)workingTheme.Typography.HeadingFontSize;
        themeTitleFontSizeBox.Value = (decimal)workingTheme.Typography.TitleFontSize;
        themeWindowPaddingBox.Value = (decimal)workingTheme.Metrics.WindowPadding;
        themeControlSpacingBox.Value = (decimal)workingTheme.Metrics.ControlSpacing;
        themeCornerRadiusBox.Value = (decimal)workingTheme.Metrics.CornerRadius;
        RefreshPaletteFields();
        refreshing = false;
    }

    private void RefreshPaletteFields()
    {
        var palette = ActivePalette;
        if (palette is null)
        {
            return;
        }

        var wasRefreshing = refreshing;
        refreshing = true;
        themeWindowBackgroundBox.Text = palette.WindowBackground;
        themeSurfaceBox.Text = palette.Surface;
        themeSurfaceAltBox.Text = palette.SurfaceAlt;
        themeTextBox.Text = palette.Text;
        themeMutedTextBox.Text = palette.MutedText;
        themeAccentBox.Text = palette.Accent;
        themeAccentTextBox.Text = palette.AccentText;
        themeBorderBox.Text = palette.Border;
        themeErrorBox.Text = palette.Error;
        RefreshSwatches();
        refreshing = wasRefreshing;
    }

    private void OnFieldChanged()
    {
        if (refreshing || workingTheme is null)
        {
            return;
        }

        dirty = true;
        ApplyLivePreview();
    }

    private bool ReadThemeFields()
    {
        if (workingTheme is null)
        {
            return false;
        }
        workingTheme.Name = themeNameBox.Text?.Trim() ?? "";
        var palette = ActivePalette;
        if (palette is not null)
        {
            palette.WindowBackground = themeWindowBackgroundBox.Text?.Trim() ?? "";
            palette.Surface = themeSurfaceBox.Text?.Trim() ?? "";
            palette.SurfaceAlt = themeSurfaceAltBox.Text?.Trim() ?? "";
            palette.Text = themeTextBox.Text?.Trim() ?? "";
            palette.MutedText = themeMutedTextBox.Text?.Trim() ?? "";
            palette.Accent = themeAccentBox.Text?.Trim() ?? "";
            palette.AccentText = themeAccentTextBox.Text?.Trim() ?? "";
            palette.Border = themeBorderBox.Text?.Trim() ?? "";
            palette.Error = themeErrorBox.Text?.Trim() ?? "";
        }
        workingTheme.Typography.FontFamily = themeFontFamilyBox.Text?.Trim() ?? "";
        workingTheme.Typography.MonoFontFamily = themeMonoFontFamilyBox.Text?.Trim() ?? "";
        workingTheme.Typography.BodyFontSize = (double)(themeBodyFontSizeBox.Value ?? 14);
        workingTheme.Typography.HeadingFontSize = (double)(themeHeadingFontSizeBox.Value ?? 16);
        workingTheme.Typography.TitleFontSize = (double)(themeTitleFontSizeBox.Value ?? 20);
        workingTheme.Metrics.WindowPadding = (double)(themeWindowPaddingBox.Value ?? 14);
        workingTheme.Metrics.ControlSpacing = (double)(themeControlSpacingBox.Value ?? 8);
        workingTheme.Metrics.CornerRadius = (double)(themeCornerRadiusBox.Value ?? 4);

        var errors = ZetlThemeValidator.Validate(workingTheme);
        themeValidationText.Text = string.Join(" ", errors);
        themeValidationText.IsVisible = errors.Count > 0;
        RefreshSwatches();
        return errors.Count == 0;
    }

    private void ApplyLivePreview()
    {
        if (themeManager is not null && workingTheme is not null && ReadThemeFields())
        {
            themeManager.Apply(workingTheme, SelectedVariant);
        }
    }

    private async Task SaveThemeAsync()
    {
        if (workingTheme is null || themeStore is null)
        {
            return;
        }
        if (!ReadThemeFields())
        {
            return;
        }

        if (ZetlThemeDefaults.IsBuiltIn(workingTheme.Id)
            && !ThemeMatchesBuiltIn())
        {
            await SaveThemeAsAsync();
            return;
        }

        if (!ZetlThemeDefaults.IsBuiltIn(workingTheme.Id))
        {
            themeStore.Save(workingTheme);
        }

        CommitActiveTheme();
    }

    private async Task SaveThemeAsAsync()
    {
        if (workingTheme is null || themeStore is null)
        {
            return;
        }
        if (!ReadThemeFields())
        {
            return;
        }

        var prompt = new TextPromptWindow("Save Theme As", "Theme name");
        await prompt.ShowDialog(this);
        if (!prompt.Saved)
        {
            return;
        }

        workingTheme.Name = prompt.Value;
        workingTheme.Id = ZetlThemeDefaults.CreateId(prompt.Value);
        themeStore.Save(workingTheme);
        CommitActiveTheme();
        ReloadThemes(workingTheme.Id);
        baselineTheme = ZetlThemeDefaults.Clone(workingTheme);
        baselineVariant = SelectedVariant;
        dirty = false;
    }

    private void CommitActiveTheme()
    {
        if (workingTheme is null || themeManager is null)
        {
            return;
        }
        themeManager.Apply(workingTheme, SelectedVariant);
        // Persist the committed choice; live previews and baseline restores above
        // apply without saving.
        if (settingsStore is not null)
        {
            settingsStore.Settings.ThemeId = themeManager.CurrentTheme.Id;
            settingsStore.Settings.ThemeVariant = themeManager.CurrentVariant;
            settingsStore.Save();
        }
        baselineTheme = ZetlThemeDefaults.Clone(workingTheme);
        baselineVariant = SelectedVariant;
        dirty = false;
        themeValidationText.Text = "Theme saved and activated.";
        themeValidationText.IsVisible = true;
    }

    private void ResetThemeValues()
    {
        if (workingTheme is null)
        {
            return;
        }
        var reset = ZetlThemeDefaults.FindBuiltIn(workingTheme.Id)
            ?? ZetlThemeDefaults.Create();
        if (!ZetlThemeDefaults.IsBuiltIn(workingTheme.Id))
        {
            reset.Id = workingTheme.Id;
            reset.Name = workingTheme.Name;
        }

        workingTheme = reset;
        dirty = true;
        RefreshAllThemeFields();
        ApplyLivePreview();
    }

    private async Task ImportThemeAsync()
    {
        if (themeStore is null)
        {
            return;
        }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Zetl Theme",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Zetl theme")
                {
                    Patterns = ["*.json"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            var imported = themeStore.Import(path);
            ReloadThemes(imported.Id);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowThemeError(ex.Message);
        }
    }

    private async Task ExportThemeAsync()
    {
        if (workingTheme is null)
        {
            return;
        }
        if (!ReadThemeFields())
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Zetl Theme",
            SuggestedFileName = $"{SafeThemeFileName(workingTheme.Name)}.json",
            FileTypeChoices =
            [
                new FilePickerFileType("Zetl theme")
                {
                    Patterns = ["*.json"]
                }
            ]
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        try
        {
            ZetlThemeStore.Export(path, workingTheme);
            themeValidationText.Text = "Theme exported.";
            themeValidationText.IsVisible = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowThemeError(ex.Message);
        }
    }

    private void RefreshSwatches()
    {
        if (themeManager is null)
        {
            return;
        }
        for (var index = 0; index < colorBoxes.Count; index++)
        {
            try
            {
                colorSwatches[index].Background = new SolidColorBrush(
                    Color.Parse(colorBoxes[index].Text ?? ""));
                colorSwatches[index].BorderBrush = themeManager.CurrentTheme.Dark.Border is { } border
                    ? new SolidColorBrush(Color.Parse(border))
                    : Brushes.Gray;
            }
            catch (FormatException)
            {
                colorSwatches[index].Background = Brushes.Transparent;
                colorSwatches[index].BorderBrush = Brushes.Red;
            }
        }
    }

    private bool ThemeMatchesBuiltIn()
    {
        if (workingTheme is null)
        {
            return false;
        }
        var builtIn = ZetlThemeDefaults.FindBuiltIn(workingTheme.Id);
        if (builtIn is null)
        {
            return false;
        }

        return System.Text.Json.JsonSerializer.Serialize(
                workingTheme,
                JsonFile.Options)
            == System.Text.Json.JsonSerializer.Serialize(
                builtIn,
                JsonFile.Options);
    }

    private IEnumerable<NumericUpDown> NumericFields()
    {
        yield return themeBodyFontSizeBox;
        yield return themeHeadingFontSizeBox;
        yield return themeTitleFontSizeBox;
        yield return themeWindowPaddingBox;
        yield return themeControlSpacingBox;
        yield return themeCornerRadiusBox;
    }

    private void ShowThemeError(string message)
    {
        themeValidationText.Text = message;
        themeValidationText.IsVisible = true;
    }

    private static string SafeThemeFileName(string name)
    {
        var safe = string.Concat(name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        return string.IsNullOrWhiteSpace(safe) ? "zetl-theme" : safe.Trim();
    }
}
