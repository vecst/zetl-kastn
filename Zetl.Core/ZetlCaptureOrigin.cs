using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZETL;

internal static class ZetlCaptureOriginDetail
{
    public const string Off = "Off";
    public const string ApplicationOnly = "ApplicationOnly";
    public const string ApplicationAndWindowTitle = "ApplicationAndWindowTitle";

    public static string Normalize(string? value)
    {
        return value is Off or ApplicationOnly or ApplicationAndWindowTitle
            ? value
            : ApplicationAndWindowTitle;
    }

    public static bool IncludesApplication(string? value) => Normalize(value) != Off;

    public static bool IncludesWindowTitle(string? value) =>
        Normalize(value) == ApplicationAndWindowTitle;
}

internal sealed class ZetlCaptureOrigin
{
    public string ApplicationName { get; set; } = "";

    public string ProcessName { get; set; } = "";

    public string? WindowTitle { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [JsonIgnore]
    public bool HasDisplayValue =>
        !string.IsNullOrWhiteSpace(ApplicationName)
        || !string.IsNullOrWhiteSpace(ProcessName)
        || !string.IsNullOrWhiteSpace(WindowTitle);

    public static ZetlCaptureOrigin? Create(
        string? applicationName,
        string? processName,
        string? windowTitle,
        string? detail)
    {
        var normalizedDetail = ZetlCaptureOriginDetail.Normalize(detail);
        if (normalizedDetail == ZetlCaptureOriginDetail.Off)
        {
            return null;
        }

        var origin = new ZetlCaptureOrigin
        {
            ApplicationName = Clean(applicationName, 200),
            ProcessName = Clean(processName, 120),
            WindowTitle = ZetlCaptureOriginDetail.IncludesWindowTitle(normalizedDetail)
                ? NullIfEmpty(Clean(windowTitle, 500))
                : null
        };
        return origin.HasDisplayValue ? origin : null;
    }

    public string FormatDisplay(DateTimeOffset capturedAt)
    {
        var application = !string.IsNullOrWhiteSpace(ApplicationName)
            ? ApplicationName
            : ProcessName;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(application))
        {
            parts.Add(application);
        }

        if (!string.IsNullOrWhiteSpace(WindowTitle)
            && !string.Equals(WindowTitle, application, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(WindowTitle!);
        }

        parts.Add(capturedAt.ToLocalTime().ToString("t"));
        return string.Join(" · ", parts);
    }

    private static string Clean(string? value, int maxLength)
    {
        var cleaned = (value ?? "").Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}

internal interface ICaptureOriginProvider
{
    ZetlCaptureOrigin? Capture(string detail);
}

internal static class ZetlProjectExportSnapshot
{
    public static ZetlProject Create(ZetlProject project, bool includeCaptureOrigins)
    {
        var json = JsonSerializer.Serialize(project, JsonFile.Options);
        var snapshot = JsonSerializer.Deserialize<ZetlProject>(json, JsonFile.Options)
            ?? throw new InvalidDataException("Could not create a project export snapshot.");
        if (!includeCaptureOrigins)
        {
            foreach (var slip in snapshot.Buckets.SelectMany(bucket => bucket.Slips))
            {
                slip.CaptureOrigin = null;
                // Rich clipboard HTML is a hidden source representation and may
                // contain producer metadata. Clean/shareable exports keep only
                // Zetl's visible text; archival exports retain Replay fidelity.
                slip.RichHtml = null;
                slip.ReplayFormats = null;
                if (slip.Image is not null)
                {
                    slip.Image.SourceUrl = null;
                }
            }
        }

        return snapshot;
    }
}
