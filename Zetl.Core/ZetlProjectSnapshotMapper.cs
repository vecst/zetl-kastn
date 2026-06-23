using ZETL.Contracts;

namespace ZETL;

internal static class ZetlProjectSnapshotMapper
{
    public static ZetlProjectSummary ToSummary(ZetlProject project)
    {
        var visibleBuckets = project.Buckets
            .Where(bucket => !ZetlStateStore.IsDeletedBucket(bucket))
            .ToList();
        var visibleNotes = visibleBuckets
            .SelectMany(bucket => bucket.Notes)
            .ToList();
        var deletedSlipCount = project.Buckets
            .Where(ZetlStateStore.IsDeletedBucket)
            .Sum(bucket => bucket.Notes.Count);

        return new ZetlProjectSummary
        {
            Id = project.Id,
            Name = project.Name,
            MetadataRevision = project.MetadataRevision,
            ChangeSequence = project.ChangeSequence,
            Status = project.Status,
            BucketCount = project.Buckets.Count,
            SlipCount = project.Buckets.Sum(bucket => bucket.Notes.Count),
            VisibleBucketCount = visibleBuckets.Count,
            VisibleSlipCount = visibleNotes.Count,
            DeletedSlipCount = deletedSlipCount,
            LastActivityUtc = LastActivityUtc(visibleNotes),
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
            ActiveBucketId = project.ActiveBucketId,
            DefaultViewId = project.DefaultViewId,
            Views = project.Views.Select(ToSnapshot).ToList(),
            Buckets = project.Buckets.Select(ToSnapshot).ToList(),
            Slips = project.Buckets
                .SelectMany(bucket => bucket.Notes.Select(note => ToSnapshot(bucket, note)))
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
            Settings = new ZetlBucketSettings
            {
                Kind = bucket.Kind,
                DefaultKind = bucket.DefaultKind,
                DefaultCompileMode = bucket.DefaultCompileMode,
                DefaultStartingText = bucket.DefaultStartingText,
                DefaultTsvRowLength = bucket.DefaultTsvRowLength,
                PopMode = bucket.PopMode,
                ReplayReviewBucketId = bucket.FifoReviewBucketId
            }
        };
    }

    public static ZetlSlipSnapshot ToSnapshot(ZetlBucket bucket, ZetlNote note)
    {
        return new ZetlSlipSnapshot
        {
            Id = note.Id,
            Revision = note.Revision,
            Type = note.IsImage ? ZetlSlipType.Picture : ZetlSlipType.Text,
            BucketId = bucket.Id,
            Title = note.Title,
            Text = note.Text,
            Picture = note.Image is null
                ? null
                : new ZetlPictureSnapshot
                {
                    SourceUrl = note.Image.SourceUrl,
                    MimeType = note.Image.MimeType,
                    Width = note.Image.Width,
                    Height = note.Image.Height,
                    ByteLength = note.Image.ByteLength,
                    Sha256 = note.Image.Sha256
                },
            CaptureOrigin = note.CaptureOrigin is null
                ? null
                : new ZetlCaptureOriginSnapshot
                {
                    ApplicationName = note.CaptureOrigin.ApplicationName,
                    ProcessName = note.CaptureOrigin.ProcessName,
                    WindowTitle = note.CaptureOrigin.WindowTitle
                },
            Source = note.Source,
            SessionId = note.SessionId,
            CapturedAtUtc = new DateTimeOffset(
                DateTime.SpecifyKind(note.CreatedAtUtc, DateTimeKind.Utc)),
            DeletedFromBucketId = note.DeletedFromBucketId,
            DeletedAtUtc = note.DeletedAtUtc is null
                ? null
                : new DateTimeOffset(
                    DateTime.SpecifyKind(note.DeletedAtUtc.Value, DateTimeKind.Utc)),
            ExcludedFromViews = note.ExcludedFromViews,
            Align = note.Align
        };
    }

    private static string SummaryPreviewText(ZetlProject project)
    {
        var snippets = project.Buckets
            .Where(bucket => !ZetlStateStore.IsDeletedBucket(bucket))
            .SelectMany(bucket => bucket.Notes)
            .Where(note => !string.IsNullOrWhiteSpace(note.Title) || !string.IsNullOrWhiteSpace(note.Text))
            .OrderByDescending(note => note.CreatedAtUtc)
            .Take(3)
            .Select(note => ZetlStateStore.PreviewText(
                string.IsNullOrWhiteSpace(note.Title) ? note.Text : note.Title))
            .ToList();

        return snippets.Count == 0
            ? ""
            : string.Join(Environment.NewLine, snippets);
    }

    private static DateTimeOffset? LastActivityUtc(IReadOnlyList<ZetlNote> visibleNotes)
    {
        return visibleNotes.Count == 0
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(
                visibleNotes.Max(note => note.CreatedAtUtc),
                DateTimeKind.Utc));
    }
}
