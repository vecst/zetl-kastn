using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlRuntimeSupportTests
{
    [Fact(DisplayName = "Runtime applies app settings defaults")]
    public static void RuntimeAppliesAppSettingsDefaults()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var settings = new ZetlAppSettings
        {
            DefaultProjectBuckets = [],
            DefaultCompileMode = "TSV",
            DefaultTsvRowLength = 4
        };

        ZetlRuntimeSettings.ApplyTo(store, settings);

        AssertEqual("Capture", store.Defaults.ProjectBuckets[0], "Empty settings should use Capture.");
        AssertEqual("Quick Note", store.Defaults.ProjectBuckets[1], "Empty settings should use Quick Note.");
        AssertEqual("TSV", store.Defaults.CompileMode, "Compile mode should flow into state defaults.");
        AssertEqual(4, store.Defaults.TsvRowLength, "TSV row length should flow into state defaults.");
    }

    [Fact(DisplayName = "Runtime undo stack keeps lanes separate")]
    public static void RuntimeUndoStackKeepsLanesSeparate()
    {
        var stack = new ZetlUndoStack(capacity: 3);
        var normalUndone = false;
        var shiftedUndone = false;
        stack.Push(false, "normal", () => normalUndone = true);
        stack.Push(true, "shifted", () => shiftedUndone = true);

        AssertTrue(stack.TryPop(false, out var normal), "Normal lane action should be available.");
        normal!.Undo();
        AssertTrue(normalUndone, "Normal lane undo should run.");
        AssertFalse(shiftedUndone, "Normal lane undo should not touch Shift.");
        AssertTrue(stack.TryPop(true, out var shifted), "Shift lane action should remain available.");
        shifted!.Undo();
        AssertTrue(shiftedUndone, "Shift lane undo should run.");
    }

    [Fact(DisplayName = "Runtime activity log buffer drains safely")]
    public static void RuntimeActivityLogBufferDrainsSafely()
    {
        var now = new DateTime(2026, 6, 6, 12, 34, 56, DateTimeKind.Local);
        var buffer = new ZetlActivityLogBuffer(() => now);
        buffer.Enqueue("Saved.");
        buffer.Enqueue(" ");

        var first = buffer.Drain();
        AssertEqual(1, first.Count, "Blank messages should not be queued.");
        AssertEqual("[12:34:56] Saved.", first[0], "Log entries should include the enqueue time.");
        AssertEqual(0, buffer.Drain().Count, "Drain should remove returned entries.");
    }

    [Fact(DisplayName = "Runtime logged fire-and-forget records async failures")]
    public static void RuntimeRunLoggedRecordsAsyncFailure()
    {
        var messages = new List<string>();

        // A synchronously-faulting operation completes RunLogged inline, so the
        // diagnostic is recorded by the time the call returns.
        ZetlAsync.RunLogged(
            () => throw new InvalidOperationException("boom"),
            "test operation",
            messages.Add);

        AssertTrue(
            messages.Exists(message => message.Contains("test operation") && message.Contains("boom")),
            "A faulted fire-and-forget task should be logged with its operation label and exception.");
    }

    [Fact(DisplayName = "Runtime logged fire-and-forget records delayed async failures")]
    public static void RuntimeRunLoggedRecordsDelayedAsyncFailure()
    {
        var messages = new List<string>();
        using var logged = new ManualResetEventSlim();

        // Faults after an await (not synchronously), proving RunLogged catches
        // post-continuation failures too.
        ZetlAsync.RunLogged(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("delayed boom");
            },
            "delayed operation",
            message =>
            {
                messages.Add(message);
                logged.Set();
            });

        AssertTrue(logged.Wait(TimeSpan.FromSeconds(5)), "A delayed async fault should be logged within the timeout.");
        AssertTrue(
            messages.Exists(message => message.Contains("delayed operation") && message.Contains("delayed boom")),
            "A fault after an await should be logged with the operation label and exception.");
    }

    [Fact(DisplayName = "Runtime parity scenario writes a reloadable snapshot")]
    public static void RuntimeParityScenarioWritesSnapshot()
    {
        using var temp = new TempStateFile();
        var directory = System.IO.Path.GetDirectoryName(temp.Path)!;

        ZetlParityScenario.Run(directory);

        AssertTrue(
            File.Exists(System.IO.Path.Combine(directory, ZetlParityScenario.SnapshotFileName)),
            "Parity scenario should write its normalized snapshot.");
        var reloaded = new ZetlStateStore(temp.Path, sessionId: "reload");
        AssertTrue(
            reloaded.State.Projects.Any(project => project.Name == "Parity Project"),
            "Parity scenario state should reload from disk.");
        AssertTrue(
            reloaded.State.Projects.Any(project => project.Name == "Shift Parity"),
            "Parity scenario should persist the Shift lane project.");
    }
}
