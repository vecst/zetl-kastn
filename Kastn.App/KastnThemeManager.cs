using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using ZETL;

namespace KASTN;

internal sealed class KastnThemeManager
{
    private readonly Application application;

    public KastnThemeManager(Application application)
    {
        this.application = application;
        application.ActualThemeVariantChanged += (_, _) =>
        {
            if (CurrentVariant == "System")
            {
                Apply(CurrentTheme, CurrentVariant);
            }
        };
    }

    public ZetlThemeDocument CurrentTheme { get; private set; } =
        ZetlThemeDefaults.Create();

    public string CurrentVariant { get; private set; } = "System";

    private string EffectiveVariant =>
        application.ActualThemeVariant == ThemeVariant.Light ? "Light" : "Dark";

    public void Apply(ZetlThemeDocument theme, string variant)
    {
        if (ZetlThemeValidator.Validate(theme).Count > 0)
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

        ApplyResources(CurrentTheme, ResolvePalette(CurrentTheme, CurrentVariant));
    }

    // Re-apply only when the resolved theme or variant actually differs from what is
    // already applied. The shared settings.json is rewritten for many unrelated Zetl
    // settings; skipping a no-op apply avoids a needless resource churn/flicker.
    public void ApplyIfChanged(ZetlThemeDocument theme, string variant)
    {
        if (NormalizeVariant(variant) == CurrentVariant && ThemesEqual(theme, CurrentTheme))
        {
            return;
        }

        Apply(theme, variant);
    }

    private static bool ThemesEqual(ZetlThemeDocument a, ZetlThemeDocument b) =>
        JsonSerializer.Serialize(a, CompareOptions) == JsonSerializer.Serialize(b, CompareOptions);

    private static readonly JsonSerializerOptions CompareOptions = new();

    private ZetlThemePalette ResolvePalette(ZetlThemeDocument theme, string variant)
    {
        return variant switch
        {
            "Light" => theme.Light,
            "Dark" => theme.Dark,
            _ => EffectiveVariant == "Light" ? theme.Light : theme.Dark
        };
    }

    private void ApplyResources(ZetlThemeDocument theme, ZetlThemePalette palette)
    {
        var resources = application.Resources;
        var accent = Color.Parse(palette.Accent);
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
        return new SolidColorBrush(Color.Parse(value));
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
