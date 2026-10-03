using ZETL.Contracts;
using static ZETL.ZetlStateRules;

namespace ZETL;

// The lanes' capture home: the rolling Journal, its day buckets, auto-return from a quiet
// project, and Ctrl+J between the Journal and the last project.
internal sealed partial class ZetlStateStore
{
    // Where a held capture gesture (Ctrl+C, Ctrl+X, Ctrl+A) files by default: the
    // lane's active project, or the Journal when none is active. Looking this up
    // never changes which project is active; activation is only ever the user's
    // decision (a dialog saved with its Activate toggle on, the Board, Ctrl+J).
    public ZetlProject GetCaptureHome(bool shifted = false)
    {
        lock (stateGate)
        {
            var project = ResolveActiveCaptureProject(shifted)
                ?? GetOrCreateJournalProject(shifted, activate: false);
            // A journal always has today's day bucket present before the capture path
            // resolves a target, highlighted unless the user picked a bucket of their
            // own. Its Capture / Quick Note children stay lazy.
            EnsureJournalDayBucket(
                project,
                DateTime.Now,
                setActive: UserSelectedJournalBucket(project) is null);
            return project;
        }
    }

    // Where a tapped Ctrl+C is auto-captured: the lane's active project, else the
    // Journal when the idle-copy setting captures there, else nowhere (null).
    public ZetlProject? GetTapCaptureProject(bool shifted = false)
    {
        lock (stateGate)
        {
            return ResolveActiveCaptureProject(shifted)
                ?? (ZetlIdleCopyCapture.CapturesToJournal(Defaults.IdleCopyCapture)
                    ? GetOrCreateJournalProject(shifted, activate: false)
                    : null);
        }
    }

    // Count a capture into <project> as activity for the auto-return window. Call
    // before the capture's own write so the timestamp persists with it.
    public void RecordCaptureActivity(ZetlProject project) => TouchProjectActivity(project);

    // The lane's active project as capture should see it. A journal from a period
    // that has rolled over hands its activation to the current journal, and a
    // deliberate project quiet past the auto-return window is switched off.
    private ZetlProject? ResolveActiveCaptureProject(bool shifted)
    {
        if (GetActiveProject(shifted) is not { } active)
        {
            return null;
        }

        if (active.JournalMode)
        {
            var expectedName = ExpectedJournalProjectName(DateTime.Now, shifted);
            return IsFormattedJournalName(active.Name, shifted)
                && !string.Equals(active.Name, expectedName, StringComparison.OrdinalIgnoreCase)
                    ? GetOrCreateJournalProject(shifted, activate: true)
                    : active;
        }

        if (!IsQuietPastAutoReturn(active))
        {
            return active;
        }

        // Quiet past the configured window: return the lane to no project, so a
        // forgotten project never traps captures. The idle-copy setting then decides
        // whether tapped copies go to the Journal.
        SetActiveProjectId(null, shifted);
        PersistWorkspace();
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
        return null;
    }

    // True when a deliberate project should be switched off: the auto-return
    // window is on and it has had no capture for at least that long.
    private bool IsQuietPastAutoReturn(ZetlProject active)
    {
        if (active.JournalMode || active.LastActiveUtc == default)
        {
            return false;
        }

        var hours = Defaults.JournalAutoReturnHours;
        return hours > 0 && DateTime.UtcNow - active.LastActiveUtc >= TimeSpan.FromHours(hours);
    }

    // Mark a deliberate project as just used, refreshing its auto-return window. The
    // Journal never auto-returns, so it is left untouched (and pays no extra cost).
    private static void TouchProjectActivity(ZetlProject project)
    {
        if (!project.JournalMode)
        {
            project.LastActiveUtc = DateTime.UtcNow;
        }
    }

    // The lane's default journal, created on first use and tracked by id (so a rename
    // never loses it). Activated only when the caller asks (Ctrl+J, a rolled-over
    // journal that was active); capture lookups use it without activating it.
    private ZetlProject GetOrCreateJournalProject(bool shifted, bool activate)
    {
        var pointerId = shifted ? State.ShiftDefaultJournalProjectId : State.DefaultJournalProjectId;
        var expectedName = ExpectedJournalProjectName(DateTime.Now, shifted);
        // Reuse the journal only while it is still Active and matches the current expected name.
        // If it was finished, archived, or the interval rolled, capture mints a fresh journal.
        if (pointerId is not null
            && State.Projects.FirstOrDefault(project => project.Id == pointerId) is { } existing
            && IsActiveStatus(existing)
            && (!IsFormattedJournalName(existing.Name, shifted) || string.Equals(existing.Name, expectedName, StringComparison.OrdinalIgnoreCase)))
        {
            if (activate)
            {
                SetActiveProjectId(existing.Id, shifted);
                PersistWorkspace();
                DisposeInactiveTemporaryProjects(persistWorkspace: true);
            }

            return existing;
        }

        var journal = new ZetlProject
        {
            Id = NewId(),
            Name = expectedName,
            JournalMode = true
        };
        State.Projects.Add(journal);
        if (shifted)
        {
            State.ShiftDefaultJournalProjectId = journal.Id;
        }
        else
        {
            State.DefaultJournalProjectId = journal.Id;
        }

        // Pre-seed the whole period's day-parent buckets so future/past days are
        // ready as reminder slots, then highlight today.
        SeedJournalDayBuckets(journal);
        EnsureJournalDayBucket(journal, DateTime.Now, setActive: true);
        if (activate)
        {
            SetActiveProjectId(journal.Id, shifted);
        }

        PersistProject(journal, workspace: true);
        DisposeInactiveTemporaryProjects(persistWorkspace: true);
        return journal;
    }

