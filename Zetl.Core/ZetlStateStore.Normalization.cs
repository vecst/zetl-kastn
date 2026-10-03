using System.Runtime.CompilerServices;
using ZETL.Contracts;
using static ZETL.ZetlStateRules;

namespace ZETL;

// Repairs state as it loads: missing ids and names, stale pointers, and the
// Journal and last-project pointers each lane keeps.
internal sealed partial class ZetlStateStore
{
    private void NormalizeLoadedState()
    {
        State.Version = Math.Max(State.Version, 1);
        State.Projects ??= new List<ZetlProject>();
        foreach (var project in State.Projects)
        {
            NormalizeProject(project);
        }

        NormalizeWorkspacePointers();
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
    }

    private void NormalizeProject(ZetlProject project)
    {
        project.Id = string.IsNullOrWhiteSpace(project.Id) ? NewId() : project.Id;
        project.Name = NormalizeName(project.Name, DefaultProjectName());
        project.MetadataRevision = Math.Max(project.MetadataRevision, 1);
        project.ChangeSequence = Math.Max(project.ChangeSequence, 0);
        project.Status = NormalizeProjectStatus(project.Status);
        project.Kind = NormalizeProjectKind(project.Kind);
        project.SourceTemplateId = string.IsNullOrWhiteSpace(project.SourceTemplateId)
            ? null
            : project.SourceTemplateId.Trim();
        project.TemporaryLane = CanonicalTemporaryLane(project.TemporaryLane);
        if (!IsTemporaryConsumableProject(project))
        {
            project.SourceTemplateId = null;
            project.TemporaryLane = null;
        }
        project.Consumable |= IsTemporaryConsumableProject(project);
        if (!project.Consumable)
        {
            project.ReturnProjectId = null;
        }
        project.Views ??= [];
        foreach (var view in project.Views)
        {
            view.Sections ??= [];
            foreach (var section in view.Sections)
            {
                section.Buckets ??= [];
            }
        }
        project.Buckets ??= new List<ZetlBucket>();
        if (project.JournalMode)
        {
            // A journal has no Scratch bucket — its per-day Quick Note child is the
            // quick-note catch-all. Normalize is the single persist chokepoint, so
            // dropping a stray empty Scratch here also cleans up journals seeded by an
            // earlier build or by a shared bucket-cleanup path.
            RemoveEmptyScratchBucket(project);
        }
        else
        {
            EnsureScratchBucket(project.Buckets);
        }
        foreach (var bucket in project.Buckets)
        {
            bucket.Id = string.IsNullOrWhiteSpace(bucket.Id) ? NewId() : bucket.Id;
            bucket.Revision = Math.Max(bucket.Revision, 1);
            bucket.Name = NormalizeName(bucket.Name, "Bucket");
            if (bucket.ParentBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.ParentBucketId || IsDeletedBucket(candidate)))
            {
                bucket.ParentBucketId = null;
            }

            if (bucket.Settings.ReplayReviewBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.Settings.ReplayReviewBucketId || IsDeletedBucket(candidate)))
            {
                bucket.Settings.ReplayReviewBucketId = null;
            }

            if (bucket.Settings.PassThroughReviewBucketId == bucket.Id
                || project.Buckets.All(candidate => candidate.Id != bucket.Settings.PassThroughReviewBucketId || IsDeletedBucket(candidate)))
            {
                bucket.Settings.PassThroughReviewBucketId = null;
            }

            bucket.Settings.Kind = NormalizeBucketKind(bucket.Settings.Kind);
            bucket.Settings.DefaultKind = NormalizeBucketKind(string.IsNullOrWhiteSpace(bucket.Settings.DefaultKind) ? bucket.Settings.Kind : bucket.Settings.DefaultKind);
            bucket.Settings.DefaultCompileMode = NormalizeCompileMode(bucket.Settings.DefaultCompileMode);
            bucket.Settings.DefaultStartingText ??= "";
            bucket.Settings.DefaultTsvRowLength = bucket.Settings.DefaultTsvRowLength <= 0 ? 5 : bucket.Settings.DefaultTsvRowLength;
            var headingAlign = ZetlViewRenderer.NormalizeHeadingAlign(bucket.HeadingAlign);
            bucket.HeadingAlign = headingAlign == "left" ? "" : headingAlign;
            bucket.HeadingLevel = Math.Clamp(bucket.HeadingLevel, 0, 6);
            if (IsDeletedBucket(bucket) || IsDeletedBucketName(bucket.Name))
            {
                EnsureDeletedBucketShape(bucket);
            }

