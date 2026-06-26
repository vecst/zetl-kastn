using ZETL.Contracts;
using ZETL;

namespace KASTN;

internal sealed record KastnInspectorField(string Label, string Value);

internal sealed record KastnInspectorSection(
    string Heading,
    IReadOnlyList<KastnInspectorField> Fields);

internal static class KastnSlipInspector
{
    public static IReadOnlyList<KastnInspectorSection> Build(
        ZetlProjectSnapshot project,
        ZetlSlipSnapshot slip)
    {
        var bucket = project.Buckets.FirstOrDefault(item => item.Id == slip.BucketId);
        var overview = new List<KastnInspectorField>
        {
            new("Type", slip.Type.ToString()),
            new("Bucket", bucket is null
                ? "Unknown bucket"
                : KastnWorkbench.BucketPathLabel(project, bucket)),
            new("Captured", slip.CapturedAtUtc.ToLocalTime().ToString("F")),
            new("Source", slip.Source)
        };
        if (!string.IsNullOrWhiteSpace(slip.Title))
        {
            overview.Insert(0, new("Title", slip.Title));
        }
        AddIfPresent(overview, "Session", slip.SessionId);

        var sections = new List<KastnInspectorSection>
        {
            new("Overview", overview)
        };

        if (slip.CaptureOrigin is { } origin)
        {
            var location = new List<KastnInspectorField>();
            AddIfPresent(location, "Application", origin.ApplicationName);
            AddIfPresent(location, "Process", origin.ProcessName);
            AddIfPresent(location, "Window", origin.WindowTitle);
            if (location.Count > 0)
            {
                sections.Add(new("Capture location", location));
            }
        }

        if (slip.Picture is { } picture)
        {
            var pictureFields = new List<KastnInspectorField>
            {
                new("Dimensions", $"{picture.Width:N0} × {picture.Height:N0} px"),
                new("Format", picture.MimeType),
                new("Size", FormatBytes(picture.ByteLength))
            };
            AddIfPresent(pictureFields, "Original URL", picture.SourceUrl);
            sections.Add(new("Picture", pictureFields));
        }

        if (slip.DeletedAtUtc is not null || !string.IsNullOrWhiteSpace(slip.DeletedFromBucketId))
        {
            var originalBucket = project.Buckets.FirstOrDefault(
                item => item.Id == slip.DeletedFromBucketId);
            var lifecycle = new List<KastnInspectorField>();
            AddIfPresent(
                lifecycle,
                "Deleted from",
                originalBucket is null
                    ? slip.DeletedFromBucketId
                    : KastnWorkbench.BucketPathLabel(project, originalBucket));
            if (slip.DeletedAtUtc is { } deletedAt)
            {
                lifecycle.Add(new("Deleted", deletedAt.ToLocalTime().ToString("F")));
            }
            sections.Add(new("Lifecycle", lifecycle));
        }

        var backlinksIndex = ZetlSlipLinks.BuildBacklinkIndex(project);
        if (backlinksIndex.TryGetValue(slip.Id, out var backlinks) && backlinks.Count > 0)
        {
            var backlinksFields = new List<KastnInspectorField>();
            foreach (var backlink in backlinks)
            {
                backlinksFields.Add(new KastnInspectorField(backlink.SourceTitle, backlink.SourceSlipId));
            }
            sections.Add(new KastnInspectorSection("Linked from", backlinksFields));
        }

        var technical = new List<KastnInspectorField>
        {
            new("Slip ID", slip.Id),
            new("Revision", slip.Revision.ToString())
        };
        if (slip.Picture is { Sha256.Length: > 0 } technicalPicture)
        {
            technical.Add(new("SHA-256", technicalPicture.Sha256));
        }
        sections.Add(new("Technical", technical));

        return sections;
    }

    private static void AddIfPresent(
        ICollection<KastnInspectorField> fields,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields.Add(new(label, value.Trim()));
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} bytes";
        }

        var kib = bytes / 1024d;
        if (kib < 1024)
        {
            return $"{kib:N1} KiB";
        }

        return $"{kib / 1024d:N1} MiB";
    }
}
