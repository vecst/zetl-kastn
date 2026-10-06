using System.Text.Json.Serialization;

namespace ZETL;

internal sealed class ZetlAppSettings
{
    public bool HasSeenFirstRun { get; set; }
    // The guided tour: empty until it has been closed once, then Completed (the
    // user finished its last hold) or Skipped (closed before that).
    public string TutorialState { get; set; } = "";
    public int ToastDisplayMs { get; set; } = 950;
    public bool AutoCaptureOnCopy { get; set; } = true;
    // Pass-through: a copy Zetl captured on its own, then pasted straight away,
    // was only passing through, so it doesn't stay in the project. A held Ctrl+P
    // flips this for a while; see ZetlShortcutCoordinator.
    public bool PassThrough { get; set; }
    public bool QuickNoteToClipboard { get; set; }
    public bool ReplayResumeClipboard { get; set; } = true;
    public string CaptureOriginDetail { get; set; } = ZetlCaptureOriginDetail.ApplicationAndWindowTitle;
    public List<string> DefaultProjectBuckets { get; set; } = ZetlBucketDefaults.Standard.ProjectBuckets.ToList();
    public string DefaultCompileMode { get; set; } = "Formatted";
    // Compose: whether Ctrl+Enter pastes the result straight into the app the
    // hold came from (otherwise it copies), and whether Formatted output starts
    // with the project and bucket headings. The dialog remembers the latter.
    public bool ComposeCtrlEnterPastes { get; set; } = true;
    public bool ComposeHeadings { get; set; } = true;
    public int DefaultTsvRowLength { get; set; } = 5;
    // Hour (0-23, local) at which a new journal day begins, so late-night captures
    // land in the right dated bucket. 0 = midnight.
    public int DayStartHour { get; set; }
    // Hours of no capture after which an active deliberate project hands capture back
    // to the Journal, so a forgotten project never traps notes. 0 = off.
    public int JournalAutoReturnHours { get; set; }
    // What a tapped Ctrl+C does when no project is active in its lane: Off leaves
    // copy and paste working like normal; Journal captures every copy all day.
    public string IdleCopyCapture { get; set; } = ZetlIdleCopyCapture.Off;
    // Hold Actions page: the rules whose action differs from Zetl's default.
    // Empty means every gesture does what Zetl does out of the box.
    public List<ZetlGestureRuleSetting> HoldActionRules { get; set; } = new();
    // Where hold popups (capture, quick note, compile, Board, templates) open,
    // and how opaque they are. Ordinary dialogs keep the top-center default.
    public string PopupPosition { get; set; } = ZetlScreenAnchor.TopCenter;
    public int PopupOpacityPercent { get; set; } = ZetlPopupOpacity.Maximum;
    // Hold indicator: a ring that fills while a shortcut is held. Detailed adds
    // the keys, a millisecond timer, and the action on every press, so a new
    // user can see where a tap ends and a hold begins. It sits in the top right,
    // away from the popups, so the action is seen happening without hiding them.
    public string HoldIndicatorStyle { get; set; } = ZetlHoldIndicatorStyle.Detailed;
    public string HoldIndicatorPosition { get; set; } = ZetlScreenAnchor.TopRight;

    // The two settings HoldIndicatorStyle replaced, read from older files only.
    [JsonPropertyName("showHoldProgress")]
    public bool? LegacyShowHoldProgress { set => legacyShowHoldProgress = value; }
    [JsonPropertyName("holdIndicatorDemo")]
    public bool? LegacyHoldIndicatorDemo { set => legacyHoldIndicatorDemo = value; }
    private bool? legacyShowHoldProgress;
    private bool? legacyHoldIndicatorDemo;
    // The template last started, highlighted when the template picker opens.
    public string? LastTemplateId { get; set; }
    public string ThemeId { get; set; } = ZetlThemeDefaults.BuiltInId;
    public string ThemeVariant { get; set; } = "System";
    public string JournalInterval { get; set; } = ZetlJournalInterval.Weekly;

