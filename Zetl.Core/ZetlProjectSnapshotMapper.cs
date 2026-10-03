using ZETL.Contracts;

namespace ZETL;

internal static class ZetlProjectSnapshotMapper
{
    public static ZetlProjectSummary ToSummary(ZetlProject project)
    {
        var visibleBuckets = project.Buckets
            .Where(bucket => !ZetlStateRules.IsDeletedBucket(bucket))
            .ToList();
        var visibleSlips = visibleBuckets
            .SelectMany(bucket => bucket.Slips)
            .ToList();
        var deletedSlipCount = project.Buckets
            .Where(ZetlStateRules.IsDeletedBucket)
            .Sum(bucket => bucket.Slips.Count);

        return new ZetlProjectSummary
        {
            Id = project.Id,
            Name = project.Name,
            MetadataRevision = project.MetadataRevision,
            ChangeSequence = project.ChangeSequence,
            Status = project.Status,
            Kind = project.Kind,
            SourceTemplateId = project.SourceTemplateId,
            TemporaryLane = project.TemporaryLane,
            BucketCount = project.Buckets.Count,
            SlipCount = project.Buckets.Sum(bucket => bucket.Slips.Count),
            VisibleBucketCount = visibleBuckets.Count,
            VisibleSlipCount = visibleSlips.Count,
            DeletedSlipCount = deletedSlipCount,
            CanCreateTemporaryFromReplay = ZetlStateStore.CanCreateTemporaryFromReplay(project),
            LastActivityUtc = LastActivityUtc(visibleSlips),
            PreviewText = SummaryPreviewText(project)
        };
    }

    public static ZetlProjectSnapshot ToSnapshot(ZetlProject project)
    {
        return new ZetlProjectSnapshot
        {
            Id = project.Id,
            Name = project.Name,
            MetadataRevision = project.MetadataRevision,
            ChangeSequence = project.ChangeSequence,
            Status = project.Status,
            Kind = project.Kind,
            SourceTemplateId = project.SourceTemplateId,
            TemporaryLane = project.TemporaryLane,
            JournalMode = project.JournalMode,
            ActiveBucketId = project.ActiveBucketId,
            DefaultViewId = project.DefaultViewId,
            Views = project.Views.Select(ToSnapshot).ToList(),
            Buckets = project.Buckets.Select(ToSnapshot).ToList(),
            Slips = project.Buckets
                .SelectMany(bucket => bucket.Slips.Select(slip => ToSnapshot(bucket, slip)))
                .ToList()
        };
    }

    public static ZetlProjectViewSnapshot ToSnapshot(ZetlViewDocument view)
    {
        return new ZetlProjectViewSnapshot
        {
            Version = view.Version,
            Id = view.Id,
            Name = view.Name,
            Category = view.Category,
            Description = view.Description,
            Kind = view.Kind,
            TsvRowLength = view.TsvRowLength,
            Sections = view.Sections.Select(section => new ZetlProjectViewSectionSnapshot
            {
                Title = section.Title,
                Buckets = section.Buckets.ToList(),
                HeadingAlign = section.HeadingAlign,
                HeadingBold = section.HeadingBold,
                HeadingLevel = section.HeadingLevel
            }).ToList(),
            ListStyle = view.ListStyle,
            NumberHeadings = view.NumberHeadings,
            ShowTitle = view.ShowTitle,
            Title = view.Title
        };
    }

    public static ZetlViewDocument ToDocument(ZetlProjectViewSnapshot view)
    {
        return new ZetlViewDocument
        {
            Version = view.Version,
            Id = view.Id,
            Name = view.Name,
            Category = view.Category,
            Description = view.Description,
            Kind = view.Kind,
            TsvRowLength = view.TsvRowLength,
            Sections = view.Sections.Select(section => new ZetlViewSection
            {
                Title = section.Title,
                Buckets = section.Buckets.ToList(),
                HeadingAlign = section.HeadingAlign,
                HeadingBold = section.HeadingBold,
                HeadingLevel = section.HeadingLevel
            }).ToList(),
            ListStyle = view.ListStyle,
            NumberHeadings = view.NumberHeadings,
            ShowTitle = view.ShowTitle,
            Title = view.Title
        };
    }