            bucket.Slips ??= new List<ZetlSlip>();
            foreach (var note in bucket.Slips)
            {
                note.Id = string.IsNullOrWhiteSpace(note.Id) ? NewId() : note.Id;
                note.Revision = Math.Max(note.Revision, 1);
                note.Title ??= "";
                note.Text ??= "";
                // Heal an image slip that lost its type (legacy files), but keep
                // dual captures: a slip with text content and a picture is
                // legitimately Text-preferred.
                if (note.Type == ZetlSlipType.Text
                    && note.Image is not null
                    && string.IsNullOrWhiteSpace(note.Text))
                {
                    note.Type = ZetlSlipType.Picture;
                }
                else if (note.Type == ZetlSlipType.Text && ZetlSlipClassifier.LooksLikeUrl(note.Text))
                {
                    note.Type = ZetlSlipType.Url;
                }
                note.Source ??= "";
                note.FontFamily = ZetlSlipTypography.NormalizeFontFamily(note.FontFamily);
                note.FontSize = ZetlSlipTypography.NormalizeFontSize(note.FontSize);
                note.TextColor = ZetlSlipTypography.NormalizeTextColor(note.TextColor);
                if (note.CreatedAtUtc == default)
                {
                    note.CreatedAtUtc = DateTimeOffset.UtcNow;
                }
            }
        }

        if (project.Buckets.All(bucket => bucket.Id != project.ActiveBucketId || IsDeletedBucket(bucket)))
        {
            project.ActiveBucketId = FirstActiveWorkflowBucket(project)?.Id;
        }

        if (project.QuickNoteBucketId is not null
            && project.Buckets.All(bucket => bucket.Id != project.QuickNoteBucketId || IsDeletedBucket(bucket)))
        {
            project.QuickNoteBucketId = null;
        }
    }

    private void NormalizeWorkspacePointers()
    {
        State.Version = Math.Max(State.Version, 1);
        if (State.ActiveProjectId is not null
            && State.Projects.All(project => project.Id != State.ActiveProjectId || !IsActiveStatus(project)))
        {
            State.ActiveProjectId = null;
        }

        if (State.ShiftActiveProjectId is not null
            && State.Projects.All(project => project.Id != State.ShiftActiveProjectId || !IsActiveStatus(project)))
        {
            State.ShiftActiveProjectId = null;
        }

        NormalizeJournalPointer(shifted: false);
        NormalizeJournalPointer(shifted: true);
        NormalizeLastDeliberatePointer(shifted: false);
        NormalizeLastDeliberatePointer(shifted: true);
    }

    private void NormalizeJournalPointer(bool shifted)
    {
        var pointer = shifted ? State.ShiftDefaultJournalProjectId : State.DefaultJournalProjectId;
        var valid = pointer is not null
            && State.Projects.Any(project =>
                project.Id == pointer && project.JournalMode && IsActiveStatus(project));
        if (!valid)
        {
            pointer = null;
        }

        if (pointer is null)
        {
            var activeId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
            pointer = State.Projects.FirstOrDefault(project =>
                    project.Id == activeId && project.JournalMode && IsActiveStatus(project))?.Id
                ?? State.Projects
                    .Where(project => project.JournalMode
                        && IsActiveStatus(project)
                        && IsFormattedJournalName(project.Name, shifted))
                    .OrderByDescending(project => project.ChangeSequence)
                    .Select(project => project.Id)
                    .FirstOrDefault();
        }

        if (shifted)
        {
            State.ShiftDefaultJournalProjectId = pointer;
        }
        else
        {
            State.DefaultJournalProjectId = pointer;
        }
    }

    private void NormalizeLastDeliberatePointer(bool shifted)
    {
        var pointer = shifted ? State.ShiftLastDeliberateProjectId : State.LastDeliberateProjectId;
        var valid = pointer is not null
            && State.Projects.Any(project =>
                project.Id == pointer && IsDeliberateProject(project));
        if (!valid)
        {
            pointer = null;
        }

        if (pointer is null)
        {
            var activeId = shifted ? State.ShiftActiveProjectId : State.ActiveProjectId;
            pointer = State.Projects.FirstOrDefault(project =>
                project.Id == activeId && IsDeliberateProject(project))?.Id;
        }

        if (shifted)
        {
            State.ShiftLastDeliberateProjectId = pointer;
        }
        else
        {
            State.LastDeliberateProjectId = pointer;
        }
    }
}
