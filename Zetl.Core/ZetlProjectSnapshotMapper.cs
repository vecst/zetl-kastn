using ZETL.Contracts;

namespace ZETL;

internal static class ZetlProjectSnapshotMapper
{
    public static ZetlProjectSummary ToSummary(ZetlProject project)
    {
        return new ZetlProjectSummary
        {
            Id = project.Id,
            Name = project.Name,
            MetadataRevision = project.MetadataRevision,
            ChangeSequence = project.ChangeSequence,
            BucketCount = project.Buckets.Count,
            SlipCount = project.Buckets.Sum(bucket => bucket.Notes.Count)
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
            ActiveBucketId = project.ActiveBucketId,
            Buckets = project.Buckets.Select(ToSnapshot).ToList(),
            Slips = project.Buckets
                .SelectMany(bucket => bucket.Notes.Select(note => ToSnapshot(bucket, note)))
                .ToList()
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
            Type = ZetlSlipType.Text,
            BucketId = bucket.Id,
            Text = note.Text,
            Source = note.Source,
            SessionId = note.SessionId,
            CapturedAtUtc = new DateTimeOffset(
                DateTime.SpecifyKind(note.CreatedAtUtc, DateTimeKind.Utc)),
            DeletedFromBucketId = note.DeletedFromBucketId,
            DeletedAtUtc = note.DeletedAtUtc is null
                ? null
                : new DateTimeOffset(
                    DateTime.SpecifyKind(note.DeletedAtUtc.Value, DateTimeKind.Utc))
        };
    }
}
