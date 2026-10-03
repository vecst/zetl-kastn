using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlStateProjectTests
{
    [Fact(DisplayName = "Zetl state creates projects and scratch buckets")]
    public static void StateCreatesProjectAndScratch()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        AssertEqual("Demo", project.Name, "Project name should persist.");
        AssertTrue(project.Buckets.Any(bucket => bucket.Name == "Scratch"), "Scratch bucket should be created.");
        AssertEqual("Inbox", store.ActiveBucket?.Name, "Requested active bucket should be active.");
    }

    [Fact(DisplayName = "Zetl state keeps active temporary consumables")]
    public static void StateKeepsActiveTemporaryConsumables()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject(
            "One Shot",
            ["Queue"],
            "Queue",
            kind: ZetlStateStore.TemporaryConsumableProjectKind,
            sourceTemplateId: "template",
            temporaryLane: ZetlStateStore.NormalLane);

        AssertEqual(project.Id, store.ActiveProject?.Id, "The temporary project should stay active in its lane.");
        AssertTrue(
            store.State.Projects.Any(item => item.Id == project.Id),
            "An active temporary project should remain in the workspace.");

        var reloaded = new ZetlStateStore(temp.Path);
        AssertEqual(project.Id, reloaded.ActiveProject?.Id, "Reload should keep an active temporary project.");
        AssertTrue(
            reloaded.State.Projects.Any(item => item.Id == project.Id),
            "Reload should not dispose a temporary project that still owns its lane.");
    }

    [Fact(DisplayName = "Zetl state disposes temporary consumables when lane clears")]
    public static void StateDisposesTemporaryConsumablesWhenLaneClears()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject(
            "One Shot",
            ["Queue"],
            "Queue",
            kind: ZetlStateStore.TemporaryConsumableProjectKind,
            sourceTemplateId: "template",
            temporaryLane: ZetlStateStore.NormalLane);

        store.ClearActiveProject();

        AssertEqual<ZetlProject?>(null, store.ActiveProject, "Clearing the lane should leave no active project.");
        AssertFalse(
            store.State.Projects.Any(item => item.Id == project.Id),
            "Clearing the lane should dispose the temporary project.");

        var reloaded = new ZetlStateStore(temp.Path);
        AssertFalse(
            reloaded.State.Projects.Any(item => item.Id == project.Id),
            "Disposed temporary projects should not return after reload.");
    }

    [Fact(DisplayName = "Zetl state disposes abandoned temporary consumables on load")]
    public static void StateDisposesAbandonedTemporaryConsumablesOnLoad()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject(
            "One Shot",
            ["Queue"],
            "Queue",
            shifted: true,
            kind: ZetlStateStore.TemporaryConsumableProjectKind,
            sourceTemplateId: "template",
            temporaryLane: ZetlStateStore.ShiftLane);
        JsonFile.WriteAtomic(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "workspace.json"),
            new ZetlWorkspaceFile { Version = 1 });

        var reloaded = new ZetlStateStore(temp.Path);

        AssertFalse(
            reloaded.State.Projects.Any(item => item.Id == project.Id),
            "A temporary project that is not active in its assigned lane should be disposed on load.");
    }

    [Fact(DisplayName = "Zetl state returns to the previous project when a temporary consumable finishes")]
    public static void StateReturnsToPreviousProjectWhenTemporaryConsumableFinishes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var work = store.CreateProject("Work", ["Inbox"], "Inbox");
        var form = store.CreateProject(
            "Personal info",
            ["Fields"],
            "Fields",
            kind: ZetlStateStore.TemporaryConsumableProjectKind,
            sourceTemplateId: "personal-info",
            temporaryLane: ZetlStateStore.NormalLane);
        var fields = form.Buckets.First(bucket => bucket.Name == "Fields");
        store.SetBucketKind(fields, "Replay");

        AssertTrue(form.Consumable, "Temporary projects are consumable.");
        AssertEqual(work.Id, form.ReturnProjectId, "Starting it records the project it replaced.");

        var finish = store.FinishReplayBucket(fields, shifted: false);

        AssertTrue(finish.Deleted, "A temporary consumable is deleted on finish.");
        AssertEqual("Work", finish.ReturnedTo, "The notice can name where the lane went.");
        AssertEqual(work.Id, store.ActiveProject?.Id, "The lane goes back to Work.");
        AssertFalse(store.State.Projects.Any(item => item.Id == form.Id), "The consumable is gone.");

        store.ToggleActiveProject();
        store.ToggleActiveProject();
        AssertEqual(work.Id, store.ActiveProject?.Id, "Ctrl+J's last project is still Work.");
    }

    [Fact(DisplayName = "Zetl state returns from a kept consumable and keeps it")]
    public static void StateReturnsFromKeptConsumable()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var work = store.CreateProject("Work", ["Inbox"], "Inbox");
        store.ClearActiveProject();
        var form = store.CreateProject("Personal info", ["Fields"], "Fields", consumable: true);
        var fields = form.Buckets.First(bucket => bucket.Name == "Fields");
        store.SetBucketKind(fields, "Replay");

        AssertEqual<string?>(null, form.ReturnProjectId, "Nothing was active when it started.");
        AssertEqual(work.Id, store.State.LastDeliberateProjectId, "Starting a consumable leaves Ctrl+J's last project alone.");

        var finish = store.FinishReplayBucket(fields, shifted: false);

        AssertFalse(finish.Deleted, "A kept consumable stays.");
        AssertEqual<string?>(null, finish.ReturnedTo, "There was nothing to go back to.");
        AssertEqual<ZetlProject?>(null, store.ActiveProject, "The lane goes back to no project.");
        AssertFalse(ZetlStateStore.IsReplayBucket(fields), "Its emptied bucket turns Standard.");
        AssertTrue(store.State.Projects.Any(item => item.Id == form.Id), "The kept project stays for review.");

        var reloaded = new ZetlStateStore(temp.Path);
        AssertTrue(
            reloaded.State.Projects.First(item => item.Id == form.Id).Consumable,
            "The marker survives a reload.");
    }

    [Fact(DisplayName = "Zetl state finishes a consumable on the shift lane without touching the normal lane")]
    public static void StateFinishesShiftLaneConsumable()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var work = store.CreateProject("Work", ["Inbox"], "Inbox");
        var side = store.CreateProject("Side", ["Inbox"], "Inbox", shifted: true);
        var form = store.CreateProject(
            "Form",
            ["Fields"],
            "Fields",
            shifted: true,
            kind: ZetlStateStore.TemporaryConsumableProjectKind,
            temporaryLane: ZetlStateStore.ShiftLane);
        var fields = form.Buckets.First(bucket => bucket.Name == "Fields");
        store.SetBucketKind(fields, "Replay");

        AssertEqual(side.Id, form.ReturnProjectId, "It records the shift lane's project.");

        store.FinishReplayBucket(fields, shifted: true);

        AssertEqual(side.Id, store.GetActiveProject(shifted: true)?.Id, "The shift lane goes back to Side.");
        AssertEqual(work.Id, store.GetActiveProject(shifted: false)?.Id, "The normal lane never moved.");
    }

    [Fact(DisplayName = "Zetl state finishing a plain Replay bucket keeps the project active")]
    public static void StateFinishingPlainReplayBucketKeepsProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var work = store.CreateProject("Work", ["Inbox", "Queue"], "Queue");
        var queue = work.Buckets.First(bucket => bucket.Name == "Queue");
        store.SetBucketKind(queue, "Replay");

        var finish = store.FinishReplayBucket(queue, shifted: false);

        AssertEqual<string?>(null, finish.ReturnedTo, "An ordinary project has nowhere to return to.");
        AssertEqual(work.Id, store.ActiveProject?.Id, "It stays active.");
        AssertFalse(ZetlStateStore.IsReplayBucket(queue), "The bucket turns Standard.");
    }

    [Fact(DisplayName = "Zetl state finds the most recently written project")]
    public static void StateFindsMostRecentlyWrittenProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var older = store.CreateProject("Older", ["Inbox"], "Inbox");
        var newer = store.CreateProject("Newer", ["Inbox"], "Inbox");
        var olderNote = store.AddSlip(older.Buckets.First(), "old", "copy");
        var newerNote = store.AddSlip(newer.Buckets.First(), "new", "copy");
        olderNote.CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        newerNote.CreatedAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        AssertEqual("Newer", store.GetMostRecentlyWrittenProject()?.Name, "The project with the latest note should win.");

        // The Zetl Logs infra project is appended to constantly but must
        // never be chosen as the last-written project.
        store.AppendLogSlips(["log line"], maxDayBuckets: 14, maxNotesPerBucket: 2000);
        AssertEqual("Newer", store.GetMostRecentlyWrittenProject()?.Name, "Zetl Logs must be excluded from the last-written project.");
    }

    [Fact(DisplayName = "Zetl state can start without an active project")]
    public static void StateCanStartWithoutActiveProject()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        store.CreateProject("Demo Shift", ["Inbox"], "Inbox", shifted: true);
        store.ClearActiveProject();
        store.ClearActiveProject(shifted: true);

        var loaded = new ZetlStateStore(temp.Path);
        AssertEqual<ZetlProject?>(null, loaded.ActiveProject, "Cleared active project should remain inactive after reload.");
        AssertEqual<ZetlProject?>(null, loaded.ShiftActiveProject, "Cleared Shift active project should remain inactive after reload.");
        AssertEqual(2, loaded.State.Projects.Count, "Inactive startup should preserve existing projects.");
    }

    [Fact(DisplayName = "Zetl state keeps normal and Shift active projects separate")]
    public static void StateKeepsNormalAndShiftProjectsSeparate()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var normal = store.GetCaptureHome();
        var shifted = store.GetCaptureHome(shifted: true);
        store.SetActiveProject(normal.Id);
        store.SetActiveProject(shifted.Id, shifted: true);

        AssertFalse(normal.Id == shifted.Id, "Normal and Shift lanes should use different default projects.");
        AssertEqual(normal.Id, store.GetActiveProject()?.Id, "Normal lane should keep its active project.");
        AssertEqual(shifted.Id, store.GetActiveProject(shifted: true)?.Id, "Shift lane should keep its active project.");
        AssertEqual(store.DefaultProjectName(shifted: true), shifted.Name, "Shift default project should be named distinctly.");
    }

    [Fact(DisplayName = "Zetl state switches active bucket")]
    public static void StateSwitchesActiveBucket()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
        var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");

        store.SetActiveBucket(project, ideas.Id);

        AssertEqual(ideas.Id, store.ActiveBucket?.Id, "Selected bucket should become active.");
        var loaded = new ZetlStateStore(temp.Path);
        AssertEqual(ideas.Id, loaded.ActiveBucket?.Id, "Selected active bucket should persist.");
    }

    [Fact(DisplayName = "Zetl state remembers quick note bucket")]
    public static void StateRemembersQuickNoteBucket()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
        var scratch = store.GetScratchBucket(project);
        var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");

        AssertEqual(scratch.Id, store.GetQuickNoteBucket(project).Id, "Quick notes should default to Scratch.");
        store.SetQuickNoteBucket(project, ideas.Id);
        AssertEqual(ideas.Id, store.GetQuickNoteBucket(project).Id, "Selected quick note bucket should be remembered.");

        var loaded = new ZetlStateStore(temp.Path);
        var loadedProject = loaded.State.Projects.Single();
        var loadedIdeas = loadedProject.Buckets.Single(bucket => bucket.Name == "Ideas");
        AssertEqual(loadedIdeas.Id, loaded.GetQuickNoteBucket(loadedProject).Id, "Quick note bucket should round-trip.");

        loaded.DeleteBucket(loadedProject, loadedIdeas.Id);
        AssertEqual("Scratch", loaded.GetQuickNoteBucket(loadedProject).Name, "Deleted quick note bucket should fall back to Scratch.");
    }

    [Fact(DisplayName = "Zetl state consolidates child buckets regardless of order")]
    public static void StateConsolidatesChildBucketsRegardlessOfOrder()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.GetCaptureHome();

        var parentId = Guid.NewGuid().ToString("N");
        var childId = Guid.NewGuid().ToString("N");
        store.State.Projects.Add(new ZetlProject
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = DateTime.Now.ToString("yyyy-MM-dd"),
            Buckets =
            [
                // Child deliberately listed before its parent.
                new ZetlBucket { Id = childId, Name = "Sub", ParentBucketId = parentId, Settings = new ZETL.ZetlBucketSettings { Kind = "Standard" } },
                new ZetlBucket { Id = parentId, Name = "Group", Settings = new ZETL.ZetlBucketSettings { Kind = "Standard" } }
            ]
        });
        store.ClearActiveProject();

        store.ConsolidateDefaultProject();

        var project = store.State.Projects.Single(item => item.Name == DateTime.Now.ToString("yyyy-MM-dd"));
        var group = project.Buckets.Single(bucket => bucket.Name == "Group");
        var sub = project.Buckets.Single(bucket => bucket.Name == "Sub");
        AssertEqual(group.Id, sub.ParentBucketId, "Child bucket should keep its parent after consolidation regardless of list order.");
    }

    [Fact(DisplayName = "Zetl state orders projects by their latest note")]
    public static void StateOrdersProjectsByLatestNote()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var older = store.CreateProject("Older", ["Inbox"], "Inbox");
        var newer = store.CreateProject("Newer", ["Inbox"], "Inbox");
        store.CreateProject("Blank", ["Inbox"], "Inbox");
        store.AddSlip(older.Buckets.First(), "old", "copy").CreatedAtUtc =
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.AddSlip(newer.Buckets.First(), "new", "copy").CreatedAtUtc =
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        store.AppendLogSlips(["log line"], maxDayBuckets: 14, maxNotesPerBucket: 2000);

        var names = store.GetProjectsByRecentWrite().Select(project => project.Name).ToList();
        AssertEqual("Newer", names[0], "The latest note leads.");
        AssertEqual("Older", names[1], "Older notes follow.");
        AssertTrue(
            names.IndexOf("Blank") > 1 && names.IndexOf(ZetlStateStore.LogProjectName) > 1,
            $"Projects without notes, and the logs, come after written ones (got {string.Join(", ", names)}).");
        AssertEqual("Newer", store.GetMostRecentCompilableProject()?.Name, "The latest written project compiles.");

        // A newer project holding nothing compilable is passed over.
        var dividers = store.CreateProject("Dividers", ["Inbox"], "Inbox");
        store.AddSlip(dividers.Buckets.First(), "", "kastn", blockKind: ZetlBlockKinds.Divider);
        AssertEqual("Dividers", store.GetProjectsByRecentWrite()[0].Name, "Any note counts as a write.");
        AssertEqual("Newer", store.GetMostRecentCompilableProject()?.Name, "Only text that compiles counts for Compile.");

        // So is a newer consumable project: its Replay queue is pasted out,
        // not compiled.
        var form = store.CreateProject("Personal info", ["Fields"], "Fields");
        store.SetBucketKind(form.Buckets.First(), "Replay");
        store.AddSlip(form.Buckets.First(), "Full name", "template");
        AssertEqual("Newer", store.GetMostRecentCompilableProject()?.Name, "Consumable projects are passed over.");
    }

    [Fact(DisplayName = "Zetl state supports child buckets")]
    public static void StateSupportsChildBuckets()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var parent = store.ActiveBucket!;
        var child = store.AddBucket(project, "Child", parent.Id);

        AssertEqual(parent.Id, child.ParentBucketId, "Child bucket should store its parent.");
        AssertTrue(store.GetBucketDisplayItems(project).Any(item => item.Bucket.Id == child.Id && item.Label.StartsWith("  ")), "Child bucket should display indented.");

        store.DeleteBucket(project, parent.Id);
        AssertFalse(project.Buckets.Any(bucket => bucket.Id == child.Id), "Deleting a parent bucket should remove child buckets.");
    }

    [Fact(DisplayName = "Zetl state gets or creates compile buckets")]
    public static void StateGetsOrCreatesCompileBuckets()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");

        var existing = store.GetOrCreateBucket(project, "inbox");
        AssertEqual("Inbox", existing.Name, "Existing bucket lookup should be case-insensitive.");
        AssertEqual(2, project.Buckets.Count, "Existing bucket lookup should not create duplicates.");

        var created = store.GetOrCreateBucket(project, "Compiled");
        store.AddSlip(created, "compiled text", "compile");
        AssertEqual("Compiled", created.Name, "Missing bucket should be created.");
        AssertEqual("compile", created.Slips.Single().Source, "Compiled note should store its source.");
    }

    [Fact(DisplayName = "Zetl adds notes preserving structure")]
    public static void StateAddsNotesPreservingStructure()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.ActiveBucket!;

        var added = store.AddSlips(bucket, ["one", "   ", "two", "three"], "compile");

        AssertEqual(3, added.Count, "AddNotes should skip blank entries.");
        AssertEqual(3, bucket.Slips.Count, "Each non-blank text should become its own note.");
        AssertEqual("one", bucket.Slips[0].Text, "Notes should keep insertion order.");
        AssertEqual("three", bucket.Slips[2].Text, "Notes should keep insertion order.");
        AssertEqual("compile", bucket.Slips[0].Source, "AddNotes should set the note source.");
    }

    [Fact(DisplayName = "Zetl state protects the Scratch bucket")]
    public static void StateProtectsScratchBucket()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var scratch = store.GetScratchBucket(project);

        store.UpdateBucketName(scratch, "Renamed");
        AssertEqual("Scratch", scratch.Name, "Scratch should not be renamable.");

        store.UpdateBucketSettings(scratch, "Renamed", "Standard", "Formatted", "", 5);
        AssertEqual("Scratch", scratch.Name, "Bucket settings should not rename Scratch.");

        store.DeleteBucket(project, scratch.Id);
        AssertTrue(project.Buckets.Any(bucket => bucket.Id == scratch.Id), "Scratch should not be deletable.");
    }

    [Fact(DisplayName = "Zetl state protects the Deleted bucket")]
    public static void StateProtectsDeletedBucket()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var inbox = store.ActiveBucket!;
        var deleted = store.GetDeletedBucket(project);
        store.AddSlip(deleted, "removed", "kastn-delete");

        AssertEqual("Deleted", deleted.Name, "Deleted bucket should have a readable name.");
        AssertEqual("Deleted", deleted.Settings.Kind, "Deleted bucket should use a protected kind.");
        AssertTrue(project.Buckets.Any(bucket => bucket.Id == deleted.Id), "Deleted bucket should remain human-readable in project JSON.");
        AssertFalse(store.GetBucketDisplayItems(project).Any(item => item.Bucket.Id == deleted.Id), "Normal bucket lists should hide Deleted.");
        AssertTrue(store.GetBucketDisplayItems(project, includeDeleted: true).Any(item => item.Bucket.Id == deleted.Id), "Explicit bucket lists may show Deleted.");
        AssertFalse(store.HasCompilableSlips(project), "Deleted notes should not make a project compilable.");

        store.SetActiveBucket(project, deleted.Id);
        AssertEqual(inbox.Id, store.ActiveBucket?.Id, "Deleted should not become the active capture bucket.");
        store.SetQuickNoteBucket(project, deleted.Id);
        AssertEqual("Scratch", store.GetQuickNoteBucket(project).Name, "Deleted should not become the quick-note bucket.");

        store.UpdateBucketName(deleted, "Trash");
        store.SetBucketKind(deleted, "Replay");
        store.DeleteBucket(project, deleted.Id);
        AssertEqual("Deleted", deleted.Name, "Deleted should not be renamable.");
        AssertEqual("Deleted", deleted.Settings.Kind, "Deleted should not change kind.");
        AssertTrue(project.Buckets.Any(bucket => bucket.Id == deleted.Id), "Deleted should not be deletable.");

        var loaded = new ZetlStateStore(temp.Path);
        var loadedProject = loaded.State.Projects.Single(project => project.Name == "Demo");
        var loadedDeleted = loadedProject.Buckets.Single(bucket => bucket.Id == deleted.Id);
        AssertEqual("Deleted", loadedDeleted.Name, "Deleted name should persist.");
        AssertEqual("Deleted", loadedDeleted.Settings.Kind, "Deleted kind should persist.");
        AssertFalse(loaded.GetBucketDisplayItems(loadedProject).Any(item => item.Bucket.Id == loadedDeleted.Id), "Reloaded normal bucket lists should hide Deleted.");
    }

    [Fact(DisplayName = "Zetl state deletes projects and repairs active lanes")]
    public static void StateDeletesProjectsAndRepairsActiveLanes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var first = store.CreateProject("First", ["Inbox"], "Inbox");
        var second = store.CreateProject("Second", ["Queue"], "Queue", shifted: true);
        store.SetActiveProject(first.Id);

        store.DeleteProject(first.Id);

        AssertFalse(store.State.Projects.Any(project => project.Id == first.Id), "Deleted project should be removed.");
        AssertEqual(second.Id, store.GetActiveProject()?.Id, "Normal lane should fall back to a remaining project.");
        AssertEqual(second.Id, store.GetActiveProject(shifted: true)?.Id, "Shift lane should preserve its remaining active project.");

        var loaded = new ZetlStateStore(temp.Path);
        AssertFalse(loaded.State.Projects.Any(project => project.Id == first.Id), "Project deletion should persist.");
        AssertEqual(second.Id, loaded.GetActiveProject()?.Id, "Repaired normal lane should persist.");
    }

    [Fact(DisplayName = "Zetl state deletes bucket trees and repairs pointers")]
    public static void StateDeletesBucketTreesAndRepairsPointers()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var parent = store.AddBucket(project, "Parent");
        var child = store.AddBucket(project, "Child", parent.Id);
        store.SetQuickNoteBucket(project, child.Id);
        store.SetActiveBucket(project, child.Id);

        store.DeleteBucket(project, parent.Id);

        AssertFalse(project.Buckets.Any(bucket => bucket.Id == parent.Id), "Deleted parent bucket should be removed.");
        AssertFalse(project.Buckets.Any(bucket => bucket.Id == child.Id), "Deleted bucket descendants should be removed.");
        AssertTrue(project.Buckets.Any(bucket => bucket.Id == project.ActiveBucketId), "Active bucket should move to a remaining bucket.");
        AssertEqual<string?>(null, project.QuickNoteBucketId, "Deleted quick-note bucket should clear its pointer.");

        var loaded = new ZetlStateStore(temp.Path);
        var loadedProject = loaded.State.Projects.Single();
        AssertTrue(loadedProject.Buckets.Any(bucket => bucket.Id == loadedProject.ActiveBucketId), "Repaired active bucket should persist.");
        AssertEqual<string?>(null, loadedProject.QuickNoteBucketId, "Cleared quick-note pointer should persist.");
    }

    [Fact(DisplayName = "Zetl state deletes notes")]
    public static void StateDeletesNotes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.ActiveBucket!;
        var keep = store.AddSlip(bucket, "keep", "copy");
        var remove = store.AddSlip(bucket, "remove", "copy");

        store.DeleteSlip(bucket, remove.Id);

        AssertEqual(1, bucket.Slips.Count, "Deleting a note should remove only the selected note.");
        AssertEqual(keep.Id, bucket.Slips.Single().Id, "Unselected notes should remain.");

        var loaded = new ZetlStateStore(temp.Path);
        AssertEqual(keep.Id, loaded.ActiveBucket!.Slips.Single().Id, "Note deletion should persist.");
    }

    [Fact(DisplayName = "Zetl state preserves bucket settings")]
    public static void StatePreservesBucketSettings()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Vehicles"], "Vehicles");
        var bucket = store.ActiveBucket!;

        store.UpdateBucketSettings(
            bucket,
            "Vehicle Entry",
            "Replay",
            "TSV",
            $"VIN{Environment.NewLine}Make{Environment.NewLine}Model",
            3);

        AssertEqual("Vehicle Entry", bucket.Name, "Bucket settings should rename the bucket.");
        AssertTrue(ZetlStateStore.IsReplayBucket(bucket), "Bucket settings should set the current kind.");
        AssertEqual("Replay", bucket.Settings.DefaultKind, "Default kind should persist in memory.");
        AssertEqual("TSV", bucket.Settings.DefaultCompileMode, "Compile mode should persist in memory.");
        AssertEqual(3, store.GetBucketTsvRowLength(bucket), "Header count should infer TSV row length.");

        store.SetBucketKind(bucket, "Standard");
        AssertEqual("Replay", bucket.Settings.DefaultKind, "Changing current kind should not erase default kind.");

        var loaded = new ZetlStateStore(temp.Path);
        var loadedBucket = loaded.ActiveBucket!;
        AssertEqual("Vehicle Entry", loadedBucket.Name, "Bucket settings name should round-trip.");
        AssertEqual("Replay", loadedBucket.Settings.DefaultKind, "Default kind should round-trip.");
        AssertEqual("TSV", loadedBucket.Settings.DefaultCompileMode, "Compile mode should round-trip.");
        AssertEqual("VIN\nMake\nModel", loadedBucket.Settings.DefaultStartingText.ReplaceLineEndings("\n"), "Starting text should round-trip.");
        AssertEqual(3, loaded.GetBucketTsvRowLength(loadedBucket), "Inferred TSV length should round-trip.");
    }

    [Fact(DisplayName = "Zetl state finds last active note")]
    public static void StateFindsLastActiveNote()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
        var inbox = store.ActiveBucket!;
        var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");
        store.AddSlip(inbox, "first", "copy");
        Thread.Sleep(2);
        store.AddSlip(ideas, "latest", "copy");

        AssertTrue(store.TryGetLastSlipDisplayItem(project, null, out var note), "Last active note should be found.");
        AssertEqual("latest", note?.Slip.Text, "Last active note should use the newest note timestamp.");
        AssertTrue(store.TryGetLastSlipDisplayItem(project, [inbox], out var scopedNote), "Scoped last note should be found.");
        AssertEqual("first", scopedNote?.Slip.Text, "Scoped last note should respect bucket scope.");
    }

    [Fact(DisplayName = "Zetl state appends activity-log notes without activating")]
    public static void StateAppendsLogNotesWithoutActivating()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Work", ["Inbox"], "Inbox");
        var activeBefore = store.State.ActiveProjectId;

        store.AppendLogSlips(["[09:00:00] one", "[09:00:01] two"], maxDayBuckets: 14, maxNotesPerBucket: 1000);

        var logProject = store.State.Projects.Single(project => project.Name == ZetlStateStore.LogProjectName);
        AssertEqual(activeBefore, store.State.ActiveProjectId, "Logging should not change the active project.");
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var dayBucket = logProject.Buckets.Single(bucket => bucket.Name == today);
        AssertEqual(2, dayBucket.Slips.Count, "Both log lines should be stored as notes.");
        AssertEqual("log", dayBucket.Slips[0].Source, "Log notes should use the log source.");

        store.AppendLogSlips(["a", "b", "c", "d", "e"], maxDayBuckets: 14, maxNotesPerBucket: 3);
        AssertEqual(3, logProject.Buckets.Single(bucket => bucket.Name == today).Slips.Count, "Day bucket should be capped to maxNotesPerBucket.");
        AssertEqual("e", logProject.Buckets.Single(bucket => bucket.Name == today).Slips[^1].Text, "Capping should keep the newest notes.");

        var reloaded = new ZetlStateStore(temp.Path);
        AssertTrue(reloaded.State.Projects.Any(project => project.Name == ZetlStateStore.LogProjectName), "Log project should persist across reload.");
        AssertEqual("Work", reloaded.ActiveProject?.Name, "Logging should leave the real active project untouched across reload.");
    }

    [Fact(DisplayName = "Zetl state applies bucket defaults")]
    public static void StateAppliesBucketDefaults()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path)
        {
            Defaults = new ZetlBucketDefaults(new[] { "Notes", "Scratch" }, "TSV", 4)
        };

        // The default capture home is the rolling Journal (its buckets are days),
        // so the configured project-bucket names apply to deliberately created
        // projects, not here. Today's bucket still takes the compile defaults.
        var project = store.GetCaptureHome();
        AssertTrue(project.JournalMode, "The default capture home is journal-mode.");
        var today = project.Buckets.Single(bucket => bucket.Id == project.ActiveBucketId);
        AssertEqual("TSV", today.Settings.DefaultCompileMode, "Today's bucket should take the default compile mode.");
        AssertEqual(4, today.Settings.DefaultTsvRowLength, "Today's bucket should take the default TSV row length.");

        var added = store.AddBucket(project, "Extra");
        AssertEqual("TSV", added.Settings.DefaultCompileMode, "Added bucket should take the default compile mode.");
        AssertEqual(4, added.Settings.DefaultTsvRowLength, "Added bucket should take the default TSV row length.");
    }

    [Fact(DisplayName = "URL slips are derived from content")]
    public static void UrlSlipsAreDerivedFromContent()
    {
        // A note is a link if it contains an http/https URL anywhere.
        AssertTrue(ZetlSlipClassifier.LooksLikeUrl("https://example.com"), "A bare URL is a link.");
        AssertTrue(ZetlSlipClassifier.LooksLikeUrl("http://x.com/a?b=1#c"), "A URL with path/query/fragment is a link.");
        AssertTrue(
            ZetlSlipClassifier.LooksLikeUrl("https://x.com/article\nMy note about it"),
            "A link followed by a note is a link.");
        AssertTrue(
            ZetlSlipClassifier.LooksLikeUrl("My note first\nhttps://x.com"),
            "A link anywhere (even after a note) is a link.");
        AssertTrue(
            ZetlSlipClassifier.LooksLikeUrl("I read https://x.com/article and loved it."),
            "A link mid-sentence is a link.");
        AssertFalse(ZetlSlipClassifier.LooksLikeUrl("example.com"), "A scheme-less host stays text.");
        AssertFalse(ZetlSlipClassifier.LooksLikeUrl("ftp://files.test"), "A non-web scheme stays text.");
        AssertFalse(ZetlSlipClassifier.LooksLikeUrl("just a plain note"), "Plain text is not a link.");

        // The mapper derives the slip type from the note text — no stored field.
        var bucket = new ZetlBucket { Id = "b", Name = "Links", Settings = new ZETL.ZetlBucketSettings { Kind = "Standard" } };
        AssertEqual(
            ZETL.Contracts.ZetlSlipType.Url,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket, new ZetlSlip { Id = "u", Text = "https://example.com" }).Type,
            "A URL note maps to a Url slip.");
        AssertEqual(
            ZETL.Contracts.ZetlSlipType.Text,
            ZetlProjectSnapshotMapper.ToSnapshot(bucket, new ZetlSlip { Id = "t", Text = "hello world" }).Type,
            "Plain text maps to a Text slip.");
    }
}
