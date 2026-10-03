namespace ZETL;

// The names, kinds, and statuses Zetl's state is built from, and the rules for
// telling them apart: which bucket is Scratch or Replay, which project is a
// consumable or the Journal, how a stored string is read back. All of it is
// pure, so anything can ask without going through the store.
internal static class ZetlStateRules
{
    // Name of the dedicated activity-log project. It is never made active.
    public const string LogProjectName = "Zetl Logs";
    public const string DeletedBucketName = "Deleted";
    public const string DeletedBucketKind = "Deleted";

    // Scratch is identified by its name, which is sound only while no other
    // bucket can take that name: see IsReservedBucketName.
    public const string ScratchBucketName = "Scratch";

    // A journal day is a parent bucket (named e.g. "Mon 07-06") holding two lazily
    // created children: Capture receives copy captures, Quick Note receives held-cut
    // quick notes. They are ordinary buckets with special routing, not protected like
    // the singular Scratch/Deleted (a journal has one pair per day).
    public const string JournalCaptureBucketName = "Capture";
    public const string JournalQuickNoteBucketName = "Quick Note";

    // Project lifecycle statuses. Stored as readable words, matching the bucket
    // Kind / compile-mode string convention.
    public const string ActiveStatus = "Active";
    public const string FinishedStatus = "Finished";
    public const string ArchivedStatus = "Archived";

    public const string StandardProjectKind = "Standard";
    public const string TemporaryConsumableProjectKind = "TemporaryConsumable";

    // The two lanes, as stored.
    public const string NormalLane = "Normal";
    public const string ShiftLane = "Shift";

    // How a tapped Ctrl+C that Zetl captured on its own is recorded, so pass-
    // through can tell it from a held capture or a quick note.
    public const string AutoCopySource = "auto-copy";

    // Where pass-through sets aside the copies it took out, one per project.
    public const string PassedThroughBucketName = "Passed Through";

    public static bool IsAutoCopy(ZetlSlip slip) =>
        string.Equals(slip.Source, AutoCopySource, StringComparison.Ordinal);

    // A structural note (divider, and later group/table/latex) is a Kastn-only rendering
    // element with no authored content, so Zetl's capture, compile, Replay, and pass-through flows
    // pass over it.
    public static bool IsStructuralSlip(ZetlSlip note) => ZetlBlockKinds.IsStructural(note.BlockKind);

    public static bool IsConsumableProject(ZetlProject project)
    {
        return project.Consumable || IsTemporaryConsumableProject(project);
    }

    // A project Ctrl+J can return to: not the Journal, not a consumable queue,
    // and not finished or archived.
    internal static bool IsDeliberateProject(ZetlProject project) =>
        !project.JournalMode && !IsConsumableProject(project) && IsActiveStatus(project);

    public static bool IsReplayBucket(ZetlBucket bucket)
    {
        return IsReplayKindValue(bucket.Settings.Kind);
    }

    // The Scratch bucket is special (always present, the quick-note default)
    // and currently cannot be renamed or deleted.
    public static bool IsScratchBucket(ZetlBucket bucket)
    {
        return IsScratchBucketName(bucket.Name);
    }

    public static bool IsDeletedBucket(ZetlBucket bucket)
    {
        return string.Equals(bucket.Settings.Kind, DeletedBucketKind, StringComparison.OrdinalIgnoreCase);
    }

    // Names Zetl owns. No ordinary bucket may take one: Scratch is recognized by
    // name, and the normalizer reshapes any bucket named Deleted into the
    // Deleted bucket, which would silently hide its slips.
    public static bool IsReservedBucketName(string? name)
    {
        return IsScratchBucketName(name) || IsDeletedBucketName(name);
    }

    public static bool IsScratchBucketName(string? name)
    {
        return string.Equals(name?.Trim(), ScratchBucketName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDeletedBucketName(string? name)
    {
        return string.Equals(name?.Trim(), DeletedBucketName, StringComparison.OrdinalIgnoreCase);
    }

    // Whether a raw kind string represents Replay Mode (accepts the legacy
    // "Fifo" value as well).
    public static bool IsReplayKind(string? kind)
    {
        return IsReplayKindValue(kind);
    }

    internal static bool IsReplayKindValue(string? kind)
    {
        // "Replay" is the stored value; "Fifo" is the legacy value from older
        // state files, mapped forward to "Replay" by NormalizeBucketKind.
        return string.Equals(kind, "Replay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "Fifo", StringComparison.OrdinalIgnoreCase);
    }

    // The canonical lifecycle status for a raw string, or null when it is not a
    // recognized status. Callers that must reject bad input (the IPC service) use
    // the null result; load-time normalization falls back to Active.
    public static string? CanonicalProjectStatus(string? status)
    {
        if (string.Equals(status, FinishedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return FinishedStatus;
        }

        if (string.Equals(status, ArchivedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ArchivedStatus;
        }

        if (string.Equals(status, ActiveStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ActiveStatus;
        }

        return null;
    }

    internal static string NormalizeProjectStatus(string? status)
    {
        return CanonicalProjectStatus(status) ?? ActiveStatus;
    }

    public static bool IsActiveStatus(ZetlProject project)
    {
        return string.Equals(project.Status, ActiveStatus, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTemporaryConsumableProject(ZetlProject project)
    {
        return string.Equals(project.Kind, TemporaryConsumableProjectKind, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeProjectKind(string? kind)
    {
        return string.Equals(kind, TemporaryConsumableProjectKind, StringComparison.OrdinalIgnoreCase)
            ? TemporaryConsumableProjectKind
            : StandardProjectKind;
    }

    public static string? CanonicalTemporaryLane(string? lane)
    {
        if (string.Equals(lane, ShiftLane, StringComparison.OrdinalIgnoreCase))
        {
            return ShiftLane;
        }

        if (string.Equals(lane, NormalLane, StringComparison.OrdinalIgnoreCase))
        {
            return NormalLane;
        }

        return null;
    }

    internal static string NormalizeBucketKind(string? kind)
    {
        if (string.Equals(kind, DeletedBucketKind, StringComparison.OrdinalIgnoreCase))
        {
            return DeletedBucketKind;
        }

        return IsReplayKindValue(kind) ? "Replay" : "Standard";
    }

    internal static string NormalizeCompileMode(string? mode)
    {
        if (string.Equals(mode, "Plain", StringComparison.OrdinalIgnoreCase))
        {
            return "Plain";
        }

        if (string.Equals(mode, "TSV", StringComparison.OrdinalIgnoreCase))
        {
            return "TSV";
        }

        return "Formatted";
    }

    public static string PreviewText(string text)
    {
        var preview = text.ReplaceLineEndings(" ").Trim();
        return preview.Length <= 80 ? preview : $"{preview[..77]}...";
    }

    // The journal "day" a capture belongs to: clock time shifted back by the
    // configured day-start hour, so e.g. with dayStartHour=4 a 1am capture lands in
    // the previous calendar day's bucket. Returns the day-parent bucket name
    // (e.g. "Mon 07-06") — weekday label for at-a-glance reading plus month-day for
    // archival clarity. Invariant culture keeps the weekday stable across locales.
    public static string JournalBucketName(DateTime localNow, int dayStartHour) =>
        localNow.AddHours(-Math.Clamp(dayStartHour, 0, 23))
            .ToString("ddd MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
