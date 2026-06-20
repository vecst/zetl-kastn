using Chordl;
using static Chordl.ChordlKeys;

namespace ZETL.Tests;

internal static class PortableSelfTests
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
                ("Fresh target key passes after Ctrl-up repeat guard", FreshTargetKeyPassesAfterCtrlUpRepeatGuard),
                ("Ctrl+B tap dispatches board shortcut on key-up", BoardTapDispatchesOnKeyUp),
                ("Ctrl+B hold opens board without dispatch", BoardHoldDoesNotDispatch),
                ("Ctrl+P tap dispatches pop key on key-up", PopToggleTapDispatchesOnKeyUp),
                ("Ctrl+P hold raises Pop toggle", PopToggleHoldDoesNotDispatch),
                ("Ctrl+R tap dispatches replay key on key-up", FifoToggleTapDispatchesOnKeyUp),
                ("Ctrl+R hold raises Replay toggle", FifoToggleHoldDoesNotDispatch),
                ("Ctrl+Z hold raises Zetl undo", UndoHoldDoesNotDispatch),
                ("Shift changes restart hold detection", ShiftChangeRestartsHold),
                ("Shift repeat does not restart hold detection", ShiftRepeatDoesNotRestartHold),
                ("Shift change after hold does not dispatch twice", ShiftChangeAfterHoldDoesNotDispatchTwice),
                ("Synthetic modifier injection uses an unheld side", SyntheticModifierUsesUnheldSide),
                ("Chord injection suppresses a held Shift for a plain chord", ChordInjectionSuppressesHeldShiftForPlainChord),
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
                ("Zetl compiles across projects without changing the active project", StateCompilesToOtherProjectWithoutChangingActive),
                ("Zetl adds notes preserving structure", StateAddsNotesPreservingStructure),
                ("Zetl state preserves bucket settings", StatePreservesBucketSettings),
                ("Zetl state protects the Scratch bucket", StateProtectsScratchBucket),
                ("Zetl state protects the Deleted bucket", StateProtectsDeletedBucket),
                ("Zetl state deletes projects and repairs active lanes", StateDeletesProjectsAndRepairsActiveLanes),
                ("Zetl state deletes bucket trees and repairs pointers", StateDeletesBucketTreesAndRepairsPointers),
                ("Zetl state deletes notes", StateDeletesNotes),
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
                ("Zetl state finds the most recently written project", StateFindsMostRecentlyWrittenProject),
                ("Zetl state round-trips JSON", StateRoundTripsJson),
                ("Zetl state stores each project in its own folder", StateStoresEachProjectInItsOwnFolder),
                ("Zetl state migrates a legacy single state file", StateMigratesLegacySingleFile),
                ("Zetl state migration tolerates a backup rename failure", StateMigrationToleratesBackupRenameFailure),
                ("Zetl json writes do not collide under concurrent writers", JsonFileConcurrentWritesDoNotCollide),
                ("Zetl json parse errors name the damaged file", JsonFileReadNamesDamagedFile),
                ("Zetl json read-or-quarantine moves corrupt files aside", JsonFileQuarantinesCorruptFile),
                ("Zetl state skips a corrupt project and keeps the rest", StateSkipsCorruptProjectFile),
                ("Zetl state recovers from a corrupt workspace file", StateRecoversFromCorruptWorkspace),
                ("Zetl state skips an unreadable project and keeps the rest", StateSkipsUnreadableProjectFile),
                ("Zetl state appends activity-log notes without activating", StateAppendsLogNotesWithoutActivating),
                ("Zetl app settings round-trip first-run flag", AppSettingsRoundTripFirstRunFlag),
                ("Zetl app settings round-trip configurable fields", AppSettingsRoundTripFields),
                ("Zetl app settings recover from a corrupt file", AppSettingsRecoverFromCorruptFile),
                ("Zetl app settings recover from an unreadable file", AppSettingsRecoverFromUnreadableFile),
                ("Zetl built-in theme validates", ThemeDefaultsValidate),
                ("Zetl Dusk built-in theme validates", ThemeDuskValidates),
                ("Zetl built-in presets all validate", ThemeBuiltInPresetsValidate),
                ("Zetl themes round-trip custom values", ThemeRoundTripsCustomValues),
                ("Zetl themes preserve unknown JSON fields", ThemePreservesUnknownJsonFields),
                ("Zetl theme store ignores invalid files", ThemeStoreIgnoresInvalidFiles),
                ("Zetl state applies bucket defaults", StateAppliesBucketDefaults),
                ("Zetl default hotkeys config parses", DefaultConfigParses),
                ("Zetl config tolerates null replay modifiers", ConfigNullReplayModifiersDoesNotThrow),
                ("Zetl config reports clean errors for null fields", ConfigNullFieldsReportCleanErrors),
                ("Runtime applies app settings defaults", RuntimeAppliesAppSettingsDefaults),
                ("Runtime undo stack keeps lanes separate", RuntimeUndoStackKeepsLanesSeparate),
                ("Runtime activity log buffer drains safely", RuntimeActivityLogBufferDrainsSafely),
                ("Runtime auto-captures copied text", RuntimeAutoCapturesCopiedText),
                ("Runtime hold cancellation prevents auto-capture", RuntimeHoldCancellationPreventsAutoCapture),
                ("Runtime claimed hold prevents delayed auto-capture", RuntimeClaimedHoldPreventsDelayedAutoCapture),
                ("Runtime claimed copy hold resolves without polling", RuntimeClaimedCopyHoldResolvesWithoutPolling),
                ("Runtime Replay tap consumes and restores clipboard", RuntimeReplayTapConsumesAndRestoresClipboard),
                ("Runtime Replay tap defers clipboard work off the hook", RuntimeReplayTapDefersClipboardWorkOffHook),
                ("Runtime Replay tap keeps the note when the paste fails", RuntimeReplayTapKeepsNoteWhenPasteFails),
                ("Runtime empty Replay reports a failed final paste", RuntimeEmptyReplayReportsFinalPasteFailure),
                ("Runtime logged fire-and-forget records async failures", RuntimeRunLoggedRecordsAsyncFailure),
                ("Runtime logged fire-and-forget records delayed async failures", RuntimeRunLoggedRecordsDelayedAsyncFailure),
                ("Runtime Pop tap removes matching note", RuntimePopTapRemovesMatchingNote),
                ("Runtime copy hold creates note request", RuntimeCopyHoldCreatesNoteRequest),
                ("Runtime empty copy hold opens Board", RuntimeEmptyCopyHoldOpensBoard),
                ("Runtime cut hold defaults to Scratch", RuntimeCutHoldDefaultsToScratch),
                ("Runtime template hold requests the picker", RuntimeTemplateHoldRequestsPicker),
                ("Runtime compile hold without a project requests the picker", RuntimeCompileHoldWithoutProjectRequestsPicker),
                ("Runtime compile hold with an active project stays compile", RuntimeCompileHoldWithActiveProjectStaysCompile),
                ("Runtime hold toggles and undo stay portable", RuntimeHoldTogglesAndUndoStayPortable),
                ("Runtime completes quick-note result", RuntimeCompletesQuickNoteResult),
                ("Runtime pastes cut back when held cut is discarded", RuntimePastesCutBackOnDiscardedCut),
                ("Runtime files quick note into the selected project", RuntimeFilesQuickNoteIntoSelectedProject),
                ("Runtime redirects quick note with no active project", RuntimeRedirectsQuickNoteWithNoActiveProject),
                ("Runtime activates the selected project from a quick note", RuntimeActivatesSelectedProjectFromQuickNote),
                ("Runtime deactivates the active project when toggled off", RuntimeDeactivatesActiveProjectWhenToggledOff),
                ("Runtime creates a new project from the capture dialog", RuntimeCreatesNewProjectFromCapture),
                ("Runtime completes flattened compile result", RuntimeCompletesCompileResult),
                ("Runtime preserves structured compile saves", RuntimePreservesStructuredCompileSaves),
                ("Runtime returns copy and paste compile outcomes", RuntimeReturnsCopyAndPasteCompileOutcomes),
                ("Runtime compile does not paste when the clipboard write fails", RuntimeCompileDoesNotPasteWhenClipboardWriteFails),
                ("Runtime reports rejected compiled paste", RuntimeReportsRejectedCompiledPaste),
                ("Runtime parity scenario writes a reloadable snapshot", RuntimeParityScenarioWritesSnapshot),
                ("Kastn command envelope round-trips", KastnContractTests.CommandEnvelopeRoundTrips),
                ("Kastn command validation enforces mutation scope", KastnContractTests.ValidationEnforcesMutationScope),
                ("Kastn unsupported protocol has a distinct result", KastnContractTests.UnsupportedProtocolIsDistinct),
                ("Kastn snapshot keeps revision scopes separate", KastnContractTests.SnapshotKeepsRevisionScopesSeparate),
                ("Kastn conflict carries the current record", KastnContractTests.ConflictCarriesCurrentRecord),
                ("Kastn mutation contracts expose no storage paths", KastnContractTests.MutationContractsExposeNoStoragePaths),
                ("Project service deduplicates retried adds", ZetlProjectServiceTests.RetriedAddDoesNotDuplicate),
                ("Project service returns current slip for stale edits", ZetlProjectServiceTests.StaleEditReturnsCurrentSlip),
                ("Project service allows rename after unrelated capture", ZetlProjectServiceTests.UnrelatedCaptureDoesNotConflictWithRename),
                ("Project service serializes concurrent adds", ZetlProjectServiceTests.ConcurrentAddsAreSerialized),
                ("Project service shares one writer with existing mutations", ZetlProjectServiceTests.DirectAndServiceMutationsShareOneWriter),
                ("Project service bucket and slip commands round-trip", ZetlProjectServiceTests.BucketAndSlipCommandsRoundTrip),
                ("Project service reorders slips within a bucket", ZetlProjectServiceTests.ReorderSlipMovesWithinBucket),
                ("Kastn built-in templates are well-formed", KastnTemplateCatalogTests.BuiltInTemplatesAreValid),
                ("Kastn template creates a project through Zetl", KastnTemplateCatalogTests.TemplateCreatesProjectThroughService),
                ("Kastn consumable template seeds an ordered Replay queue", KastnTemplateCatalogTests.ConsumableTemplateSeedsOrderedReplayQueue),
                ("Kastn blank template creates a minimal project", KastnTemplateCatalogTests.BlankTemplateCreatesMinimalProject),
                ("Template documents round-trip through JSON", ZetlTemplateDocumentTests.BuiltInsRoundTripThroughJson),
                ("Template documents preserve future versions and unknown fields", ZetlTemplateDocumentTests.FutureVersionAndUnknownFieldsArePreserved),
                ("Template validation reports actionable errors", ZetlTemplateDocumentTests.InvalidTemplatesReportActionableErrors),
                ("Template store loads built-ins without a directory", ZetlTemplateStoreTests.LoadsBuiltInsWhenNoDirectory),
                ("Template store loads valid user templates and skips bad ones", ZetlTemplateStoreTests.LoadsValidUserTemplatesAndSkipsBadOnes),
                ("Template store drops a removed user template after refresh", ZetlTemplateStoreTests.RemovingAFileDropsTheTemplateAfterRefresh),
                ("Template store saves user templates and refuses built-in ids", ZetlTemplateStoreTests.SaveRoundTripsAndRefusesBuiltInIds),
                ("Template duplicate makes an independent editable copy", ZetlTemplateStoreTests.DuplicateMakesAnIndependentEditableCopy),
                ("Template store deletes user templates but not built-ins", ZetlTemplateStoreTests.DeleteRemovesUserTemplatesButNotBuiltIns),
                ("View built-ins are valid and round-trip", ZetlViewTests.BuiltInViewsAreValidAndRoundTrip),
                ("View validation reports errors", ZetlViewTests.InvalidViewsReportErrors),
                ("View renderer formats nested buckets and slips", ZetlViewTests.RendererFormatsNestedBucketsAndSlips),
                ("View renderer builds TSV rows using bucket headers", ZetlViewTests.RendererBuildsTsvRowsUsingBucketHeaders),
                ("View renderer builds escaped HTML", ZetlViewTests.RendererBuildsEscapedHtml),
                ("View store loads, saves, and deletes user views", ZetlViewTests.StoreLoadsSavesAndDeletesUserViews),
                ("View sections rename, reorder, and omit buckets", ZetlViewTests.SectionsRenameReorderAndOmitBuckets),
                ("Project remembers its default view across reload", ZetlViewTests.ProjectRemembersDefaultViewAcrossReload),
                ("Creation type store loads, saves, and deletes", ZetlViewTests.CreationTypeStoreLoadsSavesAndDeletes),
                ("View PDF renderer produces a PDF document", ZetlViewTests.PdfRendererProducesAPdfDocument),
                ("Project service project and bucket commands honor revisions", ZetlProjectServiceTests.ProjectAndBucketCommandsHonorRevisions),
                ("Project service persists revisions", ZetlProjectServiceTests.RevisionsPersistAcrossReload),
                ("Project service publishes detailed durable events", ZetlProjectServiceTests.SuccessfulMutationPublishesOneDetailedEvent),
                ("Project service publishes no event for rejected mutations", ZetlProjectServiceTests.FailedMutationPublishesNoEvent),
                ("Project service subscriber failures do not change acknowledgement", ZetlProjectServiceTests.SubscriberFailureDoesNotChangeAcknowledgement),
                ("Project service publishes direct capture changes", ZetlProjectServiceTests.DirectCapturePublishesProjectChange),
                ("Project service lists cheap preview text", ZetlProjectServiceTests.ListProjectsIncludesCheapPreviewText),
                ("IPC client lists, opens, and mutates a project", ZetlIpcTests.ClientListsOpensAndMutatesProject),
                ("IPC two clients receive ordered changes", ZetlIpcTests.TwoClientsReceiveOrderedChanges),
                ("IPC malformed message does not stop server", ZetlIpcTests.MalformedMessageDoesNotStopServer),
                ("IPC protocol mismatch is rejected without stopping server", ZetlIpcTests.ProtocolMismatchIsRejectedWithoutStoppingServer),
                ("IPC oversized frame is rejected", ZetlIpcTests.OversizedFrameIsRejected),
                ("IPC disconnected client does not affect capture or peers", ZetlIpcTests.DisconnectedClientDoesNotAffectCaptureOrPeers),
                ("IPC real Zetl process hosts and persists commands", ZetlIpcTests.RealZetlProcessHostsIpc),
                ("Kastn activation handoff carries project ID", KastnLifecycleTests.ActivationHandoffCarriesProjectId),
                ("Kastn launches Zetl and loads requested project", KastnLifecycleTests.ControllerLaunchesZetlAndLoadsProject),
                ("Kastn launches to project selection without handoff", KastnLifecycleTests.ControllerLaunchesToProjectSelectionWithoutHandoff),
                ("Kastn refreshes after project changes", KastnLifecycleTests.ControllerRefreshesAfterProjectChange),
                ("Kastn reconnects after Zetl restarts", KastnLifecycleTests.ControllerReconnectsAfterZetlRestart),
                ("Kastn relaunches Zetl after it exits", KastnLifecycleTests.ControllerRelaunchesZetlAfterItExits),
                ("Closing Kastn leaves Zetl available", KastnLifecycleTests.ClosingControllerLeavesZetlAvailable),
                ("Kastn filters preserve order and hierarchy", KastnWorkbenchTests.FiltersPreserveSnapshotOrderAndHierarchy),
                ("Kastn dirty editor survives unrelated changes", KastnWorkbenchTests.DirtyEditorSurvivesUnrelatedChanges),
                ("Kastn same-slip changes require resolution", KastnWorkbenchTests.SameSlipChangesRequireExplicitResolution),
                ("Kastn viewer formats visible slips", KastnWorkbenchTests.ViewerFormatsVisibleSlipsAsReadableOutline),
                ("Kastn organizes through Zetl commands", KastnWorkbenchTests.CommandsOrganizeThroughZetl)
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

        Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} portable tests passed.");
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
            AssertTrue(
                processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false, isRepeat: true),
                "Post-Ctrl target repeat should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held paste should not dispatch paste.");
        }

        private static void FreshTargetKeyPassesAfterCtrlUpRepeatGuard()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            Thread.Sleep(80);
            AssertEqual(1, holds.Count, "Hold callback should fire once.");
            AssertFalse(processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true), "Ctrl key up should pass through.");
            AssertTrue(
                processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false, isRepeat: true),
                "Post-Ctrl target repeat should suppress.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
            AssertFalse(
                processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false, isRepeat: false),
                "Fresh target key press after the original key-up should pass through.");
            AssertFalse(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Fresh target key-up should pass through.");
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

        private static void ShiftRepeatDoesNotRestartHold()
        {
            using var processor = CreateProcessor(out _, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            processor.HandleKeyEvent(VK_C, isKeyDown: true, isKeyUp: false);
            Thread.Sleep(20);
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
            Thread.Sleep(45);
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
            Thread.Sleep(35);
            AssertEqual(1, holds.Count, "Shift autorepeat should not postpone the restarted hold.");
            AssertEqual("Ctrl+Shift+C", holds[0].Name, "Hold should retain the shifted chord.");
            processor.HandleKeyEvent(VK_C, isKeyDown: false, isKeyUp: true);
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: false, isKeyUp: true);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void ShiftChangeAfterHoldDoesNotDispatchTwice()
        {
            using var processor = CreateProcessor(
                out var dispatched,
                out _,
                out var taps,
                out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            processor.HandleKeyEvent(VK_B, isKeyDown: true, isKeyUp: false);
            Thread.Sleep(90);
            AssertEqual(1, holds.Count, "Initial hold should fire once.");
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: true, isKeyUp: false);
            Thread.Sleep(90);
            processor.HandleKeyEvent(VK_B, isKeyDown: false, isKeyUp: true);
            AssertEqual(1, holds.Count, "Modifier changes after a hold must not fire another hold.");
            AssertEqual(0, taps.Count, "Modifier changes after a hold must not turn it into a tap.");
            AssertEqual(0, dispatched.Count, "Modifier changes after a hold must not replay the shortcut.");
            processor.HandleKeyEvent(VK_SHIFT, isKeyDown: false, isKeyUp: true);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void SyntheticModifierUsesUnheldSide()
        {
            AssertEqual<int?>(
                VK_LCONTROL,
                ZetlSyntheticModifier.SelectInjection(
                    leftDown: false,
                    rightDown: false,
                    VK_LCONTROL,
                    VK_RCONTROL),
                "With neither side held, injection should use left Ctrl.");
            AssertEqual<int?>(
                VK_RCONTROL,
                ZetlSyntheticModifier.SelectInjection(
                    leftDown: true,
                    rightDown: false,
                    VK_LCONTROL,
                    VK_RCONTROL),
                "With left held, injection should use right Ctrl.");
            AssertEqual<int?>(
                VK_LCONTROL,
                ZetlSyntheticModifier.SelectInjection(
                    leftDown: false,
                    rightDown: true,
                    VK_LCONTROL,
                    VK_RCONTROL),
                "With right held, injection should use left Ctrl.");
            AssertEqual<int?>(
                null,
                ZetlSyntheticModifier.SelectInjection(
                    leftDown: true,
                    rightDown: true,
                    VK_LCONTROL,
                    VK_RCONTROL),
                "With both sides held, no synthetic modifier is needed.");
        }

        private static void ChordInjectionSuppressesHeldShiftForPlainChord()
        {
            // Shift-lane copy/cut replays a plain Ctrl+C/Ctrl+X while the user
            // physically holds Ctrl+Shift. A held Shift must be released for the
            // chord (otherwise the app sees Ctrl+Shift+key, not a copy/cut) and
            // restored afterwards.
            var sequence = ZetlChordInjection.BuildCtrlChord(
                VK_C,
                includeShift: false,
                leftCtrlDown: true,
                rightCtrlDown: false,
                leftShiftDown: true,
                rightShiftDown: false);

            var keyDownIndex = -1;
            var keyUpAfterDownIndex = -1;
            for (var i = 0; i < sequence.Count; i++)
            {
                if (sequence[i].VirtualKey == VK_C && !sequence[i].KeyUp)
                {
                    keyDownIndex = i;
                }
                else if (sequence[i].VirtualKey == VK_C
                    && sequence[i].KeyUp
                    && keyDownIndex >= 0
                    && keyUpAfterDownIndex < 0)
                {
                    keyUpAfterDownIndex = i;
                }
            }

            AssertTrue(keyDownIndex >= 0, "The chord must press the target key.");
            AssertTrue(keyUpAfterDownIndex > keyDownIndex, "The chord must release the target key.");

            var shiftDownDuringPress = false;
            for (var i = 0; i < keyDownIndex; i++)
            {
                if (IsShiftKey(sequence[i].VirtualKey))
                {
                    shiftDownDuringPress = !sequence[i].KeyUp;
                }
            }

            AssertTrue(
                !shiftDownDuringPress,
                "A held Shift must be released before a plain Ctrl chord so it is not Ctrl+Shift+key.");

            var shiftRestoredAfter = false;
            for (var i = keyUpAfterDownIndex + 1; i < sequence.Count; i++)
            {
                if (IsShiftKey(sequence[i].VirtualKey) && !sequence[i].KeyUp)
                {
                    shiftRestoredAfter = true;
                }
            }

            AssertTrue(shiftRestoredAfter, "A suppressed Shift must be restored after the chord.");

            // Sanity: a shifted chord with Shift already held keeps it down.
            var shifted = ZetlChordInjection.BuildCtrlChord(
                VK_C,
                includeShift: true,
                leftCtrlDown: true,
                rightCtrlDown: true,
                leftShiftDown: true,
                rightShiftDown: true);
            foreach (var keyEvent in shifted)
            {
                AssertTrue(
                    !(IsShiftKey(keyEvent.VirtualKey) && keyEvent.KeyUp),
                    "A shifted chord with both Shifts held must not release Shift.");
            }
        }

        private static bool IsShiftKey(int virtualKey)
        {
            return virtualKey == VK_LSHIFT || virtualKey == VK_RSHIFT;
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

        private static void StateFindsMostRecentlyWrittenProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var older = store.CreateProject("Older", ["Inbox"], "Inbox");
            var newer = store.CreateProject("Newer", ["Inbox"], "Inbox");
            var olderNote = store.AddNote(older.Buckets.First(), "old", "copy");
            var newerNote = store.AddNote(newer.Buckets.First(), "new", "copy");
            olderNote.CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            newerNote.CreatedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            AssertEqual("Newer", store.GetMostRecentlyWrittenProject()?.Name, "The project with the latest note should win.");

            // The Zetl Logs infra project is appended to constantly but must
            // never be chosen as the last-written project.
            store.AppendLogNotes(["log line"], maxDayBuckets: 14, maxNotesPerBucket: 2000);
            AssertEqual("Newer", store.GetMostRecentlyWrittenProject()?.Name, "Zetl Logs must be excluded from the last-written project.");
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

        private static void DefaultConfigParses()
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "hotkeys.json");
            var config = ChordlConfigLoader.LoadFromFile(configPath);
            AssertTrue(config.Actions.Count > 0, "Default config should define at least one hotkey.");
            AssertTrue(config.HoldDelay > TimeSpan.Zero, "Default config should define a positive hold delay.");
        }

        private static void ConfigNullReplayModifiersDoesNotThrow()
        {
            var json =
                """
                { "repeatSuppressionDelayMs": 33, "holdDelayMs": 353, "hotkeys": [ { "name": "Copy", "key": "C", "modifiers": ["Ctrl"], "dispatch": "None", "replayModifiers": null } ] }
                """;

            var config = ChordlConfigLoader.LoadFromJson(json);

            AssertEqual(1, config.Actions.Count, "A null replayModifiers should normalize to the default rather than crash.");
            AssertFalse(config.Actions.Values.Single().ReplayShift, "Normalized replay modifiers should not request Shift.");
        }

        private static void ConfigNullFieldsReportCleanErrors()
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

        private static void AssertConfigRejected(string json, string expectedFragment, string because)
        {
            try
            {
                ChordlConfigLoader.LoadFromJson(json);
                AssertTrue(false, $"{because} (expected InvalidOperationException, but none was thrown)");
            }
            catch (InvalidOperationException ex)
            {
                AssertTrue(
                    ex.Message.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase),
                    $"{because} Got: {ex.Message}");
            }
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

        private static void StateAddsNotesPreservingStructure()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.ActiveBucket!;

            var added = store.AddNotes(bucket, ["one", "   ", "two", "three"], "compile");

            AssertEqual(3, added.Count, "AddNotes should skip blank entries.");
            AssertEqual(3, bucket.Notes.Count, "Each non-blank text should become its own note.");
            AssertEqual("one", bucket.Notes[0].Text, "Notes should keep insertion order.");
            AssertEqual("three", bucket.Notes[2].Text, "Notes should keep insertion order.");
            AssertEqual("compile", bucket.Notes[0].Source, "AddNotes should set the note source.");
        }

        private static void StateCompilesToOtherProjectWithoutChangingActive()
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
            store.AddNote(destination, "compiled text", "compile");

            AssertEqual(source.Id, store.State.ActiveProjectId, "Compiling should not change which project is active.");
            AssertEqual(destActiveBucketBefore, dest.ActiveBucketId, "Compiling into another project should not change its active bucket.");
            AssertTrue(dest.Buckets.Any(bucket => bucket.Name == "Compiled"), "Compile destination bucket should be created in the destination project.");
            AssertEqual("compiled text", destination.Notes.Single().Text, "Compiled note should land in the destination bucket.");
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
            AssertTrue(compiled.Contains($"{Environment.NewLine}\tfirst"), "First selected note should compile indented under its bucket.");
            AssertFalse(compiled.Contains("second"), "Unselected note should not compile.");
            AssertTrue(compiled.Contains("Ideas"), "Selected ideas note should include its bucket heading.");
            AssertTrue(compiled.Contains($"{Environment.NewLine}\tthird"), "Second selected note should compile indented under its bucket.");
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

        private static void StateProtectsDeletedBucket()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var inbox = store.ActiveBucket!;
            var deleted = store.GetDeletedBucket(project);
            store.AddNote(deleted, "removed", "kastn-delete");

            AssertEqual("Deleted", deleted.Name, "Deleted bucket should have a readable name.");
            AssertEqual("Deleted", deleted.Kind, "Deleted bucket should use a protected kind.");
            AssertTrue(project.Buckets.Any(bucket => bucket.Id == deleted.Id), "Deleted bucket should remain human-readable in project JSON.");
            AssertFalse(store.GetBucketDisplayItems(project).Any(item => item.Bucket.Id == deleted.Id), "Normal bucket lists should hide Deleted.");
            AssertTrue(store.GetBucketDisplayItems(project, includeDeleted: true).Any(item => item.Bucket.Id == deleted.Id), "Explicit bucket lists may show Deleted.");
            AssertFalse(store.HasCompilableNotes(project), "Deleted notes should not make a project compilable.");
            AssertFalse(store.CompilePlainText(project, [deleted]).Contains("removed"), "Bucket-based compile should skip Deleted.");

            store.SetActiveBucket(project, deleted.Id);
            AssertEqual(inbox.Id, store.ActiveBucket?.Id, "Deleted should not become the active capture bucket.");
            store.SetQuickNoteBucket(project, deleted.Id);
            AssertEqual("Scratch", store.GetQuickNoteBucket(project).Name, "Deleted should not become the quick-note bucket.");

            store.UpdateBucketName(deleted, "Trash");
            store.SetBucketKind(deleted, "Replay");
            store.SetBucketPopMode(deleted, true);
            store.DeleteBucket(project, deleted.Id);
            AssertEqual("Deleted", deleted.Name, "Deleted should not be renamable.");
            AssertEqual("Deleted", deleted.Kind, "Deleted should not change kind.");
            AssertFalse(deleted.PopMode, "Deleted should not enable Pop.");
            AssertTrue(project.Buckets.Any(bucket => bucket.Id == deleted.Id), "Deleted should not be deletable.");

            var loaded = new ZetlStateStore(temp.Path);
            var loadedProject = loaded.State.Projects.Single(project => project.Name == "Demo");
            var loadedDeleted = loadedProject.Buckets.Single(bucket => bucket.Id == deleted.Id);
            AssertEqual("Deleted", loadedDeleted.Name, "Deleted name should persist.");
            AssertEqual("Deleted", loadedDeleted.Kind, "Deleted kind should persist.");
            AssertFalse(loaded.GetBucketDisplayItems(loadedProject).Any(item => item.Bucket.Id == loadedDeleted.Id), "Reloaded normal bucket lists should hide Deleted.");
        }

        private static void StateDeletesProjectsAndRepairsActiveLanes()
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

        private static void StateDeletesBucketTreesAndRepairsPointers()
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

        private static void StateDeletesNotes()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.ActiveBucket!;
            var keep = store.AddNote(bucket, "keep", "copy");
            var remove = store.AddNote(bucket, "remove", "copy");

            store.DeleteNote(bucket, remove.Id);

            AssertEqual(1, bucket.Notes.Count, "Deleting a note should remove only the selected note.");
            AssertEqual(keep.Id, bucket.Notes.Single().Id, "Unselected notes should remain.");

            var loaded = new ZetlStateStore(temp.Path);
            AssertEqual(keep.Id, loaded.ActiveBucket!.Notes.Single().Id, "Note deletion should persist.");
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

        private static void StateStoresEachProjectInItsOwnFolder()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Alpha", ["Inbox"], "Inbox");
            store.CreateProject("Beta", ["Inbox"], "Inbox");

            var root = System.IO.Path.GetDirectoryName(temp.Path)!;
            var projectsDirectory = System.IO.Path.Combine(root, "projects");
            AssertTrue(File.Exists(System.IO.Path.Combine(root, "workspace.json")), "Workspace pointer file should be written.");
            AssertFalse(File.Exists(temp.Path), "No monolithic state.json should be written under the new layout.");

            var projectFiles = Directory.GetFiles(projectsDirectory, "project.json", SearchOption.AllDirectories);
            AssertEqual(2, projectFiles.Length, "Each project should get its own project.json.");
            AssertTrue(
                Directory.GetDirectories(projectsDirectory).Any(dir => System.IO.Path.GetFileName(dir).StartsWith("Alpha-", StringComparison.Ordinal)),
                "Project folder should be named from the project name plus a short id.");

            var loaded = new ZetlStateStore(temp.Path);
            AssertEqual(2, loaded.State.Projects.Count, "Projects should reload from their per-project folders.");
            AssertTrue(loaded.State.Projects.Any(project => project.Name == "Beta"), "Reloaded projects should keep their names.");
        }

        private static void StateMigratesLegacySingleFile()
        {
            using var temp = new TempStateFile();
            var legacyJson =
                """
                { "version": 1, "activeProjectId": "p1", "projects": [ { "id": "p1", "name": "Legacy", "activeBucketId": "b1", "buckets": [ { "id": "b1", "name": "Inbox", "kind": "Standard", "notes": [ { "id": "n1", "text": "carried over", "source": "copy" } ] } ] } ] }
                """;
            File.WriteAllText(temp.Path, legacyJson);

            var store = new ZetlStateStore(temp.Path);
            var root = System.IO.Path.GetDirectoryName(temp.Path)!;

            AssertFalse(File.Exists(temp.Path), "Legacy state.json should be moved aside after migration.");
            AssertTrue(File.Exists(temp.Path + ".bak"), "Legacy state.json should be archived as a .bak backup.");
            AssertTrue(File.Exists(System.IO.Path.Combine(root, "workspace.json")), "Migration should write the workspace pointer file.");
            AssertEqual(
                1,
                Directory.GetFiles(System.IO.Path.Combine(root, "projects"), "project.json", SearchOption.AllDirectories).Length,
                "Migration should split the legacy project into its own file.");
            AssertEqual("Legacy", store.ActiveProject?.Name, "Migrated active project should load.");
            AssertEqual("carried over", store.ActiveBucket?.Notes.Single().Text, "Migrated note should survive the split.");

            var reloaded = new ZetlStateStore(temp.Path);
            AssertEqual("Legacy", reloaded.State.Projects.Single().Name, "Migrated project should reload from the new layout.");
        }

        private static void JsonFileConcurrentWritesDoNotCollide()
        {
            using var temp = new TempStateFile();
            // Another writer mid-write used to hold this exact temp name,
            // which made WriteAtomic throw a sharing violation.
            using var heldTemp = new FileStream(
                temp.Path + ".tmp",
                FileMode.Create,
                FileAccess.Write,
                FileShare.None);

            JsonFile.WriteAtomic(temp.Path, new[] { "first" });
            AssertTrue(File.Exists(temp.Path), "Write should land while another writer holds the shared temp name.");

            JsonFile.WriteAtomic(temp.Path, new[] { "second" });
            AssertEqual(
                "second",
                JsonFile.Read<string[]>(temp.Path)?.Single(),
                "Replacing an existing file should also ignore the held temp name.");
        }

        private static void JsonFileReadNamesDamagedFile()
        {
            using var temp = new TempStateFile();
            File.WriteAllText(temp.Path, "{ this is not json");
            try
            {
                JsonFile.Read<string[]>(temp.Path);
                AssertTrue(false, "Reading a damaged file should throw.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                AssertTrue(
                    ex.Message.Contains(temp.Path),
                    $"Parse errors should name the damaged file. Got: {ex.Message}");
            }
        }

        private static void JsonFileQuarantinesCorruptFile()
        {
            using var temp = new TempStateFile();
            var directory = System.IO.Path.GetDirectoryName(temp.Path)!;
            File.WriteAllText(temp.Path, "{ not json");

            var result = JsonFile.ReadOrQuarantine<string[]>(temp.Path);

            AssertTrue(result is null, "Reading a corrupt file should return default instead of throwing.");
            AssertFalse(File.Exists(temp.Path), "The corrupt file should be moved aside.");
            AssertEqual(
                1,
                Directory.GetFiles(directory, "state.json.corrupt-*").Length,
                "ReadOrQuarantine should leave one quarantined copy.");
        }

        private static void StateSkipsCorruptProjectFile()
        {
            using var temp = new TempStateFile();
            var projectsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "projects");
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("KeepMe", ["Inbox"], "Inbox");
            store.CreateProject("BreakMe", ["Inbox"], "Inbox");

            var corruptFile = Directory
                .GetFiles(projectsDir, "project.json", SearchOption.AllDirectories)
                .Single(path => System.IO.Path
                    .GetFileName(System.IO.Path.GetDirectoryName(path)!)
                    .StartsWith("BreakMe", StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(corruptFile, "{ not valid json");

            var reloaded = new ZetlStateStore(temp.Path);

            AssertEqual(1, reloaded.State.Projects.Count, "Only the valid project should load; the corrupt one is skipped.");
            AssertEqual("KeepMe", reloaded.State.Projects.Single().Name, "A valid project should survive a sibling's corruption.");
            AssertFalse(File.Exists(corruptFile), "The corrupt project.json should be moved aside.");
            AssertEqual(
                1,
                Directory.GetFiles(System.IO.Path.GetDirectoryName(corruptFile)!, "project.json.corrupt-*").Length,
                "The corrupt project.json should be quarantined in place.");
        }

        private static void StateRecoversFromCorruptWorkspace()
        {
            using var temp = new TempStateFile();
            var root = System.IO.Path.GetDirectoryName(temp.Path)!;
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Survivor", ["Inbox"], "Inbox");

            var workspacePath = System.IO.Path.Combine(root, "workspace.json");
            File.WriteAllText(workspacePath, "{ broken");

            var reloaded = new ZetlStateStore(temp.Path);

            AssertEqual("Survivor", reloaded.State.Projects.Single().Name, "Projects should still load when workspace.json is corrupt.");
            AssertFalse(File.Exists(workspacePath), "The corrupt workspace.json should be moved aside.");
            AssertEqual(
                1,
                Directory.GetFiles(root, "workspace.json.corrupt-*").Length,
                "The corrupt workspace.json should be quarantined.");
        }

        private static void StateSkipsUnreadableProjectFile()
        {
            using var temp = new TempStateFile();
            var projectsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "projects");
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("KeepMe", ["Inbox"], "Inbox");
            store.CreateProject("LockMe", ["Inbox"], "Inbox");

            var lockedFile = Directory
                .GetFiles(projectsDir, "project.json", SearchOption.AllDirectories)
                .Single(path => System.IO.Path
                    .GetFileName(System.IO.Path.GetDirectoryName(path)!)
                    .StartsWith("LockMe", StringComparison.OrdinalIgnoreCase));

            // Hold the file open with no sharing so the next read fails with an
            // IOException rather than parsing as corrupt.
            using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var reloaded = new ZetlStateStore(temp.Path);

                AssertEqual(1, reloaded.State.Projects.Count, "An unreadable project should be skipped, not abort the load.");
                AssertEqual("KeepMe", reloaded.State.Projects.Single().Name, "Valid projects should still load past an unreadable sibling.");
            }

            AssertTrue(File.Exists(lockedFile), "An unreadable (not corrupt) file must be left in place, not quarantined.");
        }

        private static void AppSettingsRecoverFromUnreadableFile()
        {
            using var temp = new TempStateFile();
            var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "settings.json");
            File.WriteAllText(settingsPath, "{ \"toastDisplayMs\": 1234 }");

            using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var store = new ZetlAppSettingsStore(settingsPath);
                AssertEqual(950, store.Settings.ToastDisplayMs, "Unreadable settings should fall back to defaults, not abort startup.");
            }

            AssertTrue(File.Exists(settingsPath), "Unreadable settings must be left in place, not quarantined.");
        }

        private static void StateMigrationToleratesBackupRenameFailure()
        {
            using var temp = new TempStateFile();
            var legacyJson =
                """
                { "version": 1, "activeProjectId": "p1", "projects": [ { "id": "p1", "name": "Legacy", "activeBucketId": "b1", "buckets": [ { "id": "b1", "name": "Inbox", "kind": "Standard", "notes": [] } ] } ] }
                """;
            File.WriteAllText(temp.Path, legacyJson);
            var root = System.IO.Path.GetDirectoryName(temp.Path)!;

            // Hold the legacy file readable but not renamable: migration can read
            // it, but the .bak rename fails with a sharing violation.
            using (new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var store = new ZetlStateStore(temp.Path);

                AssertEqual("Legacy", store.ActiveProject?.Name, "Migration should still produce the project when the backup rename fails.");
                AssertEqual(
                    1,
                    Directory.GetFiles(System.IO.Path.Combine(root, "projects"), "project.json", SearchOption.AllDirectories).Length,
                    "Migration should write the split project file even if the backup rename fails.");
            }
        }

        private static void StateAppendsLogNotesWithoutActivating()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Work", ["Inbox"], "Inbox");
            var activeBefore = store.State.ActiveProjectId;

            store.AppendLogNotes(["[09:00:00] one", "[09:00:01] two"], maxDayBuckets: 14, maxNotesPerBucket: 1000);

            var logProject = store.State.Projects.Single(project => project.Name == ZetlStateStore.LogProjectName);
            AssertEqual(activeBefore, store.State.ActiveProjectId, "Logging should not change the active project.");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var dayBucket = logProject.Buckets.Single(bucket => bucket.Name == today);
            AssertEqual(2, dayBucket.Notes.Count, "Both log lines should be stored as notes.");
            AssertEqual("log", dayBucket.Notes[0].Source, "Log notes should use the log source.");

            store.AppendLogNotes(["a", "b", "c", "d", "e"], maxDayBuckets: 14, maxNotesPerBucket: 3);
            AssertEqual(3, logProject.Buckets.Single(bucket => bucket.Name == today).Notes.Count, "Day bucket should be capped to maxNotesPerBucket.");
            AssertEqual("e", logProject.Buckets.Single(bucket => bucket.Name == today).Notes[^1].Text, "Capping should keep the newest notes.");

            var reloaded = new ZetlStateStore(temp.Path);
            AssertTrue(reloaded.State.Projects.Any(project => project.Name == ZetlStateStore.LogProjectName), "Log project should persist across reload.");
            AssertEqual("Work", reloaded.ActiveProject?.Name, "Logging should leave the real active project untouched across reload.");
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
            AssertFalse(store.Settings.QuickNoteToClipboard, "Quick note to clipboard should default to off.");

            store.Settings.ToastDisplayMs = 1500;
            store.Settings.AutoCaptureOnCopy = false;
            store.Settings.QuickNoteToClipboard = true;
            store.Settings.DefaultProjectBuckets = new List<string> { "Notes", "Scratch" };
            store.Settings.DefaultCompileMode = "TSV";
            store.Settings.DefaultTsvRowLength = 4;
            store.Settings.ThemeId = "custom-theme";
            store.Settings.ThemeVariant = "Dark";
            store.Save();

            var loaded = new ZetlAppSettingsStore(settingsPath);
            AssertEqual(1500, loaded.Settings.ToastDisplayMs, "Toast display should round-trip.");
            AssertFalse(loaded.Settings.AutoCaptureOnCopy, "Auto-capture flag should round-trip.");
            AssertTrue(loaded.Settings.QuickNoteToClipboard, "Quick note to clipboard flag should round-trip.");
            AssertEqual("Notes", loaded.Settings.DefaultProjectBuckets[0], "Default buckets should round-trip.");
            AssertEqual("TSV", loaded.Settings.DefaultCompileMode, "Default compile mode should round-trip.");
            AssertEqual(4, loaded.Settings.DefaultTsvRowLength, "Default TSV row length should round-trip.");
            AssertEqual("custom-theme", loaded.Settings.ThemeId, "Theme id should round-trip.");
            AssertEqual("Dark", loaded.Settings.ThemeVariant, "Theme variant should round-trip.");
        }

        private static void AppSettingsRecoverFromCorruptFile()
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

        private static void ThemeDefaultsValidate()
        {
            var theme = ZetlThemeDefaults.Create();

            AssertEqual(0, ZetlThemeValidator.Validate(theme).Count, "Built-in theme should validate.");
            AssertEqual(ZetlThemeDocument.CurrentVersion, theme.Version, "Built-in theme should use the current schema.");

            theme.Dark.Accent = "purple";
            AssertTrue(
                ZetlThemeValidator.Validate(theme).Any(error => error.Contains("accent", StringComparison.OrdinalIgnoreCase)),
                "Invalid colors should produce a useful validation error.");
        }

        private static void ThemeDuskValidates()
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

        private static void ThemeBuiltInPresetsValidate()
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

        private static void ThemeRoundTripsCustomValues()
        {
            using var temp = new TempStateFile();
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(temp.Path)!,
                "themes");
            var store = new ZetlThemeStore(directory);
            var theme = ZetlThemeDefaults.CreateCustom("Midnight Notes");
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

        private static void ThemePreservesUnknownJsonFields()
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

        private static void ThemeStoreIgnoresInvalidFiles()
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

        private static void RuntimeAppliesAppSettingsDefaults()
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

            AssertEqual("Inbox", store.Defaults.ProjectBuckets[0], "Empty settings should use Inbox.");
            AssertEqual("Scratch", store.Defaults.ProjectBuckets[1], "Empty settings should use Scratch.");
            AssertEqual("TSV", store.Defaults.CompileMode, "Compile mode should flow into state defaults.");
            AssertEqual(4, store.Defaults.TsvRowLength, "TSV row length should flow into state defaults.");
        }

        private static void RuntimeUndoStackKeepsLanesSeparate()
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

        private static void RuntimeActivityLogBufferDrainsSafely()
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

        private static void RuntimeAutoCapturesCopiedText()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var clipboard = new FakeClipboard(" copied text ", changeToken: 2);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out _,
                out _);

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1)).GetAwaiter().GetResult();

            var note = store.GetActiveBucket()!.Notes.Single();
            AssertEqual("copied text", note.Text, "Auto-capture should trim and save copied text.");
            AssertEqual("copy", note.Source, "Auto-capture should mark the copy source.");
            AssertEqual(
                "Captured to Inbox in Demo.",
                notifications.Messages.Single(),
                "Auto-capture should report its destination.");
            AssertEqual(project.Id, store.GetActiveProject()!.Id, "Auto-capture should keep the active project.");
        }

        private static void RuntimeHoldCancellationPreventsAutoCapture()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var clipboard = new FakeClipboard("copied text", changeToken: 2);
            var delay = new ManualDelay();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _,
                delay);

            var captureTask = coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1));
            var pending = coordinator.CancelPending(VK_C, shifted: false);
            AssertTrue(pending is not null, "Hold should find and cancel the pending copy.");
            delay.Release();
            captureTask.GetAwaiter().GetResult();

            AssertEqual(0, store.GetActiveBucket()!.Notes.Count, "Cancelled copy should not auto-capture.");
            AssertEqual("copied text", pending!.ObservedClipboardText, "Observed copy text should remain available to the hold flow.");
        }

        private static void RuntimeClaimedHoldPreventsDelayedAutoCapture()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var clipboard = new FakeClipboard("copied text", changeToken: 2);
            var delay = new ManualDelay();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _,
                delay);
            var context = ShortcutContext(
                VK_C,
                clipboardSequenceNumber: 1);

            var captureTask = coordinator.OnPhysicalShortcutPassedThroughAsync(
                context);
            var pending = coordinator.ClaimPendingForHold(context);
            AssertTrue(
                pending is not null,
                "Hold callback should claim the pending copy immediately.");
            var holdTask = coordinator.HandleClaimedHoldAsync(
                context,
                pending);

            delay.Release();
            Task.WhenAll(captureTask, holdTask).GetAwaiter().GetResult();

            AssertEqual(
                0,
                store.GetActiveBucket()!.Notes.Count,
                "A claimed hold must not auto-save the copied text.");
            AssertTrue(
                holdTask.Result is ZetlNoteCaptureRequest,
                "A claimed hold with copied text should open note capture.");
            AssertEqual(
                "copied text",
                ((ZetlNoteCaptureRequest)holdTask.Result!).Text,
                "The hold request should retain the observed clipboard text.");
        }

        private static void RuntimeClaimedCopyHoldResolvesWithoutPolling()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var delay = new ManualDelay();
            var clipboard = new FakeClipboard(
                "copied text",
                changeToken: 2);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _,
                delay);
            var context = ShortcutContext(
                VK_C,
                clipboardSequenceNumber: 1);
            var pending = new ZetlPendingShortcut(
                VK_C,
                shiftLane: false,
                clipboardSequenceNumber: 1);

            var copiedTask = coordinator.HandleClaimedHoldAsync(
                context,
                pending);
            AssertTrue(
                copiedTask.IsCompleted,
                "Changed clipboard text should resolve without polling.");
            AssertTrue(
                copiedTask.Result is ZetlNoteCaptureRequest,
                "Changed clipboard text should open note capture.");

            clipboard.SetState(null, changeToken: 2);
            var emptyContext = ShortcutContext(
                VK_C,
                clipboardSequenceNumber: 2);
            var emptyPending = new ZetlPendingShortcut(
                VK_C,
                shiftLane: false,
                clipboardSequenceNumber: 2);
            var emptyTask = coordinator.HandleClaimedHoldAsync(
                emptyContext,
                emptyPending);
            AssertTrue(
                emptyTask.IsCompleted,
                "Unchanged clipboard should resolve without polling.");
            AssertTrue(
                emptyTask.Result is ZetlBoardRequest,
                "Unchanged clipboard should open the Board immediately.");
        }

        private static void RuntimeReplayTapConsumesAndRestoresClipboard()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay tap should suppress the physical paste.");
            AssertEqual(1, keyboard.PasteCount, "Replay tap should send one synthetic paste.");
            AssertEqual("user clipboard", clipboard.Text, "Replay should restore the user's clipboard.");
            AssertEqual(0, queue.Notes.Count, "Replay should consume the queued note.");
            var review = project.Buckets.Single(bucket => bucket.Id == queue.FifoReviewBucketId);
            AssertEqual("queued value", review.Notes.Single().Text, "Replay should archive the consumed note.");
            AssertEqual("Standard", queue.Kind, "An empty Replay bucket should return to Standard.");
            AssertTrue(undo.TryPop(false, out _), "Replay consumption should be undoable.");
        }

        private static void RuntimeRunLoggedRecordsAsyncFailure()
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

        private static void RuntimeRunLoggedRecordsDelayedAsyncFailure()
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

        private static void RuntimeEmptyReplayReportsFinalPasteFailure()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            // No notes: the empty-Replay tap does a final pass-through paste.
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var sink = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                sink,
                out var keyboard,
                out _);
            keyboard.PasteSucceeds = false;

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "An empty Replay tap is handled; it suppresses the physical paste.");
            AssertEqual(1, keyboard.PasteCount, "The final pass-through paste should be attempted.");
            AssertEqual("Standard", queue.Kind, "An empty Replay bucket returns to Standard.");
            AssertTrue(
                sink.Messages.Exists(message => message.Contains("didn't land", StringComparison.OrdinalIgnoreCase)),
                "A failed final paste should be reported, not silently called complete.");
        }

        private static void RuntimeReplayTapKeepsNoteWhenPasteFails()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out var undo);
            // Queueing succeeds but the worker reports the synthetic paste failed
            // (e.g. SendInput blocked by an elevated target).
            keyboard.PasteSucceeds = false;

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay tap should still be handled even when the paste fails.");
            AssertEqual(1, keyboard.PasteCount, "A paste should have been attempted.");
            AssertEqual(1, queue.Notes.Count, "The note must be kept when the synthetic paste was not accepted.");
            AssertFalse(undo.TryPop(false, out _), "A failed paste should not push an undo entry.");
        }

        private static void RuntimeReplayTapDefersClipboardWorkOffHook()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var dispatcher = new QueuingDispatcher();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _,
                dispatcher: dispatcher);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            // The decision returns synchronously, but none of the replay
            // clipboard/paste work runs inline -- it is queued on the dispatcher,
            // standing in for the move off the keyboard hook thread.
            AssertTrue(handled, "Replay tap should be handled synchronously.");
            AssertTrue(dispatcher.PendingCount > 0, "Replay work should be enqueued, not run on the hook thread.");
            AssertEqual(0, keyboard.PasteCount, "No paste should be sent before the queued work runs.");
            AssertEqual(1, queue.Notes.Count, "The queued note should not be consumed inline.");

            dispatcher.RunAll();

            AssertEqual(1, keyboard.PasteCount, "Running the queued work should send the replay paste.");
            AssertEqual(0, queue.Notes.Count, "Running the queued work should consume the queued note.");
        }

        private static void RuntimePopTapRemovesMatchingNote()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.GetActiveBucket()!;
            store.SetBucketPopMode(bucket, true);
            store.AddNote(bucket, "paste once", "copy");
            var clipboard = new FakeClipboard("paste once", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertFalse(handled, "Pop tap should allow the physical paste through.");
            AssertEqual(0, bucket.Notes.Count, "Pop tap should remove the matching note.");
            AssertTrue(undo.TryPop(false, out _), "Popped note should be undoable.");
        }

        private static void RuntimeCopyHoldCreatesNoteRequest()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(" copied ", changeToken: 2),
                new FakeNotificationSink(),
                out _,
                out _);

            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1)).GetAwaiter().GetResult();

            AssertTrue(request is ZetlNoteCaptureRequest, "Copied text should open note capture.");
            var note = (ZetlNoteCaptureRequest)request!;
            AssertEqual("copied", note.Text, "Copy request should contain trimmed clipboard text.");
            AssertEqual("copy", note.Source, "Copy request should preserve its source.");
            AssertTrue(note.ShowStartProjectToggle, "First copy hold (no active project) should offer project activation.");
            AssertTrue(note.StartProjectDefault, "Held copy should activate the project by default.");
        }

        private static void RuntimeEmptyCopyHoldOpensBoard()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _);

            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1)).GetAwaiter().GetResult();

            AssertTrue(request is ZetlBoardRequest, "Copy hold without new text should open the Board.");
        }

        private static void RuntimeCutHoldDefaultsToScratch()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _);

            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_X, clipboardSequenceNumber: 1)).GetAwaiter().GetResult();

            AssertTrue(request is ZetlNoteCaptureRequest, "Cut hold should always open note capture.");
            var note = (ZetlNoteCaptureRequest)request!;
            AssertEqual("Scratch", note.PreferredBucket!.Name, "First quick note should default to Scratch.");
            AssertTrue(note.ShowStartProjectToggle, "First quick note should offer project activation.");
            AssertFalse(note.StartProjectDefault, "Quick note should not activate the project by default.");
        }

        private static void RuntimeTemplateHoldRequestsPicker()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _);

            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_T)).GetAwaiter().GetResult();

            AssertTrue(request is ZetlTemplatePickerRequest, "Held Ctrl+T should request the template picker.");
            AssertFalse(
                ((ZetlTemplatePickerRequest)request!).FromCompileFallback,
                "A held Ctrl+T picker request is not a compile fallback.");
        }

        private static void RuntimeCompileHoldWithoutProjectRequestsPicker()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _);

            // No active project and nothing in Scratch to compile: held Ctrl+V should
            // offer the template picker (flagged as the compile fallback).
            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_V)).GetAwaiter().GetResult();

            AssertTrue(
                request is ZetlTemplatePickerRequest { FromCompileFallback: true },
                "Held Ctrl+V with nothing to compile should request the template picker as a fallback.");
        }

        private static void RuntimeCompileHoldWithActiveProjectStaysCompile()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            store.SetActiveProject(project.Id, shifted: false);
            store.AddNote(project.Buckets.First(), "a note", "copy");
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _);

            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_V)).GetAwaiter().GetResult();

            AssertTrue(
                request is ZetlCompileRequest,
                "Held Ctrl+V with an active project and notes should still compile.");
        }

        private static void RuntimeHoldTogglesAndUndoStayPortable()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                notifications,
                out _,
                out var undo);

            coordinator.HandleHoldAsync(ShortcutContext(VK_P)).GetAwaiter().GetResult();
            AssertTrue(store.GetActiveBucket()!.PopMode, "Ctrl+P hold should enable Pop.");
            coordinator.HandleHoldAsync(ShortcutContext(VK_R)).GetAwaiter().GetResult();
            AssertEqual("Replay", store.GetActiveBucket()!.Kind, "Ctrl+R hold should enable Replay.");
            AssertFalse(store.GetActiveBucket()!.PopMode, "Replay should disable Pop.");

            var undone = false;
            undo.Push(false, "Undone.", () => undone = true);
            coordinator.HandleHoldAsync(ShortcutContext(VK_Z)).GetAwaiter().GetResult();
            AssertTrue(undone, "Ctrl+Z hold should run the latest lane undo.");
            AssertEqual("Undone.", notifications.Messages.Last(), "Undo should report its message.");
        }

        private static void RuntimeCompletesQuickNoteResult()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.GetOrCreateDefaultProject();
            var scratch = store.GetScratchBucket(project);
            var clipboard = new FakeClipboard("keep me", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _,
                quickNoteToClipboard: false);
            var request = new ZetlNoteCaptureRequest(
                Shifted: false,
                project,
                scratch,
                Text: "",
                Source: "cut",
                ShowStartProjectToggle: true,
                StartProjectDefault: false,
                ScratchOnlyUntilProjectStarted: true,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null);

            coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "quick note",
                    StartProject: false,
                    CreateNewProject: false,
                    ProjectName: project.Name,
                    SelectedBucketName: scratch.Name,
                    SelectedBucket: scratch));

            AssertEqual("quick note", scratch.Notes.Single().Text, "Quick-note result should save the note.");
            AssertEqual("keep me", clipboard.Text, "Quick note should preserve clipboard when disabled.");
            AssertTrue(store.GetActiveProject() is null, "Quick note should leave the project inactive.");
        }

        private static void RuntimePastesCutBackOnDiscardedCut()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.GetOrCreateDefaultProject();
            var scratch = store.GetScratchBucket(project);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("cut text", changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _,
                quickNoteToClipboard: false);

            ZetlNoteCaptureRequest CutRequest(string text) => new(
                Shifted: false,
                project,
                scratch,
                Text: text,
                Source: "cut",
                ShowStartProjectToggle: true,
                StartProjectDefault: false,
                ScratchOnlyUntilProjectStarted: true,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null);

            ZetlNoteCaptureResult Cancelled() => new(
                Committed: false,
                NoteText: "",
                StartProject: false,
                CreateNewProject: false,
                ProjectName: project.Name,
                SelectedBucketName: scratch.Name,
                SelectedBucket: scratch);

            AssertEqual(
                ZetlNoteCaptureOutcome.PasteCutBack,
                coordinator.CompleteNoteCapture(CutRequest("cut text"), Cancelled()),
                "Discarding a held cut that captured text should request a paste-back.");
            AssertEqual(
                ZetlNoteCaptureOutcome.None,
                coordinator.CompleteNoteCapture(CutRequest(""), Cancelled()),
                "Discarding a held cut that captured nothing should not paste back.");
            AssertEqual(
                ZetlNoteCaptureOutcome.None,
                coordinator.CompleteNoteCapture(
                    CutRequest("cut text"),
                    Cancelled() with { Committed = true, NoteText = "kept" }),
                "Keeping the note should not paste the cut back.");
            AssertEqual("kept", scratch.Notes.Single(note => note.Source == "cut").Text, "Only the kept cut note should be saved; discarded cuts should not.");
        }

        private static void RuntimeFilesQuickNoteIntoSelectedProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var source = store.CreateProject("Source", ["Inbox"], "Inbox");
            var target = store.CreateProject("Target", ["Notes"], "Notes");
            store.SetActiveProject(source.Id);
            var targetBucket = target.Buckets.First(bucket => bucket.Name == "Notes");
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("", changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _,
                quickNoteToClipboard: false);

            var request = new ZetlNoteCaptureRequest(
                Shifted: false,
                source,
                store.GetQuickNoteBucket(source),
                Text: "",
                Source: "cut",
                ShowStartProjectToggle: false,
                StartProjectDefault: false,
                ScratchOnlyUntilProjectStarted: false,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null);

            coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "redirected jot",
                    StartProject: false,
                    CreateNewProject: false,
                    ProjectName: source.Name,
                    SelectedBucketName: targetBucket.Name,
                    SelectedBucket: targetBucket,
                    SelectedProject: target));

            AssertEqual("redirected jot", targetBucket.Notes.Single().Text, "Note should be filed into the selected project's bucket.");
            AssertEqual(0, source.Buckets.Sum(bucket => bucket.Notes.Count), "The request's project should receive no note.");
            AssertEqual(targetBucket.Id, target.QuickNoteBucketId, "Selected project should remember its quick-note bucket.");
            AssertEqual("Source", source.Name, "Filing into another project must not rename the request project.");
            AssertEqual(source.Id, store.GetActiveProject()?.Id, "Redirecting without activating should leave the prior active project active.");
        }

        private static void RuntimeRedirectsQuickNoteWithNoActiveProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var other = store.CreateProject("Other", ["Notes"], "Notes");
            store.ClearActiveProject();
            var dated = store.GetOrCreateDefaultProject();
            var otherBucket = other.Buckets.First(bucket => bucket.Name == "Notes");
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("", changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _,
                quickNoteToClipboard: false);

            var request = new ZetlNoteCaptureRequest(
                Shifted: false,
                dated,
                store.GetScratchBucket(dated),
                Text: "",
                Source: "cut",
                ShowStartProjectToggle: true,
                StartProjectDefault: false,
                ScratchOnlyUntilProjectStarted: true,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null);

            coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "redirected jot",
                    StartProject: false,
                    CreateNewProject: false,
                    ProjectName: "Renamed Attempt",
                    SelectedBucketName: otherBucket.Name,
                    SelectedBucket: otherBucket,
                    SelectedProject: other));

            AssertEqual("redirected jot", otherBucket.Notes.Single().Text, "Redirected jot should land in the chosen existing project.");
            AssertEqual(0, dated.Buckets.Sum(bucket => bucket.Notes.Count), "The dated default should receive no note when redirected.");
            AssertFalse(dated.Name == "Renamed Attempt", "Redirecting must not rename the dated default project.");
            AssertEqual(otherBucket.Id, other.QuickNoteBucketId, "The chosen project should remember its quick-note bucket.");
            AssertTrue(store.GetActiveProject() is null, "A redirected jot should leave no active project.");
        }

        private static void RuntimeActivatesSelectedProjectFromQuickNote()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var other = store.CreateProject("Other", ["Notes"], "Notes");
            store.ClearActiveProject();
            var dated = store.GetOrCreateDefaultProject();
            var otherBucket = other.Buckets.First(bucket => bucket.Name == "Notes");
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("", changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _,
                quickNoteToClipboard: false);

            var request = new ZetlNoteCaptureRequest(
                Shifted: false,
                dated,
                store.GetScratchBucket(dated),
                Text: "",
                Source: "cut",
                ShowStartProjectToggle: true,
                StartProjectDefault: false,
                ScratchOnlyUntilProjectStarted: true,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null);

            coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "activate me",
                    StartProject: true,
                    CreateNewProject: false,
                    ProjectName: other.Name,
                    SelectedBucketName: otherBucket.Name,
                    SelectedBucket: otherBucket,
                    SelectedProject: other));

            AssertEqual("activate me", otherBucket.Notes.Single().Text, "Note should be filed into the chosen project.");
            AssertEqual(other.Id, store.GetActiveProject()?.Id, "Activating from a quick note should make the chosen project active.");
        }

        private static void RuntimeDeactivatesActiveProjectWhenToggledOff()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var active = store.CreateProject("Active", ["Inbox"], "Inbox");
            store.SetActiveProject(active.Id);
            var bucket = active.Buckets.First(item => item.Name == "Inbox");
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("copied", changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _,
                quickNoteToClipboard: false);

            var request = new ZetlNoteCaptureRequest(
                Shifted: false,
                active,
                bucket,
                Text: "copied",
                Source: "copy",
                ShowStartProjectToggle: false,
                StartProjectDefault: true,
                ScratchOnlyUntilProjectStarted: false,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null);

            coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "kept",
                    StartProject: false,
                    CreateNewProject: false,
                    ProjectName: active.Name,
                    SelectedBucketName: bucket.Name,
                    SelectedBucket: bucket,
                    SelectedProject: active));

            AssertEqual("kept", bucket.Notes.Single().Text, "The note should still be saved when deactivating.");
            AssertTrue(store.GetActiveProject() is null, "Toggling Activate off on the active project should deactivate it.");
        }

        private static void RuntimeCreatesNewProjectFromCapture()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var origin = store.GetOrCreateDefaultProject();
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("copied", changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _,
                quickNoteToClipboard: false);

            var request = new ZetlNoteCaptureRequest(
                Shifted: false,
                origin,
                store.GetScratchBucket(origin),
                Text: "copied",
                Source: "copy",
                ShowStartProjectToggle: false,
                StartProjectDefault: true,
                ScratchOnlyUntilProjectStarted: false,
                CreateNewProjectToggle: false,
                ProjectToggleText: null,
                ProjectNameDefault: null);

            coordinator.CompleteNoteCapture(
                request,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "fresh note",
                    StartProject: true,
                    CreateNewProject: true,
                    ProjectName: "Fresh",
                    SelectedBucketName: "Inbox",
                    SelectedBucket: null!,
                    SelectedProject: null));

            var fresh = store.State.Projects.SingleOrDefault(project => project.Name == "Fresh");
            AssertTrue(fresh is not null, "Choosing New project should create the named project.");
            AssertEqual("fresh note", fresh!.Buckets.Single(bucket => bucket.Name == "Inbox").Notes.Single().Text, "The note should land in the chosen bucket of the new project.");
            AssertEqual(fresh.Id, store.GetActiveProject()?.Id, "A new project created with Activate on should become active.");
        }

        private static void RuntimeCompletesCompileResult()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var source = store.CreateProject("Source", ["Inbox"], "Inbox");
            store.AddNote(store.GetActiveBucket()!, "source note", "copy");
            var destination = store.CreateProject("Destination", ["Output"], "Output");
            store.SetActiveProject(source.Id);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                notifications,
                out _,
                out var undo);
            var request = (ZetlCompileRequest)coordinator.HandleHoldAsync(
                ShortcutContext(VK_V)).GetAwaiter().GetResult()!;

            var outcome = coordinator.CompleteCompile(
                request,
                new ZetlCompileResult(
                    Committed: true,
                    CompiledText: "compiled text",
                    SaveToBucket: true,
                    DestinationProject: destination,
                    DestinationBucketName: "Output",
                    Flatten: true,
                    SelectedNoteTexts: ["source note"],
                    PasteNow: false));

            AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "Saved compile should restore the target.");
            AssertEqual(
                "compiled text",
                destination.Buckets.Single(bucket => bucket.Name == "Output").Notes.Single().Text,
                "Compile result should save to the selected destination.");
            AssertTrue(undo.TryPop(false, out _), "Saved compile should be undoable.");
            AssertTrue(
                notifications.Messages.Single().StartsWith("Compiled to Output in Destination."),
                "Compile should report its destination.");
        }

        private static void RuntimePreservesStructuredCompileSaves()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var source = store.CreateProject("Source", ["Inbox"], "Inbox");
            var destination = store.CreateProject("Destination", ["Output"], "Output");
            var destinationActiveBucketId = destination.ActiveBucketId;
            store.SetActiveProject(source.Id);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 1),
                new FakeNotificationSink(),
                out _,
                out _);
            var request = new ZetlCompileRequest(false, source, null);

            var outcome = coordinator.CompleteCompile(
                request,
                new ZetlCompileResult(
                    Committed: true,
                    CompiledText: "ignored combined text",
                    SaveToBucket: true,
                    DestinationProject: destination,
                    DestinationBucketName: "Compiled",
                    Flatten: false,
                    SelectedNoteTexts: ["one", "two"],
                    PasteNow: false));

            var compiled = destination.Buckets.Single(bucket => bucket.Name == "Compiled");
            AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "Structured save should restore the target.");
            AssertEqual(2, compiled.Notes.Count, "Structured save should preserve note boundaries.");
            AssertEqual("one", compiled.Notes[0].Text, "Structured save should preserve note order.");
            AssertEqual("two", compiled.Notes[1].Text, "Structured save should preserve note order.");
            AssertEqual(source.Id, store.GetActiveProject()?.Id, "Structured save should not change the active project.");
            AssertEqual(destinationActiveBucketId, destination.ActiveBucketId, "Structured save should not change the destination active bucket.");
        }

        private static void RuntimeReturnsCopyAndPasteCompileOutcomes()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var source = store.CreateProject("Source", ["Inbox"], "Inbox");
            var clipboard = new FakeClipboard("before", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _);
            var request = new ZetlCompileRequest(false, source, null);

            var copyOutcome = coordinator.CompleteCompile(
                request,
                new ZetlCompileResult(
                    Committed: true,
                    CompiledText: "copied compile",
                    SaveToBucket: false,
                    DestinationProject: source,
                    DestinationBucketName: "Inbox",
                    Flatten: false,
                    SelectedNoteTexts: ["copied compile"],
                    PasteNow: false));

            AssertEqual(ZetlCompileOutcome.RestoreTarget, copyOutcome, "Copy should restore the target.");
            AssertEqual("copied compile", clipboard.Text, "Copy should place compiled text on the clipboard.");

            var pasteOutcome = coordinator.CompleteCompile(
                request,
                new ZetlCompileResult(
                    Committed: true,
                    CompiledText: "pasted compile",
                    SaveToBucket: false,
                    DestinationProject: source,
                    DestinationBucketName: "Inbox",
                    Flatten: false,
                    SelectedNoteTexts: ["pasted compile"],
                    PasteNow: true));

            AssertEqual(ZetlCompileOutcome.PasteNow, pasteOutcome, "Paste Now should request the paste path.");
            AssertEqual("pasted compile", clipboard.Text, "Paste Now should stage compiled text on the clipboard.");
        }

        private static void RuntimeCompileDoesNotPasteWhenClipboardWriteFails()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var source = store.CreateProject("Source", ["Inbox"], "Inbox");
            var clipboard = new FakeClipboard("before", changeToken: 1) { SetTextSucceeds = false };
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _);
            var request = new ZetlCompileRequest(false, source, null);

            var outcome = coordinator.CompleteCompile(
                request,
                new ZetlCompileResult(
                    Committed: true,
                    CompiledText: "pasted compile",
                    SaveToBucket: false,
                    DestinationProject: source,
                    DestinationBucketName: "Inbox",
                    Flatten: false,
                    SelectedNoteTexts: ["pasted compile"],
                    PasteNow: true));

            AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "A failed clipboard write must not request the paste path.");
            AssertEqual("before", clipboard.Text, "A failed clipboard write should leave the clipboard untouched.");
        }

        private static void RuntimeReportsRejectedCompiledPaste()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("compiled text", changeToken: 1),
                notifications,
                out var keyboard,
                out _);
            keyboard.PasteSucceeds = false;

            coordinator.PasteCompiledTextAsync().GetAwaiter().GetResult();

            AssertEqual(1, keyboard.PasteCount, "Compiled paste should be attempted once.");
            AssertTrue(
                notifications.Messages.Single().Contains("remains on the clipboard", StringComparison.Ordinal),
                "Rejected paste should explain that the compiled text is preserved.");
            AssertTrue(
                notifications.Messages.Single().Contains("elevated", StringComparison.OrdinalIgnoreCase),
                "Rejected paste should mention the Windows privilege mismatch.");
        }

        private static void RuntimeParityScenarioWritesSnapshot()
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

        private static ZetlShortcutCoordinator CreateShortcutCoordinator(
            ZetlStateStore store,
            FakeClipboard clipboard,
            FakeNotificationSink notifications,
            out FakeKeyboardBackend keyboard,
            out ZetlUndoStack undo,
            IZetlDelay? delay = null,
            bool quickNoteToClipboard = false,
            IZetlDispatcher? dispatcher = null)
        {
            keyboard = new FakeKeyboardBackend();
            undo = new ZetlUndoStack(100);
            return new ZetlShortcutCoordinator(
                store,
                keyboard,
                clipboard,
                dispatcher ?? new ImmediateDispatcher(),
                delay ?? new ImmediateDelay(),
                notifications,
                undo,
                () => true,
                () => quickNoteToClipboard,
                _ => { },
                TimeSpan.FromMilliseconds(60));
        }

        private static ChordlEventContext ShortcutContext(
            int keyCode,
            bool shifted = false,
            uint clipboardSequenceNumber = 0)
        {
            return new ChordlEventContext(
                keyCode,
                ChordlKeys.FormatComboName(keyCode, shifted),
                ChordlDispatchMode.None,
                ReplayShift: false,
                ShiftLane: shifted,
                ClipboardSequenceNumber: clipboardSequenceNumber);
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
                [new ChordlChord(VK_B, Ctrl: true, Shift: true)] = new("Ctrl+Shift+B", ChordlDispatchMode.TapOnly, ReplayShift: true),
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

        private sealed class ImmediateDispatcher : IZetlDispatcher
        {
            public void Post(Action action)
            {
                action();
            }
        }

        // Captures posted actions instead of running them, so a test can assert
        // work was enqueued (deferred off the calling thread) and then run it.
        private sealed class QueuingDispatcher : IZetlDispatcher
        {
            private readonly Queue<Action> pending = new();

            public int PendingCount => pending.Count;

            public void Post(Action action)
            {
                pending.Enqueue(action);
            }

            public void RunAll()
            {
                while (pending.Count > 0)
                {
                    pending.Dequeue()();
                }
            }
        }

        private sealed class ImmediateDelay : IZetlDelay
        {
            public Task WaitAsync(TimeSpan delay)
            {
                return Task.CompletedTask;
            }
        }

        private sealed class ManualDelay : IZetlDelay
        {
            private readonly TaskCompletionSource completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public Task WaitAsync(TimeSpan delay)
            {
                return completion.Task;
            }

            public void Release()
            {
                completion.TrySetResult();
            }
        }

        private sealed class FakeNotificationSink : IZetlNotificationSink
        {
            public List<string> Messages { get; } = new();

            public void Show(string message)
            {
                Messages.Add(message);
            }
        }

        private sealed class FakeKeyboardBackend : IKeyboardBackend
        {
            public int PasteCount { get; private set; }

            public bool PasteSucceeds { get; set; } = true;

            public bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent)
            {
                return true;
            }

            public Task<bool> SendChord(
                int vkCode,
                bool includeShift,
                bool restoreCtrl,
                bool restoreShift)
            {
                return Task.FromResult(true);
            }

            public Task<bool> SendPaste()
            {
                PasteCount++;
                return Task.FromResult(PasteSucceeds);
            }

            public void Dispose()
            {
            }
        }

        private sealed class FakeClipboard : IClipboard
        {
            public FakeClipboard(string? text, uint changeToken)
            {
                Text = text;
                ChangeToken = changeToken;
            }

            public string? Text { get; private set; }

            public uint ChangeToken { get; private set; }

            public bool SetTextSucceeds { get; set; } = true;

            public string? TryGetText()
            {
                return Text;
            }

            public bool SetText(string text)
            {
                if (!SetTextSucceeds)
                {
                    return false;
                }

                Text = text;
                ChangeToken++;
                return true;
            }

            public uint GetChangeToken()
            {
                return ChangeToken;
            }

            public void SetState(string? text, uint changeToken)
            {
                Text = text;
                ChangeToken = changeToken;
            }
        }
}
