using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ZETL;

internal sealed class ZetlThemeManager
{
    private readonly Application application;
    private readonly ZetlAppSettingsStore settingsStore;

    public ZetlThemeManager(
        Application application,
        ZetlAppSettingsStore settingsStore)
    {
        this.application = application;
        this.settingsStore = settingsStore;
        application.ActualThemeVariantChanged += (_, _) =>
        {
            if (CurrentVariant == "System")
            {
                Apply(CurrentTheme, CurrentVariant, persist: false);
            }
        };
    }

    public ZetlThemeDocument CurrentTheme { get; private set; } =
        ZetlThemeDefaults.Create();

    public string CurrentVariant { get; private set; } = "System";

    public string EffectiveVariant =>
        application.ActualThemeVariant == ThemeVariant.Light ? "Light" : "Dark";

    public event EventHandler? ThemeChanged;

    public void Apply(
        ZetlThemeDocument theme,
        string variant,
        bool persist)
    {
        var errors = ZetlThemeValidator.Validate(theme);
        if (errors.Count > 0)
        {
            theme = ZetlThemeDefaults.Create();
        }

        CurrentTheme = ZetlThemeDefaults.Clone(theme);
        CurrentVariant = NormalizeVariant(variant);
        application.RequestedThemeVariant = CurrentVariant switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        var palette = ResolvePalette(CurrentTheme, CurrentVariant);
        ApplyResources(CurrentTheme, palette);

        if (persist)
        {
            settingsStore.Settings.ThemeId = CurrentTheme.Id;
            settingsStore.Settings.ThemeVariant = CurrentVariant;
            settingsStore.Save();
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private ZetlThemePalette ResolvePalette(
        ZetlThemeDocument theme,
        string variant)
    {
        if (variant == "Light")
        {
            return theme.Light;
        }

        if (variant == "Dark")
        {
            return theme.Dark;
        }

        return EffectiveVariant == "Light" ? theme.Light : theme.Dark;
    }

    private void ApplyResources(
        ZetlThemeDocument theme,
        ZetlThemePalette palette)
    {
        var resources = application.Resources;
        var accent = ParseColor(palette.Accent);
        resources["ZetlWindowBackgroundBrush"] = Brush(palette.WindowBackground);
        resources["ZetlSurfaceBrush"] = Brush(palette.Surface);
        resources["ZetlSurfaceAltBrush"] = Brush(palette.SurfaceAlt);
        resources["ZetlTextBrush"] = Brush(palette.Text);
        resources["ZetlMutedTextBrush"] = Brush(palette.MutedText);
        resources["ZetlAccentBrush"] = new SolidColorBrush(accent);
        resources["ZetlAccentTextBrush"] = Brush(palette.AccentText);
        resources["ZetlBorderBrush"] = Brush(palette.Border);
        resources["ZetlErrorBrush"] = Brush(palette.Error);
        resources["ZetlFontFamily"] = new FontFamily(theme.Typography.FontFamily);
        resources["ZetlMonoFontFamily"] = new FontFamily(theme.Typography.MonoFontFamily);
        resources["ZetlBodyFontSize"] = theme.Typography.BodyFontSize;
        resources["ZetlHeadingFontSize"] = theme.Typography.HeadingFontSize;
        resources["ZetlTitleFontSize"] = theme.Typography.TitleFontSize;
        resources["ZetlWindowPadding"] = new Thickness(theme.Metrics.WindowPadding);
        resources["ZetlControlSpacing"] = theme.Metrics.ControlSpacing;
        resources["ZetlCornerRadius"] = new CornerRadius(theme.Metrics.CornerRadius);

        // Fluent consumes these color resources for check marks, selections,
        // focus rings, and other accent-aware template parts.
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Shift(accent, 0.12);
        resources["SystemAccentColorLight2"] = Shift(accent, 0.22);
        resources["SystemAccentColorLight3"] = Shift(accent, 0.34);
        resources["SystemAccentColorDark1"] = Shift(accent, -0.12);
        resources["SystemAccentColorDark2"] = Shift(accent, -0.22);
        resources["SystemAccentColorDark3"] = Shift(accent, -0.34);
    }

    private static SolidColorBrush Brush(string value)
    {
        return new SolidColorBrush(ParseColor(value));
    }

    private static Color ParseColor(string value)
    {
        return Color.Parse(value);
    }

    private static Color Shift(Color color, double amount)
    {
        byte ShiftChannel(byte channel)
        {
            var target = amount >= 0 ? 255 : 0;
            return (byte)Math.Clamp(
                channel + ((target - channel) * Math.Abs(amount)),
                0,
                255);
        }

        return Color.FromArgb(
            color.A,
            ShiftChannel(color.R),
            ShiftChannel(color.G),
            ShiftChannel(color.B));
    }

    private static string NormalizeVariant(string? variant)
    {
        return variant?.Trim().ToLowerInvariant() switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => "System"
        };
    }
}
