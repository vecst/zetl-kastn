using Chordl;
using static Chordl.ChordlKeys;

namespace ZETL;

internal static partial class Program
{
    private static class SelfTests
    {
        public static int Run()
        {
            var tests = new (string Name, Action Test)[]
            {
                ("Ctrl+C pass-through suppresses later repeats", CopyPassThroughSuppressesRepeats),
                ("Ctrl+V tap dispatches paste on key-up", PasteTapDispatchesOnKeyUp),
                ("Ctrl+V gallop tap dispatches after Ctrl key-up", PasteGallopTapDispatchesAfterCtrlKeyUp),
                ("Ctrl+V late gallop does not dispatch paste", PasteLateGallopDoesNotDispatch),
                ("Ctrl+V handled tap suppresses default paste", PasteHandledTapSuppressesDefaultPaste),
                ("Ctrl+V hold reserves paste", PasteHoldDoesNotDispatch),
                ("Ctrl+V hold suppresses repeats after Ctrl key-up", PasteHoldSuppressesRepeatsAfterCtrlKeyUp),
                ("Ctrl+B tap dispatches board shortcut on key-up", BoardTapDispatchesOnKeyUp),
                ("Ctrl+B hold opens board without dispatch", BoardHoldDoesNotDispatch),
                ("Ctrl+P tap dispatches pop key on key-up", PopToggleTapDispatchesOnKeyUp),
                ("Ctrl+P hold raises Pop toggle", PopToggleHoldDoesNotDispatch),
                ("Ctrl+R tap dispatches replay key on key-up", FifoToggleTapDispatchesOnKeyUp),
                ("Ctrl+R hold raises Replay toggle", FifoToggleHoldDoesNotDispatch),
                ("Ctrl+Z hold raises Zetl undo", UndoHoldDoesNotDispatch),
                ("Shift changes restart hold detection", ShiftChangeRestartsHold),
                ("Zetl state creates projects and scratch buckets", StateCreatesProjectAndScratch),
                ("Zetl state creates dated default projects on demand", StateCreatesDatedDefaultProject),
                ("Zetl state reuses dated default projects", StateReusesDatedDefaultProject),
                ("Zetl state consolidates dated default projects", StateConsolidatesDatedDefaultProjects),
                ("Zetl state consolidates dated default without activation", StateConsolidatesDatedDefaultWithoutActivation),
                ("Zetl state consolidates child buckets regardless of order", StateConsolidatesChildBucketsRegardlessOfOrder),
                ("Zetl state can start without an active project", StateCanStartWithoutActiveProject),
                ("Zetl state keeps normal and Shift active projects separate", StateKeepsNormalAndShiftProjectsSeparate),
                ("Zetl state supports child buckets", StateSupportsChildBuckets),
                ("Zetl state switches active bucket", StateSwitchesActiveBucket),
                ("Zetl state remembers quick note bucket", StateRemembersQuickNoteBucket),
                ("Zetl state gets or creates compile buckets", StateGetsOrCreatesCompileBuckets),
                ("Zetl state preserves bucket settings", StatePreservesBucketSettings),
                ("Zetl state protects the Scratch bucket", StateProtectsScratchBucket),
                ("Zetl state compiles selected notes", StateCompilesSelectedNotes),
                ("Zetl state compiles selected notes unformatted", StateCompilesSelectedNotesUnformatted),
                ("Zetl state compiles selected notes as TSV rows", StateCompilesSelectedNotesAsTsvRows),
                ("Zetl state compiles TSV with bucket headers", StateCompilesTsvWithBucketHeaders),
                ("Zetl state finds last active note", StateFindsLastActiveNote),
                ("Zetl compile scope respects the session-only toggle", StateCompileScopeRespectsSessionToggle),
                ("Zetl state FIFO dequeues current-session notes in order", StateFifoDequeuesCurrentSessionNotesInOrder),
                ("Zetl state FIFO archives consumed notes for review", StateFifoArchivesConsumedNotesForReview),
                ("Zetl state FIFO restores consumed notes from review", StateFifoRestoresConsumedNotesFromReview),
                ("Zetl state FIFO disables pop mode", StateFifoDisablesPopMode),
                ("Zetl state maps legacy Fifo kind to Replay", StateMapsLegacyFifoKindToReplay),
                ("Zetl state detects compilable notes", StateDetectsCompilableNotes),
                ("Zetl state finds inactive scratch notes for compile", StateFindsInactiveScratchCompileTarget),
                ("Zetl state pop mode removes matching last note", StatePopModeRemovesLastMatchingNote),
                ("Zetl state round-trips JSON", StateRoundTripsJson),
                ("Zetl app settings round-trip first-run flag", AppSettingsRoundTripFirstRunFlag),
                ("Zetl app settings round-trip configurable fields", AppSettingsRoundTripFields),
                ("Zetl state applies bucket defaults", StateAppliesBucketDefaults),
                ("Zetl embeds a parseable default hotkeys config", EmbeddedDefaultConfigParses)
            };

            var failures = new List<string>();
            foreach (var (name, test) in tests)
            {
                try
                {
                    test();
                    Console.WriteLine($"PASS {name}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: {ex.Message}");
                    Console.WriteLine($"FAIL {name}: {ex.Message}");
                }
            }

            return failures.Count == 0 ? 0 : 1;
        }

