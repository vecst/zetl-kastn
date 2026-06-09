using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace ZETL;

internal partial class ThemeEditorWindow : Window
{
    private readonly ZetlThemeManager themeManager = null!;
    private readonly ZetlThemeStore themeStore = null!;
    private readonly ZetlAppSettingsStore settingsStore = null!;
    private readonly List<TextBox> colorBoxes = [];
    private readonly List<Border> colorSwatches = [];
    private ZetlThemeDocument workingTheme = null!;
    private ZetlThemeDocument baselineTheme = null!;
    private string baselineVariant = "System";
    private bool refreshing;
    private bool dirty;

    public ThemeEditorWindow()
    {
        InitializeComponent();
    }

    internal ThemeEditorWindow(
        ZetlThemeManager themeManager,
        ZetlThemeStore themeStore,
        ZetlAppSettingsStore settingsStore)
    {
        this.themeManager = themeManager;
        this.themeStore = themeStore;
        this.settingsStore = settingsStore;
        baselineTheme = ZetlThemeDefaults.Clone(themeManager.CurrentTheme);
        baselineVariant = themeManager.CurrentVariant;

        InitializeComponent();
        ZetlWindowPlacement.Track(this);
        variantBox.ItemsSource = new[] { "System", "Light", "Dark" };
        colorBoxes =
        [
            windowBackgroundBox,
            surfaceBox,
            surfaceAltBox,
            textBox,
            mutedTextBox,
            accentBox,
            accentTextBox,
            borderBox,
            errorBox
        ];
        colorSwatches =
        [
            windowBackgroundSwatch,
            surfaceSwatch,
            surfaceAltSwatch,
            textSwatch,
            mutedTextSwatch,
            accentSwatch,
            accentTextSwatch,
            borderSwatch,
            errorSwatch
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
            .Append(nameBox)
            .Append(fontFamilyBox)
            .Append(monoFontFamilyBox))
        {
            textBox.TextChanged += (_, _) => OnFieldChanged();
        }

        foreach (var numberBox in NumericFields())
        {
            numberBox.ValueChanged += (_, _) => OnFieldChanged();
        }

        saveButton.Click += async (_, _) => await SaveAsync();
        duplicateButton.Click += async (_, _) => await SaveAsAsync();
        resetButton.Click += (_, _) => ResetValues();
        importButton.Click += async (_, _) => await ImportAsync();
        exportButton.Click += async (_, _) => await ExportAsync();
        closeButton.Click += (_, _) => Close();
        Closed += (_, _) =>
        {
            if (dirty)
            {
                themeManager.Apply(baselineTheme, baselineVariant, persist: false);
            }
        };
        ZetlWindowShortcuts.Enable(this, () => _ = SaveAsync(), Close);

        ReloadThemes(themeManager.CurrentTheme.Id);
    }

    private ZetlThemePalette ActivePalette =>
        string.Equals(
            SelectedVariant == "System" ? themeManager.EffectiveVariant : SelectedVariant,
            "Light",
            StringComparison.Ordinal)
            ? workingTheme.Light
            : workingTheme.Dark;

    private string SelectedVariant =>
        variantBox.SelectedItem as string ?? "System";

    private void ReloadThemes(string selectedId)
    {
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
        RefreshAllFields();
        dirty = true;
        ApplyLivePreview();
    }

    private void RefreshAllFields()
    {
        refreshing = true;
        nameBox.Text = workingTheme.Name;
        idBox.Text = workingTheme.Id;
        fontFamilyBox.Text = workingTheme.Typography.FontFamily;
        monoFontFamilyBox.Text = workingTheme.Typography.MonoFontFamily;
        bodyFontSizeBox.Value = (decimal)workingTheme.Typography.BodyFontSize;
        headingFontSizeBox.Value = (decimal)workingTheme.Typography.HeadingFontSize;
        titleFontSizeBox.Value = (decimal)workingTheme.Typography.TitleFontSize;
        windowPaddingBox.Value = (decimal)workingTheme.Metrics.WindowPadding;
        controlSpacingBox.Value = (decimal)workingTheme.Metrics.ControlSpacing;
        cornerRadiusBox.Value = (decimal)workingTheme.Metrics.CornerRadius;
        RefreshPaletteFields();
        refreshing = false;
    }