    // Held Ctrl+J toggles the lane between the Journal and the last-used deliberate
    // project: on a deliberate project it returns to the Journal; on the Journal it
    // reactivates the last deliberate project (when one is still Active).
    public (ZetlProjectToggleOutcome Outcome, string? ProjectName) ToggleActiveProject(bool shifted = false)
    {
        lock (stateGate)
        {
            if (GetActiveProject(shifted) is { JournalMode: false } active)
            {
                // The deactivated project stays recorded as the last deliberate project
                // (set when it was activated), so the next toggle brings it back.
                var journal = GetOrCreateJournalProject(shifted, activate: true);
                return (ZetlProjectToggleOutcome.ReturnedToJournal, journal.Name);
            }

            var lastId = shifted ? State.ShiftLastDeliberateProjectId : State.LastDeliberateProjectId;
            var last = lastId is null
                ? null
                : State.Projects.FirstOrDefault(project =>
                    project.Id == lastId && IsDeliberateProject(project));
            if (last is null)
            {
                return (ZetlProjectToggleOutcome.NoProjectToActivate, null);
            }

            SetActiveProjectId(last.Id, shifted);
            PersistWorkspace();
            return (ZetlProjectToggleOutcome.Activated, last.Name);
        }
    }

    // Ensure the day-parent bucket for <localNow> exists (named e.g. "Mon 07-06",
    // shifted by the configured day-start hour). Returns it; optionally highlights it
    // as the active bucket. No-op (returns null) for a non-journal project.
    public ZetlBucket? EnsureJournalDayBucket(ZetlProject project, DateTime localNow, bool setActive = false)
    {
        lock (stateGate)
        {
            if (!project.JournalMode)
            {
                return null;
            }

            return GetOrCreateChildBucket(project, null, JournalBucketName(localNow, Defaults.DayStartHour), setActive);
        }
    }

    // The copy-capture target for a journal: a bucket the user selected, else
    // today's day parent's "Capture" child, created on first use and made the
    // active bucket. No-op (null) for a non-journal project. Named RollJournalBucket
    // because it also advances the active day.
    public ZetlBucket? RollJournalBucket(ZetlProject project, DateTime localNow)
    {
        lock (stateGate)
        {
            if (!project.JournalMode)
            {
                return null;
            }

            if (UserSelectedJournalBucket(project) is { } selected)
            {
                return selected;
            }

            var day = EnsureJournalDayBucket(project, localNow)!;
            return GetOrCreateChildBucket(project, day.Id, JournalCaptureBucketName, setActive: true);
        }
    }

