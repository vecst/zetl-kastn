using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZETL;

internal sealed class ZetlThemeDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public ZetlThemePalette Light { get; set; } = new();

    public ZetlThemePalette Dark { get; set; } = new();

    public ZetlThemeTypography Typography { get; set; } = new();

    public ZetlThemeMetrics Metrics { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class ZetlThemePalette
{
    public string WindowBackground { get; set; } = "#111318";

    public string Surface { get; set; } = "#1A1D24";

    public string SurfaceAlt { get; set; } = "#262A33";

    public string Text { get; set; } = "#F2F4F8";

    public string MutedText { get; set; } = "#AAB1BE";

    public string Accent { get; set; } = "#8B7CF6";

    public string AccentText { get; set; } = "#FFFFFF";

    public string Border { get; set; } = "#3B414C";

    public string Error { get; set; } = "#FF6B6B";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class ZetlThemeTypography
{
    public string FontFamily { get; set; } = "Inter";

    public string MonoFontFamily { get; set; } = "Cascadia Mono,Consolas,monospace";

    public double BodyFontSize { get; set; } = 14;

    public double HeadingFontSize { get; set; } = 16;

    public double TitleFontSize { get; set; } = 20;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class ZetlThemeMetrics
{
    public double WindowPadding { get; set; } = 14;

    public double ControlSpacing { get; set; } = 8;

    public double CornerRadius { get; set; } = 4;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal static class ZetlThemeDefaults
{
    public const string BuiltInId = "zetl-default";
    public const string DuskId = "zetl-dusk";

    public static ZetlThemeDocument Create()
    {
        return new ZetlThemeDocument
        {
            Id = BuiltInId,
            Name = "Zetl Default",
            Light = new ZetlThemePalette
            {
                WindowBackground = "#F7F8FA",
                Surface = "#FFFFFF",
                SurfaceAlt = "#E8ECF3",
                Text = "#16181D",
                MutedText = "#626A78",
                Accent = "#5B5BD6",
                AccentText = "#FFFFFF",
                Border = "#B9C0CC",
                Error = "#B42318"
            },
            Dark = new ZetlThemePalette
            {
                WindowBackground = "#111318",
                Surface = "#1A1D24",
                SurfaceAlt = "#262A33",
                Text = "#F2F4F8",
                MutedText = "#AAB1BE",
                Accent = "#8B7CF6",
                AccentText = "#FFFFFF",
                Border = "#3B414C",
                Error = "#FF6B6B"
            }
        };
    }

    public static ZetlThemeDocument CreateDusk()
    {
        return new ZetlThemeDocument
        {
            Id = DuskId,
            Name = "Zetl Dusk",
            Light = new ZetlThemePalette
            {
                WindowBackground = "#F7F4F2",
                Surface = "#FFFCFA",
                SurfaceAlt = "#EEE8ED",
                Text = "#221E28",
                MutedText = "#6D6672",
                Accent = "#5968D9",
                AccentText = "#FFFFFF",
                Border = "#C8BEC8",
                Error = "#B42318"
            },
            Dark = new ZetlThemePalette
            {
                WindowBackground = "#181620",
                Surface = "#1A1720",
                SurfaceAlt = "#2D2A33",
                Text = "#F2F4DA",
                MutedText = "#AAA4B2",
                Accent = "#8290FF",
                AccentText = "#FFFFFF",
                Border = "#4B454C",
                Error = "#FF6B6B"
            },
            Typography = new ZetlThemeTypography
            {
                BodyFontSize = 13.5
            }
        };
    }

    public static ZetlThemeDocument CreateOlive() => Make("zetl-olive", "Z-Olive",
        Palette("#F7F4F2", "#FFFCFA", "#EEE8ED", "#221E28", "#6D6672", "#5968D9", "#FFFFFF", "#C8BEC8", "#B42318"),
        Palette("#2B3026", "#2C363B", "#525E58", "#F2F4DA", "#AAA4B2", "#2A7A53", "#FFFFFF", "#5B5C3F", "#CC1212"));

    public static ZetlThemeDocument CreateEmber() => Make("zetl-ember", "Z-Ember",
        Palette("#FAF5EE", "#FFFFFF", "#F0E7DA", "#2A211A", "#7A6C5D", "#B5641C", "#FFFFFF", "#DBCDBA", "#B42318"),
        Palette("#1B1714", "#241E19", "#352A20", "#F4EBE1", "#B6A695", "#E08A3C", "#241A12", "#473A2E", "#E5645A"));

    public static ZetlThemeDocument CreateTide() => Make("zetl-tide", "Z-Tide",
        Palette("#F1F6F7", "#FFFFFF", "#E0ECEE", "#112025", "#5E767D", "#11857A", "#FFFFFF", "#C3D7DB", "#B42318"),
        Palette("#0F191E", "#16232A", "#213842", "#E7F1F3", "#92A9AF", "#2AB6A4", "#06231F", "#2C4651", "#E5645A"));

    public static ZetlThemeDocument CreateRose() => Make("zetl-rose", "Z-Rose",
        Palette("#FAF3F5", "#FFFFFF", "#F3E4EA", "#2A1A22", "#8A6B76", "#B23267", "#FFFFFF", "#E0C8D2", "#B42318"),
        Palette("#1C1418", "#251A20", "#382836", "#F6E9EE", "#C0A3AE", "#D6477E", "#FFFFFF", "#4A3340", "#E5645A"));

    public static ZetlThemeDocument CreateNord() => Make("zetl-nord", "Z-Nord",
        Palette("#ECEFF4", "#FFFFFF", "#E0E5EE", "#2E3440", "#5E6677", "#5E81AC", "#FFFFFF", "#C8D0DC", "#BF616A"),
        Palette("#2E3440", "#343C4B", "#3F4A5B", "#ECEFF4", "#9AA5B6", "#88C0D0", "#1C2530", "#4A5568", "#BF616A"));

    public static ZetlThemeDocument CreateSepia() => Make("zetl-sepia", "Z-Sepia",
        Palette("#F4ECDC", "#FBF5E9", "#EADFC6", "#3B2F1E", "#7C6B50", "#9A6E3A", "#FFFFFF", "#D8C9A8", "#A8341E"),
        Palette("#211B14", "#2A2219", "#3A2F22", "#EFE3CE", "#B3A287", "#C89B5C", "#241B10", "#46392A", "#D9685E"));

    public static ZetlThemeDocument CreateCarbon() => Make("zetl-carbon", "Z-Carbon",
        Palette("#F4F5F6", "#FFFFFF", "#E8EAEC", "#14181B", "#5C646C", "#4D7C0F", "#FFFFFF", "#CDD2D7", "#B42318"),
        Palette("#000000", "#0C0E10", "#181B1F", "#ECEFF1", "#8A929B", "#A3E635", "#11210A", "#262B30", "#FF5D5D"));

    public static ZetlThemeDocument CreateMono() => Make("zetl-mono", "Z-Mono",
        Palette("#F5F5F5", "#FFFFFF", "#EAEAEA", "#1A1A1A", "#707070", "#2563EB", "#FFFFFF", "#D4D4D4", "#B42318"),
        Palette("#141414", "#1C1C1C", "#2A2A2A", "#EDEDED", "#9A9A9A", "#3B82F6", "#FFFFFF", "#353535", "#F47171"));

    public static ZetlThemeDocument CreateContrast() => Make("zetl-contrast", "Z-Contrast",
        Palette("#FFFFFF", "#FFFFFF", "#E8E8E8", "#000000", "#2E2E2E", "#0B3FCC", "#FFFFFF", "#000000", "#C40000"),
        Palette("#000000", "#000000", "#2A2A2A", "#FFFFFF", "#D4D4D4", "#FFD400", "#000000", "#FFFFFF", "#FF7B7B"));

    public static ZetlThemeDocument CreateColorsafe() => Make("zetl-colorsafe", "Z-Colorsafe",
        Palette("#F2F5F8", "#FFFFFF", "#E4EAF0", "#14181D", "#586471", "#0077BB", "#FFFFFF", "#C6D0DB", "#CC3311"),
        Palette("#15191E", "#1C222A", "#2A323D", "#F0F3F7", "#A6B0BD", "#33BBEE", "#062430", "#38424E", "#EE7733"));

    public static ZetlThemeDocument CreateSynthwave() => Make("zetl-synthwave", "Z-Synthwave",
        Palette("#FBEFFB", "#FFFFFF", "#F3E0F5", "#2A1140", "#7A5A8C", "#D6177E", "#FFFFFF", "#E6C8EC", "#C2185B"),
        Palette("#1A0E2E", "#241540", "#341E5C", "#F5E6FF", "#B79BD6", "#FF2E97", "#FFFFFF", "#4A2E7A", "#FF5555"));

    public static ZetlThemeDocument CreateMatrix() => Make("zetl-matrix", "Z-Matrix",
        Palette("#F0F7F0", "#FFFFFF", "#DFF0DF", "#0B3D17", "#4A7A55", "#128A36", "#FFFFFF", "#C4DEC8", "#B42318"),
        Palette("#000800", "#001400", "#00220A", "#33FF66", "#1FA34A", "#6FFF8F", "#002A0E", "#0A4A1E", "#FF5555"));

    public static ZetlThemeDocument CreateBubblegum() => Make("zetl-bubblegum", "Z-Bubblegum",
        Palette("#FFF0F6", "#FFFFFF", "#FFE0EE", "#4A1533", "#A05B7E", "#C2186B", "#FFFFFF", "#F5C6DC", "#D32F2F"),
        Palette("#2A1424", "#35192D", "#4A2740", "#FDE7F2", "#C79BB5", "#FF7FB8", "#3D0021", "#5A3050", "#FF6B6B"));

    // Every built-in preset, in display order. The first (Zetl Default) is the
    // resolve fallback. CreateAll returns fresh documents each call so callers
    // (e.g. the live theme editor) can mutate their copy without disturbing the
    // shared presets.
    private static readonly Func<ZetlThemeDocument>[] BuiltInFactories =
    [
        Create, CreateDusk, CreateOlive, CreateEmber, CreateTide, CreateRose,
        CreateNord, CreateSepia, CreateCarbon, CreateMono, CreateContrast,
        CreateColorsafe, CreateSynthwave, CreateMatrix, CreateBubblegum
    ];

    private static readonly HashSet<string> BuiltInIds =
        new(BuiltInFactories.Select(factory => factory().Id), StringComparer.Ordinal);

    public static IReadOnlyList<ZetlThemeDocument> CreateAll()
    {
        return BuiltInFactories.Select(factory => factory()).ToList();
    }

    public static bool IsBuiltIn(string? id)
    {
        return id is not null && BuiltInIds.Contains(id);
    }

    private static ZetlThemeDocument Make(
        string id,
        string name,
        ZetlThemePalette light,
        ZetlThemePalette dark)
    {
        return new ZetlThemeDocument
        {
            Id = id,
            Name = name,
            Light = light,
            Dark = dark,
            Typography = new ZetlThemeTypography { BodyFontSize = 13.5 }
        };
    }

    // Positional palette builder; arguments follow the ZetlThemePalette property
    // order so each preset reads as two compact rows of colors.
    private static ZetlThemePalette Palette(
        string windowBackground,
        string surface,
        string surfaceAlt,
        string text,
        string mutedText,
        string accent,
        string accentText,
        string border,
        string error)
    {
        return new ZetlThemePalette
        {
            WindowBackground = windowBackground,
            Surface = surface,
            SurfaceAlt = surfaceAlt,
            Text = text,
            MutedText = mutedText,
            Accent = accent,
            AccentText = accentText,
            Border = border,
            Error = error
        };
    }

    public static ZetlThemeDocument? FindBuiltIn(string? id)
    {
        return CreateAll().FirstOrDefault(theme => theme.Id == id);
    }

    public static ZetlThemeDocument Clone(ZetlThemeDocument theme) => JsonFile.Clone(theme);

    public static string CreateId(string name) => ZetlDocumentId.Create(name, "theme");
}

internal static class ZetlThemeValidator
{
    public static IReadOnlyList<string> Validate(ZetlThemeDocument? theme)
    {
        var errors = new List<string>();
        if (theme is null)
        {
            return ["Theme data is missing."];
        }

        if (theme.Version < 1)
        {
            errors.Add("Theme version must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(theme.Id))
        {
            errors.Add("Theme id is required.");
        }

        if (string.IsNullOrWhiteSpace(theme.Name))
        {
            errors.Add("Theme name is required.");
        }

        ValidatePalette(theme.Light, "Light", errors);
        ValidatePalette(theme.Dark, "Dark", errors);
        ValidateRange(theme.Typography.BodyFontSize, 9, 30, "Body font size", errors);
        ValidateRange(theme.Typography.HeadingFontSize, 10, 40, "Heading font size", errors);
        ValidateRange(theme.Typography.TitleFontSize, 12, 56, "Title font size", errors);
        ValidateRange(theme.Metrics.WindowPadding, 0, 40, "Window padding", errors);
        ValidateRange(theme.Metrics.ControlSpacing, 0, 30, "Control spacing", errors);
        ValidateRange(theme.Metrics.CornerRadius, 0, 24, "Corner radius", errors);

        if (string.IsNullOrWhiteSpace(theme.Typography.FontFamily))
        {
            errors.Add("Font family is required.");
        }

        if (string.IsNullOrWhiteSpace(theme.Typography.MonoFontFamily))
        {
            errors.Add("Monospace font family is required.");
        }

        return errors;
    }

    private static void ValidatePalette(
        ZetlThemePalette? palette,
        string label,
        ICollection<string> errors)
    {
        if (palette is null)
        {
            errors.Add($"{label} palette is missing.");
            return;
        }

        ValidateColor(palette.WindowBackground, $"{label} window background", errors);
        ValidateColor(palette.Surface, $"{label} surface", errors);
        ValidateColor(palette.SurfaceAlt, $"{label} alternate surface", errors);
        ValidateColor(palette.Text, $"{label} text", errors);
        ValidateColor(palette.MutedText, $"{label} muted text", errors);
        ValidateColor(palette.Accent, $"{label} accent", errors);
        ValidateColor(palette.AccentText, $"{label} accent text", errors);
        ValidateColor(palette.Border, $"{label} border", errors);
        ValidateColor(palette.Error, $"{label} error", errors);
    }

    private static void ValidateColor(
        string? value,
        string label,
        ICollection<string> errors)
    {
        if (value is null
            || (value.Length != 7 && value.Length != 9)
            || value[0] != '#'
            || !value.Skip(1).All(Uri.IsHexDigit))
        {
            errors.Add($"{label} must be #RRGGBB or #AARRGGBB.");
        }
    }

    private static void ValidateRange(
        double value,
        double min,
        double max,
        string label,
        ICollection<string> errors)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            errors.Add($"{label} must be between {min} and {max}.");
        }
    }
}

internal sealed class ZetlThemeStore
{
    private readonly ZetlDocumentStore<ZetlThemeDocument> store;

    public ZetlThemeStore(string? themeDirectory = null, Action<string>? log = null) =>
        store = new(
            themeDirectory,
            "themes",
            "theme",
            ZetlThemeDefaults.CreateAll,
            ZetlThemeValidator.Validate,
            ZetlThemeDefaults.IsBuiltIn,
            theme => theme.Id,
            log);

    public string ThemeDirectory => store.Directory;

    public IReadOnlyList<ZetlThemeDocument> LoadAll() => store.LoadAll();

    public ZetlThemeDocument Resolve(string? id)
    {
        return LoadAll().FirstOrDefault(theme => theme.Id == id)
            ?? ZetlThemeDefaults.Create();
    }

    public void Save(ZetlThemeDocument theme) => store.Save(theme);

    public ZetlThemeDocument Import(string path)
    {
        var theme = JsonFile.Read<ZetlThemeDocument>(path)
            ?? throw new InvalidDataException("The selected file does not contain a theme.");
        var errors = ZetlThemeValidator.Validate(theme);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        if (ZetlThemeDefaults.IsBuiltIn(theme.Id)
            || File.Exists(store.PathFor(theme.Id)))
        {
            theme.Id = ZetlThemeDefaults.CreateId(theme.Name);
        }

        Save(theme);
        return theme;
    }

    public static void Export(string path, ZetlThemeDocument theme)
    {
        var errors = ZetlThemeValidator.Validate(theme);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        JsonFile.WriteAtomic(path, theme);
    }
}