    private void RefreshPaletteFields()
    {
        if (workingTheme is null)
        {
            return;
        }

        var wasRefreshing = refreshing;
        refreshing = true;
        var palette = ActivePalette;
        windowBackgroundBox.Text = palette.WindowBackground;
        surfaceBox.Text = palette.Surface;
        surfaceAltBox.Text = palette.SurfaceAlt;
        textBox.Text = palette.Text;
        mutedTextBox.Text = palette.MutedText;
        accentBox.Text = palette.Accent;
        accentTextBox.Text = palette.AccentText;
        borderBox.Text = palette.Border;
        errorBox.Text = palette.Error;
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

    private bool ReadFields()
    {
        workingTheme.Name = nameBox.Text?.Trim() ?? "";
        var palette = ActivePalette;
        palette.WindowBackground = windowBackgroundBox.Text?.Trim() ?? "";
        palette.Surface = surfaceBox.Text?.Trim() ?? "";
        palette.SurfaceAlt = surfaceAltBox.Text?.Trim() ?? "";
        palette.Text = textBox.Text?.Trim() ?? "";
        palette.MutedText = mutedTextBox.Text?.Trim() ?? "";
        palette.Accent = accentBox.Text?.Trim() ?? "";
        palette.AccentText = accentTextBox.Text?.Trim() ?? "";
        palette.Border = borderBox.Text?.Trim() ?? "";
        palette.Error = errorBox.Text?.Trim() ?? "";
        workingTheme.Typography.FontFamily = fontFamilyBox.Text?.Trim() ?? "";
        workingTheme.Typography.MonoFontFamily = monoFontFamilyBox.Text?.Trim() ?? "";
        workingTheme.Typography.BodyFontSize = (double)(bodyFontSizeBox.Value ?? 14);
        workingTheme.Typography.HeadingFontSize = (double)(headingFontSizeBox.Value ?? 16);
        workingTheme.Typography.TitleFontSize = (double)(titleFontSizeBox.Value ?? 20);
        workingTheme.Metrics.WindowPadding = (double)(windowPaddingBox.Value ?? 14);
        workingTheme.Metrics.ControlSpacing = (double)(controlSpacingBox.Value ?? 8);
        workingTheme.Metrics.CornerRadius = (double)(cornerRadiusBox.Value ?? 4);

        var errors = ZetlThemeValidator.Validate(workingTheme);
        validationText.Text = string.Join(" ", errors);
        validationText.IsVisible = errors.Count > 0;
        RefreshSwatches();
        return errors.Count == 0;
    }

    private void ApplyLivePreview()
    {
        if (ReadFields())
        {
            themeManager.Apply(workingTheme, SelectedVariant, persist: false);
        }
    }

    private async Task SaveAsync()
    {
        if (!ReadFields())
        {
            return;
        }

        if (ZetlThemeDefaults.IsBuiltIn(workingTheme.Id)
            && !ThemeMatchesBuiltIn())
        {
            await SaveAsAsync();
            return;
        }

        if (!ZetlThemeDefaults.IsBuiltIn(workingTheme.Id))
        {
            themeStore.Save(workingTheme);
        }

        CommitActiveTheme();
    }

    private async Task SaveAsAsync()
    {
        if (!ReadFields())
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
        themeManager.Apply(workingTheme, SelectedVariant, persist: true);
        baselineTheme = ZetlThemeDefaults.Clone(workingTheme);
        baselineVariant = SelectedVariant;
        dirty = false;
        validationText.Text = "Theme saved and activated.";
        validationText.IsVisible = true;
    }

    private void ResetValues()
    {
        var reset = ZetlThemeDefaults.FindBuiltIn(workingTheme.Id)
            ?? ZetlThemeDefaults.Create();
        if (!ZetlThemeDefaults.IsBuiltIn(workingTheme.Id))
        {
            reset.Id = workingTheme.Id;
            reset.Name = workingTheme.Name;
        }

        workingTheme = reset;
        dirty = true;
        RefreshAllFields();
        ApplyLivePreview();
    }

    private async Task ImportAsync()
    {
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
            ShowError(ex.Message);
        }
    }

    private async Task ExportAsync()
    {
        if (!ReadFields())
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Zetl Theme",
            SuggestedFileName = $"{FileName(workingTheme.Name)}.json",
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
            validationText.Text = "Theme exported.";
            validationText.IsVisible = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private void RefreshSwatches()
    {
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
        yield return bodyFontSizeBox;
        yield return headingFontSizeBox;
        yield return titleFontSizeBox;
        yield return windowPaddingBox;
        yield return controlSpacingBox;
        yield return cornerRadiusBox;
    }

    private void ShowError(string message)
    {
        validationText.Text = message;
        validationText.IsVisible = true;
    }

    private static string FileName(string name)
    {
        var safe = string.Concat(name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        return string.IsNullOrWhiteSpace(safe) ? "zetl-theme" : safe.Trim();
    }
}