    // The journal's active bucket when the user chose it: anything other than the
    // day buckets Zetl manages (a dated day, or its Capture / Quick Note child),
    // which roll forward with the calendar instead.
    private static ZetlBucket? UserSelectedJournalBucket(ZetlProject project)
    {
        if (!project.JournalMode
            || project.Buckets.FirstOrDefault(bucket => bucket.Id == project.ActiveBucketId) is not { } active
            || IsDeletedBucket(active)
            || IsReservedBucketName(active.Name)
            || IsJournalDayBucket(active))
        {
            return null;
        }

        var isManagedDayChild = active.ParentBucketId is { } parentId
            && project.Buckets.Any(parent => parent.Id == parentId && IsJournalDayBucket(parent))
            && (string.Equals(active.Name, JournalCaptureBucketName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(active.Name, JournalQuickNoteBucketName, StringComparison.OrdinalIgnoreCase));
        return isManagedDayChild ? null : active;
    }

    // A top-level dated day bucket, named like JournalBucketName ("Mon 07-06").
    private static bool IsJournalDayBucket(ZetlBucket bucket) =>
        bucket.ParentBucketId is null
        && System.Text.RegularExpressions.Regex.IsMatch(bucket.Name, @"^[A-Z][a-z]{2} \d{2}-\d{2}$");

    // The quick-note target for a journal: today's day parent's "Quick Note" child,
    // created on first use. Does not steal the active bucket from the copy target.
    public ZetlBucket? ResolveJournalQuickNoteBucket(ZetlProject project, DateTime localNow)
    {
        lock (stateGate)
        {
            if (!project.JournalMode)
            {
                return null;
            }

            var day = EnsureJournalDayBucket(project, localNow)!;
            return GetOrCreateChildBucket(project, day.Id, JournalQuickNoteBucketName, setActive: false);
        }
    }

    // Seed the whole period's day-parent buckets up front — Mon–Sun for Weekly, the
    // full month for Monthly, just today for Daily — in chronological order so the
    // canonical bucket order reads as a calendar. Future and past days become empty
    // reminder slots; their Capture / Quick Note children stay lazy.
    private void SeedJournalDayBuckets(ZetlProject project)
    {
        if (!project.JournalMode)
        {
            return;
        }

        var dayStart = Math.Clamp(Defaults.DayStartHour, 0, 23);
        var shiftedToday = DateTime.Now.AddHours(-dayStart).Date;
        var interval = ZetlJournalInterval.Normalize(Defaults.JournalInterval);

        DateTime firstDay;
        int dayCount;
        if (interval == ZetlJournalInterval.Weekly)
        {
            // ISO week starts Monday: DayOfWeek has Sunday = 0, so shift to Monday = 0.
            var offsetFromMonday = ((int)shiftedToday.DayOfWeek + 6) % 7;
            firstDay = shiftedToday.AddDays(-offsetFromMonday);
            dayCount = 7;
        }
        else if (interval == ZetlJournalInterval.Monthly)
        {
            firstDay = new DateTime(shiftedToday.Year, shiftedToday.Month, 1);
            dayCount = DateTime.DaysInMonth(shiftedToday.Year, shiftedToday.Month);
        }
        else
        {
            firstDay = shiftedToday;
            dayCount = 1;
        }

        for (var i = 0; i < dayCount; i++)
        {
            // Re-add the day-start hour so JournalBucketName's own shift lands the name
            // back on this calendar day.
            var probe = firstDay.AddDays(i).AddHours(dayStart);
            GetOrCreateChildBucket(project, null, JournalBucketName(probe, dayStart), setActive: false);
        }
    }

    public void SetJournalMode(ZetlProject project, bool journalMode)
    {
        lock (stateGate)
        {
            project.JournalMode = journalMode;
            project.MetadataRevision++;
            PersistProject(project);
        }
    }

    // The bucket a copy capture (auto, held, or image) should land in for <project>.
    // Journals roll to today's Capture child; deliberate projects use their active
    // bucket, falling back to Scratch. Centralizes copy-target routing so the shortcut
    // coordinator does not branch on journal mode itself.
    public ZetlBucket ResolveCaptureBucket(ZetlProject project, bool shifted)
    {
        lock (stateGate)
        {
            if (project.JournalMode)
            {
                return RollJournalBucket(project, DateTime.Now)!;
            }

            return GetActiveBucket(shifted) ?? GetScratchBucket(project);
        }
    }

    public string ExpectedJournalProjectName(DateTime localNow, bool shifted)
    {
        var interval = ZetlJournalInterval.Normalize(Defaults.JournalInterval);
        var baseName = shifted ? "Journal Shift" : "Journal";
        var shiftedNow = localNow.AddHours(-Math.Clamp(Defaults.DayStartHour, 0, 23));

        if (interval == ZetlJournalInterval.Weekly)
        {
            var week = System.Globalization.ISOWeek.GetWeekOfYear(shiftedNow);
            var year = System.Globalization.ISOWeek.GetYear(shiftedNow);
            return $"{baseName} Week {week:D2} {year}";
        }
        else if (interval == ZetlJournalInterval.Monthly)
        {
            return $"{baseName} {shiftedNow:yyyy-MM}";
        }
        else
        {
            return $"{baseName} {shiftedNow:yyyy-MM-dd}";
        }
    }

    public string DefaultProjectName(bool shifted = false)
    {
        lock (stateGate)
        {
            return ExpectedJournalProjectName(DateTime.Now, shifted);
        }
    }

    public bool IsFormattedJournalName(string name, bool shifted)
    {
        var prefix = shifted ? "Journal Shift" : "Journal";
        if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var suffix = name.Substring(prefix.Length).Trim();
        if (suffix.StartsWith("Week ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && int.TryParse(parts[1], out _) && int.TryParse(parts[2], out _))
            {
                return true;
            }
        }
        else if (suffix.Length == 10 && suffix[4] == '-' && suffix[7] == '-')
        {
            var parts = suffix.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _) && int.TryParse(parts[2], out _))
            {
                return true;
            }
        }
        else if (suffix.Length == 7 && suffix[4] == '-')
        {
            var parts = suffix.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
            {
                return true;
            }
        }
        return false;
    }
}
