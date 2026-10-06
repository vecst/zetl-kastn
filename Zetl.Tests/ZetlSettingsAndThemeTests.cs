using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlSettingsAndThemeTests
{
    [Fact(DisplayName = "Zetl default hotkeys config parses")]
    public static void DefaultConfigParses()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "hotkeys.json");
        var config = ChordlConfigLoader.LoadFromFile(configPath);
        AssertTrue(config.Actions.Count > 0, "Default config should define at least one hotkey.");
        AssertTrue(config.HoldDelay > TimeSpan.Zero, "Default config should define a positive hold delay.");

        // Shift-lane TapOnly chords must replay the chord the user physically
        // pressed. Dropping the Shift turns e.g. paste-special/paste-plain
        // (Ctrl+Shift+V) into a plain Ctrl+V in the foreground app. Immediate
        // chords (Ctrl+Shift+C/X) intentionally drop Shift so the app still
        // copies or cuts for the Shift-lane capture.
        foreach (var (chord, action) in config.Actions)
        {
            if (chord.Shift && action.Dispatch == ChordlDispatchMode.TapOnly)
            {
                AssertTrue(
                    action.ReplayShift,
                    $"{action.Name} should replay Shift so the foreground app sees the chord the user pressed.");
            }
        }
    }

    [Fact(DisplayName = "Zetl config tolerates null replay modifiers")]
    public static void ConfigNullReplayModifiersDoesNotThrow()
    {
        var json =
            """
            { "repeatSuppressionDelayMs": 33, "holdDelayMs": 353, "hotkeys": [ { "name": "Copy", "key": "C", "modifiers": ["Ctrl"], "dispatch": "None", "replayModifiers": null } ] }
            """;

        var config = ChordlConfigLoader.LoadFromJson(json);

        AssertEqual(1, config.Actions.Count, "A null replayModifiers should normalize to the default rather than crash.");
        AssertFalse(config.Actions.Values.Single().ReplayShift, "Normalized replay modifiers should not request Shift.");
    }

    [Fact(DisplayName = "Zetl config reports clean errors for null fields")]
    public static void ConfigNullFieldsReportCleanErrors()
    {
        var nullModifiers =
            """
            { "repeatSuppressionDelayMs": 33, "holdDelayMs": 353, "hotkeys": [ { "name": "Copy", "key": "C", "modifiers": null, "dispatch": "None" } ] }
            """;
        AssertConfigRejected(nullModifiers, "Ctrl", "Null modifiers should produce a clean validation error, not a crash.");

        var nullEntry =
            """
            { "repeatSuppressionDelayMs": 33, "holdDelayMs": 353, "hotkeys": [ null ] }
            """;
        AssertConfigRejected(nullEntry, "must be an object", "A null hotkey entry should produce a clean validation error, not a crash.");

        var nullModifierEntry =
            """
            { "repeatSuppressionDelayMs": 33, "holdDelayMs": 353, "hotkeys": [ { "name": "Copy", "key": "C", "modifiers": ["Ctrl", null], "dispatch": "None" } ] }
            """;
        AssertConfigRejected(nullModifierEntry, "modifiers[1]", "A null entry inside modifiers should be reported by index, not silently dropped.");

        var nullReplayModifierEntry =
            """
            { "repeatSuppressionDelayMs": 33, "holdDelayMs": 353, "hotkeys": [ { "name": "Copy", "key": "C", "modifiers": ["Ctrl"], "dispatch": "None", "replayModifiers": ["Ctrl", null] } ] }
            """;
        AssertConfigRejected(nullReplayModifierEntry, "replayModifiers[1]", "A null entry inside replayModifiers should be reported by index.");

        var nullDispatch =
            """
            { "repeatSuppressionDelayMs": 33, "holdDelayMs": 353, "hotkeys": [ { "name": "Copy", "key": "C", "modifiers": ["Ctrl"], "dispatch": null } ] }
            """;
        AssertConfigRejected(nullDispatch, "dispatch", "An explicit null dispatch should be reported as malformed, not silently treated as None.");
    }

    [Fact(DisplayName = "Zetl settings give new installs the detailed indicator in the top right")]
    public static void SettingsDefaultToDetailedIndicatorTopRight()
    {
        using var temp = new TempStateFile();
        var store = new ZetlAppSettingsStore(temp.Path + ".settings.json");

        AssertEqual(ZetlHoldIndicatorStyle.Detailed, store.Settings.HoldIndicatorStyle, "New installs see the detailed overlay.");
        AssertEqual(ZetlScreenAnchor.TopRight, store.Settings.HoldIndicatorPosition, "It sits in the top right, away from popups.");
    }

    [Fact(DisplayName = "Zetl settings carry an older indicator choice forward")]
    public static void SettingsCarryOlderIndicatorChoiceForward()
    {
        using var temp = new TempStateFile();
        string Migrated(string json)
        {
            var path = temp.Path + ".settings.json";
            File.WriteAllText(path, json);
            return new ZetlAppSettingsStore(path).Settings.HoldIndicatorStyle;
        }

        AssertEqual(ZetlHoldIndicatorStyle.Detailed, Migrated("""{ "showHoldProgress": true, "holdIndicatorDemo": true }"""), "The demo overlay becomes Detailed.");
        AssertEqual(ZetlHoldIndicatorStyle.Ring, Migrated("""{ "showHoldProgress": true, "holdIndicatorDemo": false }"""), "A plain ring stays a ring.");
        AssertEqual(ZetlHoldIndicatorStyle.Off, Migrated("""{ "showHoldProgress": false }"""), "A hidden indicator stays off.");
        AssertEqual(ZetlHoldIndicatorStyle.Ring, Migrated("""{ "holdIndicatorStyle": "Ring" }"""), "A saved style is kept.");

        var path = temp.Path + ".settings.json";
        File.WriteAllText(path, """{ "showHoldProgress": false, "holdIndicatorPosition": "Popups" }""");
        var store = new ZetlAppSettingsStore(path);
        store.Save();
        var saved = File.ReadAllText(path);
        AssertFalse(saved.Contains("showHoldProgress", StringComparison.Ordinal), "The old setting is dropped on save.");
        AssertEqual(ZetlHoldIndicatorStyle.Off, new ZetlAppSettingsStore(path).Settings.HoldIndicatorStyle, "The migrated style survives a save.");
        AssertEqual(ZetlHoldIndicatorPosition.FollowPopups, store.Settings.HoldIndicatorPosition, "An existing position choice is kept.");
    }

    [Fact(DisplayName = "Zetl app settings round-trip first-run flag")]
    public static void AppSettingsRoundTripFirstRunFlag()
    {
        using var temp = new TempStateFile();
        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "settings.json");
        var store = new ZetlAppSettingsStore(settingsPath);

        AssertFalse(store.Settings.HasSeenFirstRun, "First-run flag should default to false.");
        store.MarkFirstRunSeen();

        var loaded = new ZetlAppSettingsStore(settingsPath);
        AssertTrue(loaded.Settings.HasSeenFirstRun, "First-run flag should round-trip.");
    }

    [Fact(DisplayName = "Zetl offers the tour until it is finished or skipped, including after RC 1's help")]
    public static void TourIsOfferedUntilFinishedOrSkipped()
    {
        using var temp = new TempStateFile();
        var directory = System.IO.Path.GetDirectoryName(temp.Path)!;
        ZetlAppSettings Profile(string name, string json)
        {
            var path = System.IO.Path.Combine(directory, name);
            System.IO.File.WriteAllText(path, json);
            return new ZetlAppSettingsStore(path).Settings;
        }

        AssertTrue(
            ZetlTutorialState.ShouldOffer(new ZetlAppSettings()),
            "A fresh profile is offered the tour.");
        AssertTrue(
            ZetlTutorialState.ShouldOffer(Profile("rc1.json", """{ "hasSeenFirstRun": true }""")),
            "An RC 1 profile saw only the old help window, so it still gets the tour.");
        AssertFalse(
            ZetlTutorialState.ShouldOffer(Profile("done.json", """{ "hasSeenFirstRun": true, "tutorialState": "Completed" }""")),
            "Someone who finished the tour isn't shown it again.");
        AssertFalse(
            ZetlTutorialState.ShouldOffer(Profile("skipped.json", """{ "hasSeenFirstRun": true, "tutorialState": "Skipped" }""")),
            "Someone who skipped the tour isn't shown it again.");
    }

    [Fact(DisplayName = "Zetl app settings default to Capture and Quick Note buckets")]
    public static void AppSettingsDefaultToCaptureAndQuickNoteBuckets()
    {
        using var temp = new TempStateFile();
        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "settings.json");
        var store = new ZetlAppSettingsStore(settingsPath);
        AssertEqual(
            "Capture|Quick Note",
            string.Join("|", store.Settings.DefaultProjectBuckets),
            "New projects default to the same pair as a journal day.");

        store.Settings.DefaultProjectBuckets = ["Inbox", "Scratch"];
        store.Save();
        AssertEqual(
            "Capture|Quick Note",
            string.Join("|", new ZetlAppSettingsStore(settingsPath).Settings.DefaultProjectBuckets),
            "The old untouched Inbox/Scratch default moves to the new default on load.");

        store.Settings.DefaultProjectBuckets = ["Inbox", "Ideas", "Scratch"];
        store.Save();
        AssertEqual(
            "Inbox|Ideas|Scratch",
            string.Join("|", new ZetlAppSettingsStore(settingsPath).Settings.DefaultProjectBuckets),
            "A customized bucket list is left alone.");
    }

    [Fact(DisplayName = "Zetl app settings round-trip configurable fields")]
    public static void AppSettingsRoundTripFields()
    {
        using var temp = new TempStateFile();
        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "settings.json");
        var store = new ZetlAppSettingsStore(settingsPath);

        AssertEqual(950, store.Settings.ToastDisplayMs, "Toast display should default to 950.");
        AssertTrue(store.Settings.AutoCaptureOnCopy, "Auto-capture should default to on.");
        AssertFalse(store.Settings.QuickNoteToClipboard, "Quick note to clipboard should default to off.");
        AssertEqual(
            ZetlCaptureOriginDetail.ApplicationAndWindowTitle,
            store.Settings.CaptureOriginDetail,
            "Capture origin should default to application and window title.");

        store.Settings.ToastDisplayMs = 1500;
        store.Settings.AutoCaptureOnCopy = false;
        store.Settings.QuickNoteToClipboard = true;
        store.Settings.CaptureOriginDetail = ZetlCaptureOriginDetail.ApplicationOnly;
        store.Settings.DefaultProjectBuckets = new List<string> { "Notes", "Scratch" };
        store.Settings.DefaultCompileMode = "TSV";
        store.Settings.DefaultTsvRowLength = 4;
        store.Settings.DayStartHour = 4;
        store.Settings.JournalAutoReturnHours = 6;
        store.Settings.ThemeId = "custom-theme";
        store.Settings.ThemeVariant = "Dark";
        AssertTrue(store.Settings.KastnAutosave, "Kastn autosave should default on.");
        AssertEqual(
            ZetlKastnStartup.Landing,
            store.Settings.KastnStartup,
            "Kastn startup should default to the landing page.");
        AssertTrue(
            store.Settings.KastnMinimizeAfterTemplate,
            "Kastn should default to stepping aside after a template create.");
        AssertTrue(
            store.Settings.KastnCloseToTray,
            "Kastn should default to staying available in the tray on window close.");
        AssertEqual(
            ZetlKastnTemplateLaneDefault.Ask,
            store.Settings.KastnTemporaryTemplateLaneDefault,
            "Temporary template lane should default to asking.");
        AssertEqual("", store.Settings.KastnMainLaneLabel, "Main lane label should default to the built-in name.");
        AssertEqual("", store.Settings.KastnAlternateLaneLabel, "Alternate lane label should default to the built-in name.");
        AssertFalse(
            store.Settings.KastnPreferSlipKindOverBucketKind,
            "Kastn should default to composing bucket and slip kinds.");
        store.Settings.KastnAutosave = false;
        store.Settings.KastnStartup = ZetlKastnStartup.LastProject;
        store.Settings.KastnDefaultViewId = "markdown";
        store.Settings.KastnMainLaneLabel = "Capture";
        store.Settings.KastnAlternateLaneLabel = "Queue";
        store.Settings.KastnMinimizeAfterTemplate = false;
        store.Settings.KastnCloseToTray = false;
        store.Settings.KastnTemporaryTemplateLaneDefault = ZetlStateRules.ShiftLane;
        store.Settings.KastnPreferSlipKindOverBucketKind = true;
        store.Save();

        var loaded = new ZetlAppSettingsStore(settingsPath);
        AssertEqual(1500, loaded.Settings.ToastDisplayMs, "Toast display should round-trip.");
        AssertFalse(loaded.Settings.AutoCaptureOnCopy, "Auto-capture flag should round-trip.");
        AssertTrue(loaded.Settings.QuickNoteToClipboard, "Quick note to clipboard flag should round-trip.");
        AssertEqual(
            ZetlCaptureOriginDetail.ApplicationOnly,
            loaded.Settings.CaptureOriginDetail,
            "Capture-origin privacy detail should round-trip.");
        AssertEqual("Notes", loaded.Settings.DefaultProjectBuckets[0], "Default buckets should round-trip.");
        AssertEqual("TSV", loaded.Settings.DefaultCompileMode, "Default compile mode should round-trip.");
        AssertEqual(4, loaded.Settings.DefaultTsvRowLength, "Default TSV row length should round-trip.");
        AssertEqual(4, loaded.Settings.DayStartHour, "Journal day-start hour should round-trip.");
        AssertEqual(6, loaded.Settings.JournalAutoReturnHours, "Journal auto-return hours should round-trip.");
        AssertEqual("custom-theme", loaded.Settings.ThemeId, "Theme id should round-trip.");
        AssertEqual("Dark", loaded.Settings.ThemeVariant, "Theme variant should round-trip.");
        AssertFalse(loaded.Settings.KastnAutosave, "Kastn autosave flag should round-trip.");
        AssertEqual(
            ZetlKastnStartup.LastProject,
            loaded.Settings.KastnStartup,
            "Kastn startup choice should round-trip.");
        AssertEqual("markdown", loaded.Settings.KastnDefaultViewId, "Kastn default view should round-trip.");
        AssertEqual("Capture", loaded.Settings.KastnMainLaneLabel, "Main lane label should round-trip.");
        AssertEqual("Queue", loaded.Settings.KastnAlternateLaneLabel, "Alternate lane label should round-trip.");
        AssertFalse(
            loaded.Settings.KastnMinimizeAfterTemplate,
            "Kastn minimize-after-template flag should round-trip.");
        AssertFalse(
            loaded.Settings.KastnCloseToTray,
            "Kastn close-to-tray flag should round-trip.");
        AssertEqual(
            ZetlStateRules.ShiftLane,
            loaded.Settings.KastnTemporaryTemplateLaneDefault,
            "Kastn temporary template lane default should round-trip.");
        AssertTrue(
            loaded.Settings.KastnPreferSlipKindOverBucketKind,
            "Kastn slip-kind preference should round-trip.");
    }

    [Fact(DisplayName = "Kastn state round-trips the last project")]
    public static void KastnStateRoundTripsLastProject()
    {
        using var temp = new TempStateFile();
        var directory = System.IO.Path.GetDirectoryName(temp.Path)!;
        var statePath = System.IO.Path.Combine(directory, "kastn-state.json");

        var store = new KASTN.KastnStateStore(statePath);
        AssertEqual("", store.LastProjectId, "Last project id should default to empty.");
        store.LastProjectId = "proj-42";

        var loaded = new KASTN.KastnStateStore(statePath);
        AssertEqual("proj-42", loaded.LastProjectId, "Last project id should round-trip.");

        // State files written while project pinning existed must still load.
        System.IO.File.WriteAllText(
            statePath,
            "{ \"lastProjectId\": \"proj-7\", \"pinnedProjectIds\": [\"proj-7\"] }");
        var legacy = new KASTN.KastnStateStore(statePath);
        AssertEqual("proj-7", legacy.LastProjectId, "A legacy state file with pins should still load.");
    }

    [Fact(DisplayName = "Zetl app settings recover from a corrupt file")]
    public static void AppSettingsRecoverFromCorruptFile()
    {
        using var temp = new TempStateFile();
        var directory = System.IO.Path.GetDirectoryName(temp.Path)!;
        var settingsPath = System.IO.Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, "{ not settings");

        var store = new ZetlAppSettingsStore(settingsPath);

        AssertTrue(store.Settings.AutoCaptureOnCopy, "Corrupt settings should fall back to defaults, not abort.");
        AssertFalse(File.Exists(settingsPath), "The corrupt settings.json should be moved aside.");
        AssertEqual(
            1,
            Directory.GetFiles(directory, "settings.json.corrupt-*").Length,
            "The corrupt settings.json should be quarantined.");
    }

    [Fact(DisplayName = "Zetl built-in theme validates")]
    public static void ThemeDefaultsValidate()
    {
        var theme = ZetlThemeDefaults.Create();

        AssertEqual(0, ZetlThemeValidator.Validate(theme).Count, "Built-in theme should validate.");
        AssertEqual(ZetlThemeDocument.CurrentVersion, theme.Version, "Built-in theme should use the current schema.");

        theme.Dark.Accent = "purple";
        AssertTrue(
            ZetlThemeValidator.Validate(theme).Any(error => error.Contains("accent", StringComparison.OrdinalIgnoreCase)),
            "Invalid colors should produce a useful validation error.");
    }

    [Fact(DisplayName = "Zetl Dusk built-in theme validates")]
    public static void ThemeDuskValidates()
    {
        var theme = ZetlThemeDefaults.CreateDusk();

        AssertEqual(0, ZetlThemeValidator.Validate(theme).Count, "Dusk theme should validate.");
        AssertEqual(ZetlThemeDefaults.DuskId, theme.Id, "Dusk should have a stable built-in id.");
        AssertTrue(ZetlThemeDefaults.IsBuiltIn(theme.Id), "Dusk should be protected as built-in.");
        AssertEqual(
            "#8290FF",
            theme.Dark.Accent,
            "Dusk should retain the First Build periwinkle accent.");
    }

    [Fact(DisplayName = "Zetl built-in presets all validate")]
    public static void ThemeBuiltInPresetsValidate()
    {
        var presets = ZetlThemeDefaults.CreateAll();

        AssertTrue(presets.Count >= 2, "There should be at least the two original presets.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in presets)
        {
            AssertEqual(
                0,
                ZetlThemeValidator.Validate(theme).Count,
                $"Built-in preset '{theme.Name}' ({theme.Id}) should validate.");
            AssertTrue(
                ZetlThemeDefaults.IsBuiltIn(theme.Id),
                $"Preset '{theme.Id}' should be reported as built-in.");
            AssertTrue(
                seenIds.Add(theme.Id),
                $"Built-in preset id '{theme.Id}' should be unique.");
        }
    }

    [Fact(DisplayName = "Zetl themes round-trip custom values")]
    public static void ThemeRoundTripsCustomValues()
    {
        using var temp = new TempStateFile();
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(temp.Path)!,
            "themes");
        var store = new ZetlThemeStore(directory);
        var theme = ZetlThemeDefaults.Create();
        theme.Id = ZetlThemeDefaults.CreateId("Midnight Notes");
        theme.Name = "Midnight Notes";
        theme.Dark.Accent = "#12ABEF";
        theme.Light.Surface = "#FAFAFA";
        theme.Typography.FontFamily = "Segoe UI";
        theme.Metrics.CornerRadius = 9;

        store.Save(theme);

        var loaded = store.Resolve(theme.Id);
        AssertEqual("Midnight Notes", loaded.Name, "Custom theme name should round-trip.");
        AssertEqual("#12ABEF", loaded.Dark.Accent, "Dark palette should round-trip.");
        AssertEqual("#FAFAFA", loaded.Light.Surface, "Light palette should round-trip.");
        AssertEqual("Segoe UI", loaded.Typography.FontFamily, "Typography should round-trip.");
        AssertEqual(9d, loaded.Metrics.CornerRadius, "Theme metrics should round-trip.");
    }

    [Fact(DisplayName = "Zetl themes preserve unknown JSON fields")]
    public static void ThemePreservesUnknownJsonFields()
    {
        using var temp = new TempStateFile();
        var root = System.IO.Path.GetDirectoryName(temp.Path)!;
        var source = System.IO.Path.Combine(root, "future-theme.json");
        var directory = System.IO.Path.Combine(root, "themes");
        File.WriteAllText(
            source,
            """
            {
              "version": 2,
              "id": "future-theme",
              "name": "Future Theme",
              "futureRoot": { "enabled": true },
              "light": {
                "windowBackground": "#FFFFFF",
                "surface": "#F8F8F8",
                "surfaceAlt": "#EEEEEE",
                "text": "#111111",
                "mutedText": "#666666",
                "accent": "#3366FF",
                "accentText": "#FFFFFF",
                "border": "#BBBBBB",
                "error": "#AA0000",
                "futurePaletteMode": "soft"
              },
              "dark": {
                "windowBackground": "#111111",
                "surface": "#181818",
                "surfaceAlt": "#242424",
                "text": "#FFFFFF",
                "mutedText": "#AAAAAA",
                "accent": "#7799FF",
                "accentText": "#FFFFFF",
                "border": "#444444",
                "error": "#FF7777"
              },
              "typography": {
                "fontFamily": "Inter",
                "monoFontFamily": "Consolas",
                "bodyFontSize": 14,
                "headingFontSize": 16,
                "titleFontSize": 20
              },
              "metrics": {
                "windowPadding": 14,
                "controlSpacing": 8,
                "cornerRadius": 4
              }
            }
            """);
        var store = new ZetlThemeStore(directory);

        var imported = store.Import(source);
        store.Save(imported);

        var saved = File.ReadAllText(Directory.GetFiles(directory, "*.json").Single());
        AssertTrue(saved.Contains("\"futureRoot\"", StringComparison.Ordinal), "Unknown root values should survive.");
        AssertTrue(saved.Contains("\"futurePaletteMode\"", StringComparison.Ordinal), "Unknown palette values should survive.");
    }

    [Fact(DisplayName = "Zetl theme store ignores invalid files")]
    public static void ThemeStoreIgnoresInvalidFiles()
    {
        using var temp = new TempStateFile();
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(temp.Path)!,
            "themes");
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, "broken.json"), "{ no");
        var store = new ZetlThemeStore(directory);

        var themes = store.LoadAll();

        AssertEqual(
            ZetlThemeDefaults.CreateAll().Count,
            themes.Count,
            "Invalid theme files should leave only the built-in presets.");
        AssertTrue(
            themes.Any(theme => theme.Id == ZetlThemeDefaults.DuskId),
            "Dusk should remain available when a custom theme file is invalid.");
        AssertEqual(ZetlThemeDefaults.BuiltInId, store.Resolve("missing").Id, "Missing themes should resolve to the built-in fallback.");
    }
}
