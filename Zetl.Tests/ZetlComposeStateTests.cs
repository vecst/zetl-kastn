using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlComposeStateTests
{
    [Fact(DisplayName = "Zetl state detects compilable notes")]
    public static void StateDetectsCompilableNotes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        AssertFalse(store.HasCompilableSlips(project), "Empty buckets should not be compilable.");

        store.AddSlip(store.ActiveBucket!, "compiled", "copy");
        AssertTrue(store.HasCompilableSlips(project), "A project with a note should be compilable.");
    }

    [Fact(DisplayName = "Zetl compiles across projects without changing the active project")]
    public static void StateCompilesToOtherProjectWithoutChangingActive()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var source = store.CreateProject("Source", ["Inbox"], "Inbox");
        var dest = store.CreateProject("Dest", ["Notes"], "Notes");
        // CreateProject activates Dest; activate Source to mirror the app state
        // when you open compile against Source but pick Dest as the target.
        store.SetActiveProject(source.Id);
        var destActiveBucketBefore = dest.ActiveBucketId;

        // Compile-to path: create/find a bucket in another project, add the note.
        var destination = store.GetOrCreateBucket(dest, "Compiled", setActive: false);
        store.AddSlip(destination, "compiled text", "compile");

        AssertEqual(source.Id, store.State.ActiveProjectId, "Compiling should not change which project is active.");
        AssertEqual(destActiveBucketBefore, dest.ActiveBucketId, "Compiling into another project should not change its active bucket.");
        AssertTrue(dest.Buckets.Any(bucket => bucket.Name == "Compiled"), "Compile destination bucket should be created in the destination project.");
        AssertEqual("compiled text", destination.Slips.Single().Text, "Compiled note should land in the destination bucket.");
    }

    [Fact(DisplayName = "Zetl state compiles selected notes")]
    public static void StateCompilesSelectedNotes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
        var inbox = store.ActiveBucket!;
        var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");
        store.AddSlip(inbox, "first", "copy");
        store.AddSlip(inbox, "second", "copy");
        store.AddSlip(ideas, "third", "copy");

        var notes = store.GetSlipDisplayItems(project);
        var compiled = ZetlComposeOutput.PlainText(project,
        [
            notes.Single(item => item.Slip.Text == "first"),
            notes.Single(item => item.Slip.Text == "third")
        ]);

        AssertTrue(compiled.Contains("Inbox"), "Selected inbox note should include its bucket heading.");
        AssertTrue(compiled.Contains($"{Environment.NewLine}\tfirst"), "First selected note should compile indented under its bucket.");
        AssertFalse(compiled.Contains("second"), "Unselected note should not compile.");
        AssertTrue(compiled.Contains("Ideas"), "Selected ideas note should include its bucket heading.");
        AssertTrue(compiled.Contains($"{Environment.NewLine}\tthird"), "Second selected note should compile indented under its bucket.");
    }

    [Fact(DisplayName = "Zetl state compiles selected notes unformatted")]
    public static void StateCompilesSelectedNotesUnformatted()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
        var inbox = store.ActiveBucket!;
        var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");
        store.AddSlip(inbox, "first", "copy");
        store.AddSlip(ideas, "third", "copy");

        var notes = store.GetSlipDisplayItems(project);
        var compiled = ZetlComposeOutput.Unformatted(
        [
            notes.Single(item => item.Slip.Text == "first"),
            notes.Single(item => item.Slip.Text == "third")
        ]);

        AssertEqual($"first{Environment.NewLine}third", compiled, "Unformatted compile should include only note text.");
    }

    [Fact(DisplayName = "Zetl state compiles selected notes as TSV rows")]
    public static void StateCompilesSelectedNotesAsTsvRows()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.ActiveBucket!;
        store.AddSlip(queue, "one", "copy");
        store.AddSlip(queue, "two", "copy");
        store.AddSlip(queue, "three", "copy");
        store.AddSlip(queue, $"four{Environment.NewLine}line", "copy");
        store.AddSlip(queue, "five\tcell", "copy");

        var compiled = ZetlComposeOutput.Tsv(store.GetSlipDisplayItems(project), 3);
        var expected = string.Join(Environment.NewLine,
        [
            "one\ttwo\tthree",
            "four line\tfive cell"
        ]);

        AssertEqual(expected, compiled, "TSV compile should split selected notes into fixed-length rows.");
    }

    [Fact(DisplayName = "Zetl state compiles TSV with bucket headers")]
    public static void StateCompilesTsvWithBucketHeaders()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Vehicles"], "Vehicles");
        var vehicles = store.ActiveBucket!;
        store.UpdateBucketSettings(
            vehicles,
            "Vehicles",
            "Standard",
            "TSV",
            $"VIN{Environment.NewLine}Make{Environment.NewLine}Model",
            3);
        store.AddSlip(vehicles, "vin-1", "copy");
        store.AddSlip(vehicles, "ford", "copy");
        store.AddSlip(vehicles, "f150", "copy");
        store.AddSlip(vehicles, "vin-2", "copy");

        var compiled = ZetlComposeOutput.Tsv(store.GetSlipDisplayItems(project), ZetlComposeOutput.TsvRowLength(vehicles));
        var expected = string.Join(Environment.NewLine,
        [
            "VIN\tMake\tModel",
            "vin-1\tford\tf150",
            "vin-2"
        ]);

        AssertEqual(expected, compiled, "TSV compile should include bucket headers before data rows.");
    }

    [Fact(DisplayName = "Zetl TSV compose is one table across buckets")]
    public static void TsvComposeIsOneTableAcrossBuckets()
    {
        var lines = ZetlTsv.Table(
        [
            (["Name", "Number"], ["a", "1"], 2),
            (["Name", "Number"], ["b", "2"], 2),
            ([], ["loose", "cells", "here"], 3),
            (["Other"], ["x"], 1)
        ]);

        AssertEqual(
            "Name\tNumber|a\t1|b\t2|loose\tcells\there|Other|x",
            string.Join("|", lines),
            "No titles or blank lines; a repeated header row appears once.");
        AssertEqual(
            "<table><tr><td>Name</td><td>A &amp; B</td></tr></table>",
            ZetlTsv.HtmlTable(["Name\tA & B"]),
            "The HTML table escapes cell text.");
    }

    [Fact(DisplayName = "Zetl compile scope respects the session-only toggle")]
    public static void StateCompileScopeRespectsSessionToggle()
    {
        using var temp = new TempStateFile();
        var oldStore = new ZetlStateStore(temp.Path, "old-session");
        var project = oldStore.CreateProject("Demo", ["Inbox"], "Inbox");
        oldStore.AddSlip(oldStore.ActiveBucket!, "old note", "copy");

        var newStore = new ZetlStateStore(temp.Path, "new-session");
        var loadedProject = newStore.State.Projects.Single(project => project.Name == "Demo");
        newStore.SetActiveProject(loadedProject.Id);

        // Whole-project compile (the default) now reaches across sessions, so
        // a reactivated project still has its old notes available to compile.
        AssertTrue(newStore.HasCompilableSlips(loadedProject), "Whole-project compile should include old-session notes.");
        AssertEqual(1, newStore.GetSlipDisplayItems(loadedProject).Count, "Whole-project compile should list old-session notes.");

        // The "This session only" toggle narrows compile back to the session.
        AssertFalse(newStore.HasCompilableSlips(loadedProject, currentSessionOnly: true), "Session-only compile should exclude old-session notes.");
        AssertEqual(0, newStore.GetSlipDisplayItems(loadedProject, null, currentSessionOnly: true).Count, "Session-only compile should not list old-session notes.");

        var inbox = newStore.ActiveBucket!;
        newStore.AddSlip(inbox, "new note", "copy");

        var sessionNotes = newStore.GetSlipDisplayItems(loadedProject, null, currentSessionOnly: true);
        AssertEqual(1, sessionNotes.Count, "Session-only compile should list just the current-session note.");
        var sessionCompiled = ZetlComposeOutput.PlainText(loadedProject, sessionNotes);
        AssertFalse(sessionCompiled.Contains("old note"), "Session-only compile should omit old-session notes.");
        AssertTrue(sessionCompiled.Contains("new note"), "Session-only compile should include current-session notes.");

        var allNotes = newStore.GetSlipDisplayItems(loadedProject);
        var allCompiled = ZetlComposeOutput.PlainText(loadedProject, allNotes);
        AssertEqual(2, allNotes.Count, "Whole-project compile should list both notes.");
        AssertTrue(allCompiled.Contains("old note"), "Whole-project compile should include old-session notes.");
        AssertTrue(allCompiled.Contains("new note"), "Whole-project compile should include current-session notes.");
        AssertEqual(2, inbox.Slips.Count, "Old notes should remain stored for board/history.");
    }
}
