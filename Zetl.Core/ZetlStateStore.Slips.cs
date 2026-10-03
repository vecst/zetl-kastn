using ZETL.Contracts;
using static ZETL.ZetlStateRules;

namespace ZETL;

// Slips: adding text and pictures, editing, moving, cloning, and the lists
// Compose and the Board show.
internal sealed partial class ZetlStateStore
{
    // Forwards to the overload below, which takes the lock.
    public ZetlSlip AddSlip(
        ZetlBucket bucket,
        string text,
        string source,
        ZetlCaptureOrigin? captureOrigin) =>
        AddSlip(bucket, text, source, null, null, captureOrigin);

    public ZetlSlip AddSlip(
        ZetlBucket bucket,
        string text,
        string source,
        string? noteSessionId = null,
        DateTime? createdAtUtc = null,
        ZetlCaptureOrigin? captureOrigin = null,
        string? title = null,
        string? blockKind = null,
        bool? ignoreBucketRenderKind = null,
        string? richHtml = null,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats = null)
    {
        lock (stateGate)
        {
            var note = new ZetlSlip
            {
                Id = NewId(),
                Title = (title ?? "").Trim(),
                Text = text.Trim(),
                RichHtml = string.IsNullOrWhiteSpace(richHtml) ? null : richHtml,
                ReplayFormats = CloneReplayFormats(replayFormats),
                Source = source,
                SessionId = noteSessionId ?? sessionId,
                CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow,
                CaptureOrigin = captureOrigin,
                BlockKind = ZetlBlockKinds.Normalize(blockKind),
                IgnoreBucketRenderKind = ignoreBucketRenderKind == true
            };
            bucket.Slips.Add(note);
            PersistBucket(bucket);
            return note;
        }
    }

    // With preferTextContent, the caption is the slip's text *content* and the
    // slip presents as Text/Url with the picture attached — a dual capture where
    // the clipboard carried both formats (e.g. spreadsheet cells). Type stays
    // the preferred representation; the picture rides along for Kastn.
    public ZetlSlip AddImageSlip(
        ZetlProject project,
        ZetlBucket bucket,
        ZetlClipboardImage image,
        string source,
        ZetlCaptureOrigin? captureOrigin = null,
        string? caption = null,
        string? sourceUrl = null,
        bool preferTextContent = false,
        string? richHtml = null,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats = null)
    {
        if (!project.Buckets.Any(item => item.Id == bucket.Id))
        {
            throw new InvalidOperationException("The image destination bucket does not belong to the project.");
        }

        var note = new ZetlSlip
        {
            Id = NewId(),
            Type = ZetlSlipType.Picture,
            Text = (caption ?? "").Trim(),
            RichHtml = string.IsNullOrWhiteSpace(richHtml) ? null : richHtml,
            ReplayFormats = CloneReplayFormats(replayFormats),
            Image = CreateImageAsset(project, image, sourceUrl),
            Source = source,
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CaptureOrigin = captureOrigin
        };
        if (preferTextContent && !string.IsNullOrWhiteSpace(note.Text))
        {
            note.Type = ZetlSlipClassifier.LooksLikeUrl(note.Text)
                ? ZetlSlipType.Url
                : ZetlSlipType.Text;
        }

        bucket.Slips.Add(note);
        PersistProject(project);
        return note;
    }

    // Attach or replace a slip's picture. The Image setter keeps a slip with
    // text content presenting as text (a dual slip) and turns a text-less slip
    // into a picture slip. The prior asset file stays content-addressed on disk.
    public void SetSlipImage(ZetlProject project, ZetlSlip note, ZetlClipboardImage image)
    {
        lock (stateGate)
        {
            note.Image = CreateImageAsset(project, image, sourceUrl: null);
            note.Revision++;
            PersistProject(project);
        }
    }

    // Detach a slip's picture. The Image setter returns a picture-presenting
    // slip to its text (or URL) representation.
    public void RemoveSlipImage(ZetlProject project, ZetlSlip note)
    {
        lock (stateGate)
        {
            note.Image = null;
            note.Revision++;
            PersistProject(project);
        }
    }