    // Kastn workbench preferences. Edited from Zetl's Settings window (the single
    // settings surface) and consumed by the Kastn process, which shares this file.
    public bool KastnAutosave { get; set; } = true;
    public string KastnStartup { get; set; } = ZetlKastnStartup.Landing;
    public string KastnDefaultViewId { get; set; } = "";
    public string KastnMainLaneLabel { get; set; } = "";
    public string KastnAlternateLaneLabel { get; set; } = "";
    public string KastnTemporaryTemplateLaneDefault { get; set; } = "";
    public bool KastnMinimizeAfterTemplate { get; set; } = true;
    public bool KastnCloseToTray { get; set; } = true;
    public bool KastnPreferSlipKindOverBucketKind { get; set; }

    // Advanced Zetl and log settings.
    public int LogRetentionDays { get; set; } = 14;
    public int LogMaxNotesPerDay { get; set; } = 2000;
    public int LogFlushIntervalMs { get; set; } = 5000;
    public int MaxUndoActions { get; set; } = 100;
    public string UntitledSlipTitle { get; set; } = "Untitled";
    public int MaxSlipLabelLength { get; set; } = 24;
    public string PdfPageFormat { get; set; } = "Letter";
    public int PdfFontSize { get; set; } = 11;

    // Chordl Keyboard timings.
    public int HoldDelayMs { get; set; } = 353;
    public int RepeatSuppressionDelayMs { get; set; } = 33;

    // Advanced timings and timeouts.
    public int ClipboardPollIntervalMs { get; set; } = 20;
    public int ClipboardObservationTimeoutMs { get; set; } = 500;
    public int AutoCaptureClipboardTimeoutMs { get; set; } = 75;
    public int PopClipboardDelayMs { get; set; } = 75;
    public int ReplayClipboardRestoreDelayMs { get; set; } = 150;
    public int DownloadTimeoutSeconds { get; set; } = 10;

    // The user-facing name of a lane: the configured label, or the default when
    // blank. The stored lane values stay Normal and Shift.
    public string LaneLabel(bool shifted) => ZetlLaneLabels.Resolve(
        shifted ? KastnAlternateLaneLabel : KastnMainLaneLabel,
        shifted);

    // The Settings window saves the bucket list whether or not it was edited, so
    // an untouched install carries the old Inbox/Scratch default forever. Move
    // that exact list to the current default.
    public void MigrateLegacyDefaults()
    {
        if (DefaultProjectBuckets is { Count: 2 } buckets
            && string.Equals(buckets[0].Trim(), "Inbox", StringComparison.OrdinalIgnoreCase)
            && ZetlStateRules.IsScratchBucketName(buckets[1]))
        {
            DefaultProjectBuckets = ZetlBucketDefaults.Standard.ProjectBuckets.ToList();
        }

        // An install from before the style setting keeps the indicator it had:
        // the demo overlay became Detailed, a plain ring stays a plain ring.
        if (legacyShowHoldProgress is not null || legacyHoldIndicatorDemo is not null)
        {
            HoldIndicatorStyle = legacyHoldIndicatorDemo == true
                ? ZetlHoldIndicatorStyle.Detailed
                : legacyShowHoldProgress == false
                    ? ZetlHoldIndicatorStyle.Off
                    : ZetlHoldIndicatorStyle.Ring;
            legacyShowHoldProgress = null;
            legacyHoldIndicatorDemo = null;
        }

        HoldIndicatorStyle = ZetlHoldIndicatorStyle.Normalize(HoldIndicatorStyle);
    }
}

// A saved Hold Actions choice: the rule is named by kind ("Tap"/"Hold"), key
// letter, and focus ("Any"/"FileView"); Action is the action id it runs.
internal sealed class ZetlGestureRuleSetting
{
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public string Focus { get; set; } = "Any";
    public string Action { get; set; } = "";
}