        private static void CopyPassThroughSuppressesRepeats()
        {
            using var processor = CreateProcessor(out _, out _, out _, out _);
            AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false), "Ctrl down should pass through.");
            AssertFalse(processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false), "First copy press should pass through.");
            Thread.Sleep(30);
            AssertTrue(processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false), "Later repeat should suppress.");
            processor.HandleKeyEvent(VK_C, isKeyDown: false, isKeyUp: true);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void PasteTapDispatchesOnKeyUp()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Paste key up should suppress physical event.");
            AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic paste.");
            AssertEqual(VK_V, dispatched[0], "Dispatched key should be V.");
            AssertEqual(1, taps.Count, "Tap callback should fire once.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void PasteGallopTapDispatchesAfterCtrlKeyUp()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Paste key up should still finish the tap.");
            AssertEqual(1, dispatched.Count, "Gallop tap should dispatch one synthetic paste.");
            AssertEqual(VK_V, dispatched[0], "Dispatched key should be V.");
            AssertEqual(1, taps.Count, "Tap callback should fire once.");
        }

        private static void PasteLateGallopDoesNotDispatch()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out _);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            Thread.Sleep(80);
            AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
            _ = processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true);
            AssertEqual(0, dispatched.Count, "Late gallop should not dispatch paste.");
        }

        private static void PasteHandledTapSuppressesDefaultPaste()
        {
            using var processor = CreateProcessor(
                out var dispatched,
                out _,
                out var taps,
                out _,
                tapHandled: true);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Paste key up should suppress physical event.");
            AssertEqual(1, taps.Count, "Handled tap callback should fire once.");
            AssertEqual(0, dispatched.Count, "Handled tap should not dispatch the default paste.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void PasteHoldDoesNotDispatch()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            Thread.Sleep(80);
            AssertEqual(1, holds.Count, "Hold callback should fire once.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held paste should not dispatch paste.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void PasteHoldSuppressesRepeatsAfterCtrlKeyUp()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            Thread.Sleep(80);
            AssertEqual(1, holds.Count, "Hold callback should fire once.");
            AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Post-Ctrl target repeat should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held paste should not dispatch paste.");
        }

        private static void BoardTapDispatchesOnKeyUp()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: true, isKeyUp: false), "Board key down should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: false, isKeyUp: true), "Board key up should suppress physical event.");
            AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic board shortcut.");
            AssertEqual(VK_B, dispatched[0], "Dispatched key should be B.");
            AssertEqual(1, taps.Count, "Tap callback should fire once.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void BoardHoldDoesNotDispatch()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: true, isKeyUp: false), "Board key down should suppress.");
            Thread.Sleep(80);
            AssertEqual(1, holds.Count, "Hold callback should fire once.");
            AssertEqual("Ctrl+B", holds[0].Name, "Hold should use board chord.");
            AssertTrue(processor.HandleKeyEvent(VK_B, isKeyDown: false, isKeyUp: true), "Held board key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held board chord should not dispatch Ctrl+B.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void PopToggleTapDispatchesOnKeyUp()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: true, isKeyUp: false), "Pop toggle key down should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: false, isKeyUp: true), "Pop toggle key up should suppress physical event.");
            AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic shortcut.");
            AssertEqual(VK_P, dispatched[0], "Dispatched key should be P.");
            AssertEqual(1, taps.Count, "Tap callback should fire once.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void PopToggleHoldDoesNotDispatch()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: true, isKeyUp: false), "Pop toggle key down should suppress.");
            Thread.Sleep(80);
            AssertEqual(1, holds.Count, "Hold callback should fire once.");
            AssertEqual("Ctrl+P", holds[0].Name, "Hold should use Pop toggle chord.");
            AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: false, isKeyUp: true), "Held Pop toggle key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held Pop toggle should not dispatch the pop key.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void FifoToggleTapDispatchesOnKeyUp()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out var taps, out _);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: true, isKeyUp: false), "Replay toggle key down should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: false, isKeyUp: true), "Replay toggle key up should suppress physical event.");
            AssertEqual(1, dispatched.Count, "Tap should dispatch one synthetic shortcut.");
            AssertEqual(VK_R, dispatched[0], "Dispatched key should be R.");
            AssertEqual(1, taps.Count, "Tap callback should fire once.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void FifoToggleHoldDoesNotDispatch()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: true, isKeyUp: false), "Replay toggle key down should suppress.");
            Thread.Sleep(80);
            AssertEqual(1, holds.Count, "Hold callback should fire once.");
            AssertEqual("Ctrl+R", holds[0].Name, "Hold should use Replay toggle chord.");
            AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: false, isKeyUp: true), "Held Replay toggle key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held Replay toggle should not dispatch the replay key.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void UndoHoldDoesNotDispatch()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_Z, isKeyDown: true, isKeyUp: false), "Undo key down should suppress.");
            Thread.Sleep(80);
            AssertEqual(1, holds.Count, "Hold callback should fire once.");
            AssertEqual("Ctrl+Z", holds[0].Name, "Hold should use undo chord.");
            AssertTrue(processor.HandleKeyEvent(VK_Z, isKeyDown: false, isKeyUp: true), "Held undo key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held undo should not dispatch app undo.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void ShiftChangeRestartsHold()
        {
            using var processor = CreateProcessor(out _, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false);
            Thread.Sleep(25);
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
            Thread.Sleep(25);
            AssertEqual(0, holds.Count, "Hold should not fire before the restarted threshold.");
            Thread.Sleep(90);
            AssertEqual(1, holds.Count, "Hold should fire after Shift restart threshold.");
            AssertEqual("Ctrl+Shift+C", holds[0].Name, "Hold should use shifted chord.");
            processor.HandleKeyEvent(VK_C, isKeyDown: false, isKeyUp: true);
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: false, isKeyUp: true);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void StateCreatesProjectAndScratch()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            AssertEqual("Demo", project.Name, "Project name should persist.");
            AssertTrue(project.Buckets.Any(bucket => bucket.Name == "Scratch"), "Scratch bucket should be created.");
            AssertEqual("Inbox", store.ActiveBucket?.Name, "Requested active bucket should be active.");
        }

        private static void StatePopModeRemovesLastMatchingNote()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.ActiveBucket!;
            store.AddNote(bucket, "alpha", "copy");
            store.AddNote(bucket, "beta", "copy");
            store.SetBucketPopMode(bucket, true);
            AssertFalse(store.TryPopLastMatchingActiveNote("alpha"), "Only the last note may pop.");
            AssertTrue(store.TryPopLastMatchingActiveNote("beta"), "Matching last note should pop.");
            AssertEqual(1, bucket.Notes.Count, "One note should remain.");
            AssertEqual("alpha", bucket.Notes[0].Text, "The earlier note should remain.");

            store.RestoreNote(bucket, bucket.Notes[0]);
            AssertEqual(1, bucket.Notes.Count, "Restoring an existing note should not duplicate it.");
            AssertTrue(store.TryPopLastMatchingActiveNote("alpha", shifted: false, out var poppedBucket, out var poppedNote), "Pop should return undo details.");
            AssertEqual(bucket.Id, poppedBucket?.Id, "Pop should report the source bucket.");
            AssertEqual("alpha", poppedNote?.Text, "Pop should report the removed note.");
            store.RestoreNote(poppedBucket!, poppedNote!);
            AssertEqual("alpha", bucket.Notes.Single().Text, "Restore should put popped note back.");
        }

        private static void StateCreatesDatedDefaultProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.GetOrCreateDefaultProject();
            AssertEqual(DateTime.Now.ToString("yyyy-MM-dd"), project.Name, "Default project should use today's date.");
            AssertEqual("Inbox", store.ActiveBucket?.Name, "Default project should start in Inbox.");
            AssertTrue(project.Buckets.Any(bucket => bucket.Name == "Scratch"), "Default project should include Scratch.");

            store.UpdateProjectName(project, "Renamed");
            AssertEqual("Renamed", store.CompilePlainText(project, [store.ActiveBucket!]).Split(Environment.NewLine)[0], "Compile should use the updated project name.");
        }

        private static void StateCanStartWithoutActiveProject()
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

        private static void StateKeepsNormalAndShiftProjectsSeparate()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var normal = store.GetOrCreateDefaultProject();
            var shifted = store.GetOrCreateDefaultProject(shifted: true);

            AssertFalse(normal.Id == shifted.Id, "Normal and Shift lanes should use different default projects.");
            AssertEqual(normal.Id, store.GetActiveProject()?.Id, "Normal lane should keep its active project.");
            AssertEqual(shifted.Id, store.GetActiveProject(shifted: true)?.Id, "Shift lane should keep its active project.");
            AssertTrue(shifted.Name.EndsWith(" Shift", StringComparison.Ordinal), "Shift default project should be named distinctly.");
        }

        private static void StateSwitchesActiveBucket()
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

        private static void StateRemembersQuickNoteBucket()
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

        private static void StateReusesDatedDefaultProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var first = store.GetOrCreateDefaultProject();
            store.ClearActiveProject();
            var second = store.GetOrCreateDefaultProject();

            AssertEqual(first.Id, second.Id, "Default project should be reused after it is cleared inactive.");
            AssertEqual(1, store.State.Projects.Count, "Default project reuse should not create duplicates.");
        }

        private static void StateConsolidatesDatedDefaultProjects()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var first = store.GetOrCreateDefaultProject();
            var firstScratch = store.GetScratchBucket(first);
            store.AddNote(firstScratch, "first", "cut");
            store.State.Projects.Add(new ZetlProject
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = DateTime.Now.ToString("yyyy-MM-dd"),
                Buckets =
                [
                    new ZetlBucket
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = "Scratch",
                        Kind = "Standard",
                        Notes =
                        [
                            new ZetlNote
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Text = "second",
                                Source = "cut",
                                CreatedAtUtc = DateTime.UtcNow
                            }
                        ]
                    }
                ]
            });
            store.ClearActiveProject();

            var consolidated = store.GetOrCreateDefaultProject();
            var scratch = store.GetScratchBucket(consolidated);
            AssertEqual(first.Id, consolidated.Id, "Default project consolidation should keep the first project.");
            AssertEqual(1, store.State.Projects.Count(project => project.Name == DateTime.Now.ToString("yyyy-MM-dd")), "Duplicate daily projects should merge into one.");
            AssertTrue(scratch.Notes.Any(note => note.Text == "first"), "First scratch note should survive consolidation.");
            AssertTrue(scratch.Notes.Any(note => note.Text == "second"), "Duplicate scratch note should merge into primary scratch.");
        }

        private static void StateConsolidatesDatedDefaultWithoutActivation()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var first = store.GetOrCreateDefaultProject();
            var firstScratch = store.GetScratchBucket(first);
            store.AddNote(firstScratch, "first", "cut");
            store.State.Projects.Add(new ZetlProject
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = DateTime.Now.ToString("yyyy-MM-dd"),
                Buckets =
                [
                    new ZetlBucket
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = "Scratch",
                        Kind = "Standard",
                        Notes =
                        [
                            new ZetlNote
                            {
                                Id = Guid.NewGuid().ToString("N"),
                                Text = "second",
                                Source = "cut",
                                CreatedAtUtc = DateTime.UtcNow
                            }
                        ]
                    }
                ]
            });
            store.ClearActiveProject();

            store.ConsolidateDefaultProject();

            AssertEqual<ZetlProject?>(null, store.ActiveProject, "Default project consolidation should not activate a project.");
            AssertEqual(1, store.State.Projects.Count(project => project.Name == DateTime.Now.ToString("yyyy-MM-dd")), "Duplicate daily projects should merge into one.");
            var scratch = store.State.Projects.Single(project => project.Name == DateTime.Now.ToString("yyyy-MM-dd"))
                .Buckets.Single(bucket => bucket.Name == "Scratch");
            AssertTrue(scratch.Notes.Any(note => note.Text == "first"), "First scratch note should survive consolidation.");
            AssertTrue(scratch.Notes.Any(note => note.Text == "second"), "Duplicate scratch note should merge into primary scratch.");
        }

        private static void StateConsolidatesChildBucketsRegardlessOfOrder()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.GetOrCreateDefaultProject();

            var parentId = Guid.NewGuid().ToString("N");
            var childId = Guid.NewGuid().ToString("N");
            store.State.Projects.Add(new ZetlProject
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = DateTime.Now.ToString("yyyy-MM-dd"),
                Buckets =
                [
                    // Child deliberately listed before its parent.
                    new ZetlBucket { Id = childId, Name = "Sub", ParentBucketId = parentId, Kind = "Standard" },
                    new ZetlBucket { Id = parentId, Name = "Group", Kind = "Standard" }
                ]
            });
            store.ClearActiveProject();

            store.ConsolidateDefaultProject();

            var project = store.State.Projects.Single(item => item.Name == DateTime.Now.ToString("yyyy-MM-dd"));
            var group = project.Buckets.Single(bucket => bucket.Name == "Group");
            var sub = project.Buckets.Single(bucket => bucket.Name == "Sub");
            AssertEqual(group.Id, sub.ParentBucketId, "Child bucket should keep its parent after consolidation regardless of list order.");
        }

        private static void EmbeddedDefaultConfigParses()
        {
            // Guards the single-file publish fallback: the canonical hotkeys.json
            // must be embedded under this exact name and parse through the same
            // loader path Program uses when no external file is found.
            var assembly = typeof(Program).Assembly;
            using var stream = assembly.GetManifestResourceStream("hotkeys.json");
            AssertTrue(stream is not null, "Embedded default hotkeys.json should be present in the assembly.");

            using var reader = new StreamReader(stream!);
            var config = ChordlConfigLoader.LoadFromJson(reader.ReadToEnd());
            AssertTrue(config.Actions.Count > 0, "Embedded default config should define at least one hotkey.");
            AssertTrue(config.HoldDelay > TimeSpan.Zero, "Embedded default config should define a positive hold delay.");
        }

        private static void StateDetectsCompilableNotes()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            AssertFalse(store.HasCompilableNotes(project), "Empty buckets should not be compilable.");

            store.AddNote(store.ActiveBucket!, "compiled", "copy");
            AssertTrue(store.HasCompilableNotes(project), "A project with a note should be compilable.");
        }

        private static void StateFindsInactiveScratchCompileTarget()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.GetOrCreateDefaultProject();
            var scratch = store.GetScratchBucket(project);
            store.AddNote(scratch, "scratch note", "cut");
            store.ClearActiveProject();

            AssertTrue(store.TryGetScratchCompileTarget(out var compileProject, out var compileBucket), "Inactive scratch note should be compilable.");
            AssertEqual(project.Id, compileProject?.Id, "Scratch compile should use the project that owns Scratch.");
            AssertEqual(scratch.Id, compileBucket?.Id, "Scratch compile should return the Scratch bucket.");
            AssertEqual<ZetlProject?>(null, store.ActiveProject, "Scratch compile lookup should not activate the project.");
        }

        private static void StateSupportsChildBuckets()
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

        private static void StateGetsOrCreatesCompileBuckets()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");

            var existing = store.GetOrCreateBucket(project, "inbox");
            AssertEqual("Inbox", existing.Name, "Existing bucket lookup should be case-insensitive.");
            AssertEqual(2, project.Buckets.Count, "Existing bucket lookup should not create duplicates.");

            var created = store.GetOrCreateBucket(project, "Compiled");
            store.AddNote(created, store.CompilePlainText(project, [existing]), "compile");
            AssertEqual("Compiled", created.Name, "Missing bucket should be created.");
            AssertEqual("compile", created.Notes.Single().Source, "Compiled note should store its source.");
        }

        private static void StateCompilesSelectedNotes()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
            var inbox = store.ActiveBucket!;
            var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");
            store.AddNote(inbox, "first", "copy");
            store.AddNote(inbox, "second", "copy");
            store.AddNote(ideas, "third", "copy");

            var notes = store.GetNoteDisplayItems(project);
            var compiled = store.CompilePlainTextFromNotes(project,
            [
                notes.Single(item => item.Note.Text == "first"),
                notes.Single(item => item.Note.Text == "third")
            ]);

            AssertTrue(compiled.Contains("Inbox"), "Selected inbox note should include its bucket heading.");
            AssertTrue(compiled.Contains("first"), "First selected note should compile.");
            AssertFalse(compiled.Contains("second"), "Unselected note should not compile.");
            AssertTrue(compiled.Contains("Ideas"), "Selected ideas note should include its bucket heading.");
            AssertTrue(compiled.Contains("third"), "Second selected note should compile.");
        }

        private static void StateProtectsScratchBucket()
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

        private static void StatePreservesBucketSettings()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Vehicles"], "Vehicles");
            var bucket = store.ActiveBucket!;

            store.SetBucketPopMode(bucket, true);
            store.UpdateBucketSettings(
                bucket,
                "Vehicle Entry",
                "Replay",
                "TSV",
                $"VIN{Environment.NewLine}Make{Environment.NewLine}Model",
                3);

            AssertEqual("Vehicle Entry", bucket.Name, "Bucket settings should rename the bucket.");
            AssertTrue(ZetlStateStore.IsFifoBucket(bucket), "Bucket settings should set the current kind.");
            AssertFalse(bucket.PopMode, "Replay bucket settings should disable pop mode.");
            AssertEqual("Replay", bucket.DefaultKind, "Default kind should persist in memory.");
            AssertEqual("TSV", bucket.DefaultCompileMode, "Compile mode should persist in memory.");
            AssertEqual(3, store.GetBucketTsvRowLength(bucket), "Header count should infer TSV row length.");

            store.SetBucketKind(bucket, "Standard");
            AssertEqual("Replay", bucket.DefaultKind, "Changing current kind should not erase default kind.");

            var loaded = new ZetlStateStore(temp.Path);
            var loadedBucket = loaded.ActiveBucket!;
            AssertEqual("Vehicle Entry", loadedBucket.Name, "Bucket settings name should round-trip.");
            AssertEqual("Replay", loadedBucket.DefaultKind, "Default kind should round-trip.");
            AssertEqual("TSV", loadedBucket.DefaultCompileMode, "Compile mode should round-trip.");
            AssertEqual("VIN\nMake\nModel", loadedBucket.DefaultStartingText.ReplaceLineEndings("\n"), "Starting text should round-trip.");
            AssertEqual(3, loaded.GetBucketTsvRowLength(loadedBucket), "Inferred TSV length should round-trip.");
        }

        private static void StateCompilesSelectedNotesUnformatted()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
            var inbox = store.ActiveBucket!;
            var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");
            store.AddNote(inbox, "first", "copy");
            store.AddNote(ideas, "third", "copy");

            var notes = store.GetNoteDisplayItems(project);
            var compiled = store.CompileUnformattedFromNotes(
            [
                notes.Single(item => item.Note.Text == "first"),
                notes.Single(item => item.Note.Text == "third")
            ]);

            AssertEqual($"first{Environment.NewLine}third", compiled, "Unformatted compile should include only note text.");
        }

        private static void StateCompilesSelectedNotesAsTsvRows()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;
            store.AddNote(queue, "one", "copy");
            store.AddNote(queue, "two", "copy");
            store.AddNote(queue, "three", "copy");
            store.AddNote(queue, $"four{Environment.NewLine}line", "copy");
            store.AddNote(queue, "five\tcell", "copy");

            var compiled = store.CompileTsvFromNotes(project, store.GetNoteDisplayItems(project), 3);
            var expected = string.Join(Environment.NewLine,
            [
                "Demo",
                "Queue",
                "one\ttwo\tthree",
                "four line\tfive cell"
            ]);

            AssertEqual(expected, compiled, "TSV compile should split selected notes into fixed-length rows.");
        }

        private static void StateCompilesTsvWithBucketHeaders()
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
            store.AddNote(vehicles, "vin-1", "copy");
            store.AddNote(vehicles, "ford", "copy");
            store.AddNote(vehicles, "f150", "copy");
            store.AddNote(vehicles, "vin-2", "copy");

            var compiled = store.CompileTsvFromNotes(project, store.GetNoteDisplayItems(project), store.GetBucketTsvRowLength(vehicles));
            var expected = string.Join(Environment.NewLine,
            [
                "Demo",
                "Vehicles",
                "VIN\tMake\tModel",
                "vin-1\tford\tf150",
                "vin-2"
            ]);

            AssertEqual(expected, compiled, "TSV compile should include bucket headers before data rows.");
        }

        private static void StateFindsLastActiveNote()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox", "Ideas"], "Inbox");
            var inbox = store.ActiveBucket!;
            var ideas = project.Buckets.Single(bucket => bucket.Name == "Ideas");
            store.AddNote(inbox, "first", "copy");
            Thread.Sleep(2);
            store.AddNote(ideas, "latest", "copy");

            AssertTrue(store.TryGetLastNoteDisplayItem(project, null, out var note), "Last active note should be found.");
            AssertEqual("latest", note?.Note.Text, "Last active note should use the newest note timestamp.");
            AssertTrue(store.TryGetLastNoteDisplayItem(project, [inbox], out var scopedNote), "Scoped last note should be found.");
            AssertEqual("first", scopedNote?.Note.Text, "Scoped last note should respect bucket scope.");
        }

        private static void StateCompileScopeRespectsSessionToggle()
        {
            using var temp = new TempStateFile();
            var oldStore = new ZetlStateStore(temp.Path, "old-session");
            var project = oldStore.CreateProject("Demo", ["Inbox"], "Inbox");
            oldStore.AddNote(oldStore.ActiveBucket!, "old note", "copy");

            var newStore = new ZetlStateStore(temp.Path, "new-session");
            var loadedProject = newStore.State.Projects.Single(project => project.Name == "Demo");
            newStore.SetActiveProject(loadedProject.Id);

            // Whole-project compile (the default) now reaches across sessions, so
            // a reactivated project still has its old notes available to compile.
            AssertTrue(newStore.HasCompilableNotes(loadedProject), "Whole-project compile should include old-session notes.");
            AssertEqual(1, newStore.GetNoteDisplayItems(loadedProject).Count, "Whole-project compile should list old-session notes.");

            // The "This session only" toggle narrows compile back to the session.
            AssertFalse(newStore.HasCompilableNotes(loadedProject, currentSessionOnly: true), "Session-only compile should exclude old-session notes.");
            AssertEqual(0, newStore.GetNoteDisplayItems(loadedProject, null, currentSessionOnly: true).Count, "Session-only compile should not list old-session notes.");

            var inbox = newStore.ActiveBucket!;
            newStore.AddNote(inbox, "new note", "copy");

            var sessionNotes = newStore.GetNoteDisplayItems(loadedProject, null, currentSessionOnly: true);
            AssertEqual(1, sessionNotes.Count, "Session-only compile should list just the current-session note.");
            var sessionCompiled = newStore.CompilePlainTextFromNotes(loadedProject, sessionNotes);
            AssertFalse(sessionCompiled.Contains("old note"), "Session-only compile should omit old-session notes.");
            AssertTrue(sessionCompiled.Contains("new note"), "Session-only compile should include current-session notes.");

            var allNotes = newStore.GetNoteDisplayItems(loadedProject);
            var allCompiled = newStore.CompilePlainTextFromNotes(loadedProject, allNotes);
            AssertEqual(2, allNotes.Count, "Whole-project compile should list both notes.");
            AssertTrue(allCompiled.Contains("old note"), "Whole-project compile should include old-session notes.");
            AssertTrue(allCompiled.Contains("new note"), "Whole-project compile should include current-session notes.");
            AssertEqual(2, inbox.Notes.Count, "Old notes should remain stored for board/history.");
        }

        private static void StateFifoDequeuesCurrentSessionNotesInOrder()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path, "fifo-session");
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "one", "copy");
            store.AddNote(queue, "two", "copy");

            AssertTrue(store.TryPeekNextFifoNote(queue, out var first), "FIFO bucket should expose its first note.");
            AssertEqual("one", first?.Text, "FIFO should start with the oldest current-session note.");
            AssertTrue(store.TryConsumeFifoNote(queue, first!.Id), "FIFO should consume the first note.");
            AssertTrue(store.TryPeekNextFifoNote(queue, out var second), "FIFO bucket should expose the next note.");
            AssertEqual("two", second?.Text, "FIFO should advance to the next note.");
            AssertTrue(store.TryConsumeFifoNote(queue, second!.Id), "FIFO should consume the second note.");
            AssertFalse(store.TryPeekNextFifoNote(queue, out _), "FIFO should be empty after its last note is consumed.");

            var reloaded = new ZetlStateStore(temp.Path, "new-session");
            var loadedQueue = reloaded.State.Projects.Single().Buckets.Single(bucket => bucket.Name == "Queue");
            AssertFalse(reloaded.TryPeekNextFifoNote(loadedQueue, out _), "Old-session FIFO notes should not be active after restart.");
            AssertEqual(0, loadedQueue.Notes.Count, "Consumed FIFO notes should stay consumed after reload.");
        }

        private static void StateFifoArchivesConsumedNotesForReview()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path, "fifo-session");
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "posted", "copy");

            AssertTrue(store.TryPeekNextFifoNote(queue, out var note), "FIFO bucket should expose a note.");
            AssertTrue(store.TryConsumeFifoNoteToReview(project, queue, note!.Id, out var reviewBucket), "FIFO should consume into review.");

            AssertTrue(reviewBucket is not null, "FIFO consume should create a review bucket.");
            AssertEqual(queue.Id, project.ActiveBucketId, "Review archive should not steal the active bucket.");
            AssertEqual("Queue Review", reviewBucket!.Name, "Review bucket should be named from the FIFO bucket.");
            AssertEqual("Standard", reviewBucket.Kind, "Review bucket should stay standard.");
            AssertEqual("posted", reviewBucket.Notes.Single().Text, "Review bucket should keep consumed text.");
            AssertEqual("replay", reviewBucket.Notes.Single().Source, "Review note should be tagged as replay.");
            AssertFalse(store.TryPeekNextFifoNote(queue, out _), "Consumed FIFO note should leave the queue.");

            store.AddNote(queue, "posted again", "copy");
            AssertTrue(store.TryPeekNextFifoNote(queue, out var second), "FIFO bucket should expose another note.");
            AssertTrue(store.TryConsumeFifoNoteToReview(project, queue, second!.Id, out var sameReviewBucket), "FIFO should consume into the same review bucket.");
            AssertTrue(sameReviewBucket is not null, "FIFO consume should return the reused review bucket.");
            AssertEqual(reviewBucket.Id, sameReviewBucket!.Id, "Review bucket should be reused.");
            AssertEqual(2, sameReviewBucket.Notes.Count, "Review bucket should accumulate consumed FIFO notes.");
        }

        private static void StateFifoRestoresConsumedNotesFromReview()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path, "fifo-session");
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "posted", "copy");

            AssertTrue(store.TryPeekNextFifoNote(queue, out var note), "FIFO bucket should expose a note.");
            AssertTrue(
                store.TryConsumeFifoNoteToReview(project, queue, note!.Id, out var reviewBucket, out var consumedNote, out var reviewNote),
                "FIFO should consume with undo details.");
            AssertTrue(consumedNote is not null, "FIFO consume should return consumed note.");
            AssertTrue(reviewBucket is not null, "FIFO consume should return review bucket.");
            AssertTrue(reviewNote is not null, "FIFO consume should return review note.");

            store.SetBucketKind(queue, "Standard");
            store.RestoreFifoConsumedNote(queue, consumedNote!, reviewBucket, reviewNote?.Id);

            AssertTrue(ZetlStateStore.IsFifoBucket(queue), "FIFO undo should restore FIFO kind.");
            AssertEqual("posted", queue.Notes.Single().Text, "FIFO undo should restore consumed note.");
            AssertEqual(0, reviewBucket!.Notes.Count, "FIFO undo should remove the review copy.");
        }

        private static void StateFifoDisablesPopMode()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;

            store.SetBucketPopMode(queue, true);
            AssertTrue(queue.PopMode, "Standard bucket should accept pop mode.");
            store.SetBucketKind(queue, "Replay");
            AssertFalse(queue.PopMode, "Switching to FIFO should turn pop mode off.");
            store.SetBucketPopMode(queue, true);
            AssertFalse(queue.PopMode, "FIFO bucket should reject pop mode.");
        }

        private static void StateMapsLegacyFifoKindToReplay()
        {
            using var temp = new TempStateFile();
            // A state file written by an older build that used the "Fifo" kind.
            var legacyJson =
                """
                { "version": 1, "activeProjectId": "p1", "projects": [ { "id": "p1", "name": "Demo", "activeBucketId": "b1", "buckets": [ { "id": "b1", "name": "Queue", "kind": "Fifo", "defaultKind": "Fifo", "notes": [] } ] } ] }
                """;
            System.IO.File.WriteAllText(temp.Path, legacyJson);

            var store = new ZetlStateStore(temp.Path);
            var queue = store.ActiveBucket!;
            AssertEqual("Queue", queue.Name, "Legacy bucket should load.");
            AssertEqual("Replay", queue.Kind, "Legacy Fifo kind should load as Replay.");
            AssertEqual("Replay", queue.DefaultKind, "Legacy Fifo default kind should load as Replay.");
            AssertTrue(ZetlStateStore.IsFifoBucket(queue), "Legacy Fifo bucket should still be a replay bucket.");
        }

        private static void StateRoundTripsJson()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.ActiveBucket!;
            var note = store.AddNote(bucket, "round trip", "copy");

            var loaded = new ZetlStateStore(temp.Path);
            AssertEqual(project.Id, loaded.ActiveProject?.Id, "Active project id should round-trip.");
            AssertEqual(bucket.Id, loaded.ActiveBucket?.Id, "Active bucket id should round-trip.");
            AssertEqual(note.Id, loaded.ActiveBucket?.Notes.Single().Id, "Note id should round-trip.");
            AssertEqual("round trip", loaded.ActiveBucket?.Notes.Single().Text, "Note text should round-trip.");
        }

        private static void AppSettingsRoundTripFirstRunFlag()
        {
            using var temp = new TempStateFile();
            var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "settings.json");
            var store = new ZetlAppSettingsStore(settingsPath);

            AssertFalse(store.Settings.HasSeenFirstRun, "First-run flag should default to false.");
            store.MarkFirstRunSeen();

            var loaded = new ZetlAppSettingsStore(settingsPath);
            AssertTrue(loaded.Settings.HasSeenFirstRun, "First-run flag should round-trip.");
        }

        private static void AppSettingsRoundTripFields()
        {
            using var temp = new TempStateFile();
            var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "settings.json");
            var store = new ZetlAppSettingsStore(settingsPath);

            AssertEqual(950, store.Settings.ToastDisplayMs, "Toast display should default to 950.");
            AssertTrue(store.Settings.AutoCaptureOnCopy, "Auto-capture should default to on.");

            store.Settings.ToastDisplayMs = 1500;
            store.Settings.AutoCaptureOnCopy = false;
            store.Settings.DefaultProjectBuckets = new List<string> { "Notes", "Scratch" };
            store.Settings.DefaultCompileMode = "TSV";
            store.Settings.DefaultTsvRowLength = 4;
            store.Save();

            var loaded = new ZetlAppSettingsStore(settingsPath);
            AssertEqual(1500, loaded.Settings.ToastDisplayMs, "Toast display should round-trip.");
            AssertFalse(loaded.Settings.AutoCaptureOnCopy, "Auto-capture flag should round-trip.");
            AssertEqual("Notes", loaded.Settings.DefaultProjectBuckets[0], "Default buckets should round-trip.");
            AssertEqual("TSV", loaded.Settings.DefaultCompileMode, "Default compile mode should round-trip.");
            AssertEqual(4, loaded.Settings.DefaultTsvRowLength, "Default TSV row length should round-trip.");
        }

        private static void StateAppliesBucketDefaults()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path)
            {
                Defaults = new ZetlBucketDefaults(new[] { "Notes", "Scratch" }, "TSV", 4)
            };

            var project = store.GetOrCreateDefaultProject();
            var notes = project.Buckets.FirstOrDefault(bucket => bucket.Name == "Notes");
            AssertTrue(notes is not null, "Default project should use the configured buckets.");
            AssertEqual("TSV", notes!.DefaultCompileMode, "New bucket should take the default compile mode.");
            AssertEqual(4, notes.DefaultTsvRowLength, "New bucket should take the default TSV row length.");

            var added = store.AddBucket(project, "Extra");
            AssertEqual("TSV", added.DefaultCompileMode, "Added bucket should take the default compile mode.");
            AssertEqual(4, added.DefaultTsvRowLength, "Added bucket should take the default TSV row length.");
        }

        private static ChordlProcessor CreateProcessor(
            out List<int> dispatched,
            out List<ChordlEventContext> passThrough,
            out List<ChordlEventContext> taps,
            out List<ChordlEventContext> holds,
            bool tapHandled = false)
        {
            dispatched = new List<int>();
            passThrough = new List<ChordlEventContext>();
            taps = new List<ChordlEventContext>();
            holds = new List<ChordlEventContext>();

            var chordlMap = new Dictionary<ChordlChord, ChordlAction>
            {
                [new ChordlChord(VK_B, Ctrl: true, Shift: false)] = new("Ctrl+B", ChordlDispatchMode.TapOnly, ReplayShift: false),
                [new ChordlChord(VK_C, Ctrl: true, Shift: false)] = new("Ctrl+C", ChordlDispatchMode.None, ReplayShift: false),
                [new ChordlChord(VK_C, Ctrl: true, Shift: true)] = new("Ctrl+Shift+C", ChordlDispatchMode.None, ReplayShift: false),
                [new ChordlChord(VK_P, Ctrl: true, Shift: false)] = new("Ctrl+P", ChordlDispatchMode.TapOnly, ReplayShift: false),
                [new ChordlChord(VK_R, Ctrl: true, Shift: false)] = new("Ctrl+R", ChordlDispatchMode.TapOnly, ReplayShift: false),
                [new ChordlChord(VK_V, Ctrl: true, Shift: false)] = new("Ctrl+V", ChordlDispatchMode.TapOnly, ReplayShift: false),
                [new ChordlChord(VK_Z, Ctrl: true, Shift: false)] = new("Ctrl+Z", ChordlDispatchMode.TapOnly, ReplayShift: false)
            };
            var keys = chordlMap.Keys.Select(chord => chord.KeyCode).ToHashSet();

            var localDispatched = dispatched;
            var localPassThrough = passThrough;
            var localTaps = taps;
            var localHolds = holds;
            return new ChordlProcessor(
                chordlMap,
                keys,
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(60),
                (key, _, _, _) => localDispatched.Add(key),
                localPassThrough.Add,
                context =>
                {
                    localTaps.Add(context);
                    return tapHandled;
                },
                localHolds.Add,
                _ => { },
                () => 0);
        }

        private static void AssertTrue(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertFalse(bool value, string message)
        {
            if (value)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
            }
        }

        private sealed class TempStateFile : IDisposable
        {
            private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZetlTests", Guid.NewGuid().ToString("N"));

            public TempStateFile()
            {
                Directory.CreateDirectory(directory);
                Path = System.IO.Path.Combine(directory, "state.json");
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}
