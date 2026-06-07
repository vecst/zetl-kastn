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

internal static partial class ZetlThemeDefaults
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

    public static IReadOnlyList<ZetlThemeDocument> CreateAll()
    {
        return [Create(), CreateDusk()];
    }

    public static bool IsBuiltIn(string? id)
    {
        return id is BuiltInId or DuskId;
    }

    public static ZetlThemeDocument? FindBuiltIn(string? id)
    {
        return CreateAll().FirstOrDefault(theme => theme.Id == id);
    }

    public static ZetlThemeDocument Clone(ZetlThemeDocument theme)
    {
        var json = JsonSerializer.Serialize(theme, JsonFile.Options);
        return JsonSerializer.Deserialize<ZetlThemeDocument>(json, JsonFile.Options)
            ?? Create();
    }

    public static ZetlThemeDocument CreateCustom(string name)
    {
        var theme = Create();
        theme.Id = CreateId(name);
        theme.Name = string.IsNullOrWhiteSpace(name) ? "Custom Theme" : name.Trim();
        return theme;
    }

    public static string CreateId(string name)
    {
        var slug = NonSlugCharacters().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "theme";
        }

        slug = slug[..Math.Min(slug.Length, 30)];
        return $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 48)];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
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
    private readonly string themeDirectory;

    public ZetlThemeStore(string? themeDirectory = null)
    {
        this.themeDirectory = themeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "themes");
    }

    public string ThemeDirectory => themeDirectory;

    public IReadOnlyList<ZetlThemeDocument> LoadAll()
    {
        var themes = ZetlThemeDefaults.CreateAll().ToList();
        if (!Directory.Exists(themeDirectory))
        {
            return themes;
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(themeDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return themes;
        }

        foreach (var path in paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var theme = JsonFile.Read<ZetlThemeDocument>(path);
                if (ZetlThemeValidator.Validate(theme).Count == 0
                    && !ZetlThemeDefaults.IsBuiltIn(theme!.Id))
                {
                    themes.Add(theme);
                }
            }
            catch (JsonException)
            {
                // Invalid user themes are ignored so startup always has a fallback.
            }
            catch (IOException)
            {
                // A temporarily unavailable theme file must not block startup.
            }
            catch (UnauthorizedAccessException)
            {
                // An unreadable user theme must not block startup.
            }
            catch (NotSupportedException)
            {
                // Unsupported future JSON values fall back to the built-in theme.
            }
        }

        return themes;
    }

    public ZetlThemeDocument Resolve(string? id)
    {
        return LoadAll().FirstOrDefault(theme => theme.Id == id)
            ?? ZetlThemeDefaults.Create();
    }

    public void Save(ZetlThemeDocument theme)
    {
        var errors = ZetlThemeValidator.Validate(theme);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        if (ZetlThemeDefaults.IsBuiltIn(theme.Id))
        {
            throw new InvalidOperationException("The built-in theme cannot be overwritten.");
        }

        JsonFile.WriteAtomic(PathFor(theme.Id), theme);
    }

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
            || File.Exists(PathFor(theme.Id)))
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

    private string PathFor(string id)
    {
        var safeId = string.Concat(id.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        return Path.Combine(themeDirectory, $"{safeId}.json");
    }
}