internal static class ZetlLaneLabels
{
    // Short enough that card headers, buttons, and menus stay predictable.
    public const int MaxLength = 20;
    public const string DefaultMain = "Main";
    public const string DefaultShift = "Shift";

    public static string Trim(string? value)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength].TrimEnd();
    }

    public static string Resolve(string? value, bool shifted)
    {
        var trimmed = Trim(value);
        return trimmed.Length > 0
            ? trimmed
            : shifted ? DefaultShift : DefaultMain;
    }
}

internal static class ZetlKastnStartup
{
    public const string Landing = "Landing";
    public const string LastProject = "LastProject";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), LastProject, StringComparison.OrdinalIgnoreCase)
            ? LastProject
            : Landing;
}

internal static class ZetlKastnTemplateLaneDefault
{
    public const string Ask = "";

    public static string Normalize(string? value)
    {
        var lane = ZetlStateRules.CanonicalTemporaryLane(value);
        return lane ?? Ask;
    }
}

internal static class ZetlTutorialState
{
    public const string Completed = "Completed";
    public const string Skipped = "Skipped";

    // Startup offers the tour until it has been finished or skipped once. This
    // reads the tour's own state rather than HasSeenFirstRun, which RC 1's old
    // help window also set, so people upgrading from it still get the tour.
    public static bool ShouldOffer(ZetlAppSettings settings) =>
        !string.Equals(settings.TutorialState, Completed, StringComparison.Ordinal)
        && !string.Equals(settings.TutorialState, Skipped, StringComparison.Ordinal);
}

internal static class ZetlIdleCopyCapture
{
    public const string Off = "Off";
    public const string Journal = "Journal";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Journal, StringComparison.OrdinalIgnoreCase) ? Journal : Off;

    public static bool CapturesToJournal(string? value) => Normalize(value) == Journal;
}

internal static class ZetlJournalInterval
{
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    public static string Normalize(string? value) =>
        string.Equals(value?.Trim(), Weekly, StringComparison.OrdinalIgnoreCase) ? Weekly
        : string.Equals(value?.Trim(), Monthly, StringComparison.OrdinalIgnoreCase) ? Monthly
        : Daily;
}

internal sealed class ZetlAppSettingsStore
{
    public static string? DefaultSettingsPathOverride { get; set; }

    private readonly string settingsPath;
    private readonly Action<string>? log;

    public ZetlAppSettingsStore(string? settingsPath = null, Action<string>? log = null)
    {
        this.settingsPath = Path.GetFullPath(settingsPath ?? DefaultSettingsPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "settings.json"));
        this.log = log;
        Settings = Load();
    }

    public ZetlAppSettings Settings { get; private set; }

    public string SettingsPath => settingsPath;

    public void MarkFirstRunSeen()
    {
        Settings.HasSeenFirstRun = true;
        Save();
    }

    // Bumped on every in-process save so read-side caches can invalidate
    // immediately instead of waiting out their staleness window.
    public static int SaveStamp => Volatile.Read(ref saveStamp);
    private static int saveStamp;

    public void Save()
    {
        JsonFile.WriteAtomic(settingsPath, Settings);
        Interlocked.Increment(ref saveStamp);
    }

    // A selective update must merge into the latest file. Unlike startup reads,
    // an unreadable or corrupt file must fail instead of overwriting it with
    // defaults. Only publish the new in-memory snapshot after persistence succeeds.
    public void Update(Action<ZetlAppSettings> update)
    {
        var latest = JsonFile.Read<ZetlAppSettings>(settingsPath) ?? new ZetlAppSettings();
        latest.MigrateLegacyDefaults();
        update(latest);
        JsonFile.WriteAtomic(settingsPath, latest);
        Settings = latest;
        Interlocked.Increment(ref saveStamp);
    }

    private ZetlAppSettings Load()
    {
        var settings = JsonFile.ReadOrQuarantine<ZetlAppSettings>(settingsPath, log) ?? new ZetlAppSettings();
        settings.MigrateLegacyDefaults();
        return settings;
    }
}