    private ZetlImageAsset CreateImageAsset(
        ZetlProject project,
        ZetlClipboardImage image,
        string? sourceUrl)
    {
        if (image.PngBytes.Length == 0 || image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidDataException("The image is empty or has invalid dimensions.");
        }

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(image.PngBytes))
            .ToLowerInvariant();
        var relativePath = projectStorage.WriteAsset(project, hash, ".png", image.PngBytes);
        return new ZetlImageAsset
        {
            RelativePath = relativePath,
            SourceUrl = sourceUrl,
            Width = image.Width,
            Height = image.Height,
            ByteLength = image.PngBytes.LongLength,
            Sha256 = hash
        };
    }

    public byte[]? ReadImageAsset(ZetlProject project, ZetlSlip note)
    {
        return note.Image is not null
            ? projectStorage.ReadAsset(project, note.Image.RelativePath)
            : null;
    }

    public IReadOnlyList<ZetlProjectAssetFile> GetProjectAssets(ZetlProject project) =>
        projectStorage.GetAssets(project);

    // Adds one note per non-blank text, preserving order, with a single save.
    // Used by a structured compile-to-bucket that keeps notes separate instead
    // of flattening them into one combined note.
    public IReadOnlyList<ZetlSlip> AddSlips(ZetlBucket bucket, IEnumerable<string> texts, string source)
    {
        lock (stateGate)
        {
            var added = new List<ZetlSlip>();
            foreach (var text in texts)
            {
                var trimmed = (text ?? "").Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                var note = new ZetlSlip
                {
                    Id = NewId(),
                    Text = trimmed,
                    Source = source,
                    SessionId = sessionId,
                    CreatedAtUtc = DateTime.UtcNow
                };
                bucket.Slips.Add(note);
                added.Add(note);
            }

            if (added.Count > 0)
            {
                PersistBucket(bucket);
            }

            return added;
        }
    }

    // Appends activity-log lines as notes in a dedicated "Zetl Logs" project,
    // grouped into a per-day bucket. The log project is infrastructure: it is
    // never made the active project, so it cannot hijack a lane. Retention is
    // bounded (the day bucket is capped and stale day buckets are dropped) so the
    // file cannot grow without limit, and it saves once per call -- the caller
    // batches lines so logging stays off the per-keystroke path.
    public void AppendLogSlips(IReadOnlyCollection<string> messages, int maxDayBuckets, int maxNotesPerBucket)
    {
        lock (stateGate)
        {
            if (messages.Count == 0)
            {
                return;
            }

            var project = State.Projects.FirstOrDefault(item =>
                string.Equals(item.Name, LogProjectName, StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                project = new ZetlProject { Id = NewId(), Name = LogProjectName };
                State.Projects.Add(project);
            }

            var dayName = DateTime.Now.ToString("yyyy-MM-dd");
            var bucket = project.Buckets.FirstOrDefault(item =>
                string.Equals(item.Name, dayName, StringComparison.OrdinalIgnoreCase));
            if (bucket is null)
            {
                bucket = CreateBucket(dayName);
                ApplyBucketDefaults(bucket);
                project.Buckets.Add(bucket);
            }

            foreach (var message in messages)
            {
                var trimmed = (message ?? "").Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                bucket.Slips.Add(new ZetlSlip
                {
                    Id = NewId(),
                    Text = trimmed,
                    Source = "log",
                    SessionId = sessionId,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            if (bucket.Slips.Count > maxNotesPerBucket)
            {
                bucket.Slips.RemoveRange(0, bucket.Slips.Count - maxNotesPerBucket);
            }

            // Day-bucket names are yyyy-MM-dd, so ordinal-descending order is newest
            // first. Keep only the most recent day buckets; never drop Scratch.
            foreach (var stale in project.Buckets
                .Where(item => !IsScratchBucket(item))
                .OrderByDescending(item => item.Name, StringComparer.Ordinal)
                .Skip(maxDayBuckets)
                .ToList())
            {
                project.Buckets.Remove(stale);
            }

            PersistProject(project);
        }
    }

    public void DeleteSlip(ZetlBucket bucket, string noteId)
    {
        lock (stateGate)
        {
            var note = bucket.Slips.FirstOrDefault(item => item.Id == noteId);
            if (note is null)
            {
                return;
            }

            bucket.Slips.Remove(note);
            PersistBucket(bucket);
        }
    }

    public void UpdateSlip(
        ZetlSlip note,
        string text,
        string? title = null,
        bool? excludedFromViews = null,
        string? align = null,
        string? blockKind = null,
        bool? ignoreBucketRenderKind = null,
        bool? @checked = null,
        IReadOnlyList<ZetlInlineStyleRange>? inlineStyles = null,
        bool? bold = null,
        bool? italic = null,
        bool? strike = null,
        ZetlSlipType? type = null,
        string? fontFamily = null,
        int? fontSize = null,
        string? textColor = null)
    {
        lock (stateGate)
        {
            var normalizedText = text.Trim();
            if (!string.Equals(note.Text, normalizedText, StringComparison.Ordinal))
            {
                // Once the visible text is edited, the source application's HTML no
                // longer describes it and must not be replayed as stale content.
                note.RichHtml = null;
                note.ReplayFormats = null;
            }
            note.Text = normalizedText;
            // The preferred representation of a dual slip. Picture requires an
            // attached picture (the caller validates); a text preference is
            // re-classified so a bare link presents as Url.
            if (type is { } preferredType)
            {
                note.Type = preferredType == ZetlSlipType.Picture && note.Image is not null
                    ? ZetlSlipType.Picture
                    : ZetlSlipClassifier.LooksLikeUrl(note.Text)
                        ? ZetlSlipType.Url
                        : ZetlSlipType.Text;
            }

            if (title is not null)
            {
                note.Title = title.Trim();
            }

            if (excludedFromViews is { } excluded)
            {
                note.ExcludedFromViews = excluded;
            }

            if (align is not null)
            {
                // Normalize to keep JSON clean: left is the implicit default (null).
                var normalized = align.Trim().ToLowerInvariant();
                note.Align = normalized is "center" or "right" ? normalized : null;
            }

            if (blockKind is not null)
            {
                // Normalize to a known kind; anything else (including "paragraph"/"none")
                // clears it back to a plain paragraph.
                note.BlockKind = ZetlBlockKinds.Normalize(blockKind);
            }

            if (ignoreBucketRenderKind is { } ignoreBucket)
            {
                note.IgnoreBucketRenderKind = ignoreBucket;
            }

            if (@checked is { } isChecked)
            {
                note.Checked = isChecked;
            }

            // Checked can be rendered by explicit task notes or by a task bucket composed
            // with another slip kind. Clearing happens only when a command explicitly
            // changes the slip kind away from task.
            if (blockKind is not null && note.BlockKind != ZetlBlockKinds.Task)
            {
                note.Checked = false;
            }

            if (bold is { } isBold)
            {
                note.Bold = isBold;
            }

            if (italic is { } isItalic)
            {
                note.Italic = isItalic;
            }

            if (strike is { } isStrike)
            {
                note.Strike = isStrike;
            }

            if (fontFamily is not null)
            {
                note.FontFamily = ZetlSlipTypography.NormalizeFontFamily(fontFamily);
            }

            if (fontSize is { } authoredFontSize)
            {
                note.FontSize = ZetlSlipTypography.NormalizeFontSize(authoredFontSize);
            }

            if (textColor is not null)
            {
                note.TextColor = ZetlSlipTypography.NormalizeTextColor(textColor);
            }

            note.InlineStyles = ZetlInlineStyles.Normalize(
                note.Text,
                inlineStyles ?? note.InlineStyles);

            note.Revision++;
            PersistSlip(note);
        }
    }

    public bool MoveSlip(ZetlProject project, ZetlSlip note, ZetlBucket destination)
    {
        lock (stateGate)
        {
            var source = project.Buckets.FirstOrDefault(bucket =>
                bucket.Slips.Any(item => item.Id == note.Id));
            if (source is null
                || project.Buckets.All(bucket => bucket.Id != destination.Id)
                || source.Id == destination.Id)
            {
                return false;
            }

            source.Slips.RemoveAll(item => item.Id == note.Id);
            destination.Slips.Add(note);
            if (IsDeletedBucket(destination))
            {
                note.DeletedFromBucketId = source.Id;
                note.DeletedAtUtc = DateTime.UtcNow;
            }
            else if (IsDeletedBucket(source))
            {
                note.DeletedFromBucketId = null;
                note.DeletedAtUtc = null;
            }

            note.Revision++;
            PersistProject(project);
            return true;
        }
    }

    public bool ReorderSlip(ZetlProject project, ZetlSlip note, string? beforeNoteId)
    {
        lock (stateGate)
        {
            var bucket = project.Buckets.FirstOrDefault(bucket =>
                bucket.Slips.Any(item => item.Id == note.Id));
            if (bucket is null)
            {
                return false;
            }

            var currentIndex = bucket.Slips.FindIndex(item => item.Id == note.Id);
            int targetIndex;
            if (beforeNoteId is null)
            {
                targetIndex = bucket.Slips.Count;
            }
            else
            {
                var anchorIndex = bucket.Slips.FindIndex(item => item.Id == beforeNoteId);
                if (anchorIndex < 0)
                {
                    return false;
                }

                targetIndex = anchorIndex;
            }

            bucket.Slips.RemoveAt(currentIndex);
            if (targetIndex > currentIndex)
            {
                targetIndex--;
            }

            targetIndex = Math.Clamp(targetIndex, 0, bucket.Slips.Count);
            bucket.Slips.Insert(targetIndex, note);
            note.Revision++;
            PersistProject(project);
            return true;
        }
    }

    // Compile operates on the whole project by default. Passing
    // currentSessionOnly narrows it to notes captured this session, which the
    // compile dialog exposes as a "This session only" toggle.
    public IReadOnlyList<SlipDisplayItem> GetSlipDisplayItems(ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope = null, bool currentSessionOnly = false)
    {
        var scopedBucketIds = bucketScope?.Select(bucket => bucket.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<SlipDisplayItem>();
        foreach (var bucketItem in GetBucketDisplayItems(project)
            .Where(item => scopedBucketIds is null || scopedBucketIds.Contains(item.Bucket.Id)))
        {
            foreach (var note in bucketItem.Bucket.Slips.Where(note => IsCompilableSlip(note, currentSessionOnly)))
            {
                result.Add(new SlipDisplayItem(bucketItem.Bucket, note, $"{bucketItem.Label.Trim()}: {PreviewText(note.Text)}"));
            }
        }

        return result;
    }

    public bool TryGetLastSlipDisplayItem(ZetlProject project, IReadOnlyList<ZetlBucket>? bucketScope, out SlipDisplayItem? slip, bool currentSessionOnly = false)
    {
        slip = GetSlipDisplayItems(project, bucketScope, currentSessionOnly)
            .OrderByDescending(item => item.Slip.CreatedAtUtc)
            .FirstOrDefault();
        return slip is not null;
    }

    public bool HasCompilableSlips(ZetlProject project, bool currentSessionOnly = false)
    {
        return project.Buckets.Any(bucket =>
            !IsDeletedBucket(bucket)
            && bucket.Slips.Any(note => IsCompilableSlip(note, currentSessionOnly)));
    }

    private bool IsCompilableSlip(ZetlSlip note, bool currentSessionOnly)
    {
        return (!currentSessionOnly || IsCurrentSessionSlip(note))
            && !IsStructuralSlip(note)
            && !string.IsNullOrWhiteSpace(note.Text);
    }

    // A fresh copy of a slip's authored content and presentation under a new id,
    // stamped as captured now in this session. The caller supplies the picture: a
    // reference to the same asset within a project, or an asset copied across.
    private ZetlSlip CloneSlip(
        ZetlSlip source,
        string cloneSource,
        ZetlImageAsset? image,
        bool trimText = false)
    {
        var clone = new ZetlSlip
        {
            Id = NewId(),
            Type = source.Type,
            Title = source.Title,
            Text = trimText ? source.Text.Trim() : source.Text,
            RichHtml = source.RichHtml,
            ReplayFormats = CloneReplayFormats(source.ReplayFormats),
            Source = cloneSource,
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExcludedFromViews = source.ExcludedFromViews,
            Align = source.Align,
            BlockKind = source.BlockKind,
            IgnoreBucketRenderKind = source.IgnoreBucketRenderKind,
            Checked = source.Checked,
            Bold = source.Bold,
            Italic = source.Italic,
            Strike = source.Strike,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            TextColor = source.TextColor,
            InlineStyles = source.InlineStyles.Select(style => style with { }).ToList(),
            CaptureOrigin = source.CaptureOrigin is { } origin
                ? new ZetlCaptureOrigin
                {
                    ApplicationName = origin.ApplicationName,
                    ProcessName = origin.ProcessName,
                    WindowTitle = origin.WindowTitle
                }
                : null
        };

        // Assigned after Text so the Image setter sees the content and keeps a
        // text-preferred dual slip presenting as text.
        clone.Image = image;
        return clone;
    }

    private static List<ZetlClipboardFormatData>? CloneReplayFormats(
        IReadOnlyList<ZetlClipboardFormatData>? formats) =>
        formats is not { Count: > 0 }
            ? null
            : formats.Select(item => new ZetlClipboardFormatData(
                item.Format,
                item.Data.ToArray(),
                item.RegisteredName)).ToList();

    private static ZetlImageAsset? CloneImageAssetReference(ZetlImageAsset? source)
    {
        return source is null
            ? null
            : new ZetlImageAsset
            {
                RelativePath = source.RelativePath,
                SourceUrl = source.SourceUrl,
                MimeType = source.MimeType,
                Width = source.Width,
                Height = source.Height,
                ByteLength = source.ByteLength,
                Sha256 = source.Sha256
            };
    }

    // The picture copied into another project's asset folder, or null when the
    // source has none or its asset file is missing.
    private ZetlImageAsset? CopyImageAssetAcross(
        ZetlProject sourceProject,
        ZetlProject targetProject,
        ZetlImageAsset? source)
    {
        var bytes = source is null ? null : projectStorage.ReadAsset(sourceProject, source.RelativePath);
        if (bytes is null)
        {
            return null;
        }

        var extension = Path.GetExtension(source!.RelativePath);
        var copy = CloneImageAssetReference(source)!;
        copy.RelativePath = projectStorage.WriteAsset(
            targetProject,
            source.Sha256,
            string.IsNullOrWhiteSpace(extension) ? ".png" : extension,
            bytes);
        return copy;
    }
}
