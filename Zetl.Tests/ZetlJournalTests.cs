using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlJournalTests
{
    [Fact(DisplayName = "Zetl state creates the journal default home")]
    public static void StateCreatesJournalDefaultProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.GetCaptureHome();
        AssertEqual(store.DefaultProjectName(), project.Name, "The default capture home is the Journal.");
        AssertTrue(project.JournalMode, "The default home is journal-mode.");
        AssertEqual(
            ZetlStateRules.JournalBucketName(DateTime.Now, 0),
            project.Buckets.Single(bucket => bucket.Id == project.ActiveBucketId).Name,
            "The journal highlights today's day bucket.");
        AssertTrue(store.GetActiveProject() is null, "Looking up the capture home activates nothing.");

        // A fresh weekly journal seeds the whole Mon–Sun week up front as empty
        // day-parent slots (children stay lazy), so future days are ready to hold
        // reminder notes.
        var monday = DateTime.Now.AddDays(-(((int)DateTime.Now.DayOfWeek + 6) % 7)).Date;
        for (var i = 0; i < 7; i++)
        {
            var dayName = ZetlStateRules.JournalBucketName(monday.AddDays(i), 0);
            var dayBucket = project.Buckets.SingleOrDefault(bucket => bucket.ParentBucketId is null
                && string.Equals(bucket.Name, dayName, StringComparison.OrdinalIgnoreCase));
            AssertTrue(dayBucket is not null, $"The weekly journal seeds a day bucket for {dayName}.");
            AssertEqual(0, dayBucket!.Slips.Count, $"Seeded day bucket {dayName} starts empty.");
        }
        AssertTrue(
            project.Buckets.All(bucket => !string.Equals(bucket.Name, ZetlStateRules.JournalCaptureBucketName, StringComparison.OrdinalIgnoreCase)),
            "Capture children are not created until a capture happens.");
        AssertTrue(
            project.Buckets.All(bucket => !string.Equals(bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase)),
            "A journal has no Scratch bucket.");

        // Renaming the journal keeps it the default (it is tracked by id), and
        // compile reflects the new name.
        store.UpdateProjectName(project, "Renamed");
        AssertEqual("Renamed", ZetlComposeOutput.PlainText(project, []).Split(Environment.NewLine)[0], "Compile should use the updated project name.");
        store.ClearActiveProject();
        AssertEqual(project.Id, store.GetCaptureHome().Id, "The renamed journal is still the default home.");
    }

    [Fact(DisplayName = "Zetl state configures and rolls journal intervals")]
    public static void StateJournalIntervalConfiguresAndRolls()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);

        // Test name format mapping directly
        var testDate = new DateTime(2026, 6, 26, 12, 0, 0); // Friday, Week 26 of 2026

        // 1. Daily
        store.Defaults = store.Defaults with { JournalInterval = "Daily", DayStartHour = 0 };
        AssertEqual("Journal 2026-06-26", store.ExpectedJournalProjectName(testDate, false), "Daily interval name matches");
        AssertEqual("Journal Shift 2026-06-26", store.ExpectedJournalProjectName(testDate, true), "Shifted daily interval name matches");

        // 2. Weekly
        store.Defaults = store.Defaults with { JournalInterval = "Weekly", DayStartHour = 0 };
        AssertEqual("Journal Week 26 2026", store.ExpectedJournalProjectName(testDate, false), "Weekly interval name matches");
        AssertEqual("Journal Shift Week 26 2026", store.ExpectedJournalProjectName(testDate, true), "Shifted weekly interval name matches");

        // 3. Monthly
        store.Defaults = store.Defaults with { JournalInterval = "Monthly", DayStartHour = 0 };
        AssertEqual("Journal 2026-06", store.ExpectedJournalProjectName(testDate, false), "Monthly interval name matches");
        AssertEqual("Journal Shift 2026-06", store.ExpectedJournalProjectName(testDate, true), "Shifted monthly interval name matches");

        // 4. DayStartHour offset shift
        // A time like 3am on Friday 2026-06-26 with DayStartHour = 4 should land in Thursday 2026-06-25 (which is still week 26)
        var earlyMorning = new DateTime(2026, 6, 26, 3, 0, 0);
        store.Defaults = store.Defaults with { JournalInterval = "Daily", DayStartHour = 4 };
        AssertEqual("Journal 2026-06-25", store.ExpectedJournalProjectName(earlyMorning, false), "DayStartHour shifts daily name back");

        // An early morning time like 3am on Monday 2026-06-22 (Week 26 starts on Monday) with DayStartHour = 4 should land in Sunday 2026-06-21 (Week 25)
        var earlyMonday = new DateTime(2026, 6, 22, 3, 0, 0);
        store.Defaults = store.Defaults with { JournalInterval = "Weekly", DayStartHour = 4 };
        AssertEqual("Journal Week 25 2026", store.ExpectedJournalProjectName(earlyMonday, false), "DayStartHour shifts weekly name back");

        // 5. Verify rolling on interval change / rollover
        store.Defaults = store.Defaults with { JournalInterval = "Daily", DayStartHour = 0 };
        var dailyProject = store.GetCaptureHome();
        AssertEqual(store.DefaultProjectName(), dailyProject.Name, "Daily project has correct name");

        // Change interval to weekly. Next default project call should roll to a new project because the expected name doesn't match daily project's name.
        store.Defaults = store.Defaults with { JournalInterval = "Weekly", DayStartHour = 0 };
        var weeklyProject = store.GetCaptureHome();
        AssertTrue(dailyProject.Id != weeklyProject.Id, "Changing interval rolls to a new default project");
        AssertEqual(store.DefaultProjectName(), weeklyProject.Name, "Weekly project has correct name");

        // Verify rolling via manual name change (simulating a calendar rollover)
        // Rename active weekly project to simulate an older week
        store.UpdateProjectName(weeklyProject, "Journal Week 01 2020");
        var rolledProject = store.GetCaptureHome();
        AssertTrue(weeklyProject.Id != rolledProject.Id, "Old journal project name triggers rollover and mints fresh project");
        AssertEqual(store.DefaultProjectName(), rolledProject.Name, "Rolled project has current week's name");
    }

    [Fact(DisplayName = "Zetl state reuses the journal default home")]
    public static void StateReusesDatedDefaultProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var first = store.GetCaptureHome();
        store.ClearActiveProject();
        var second = store.GetCaptureHome();

        AssertEqual(first.Id, second.Id, "Default project should be reused after it is cleared inactive.");
        AssertEqual(1, store.State.Projects.Count, "Default project reuse should not create duplicates.");
    }

    [Fact(DisplayName = "Zetl state finish starts a fresh journal")]
    public static void StateFinishStartsFreshJournal()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);

        var first = store.GetCaptureHome();
        AssertTrue(first.JournalMode, "The default home is the Journal.");

        var finished = store.FinishProject(first.Id);
        AssertEqual(first.Id, finished?.Id, "Finish should return the sealed journal.");
        AssertEqual("Finished", first.Status, "Finish should mark the journal Finished.");
        AssertEqual<ZetlProject?>(null, store.ActiveProject, "Finish should clear the lane.");

        // The Journal is never reopened once sealed (a non-Active project is never
        // lane-active); capture mints a fresh journal instead.
        var second = store.GetCaptureHome();
        AssertTrue(second.Id != first.Id, "Capture after finishing the journal starts a fresh one.");
        AssertTrue(second.JournalMode, "The fresh journal is journal-mode.");
        AssertEqual("Active", second.Status, "The fresh journal starts Active.");
        AssertTrue(
            store.State.Projects.Any(project => project.Id == first.Id && project.Status == "Finished"),
            "The finished journal is retained, not deleted.");
    }

    [Fact(DisplayName = "Journal bucket rolls at the day-start hour")]
    public static void JournalBucketRollsAtDayStartHour()
    {
        // Day parents are named "ddd MM-dd" (2026-06-24 is a Wednesday, 06-23 a Tuesday).
        // Midnight boundary: every clock hour maps to its own calendar day.
        AssertEqual(
            "Wed 06-24",
            ZetlStateRules.JournalBucketName(new DateTime(2026, 6, 24, 0, 30, 0), 0),
            "Midnight start: 00:30 belongs to that day.");
        AssertEqual(
            "Wed 06-24",
            ZetlStateRules.JournalBucketName(new DateTime(2026, 6, 24, 23, 59, 0), 0),
            "Midnight start: 23:59 belongs to that day.");

        // 4am start: captures before 04:00 belong to the previous day.
        AssertEqual(
            "Tue 06-23",
            ZetlStateRules.JournalBucketName(new DateTime(2026, 6, 24, 1, 0, 0), 4),
            "4am start: 01:00 rolls back to the previous day.");
        AssertEqual(
            "Tue 06-23",
            ZetlStateRules.JournalBucketName(new DateTime(2026, 6, 24, 3, 59, 0), 4),
            "4am start: 03:59 is still the previous day.");
        AssertEqual(
            "Wed 06-24",
            ZetlStateRules.JournalBucketName(new DateTime(2026, 6, 24, 4, 0, 0), 4),
            "4am start: 04:00 begins the new day.");

        // Out-of-range hours clamp rather than throw.
        AssertEqual(
            "Wed 06-24",
            ZetlStateRules.JournalBucketName(new DateTime(2026, 6, 24, 23, 30, 0), 99),
            "An out-of-range day-start hour clamps to 23.");
    }

    [Fact(DisplayName = "Journal mode rolls into dated buckets")]
    public static void JournalModeRollsIntoDatedBuckets()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Journal", new[] { "Scratch" });
        project.JournalMode = true;

        var day1 = new DateTime(2026, 6, 23, 9, 0, 0); // Tuesday
        var day2 = new DateTime(2026, 6, 24, 9, 0, 0); // Wednesday

        // Copy capture rolls into today's day parent's "Capture" child and activates it.
        var first = store.RollJournalBucket(project, day1);
        AssertTrue(first is not null, "Rolling a journal project yields a bucket.");
        AssertEqual(ZetlStateRules.JournalCaptureBucketName, first!.Name, "The copy-capture roll targets the Capture child.");
        AssertEqual(first.Id, project.ActiveBucketId, "Today's Capture bucket becomes active.");
        var day1Parent = project.Buckets.Single(bucket => bucket.Id == first.ParentBucketId);
        AssertEqual("Tue 06-23", day1Parent.Name, "The Capture child sits under today's day parent.");
        AssertTrue(day1Parent.ParentBucketId is null, "The day parent is a top-level bucket.");

        var sameDay = store.RollJournalBucket(project, day1);
        AssertEqual(first.Id, sameDay!.Id, "A second capture the same day reuses the Capture bucket.");

        // Quick notes route to the same day parent's separate "Quick Note" child,
        // without stealing the active bucket from the copy target.
        var quickNote = store.ResolveJournalQuickNoteBucket(project, day1);
        AssertEqual(ZetlStateRules.JournalQuickNoteBucketName, quickNote!.Name, "Quick notes target the Quick Note child.");
        AssertEqual(day1Parent.Id, quickNote.ParentBucketId, "The Quick Note child shares the day parent.");
        AssertTrue(quickNote.Id != first.Id, "Capture and Quick Note are distinct children.");
        AssertEqual(first.Id, project.ActiveBucketId, "Quick notes do not steal the active bucket.");

        var nextDay = store.RollJournalBucket(project, day2);
        AssertEqual(ZetlStateRules.JournalCaptureBucketName, nextDay!.Name, "A new day creates its own Capture child.");
        var day2Parent = project.Buckets.Single(bucket => bucket.Id == nextDay.ParentBucketId);
        AssertEqual("Wed 06-24", day2Parent.Name, "The new day gets its own day parent.");
        AssertTrue(nextDay.Id != first.Id, "The new day's Capture bucket is distinct.");
        AssertEqual(nextDay.Id, project.ActiveBucketId, "The new day's Capture bucket becomes active.");

        var plain = store.CreateProject("Plain", new[] { "Scratch" });
        AssertTrue(
            store.RollJournalBucket(plain, day1) is null,
            "A non-journal project does not roll into a day bucket.");
        AssertTrue(
            store.ResolveJournalQuickNoteBucket(plain, day1) is null,
            "A non-journal project has no journal quick-note bucket.");
    }

    [Fact(DisplayName = "Journal auto-returns from a quiet project")]
    public static void JournalAutoReturnsFromQuietProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path)
        {
            Defaults = ZetlBucketDefaults.Standard with { JournalAutoReturnHours = 2 }
        };

        var work = store.CreateProject("Work", new[] { "Notes" }, "Notes");
        AssertEqual(work.Id, store.GetActiveProject()?.Id, "A new deliberate project is active.");
        AssertEqual(work.Id, store.GetCaptureHome().Id, "A fresh active project keeps capture.");

        // Simulate the project going quiet past the window.
        work.LastActiveUtc = DateTime.UtcNow.AddHours(-3);
        var resolved = store.GetCaptureHome();
        AssertTrue(resolved.JournalMode, "A quiet deliberate project hands held captures back to the Journal.");
        AssertTrue(store.GetActiveProject() is null, "Auto-return leaves the lane with no active project.");
        AssertTrue(resolved.Id != work.Id, "Capture left the quiet deliberate project.");
    }

    [Fact(DisplayName = "Journal auto-return off keeps the project")]
    public static void JournalAutoReturnOffKeepsProject()
    {
        using var temp = new TempStateFile();
        // JournalAutoReturnHours defaults to 0 (off).
        var store = new ZetlStateStore(temp.Path);
        var work = store.CreateProject("Work", new[] { "Notes" }, "Notes");
        work.LastActiveUtc = DateTime.UtcNow.AddHours(-10);
        AssertEqual(
            work.Id,
            store.GetCaptureHome().Id,
            "With auto-return off, even a long-quiet project keeps capture.");
    }

    [Fact(DisplayName = "Ctrl+J toggles between the Journal and the last project")]
    public static void ToggleActiveProjectSwitchesBetweenJournalAndLastProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);

        // The Journal is the home, with no deliberate project used yet.
        store.GetCaptureHome();
        AssertEqual(
            ZetlProjectToggleOutcome.NoProjectToActivate,
            store.ToggleActiveProject().Outcome,
            "With no deliberate project yet, there is nothing to activate.");

        // Activating a deliberate project records it as the last deliberate project.
        var work = store.CreateProject("Work", new[] { "Notes" }, "Notes");
        AssertEqual(work.Id, store.GetActiveProject()?.Id, "The new project is active.");

        var off = store.ToggleActiveProject();
        AssertEqual(
            ZetlProjectToggleOutcome.ReturnedToJournal,
            off.Outcome,
            "Toggling a deliberate project returns to the Journal.");
        AssertTrue(store.GetActiveProject()?.JournalMode == true, "The Journal is active after deactivating.");

        var on = store.ToggleActiveProject();
        AssertEqual(
            ZetlProjectToggleOutcome.Activated,
            on.Outcome,
            "Toggling from the Journal reactivates the last deliberate project.");
        AssertEqual("Work", on.ProjectName, "It reactivates the last deliberate project by name.");
        AssertEqual(work.Id, store.GetActiveProject()?.Id, "Work is active again.");
    }

    [Fact(DisplayName = "Journal lookup finds each lane's journal without creating one")]
    public static void FindLaneJournalNeverCreates()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var before = store.State.Projects.Count;

        AssertTrue(store.FindLaneJournal(shifted: true) is null, "No Shift journal exists yet.");
        AssertEqual(before, store.State.Projects.Count, "Looking doesn't create one.");

        var main = store.GetCaptureHome();
        var shift = store.GetCaptureHome(shifted: true);
        AssertEqual(shift.Id, store.FindLaneJournal(shifted: true)?.Id, "The Shift lane finds its own journal.");
        AssertEqual(main.Id, store.FindLaneJournal(shifted: false)?.Id, "The Main lane finds its own.");
        AssertTrue(main.Id != shift.Id, "The two lanes keep separate journals.");

        store.SetProjectStatus(shift, ZetlStateRules.FinishedStatus);
        AssertTrue(store.FindLaneJournal(shifted: true) is null, "A finished journal is no longer the lane's home.");
    }
}