    public static ZetlBucketSnapshot ToSnapshot(ZetlBucket bucket)
    {
        return new ZetlBucketSnapshot
        {
            Id = bucket.Id,
            Revision = bucket.Revision,
            Name = bucket.Name,
            ParentBucketId = bucket.ParentBucketId,
            Settings = new ZETL.Contracts.ZetlBucketSettings
            {
                Kind = bucket.Settings.Kind,
                DefaultKind = bucket.Settings.DefaultKind,
                DefaultCompileMode = bucket.Settings.DefaultCompileMode,
                DefaultStartingText = bucket.Settings.DefaultStartingText,
                DefaultTsvRowLength = bucket.Settings.DefaultTsvRowLength,
                ReplayReviewBucketId = bucket.Settings.ReplayReviewBucketId
            },
            HeadingAlign = bucket.HeadingAlign,
            HeadingBold = bucket.HeadingBold,
            HeadingLevel = bucket.HeadingLevel,
            RenderKind = bucket.RenderKind
        };
    }

    public static ZetlSlipSnapshot ToSnapshot(ZetlBucket bucket, ZetlSlip slip)
    {
        return new ZetlSlipSnapshot
        {
            Id = slip.Id,
            Revision = slip.Revision,
            Type = slip.Type,
            BucketId = bucket.Id,
            Title = slip.Title,
            Text = slip.Text,
            Picture = slip.Image is null
                ? null
                : new ZetlPictureSnapshot
                {
                    SourceUrl = slip.Image.SourceUrl,
                    MimeType = slip.Image.MimeType,
                    Width = slip.Image.Width,
                    Height = slip.Image.Height,
                    ByteLength = slip.Image.ByteLength,
                    Sha256 = slip.Image.Sha256
                },
            CaptureOrigin = slip.CaptureOrigin is null
                ? null
                : new ZetlCaptureOriginSnapshot
                {
                    ApplicationName = slip.CaptureOrigin.ApplicationName,
                    ProcessName = slip.CaptureOrigin.ProcessName,
                    WindowTitle = slip.CaptureOrigin.WindowTitle
                },
            Source = slip.Source,
            SessionId = slip.SessionId,
            CapturedAtUtc = slip.CreatedAtUtc,
            DeletedFromBucketId = slip.DeletedFromBucketId,
            DeletedAtUtc = slip.DeletedAtUtc,
            ExcludedFromViews = slip.ExcludedFromViews,
            Align = slip.Align,
            BlockKind = slip.BlockKind,
            IgnoreBucketRenderKind = slip.IgnoreBucketRenderKind,
            Checked = slip.Checked,
            Bold = slip.Bold,
            Italic = slip.Italic,
            Strike = slip.Strike,
            FontFamily = slip.FontFamily,
            FontSize = slip.FontSize,
            TextColor = slip.TextColor,
            InlineStyles = slip.InlineStyles.Select(style => style with { }).ToList()
        };
    }

    private static string SummaryPreviewText(ZetlProject project)
    {
        var snippets = project.Buckets
            .Where(bucket => !ZetlStateRules.IsDeletedBucket(bucket))
            .SelectMany(bucket => bucket.Slips)
            .Where(slip => !string.IsNullOrWhiteSpace(slip.Title) || !string.IsNullOrWhiteSpace(slip.Text))
            .OrderByDescending(slip => slip.CreatedAtUtc)
            .Take(3)
            .Select(slip => ZetlStateRules.PreviewText(
                string.IsNullOrWhiteSpace(slip.Title) ? slip.Text : slip.Title))
            .ToList();

        return snippets.Count == 0
            ? ""
            : string.Join(Environment.NewLine, snippets);
    }

    private static DateTimeOffset? LastActivityUtc(IReadOnlyList<ZetlSlip> visibleSlips)
    {
        return visibleSlips.Count == 0
            ? null
            : visibleSlips.Max(slip => slip.CreatedAtUtc);
    }
}
