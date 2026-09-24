using Chordl;
using static Chordl.ChordlKeys;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class PortableSelfTests
{
    public static IEnumerable<object[]> GetTests()
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
                ("Ctrl+R tap dispatches replay key on key-up", ReplayToggleTapDispatchesOnKeyUp),
                ("Ctrl+R hold raises Replay toggle", ReplayToggleHoldDoesNotDispatch),
                ("Ctrl+Z hold raises Zetl undo", UndoHoldDoesNotDispatch),
                ("Shift changes restart hold detection", ShiftChangeRestartsHold),
                ("Shift repeat does not restart hold detection", ShiftRepeatDoesNotRestartHold),
                ("Shift change after hold does not dispatch twice", ShiftChangeAfterHoldDoesNotDispatchTwice),
                ("Synthetic modifier injection uses an unheld side", SyntheticModifierUsesUnheldSide),
                ("Chord injection suppresses a held Shift for a plain chord", ChordInjectionSuppressesHeldShiftForPlainChord),
                ("Zetl state creates projects and scratch buckets", StateCreatesProjectAndScratch),
                ("Zetl state keeps active temporary consumables", StateKeepsActiveTemporaryConsumables),
                ("Zetl state disposes temporary consumables when lane clears", StateDisposesTemporaryConsumablesWhenLaneClears),
                ("Zetl state disposes abandoned temporary consumables on load", StateDisposesAbandonedTemporaryConsumablesOnLoad),
                ("Zetl state creates the journal default home", StateCreatesJournalDefaultProject),
                ("Zetl state configures and rolls journal intervals", StateJournalIntervalConfiguresAndRolls),
                ("Zetl state reuses the journal default home", StateReusesDatedDefaultProject),
                ("Zetl state finish starts a fresh journal", StateFinishStartsFreshJournal),
                ("Zetl state finish with no active project is a no-op", StateFinishWithNoActiveProjectIsNoop),
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
                ("Zetl state Replay resumes queued slips across restart", StateReplayResumesQueuedSlipsAcrossRestart),
                ("Zetl state Replay archives consumed slips for review", StateReplayArchivesConsumedSlipsForReview),
                ("Zetl state Replay restores consumed slips from review", StateReplayRestoresConsumedSlipsFromReview),
                ("Zetl state Replay disables pop mode", StateReplayDisablesPopMode),
                ("Zetl state maps legacy Fifo kind to Replay", StateMapsLegacyFifoKindToReplay),
                ("Zetl state detects compilable notes", StateDetectsCompilableNotes),
                ("Zetl state finds inactive scratch notes for compile", StateFindsInactiveScratchCompileTarget),
                ("Zetl state pop mode removes matching last note", StatePopModeRemovesLastMatchingNote),
                ("Zetl state Pop recovers text across restart", StatePopRecoversTextAcrossRestart),
                ("Zetl state Pop recovers images across restart", StatePopRecoversImageAcrossRestart),
                ("Zetl state Pop recovers mixed slips across restart", StatePopRecoversMixedSlipAcrossRestart),
                ("Zetl state finds the most recently written project", StateFindsMostRecentlyWrittenProject),
                ("Zetl capture origin respects privacy detail", CaptureOriginRespectsPrivacyDetail),
                ("Zetl capture origin round-trips with notes", CaptureOriginRoundTripsWithNotes),
                ("Zetl clean export strips capture origin", CleanExportStripsCaptureOrigin),
                ("Zetl project packages separate clean and archive provenance", ProjectPackagesSeparateProvenance),
                ("Zetl image slips store deduplicated project assets", ImageSlipsStoreDeduplicatedAssets),
                ("Zetl image project packages include referenced assets", ImageProjectPackagesIncludeAssets),
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
                ("Kastn state round-trips the last project", KastnStateRoundTripsLastProject),
                ("Journal bucket rolls at the day-start hour", JournalBucketRollsAtDayStartHour),
                ("Zetl app settings recover from a corrupt file", AppSettingsRecoverFromCorruptFile),
                ("Zetl app settings recover from an unreadable file", AppSettingsRecoverFromUnreadableFile),
                ("Zetl built-in theme validates", ThemeDefaultsValidate),
                ("Zetl Dusk built-in theme validates", ThemeDuskValidates),
                ("Zetl built-in presets all validate", ThemeBuiltInPresetsValidate),
                ("Zetl themes round-trip custom values", ThemeRoundTripsCustomValues),
                ("Zetl themes preserve unknown JSON fields", ThemePreservesUnknownJsonFields),
                ("Zetl theme store ignores invalid files", ThemeStoreIgnoresInvalidFiles),
                ("Zetl state applies bucket defaults", StateAppliesBucketDefaults),
                ("Journal mode rolls into dated buckets", JournalModeRollsIntoDatedBuckets),
                ("Journal auto-returns from a quiet project", JournalAutoReturnsFromQuietProject),
                ("Journal auto-return off keeps the project", JournalAutoReturnOffKeepsProject),
                ("Ctrl+J toggles between the Journal and the last project", ToggleActiveProjectSwitchesBetweenJournalAndLastProject),
                ("URL slips are derived from content", UrlSlipsAreDerivedFromContent),
                ("Zetl default hotkeys config parses", DefaultConfigParses),
                ("Zetl config tolerates null replay modifiers", ConfigNullReplayModifiersDoesNotThrow),
                ("Zetl config reports clean errors for null fields", ConfigNullFieldsReportCleanErrors),
                ("Runtime applies app settings defaults", RuntimeAppliesAppSettingsDefaults),
                ("Runtime undo stack keeps lanes separate", RuntimeUndoStackKeepsLanesSeparate),
                ("Runtime activity log buffer drains safely", RuntimeActivityLogBufferDrainsSafely),
                ("Runtime auto-captures copied text", RuntimeAutoCapturesCopiedText),
                ("Runtime auto-captures and replays rich text", RuntimeAutoCapturesAndReplaysRichText),
                ("Runtime auto-captures copied images", RuntimeAutoCapturesCopiedImages),
                ("Runtime auto-captures dual text+image clipboards as text", RuntimeAutoCapturesDualClipboardAsText),
                ("Runtime clipboard capture retries a changed generation atomically", RuntimeClipboardCaptureRetriesChangedGeneration),
                ("Runtime clipboard capture refuses persistently unstable generations", RuntimeClipboardCaptureRefusesUnstableGenerations),
                ("Dual slips survive persistence text-preferred", DualSlipSurvivesPersistence),
                ("Runtime Pop removes a dual slip by image hash", RuntimePopRemovesDualSlipByImageHash),
                ("Runtime Replay pastes a dual slip as text", RuntimeReplayPastesDualSlipAsText),
                ("Runtime downloads copied image URLs", RuntimeAutoCapturesCopiedImageUrls),
                ("Runtime keeps non-image URLs as text", RuntimeKeepsNonImageUrlsAsText),
                ("Runtime held copy opens image capture and saves captions", RuntimeHeldCopyCapturesImagesDirectly),
                ("Runtime held dual copy saves text-preferred with the picture", RuntimeHeldCopyDualSavesTextPreferred),
                ("Runtime held dual copy with cleared text saves a picture", RuntimeHeldCopyDualClearedTextSavesPicture),
                ("Runtime held copy opens downloaded image URLs", RuntimeHeldCopyCapturesImageUrls),
                ("Runtime hold cancellation prevents auto-capture", RuntimeHoldCancellationPreventsAutoCapture),
                ("Runtime claimed hold prevents delayed auto-capture", RuntimeClaimedHoldPreventsDelayedAutoCapture),
                ("Runtime claimed copy hold resolves without polling", RuntimeClaimedCopyHoldResolvesWithoutPolling),
                ("Runtime Replay tap consumes and restores clipboard", RuntimeReplayTapConsumesAndRestoresClipboard),
                ("Runtime Replay clipboard session reports restore outcomes", RuntimeReplayClipboardSessionReportsRestoreOutcomes),
                ("Runtime clipboard content writer chooses the richest representation", RuntimeClipboardContentWriterChoosesRichestRepresentation),
                ("Runtime Replay resumes visible items after restart", RuntimeReplayResumesVisibleItemsAfterRestart),
                ("Runtime rapid Replay taps consume distinct slips", RuntimeRapidReplayTapsConsumeDistinctSlips),
                ("Runtime Replay lanes progress independently", RuntimeReplayLanesProgressIndependently),
                ("Runtime Replay suppresses taps during final clipboard restoration", RuntimeReplaySuppressesTapDuringFinalRestore),
                ("Runtime Replay final restoration remains lane-local", RuntimeReplayFinalRestoreRemainsLaneLocal),
                ("Runtime Replay final restore failures complete visibly", RuntimeReplayFinalRestoreFailureCompletesVisibly),
                ("Runtime Shift-lane Replay tap consumes a shifted paste chord", RuntimeShiftLaneReplayTapConsumesShiftedPaste),
                ("Runtime Replay handles images and restores image clipboard", RuntimeReplayHandlesImagesAndRestoresImageClipboard),
                ("Runtime Replay restores rich and mixed clipboard formats", RuntimeReplayRestoresRichAndMixedClipboardFormats),
                ("Runtime Replay refuses a lossy clipboard replacement", RuntimeReplayRefusesLossyClipboardReplacement),
                ("Runtime Replay does not paste after transactional staging fails", RuntimeReplayDoesNotPasteAfterTransactionalStageFailure),
                ("Runtime Replay does not overwrite a newer matching clipboard", RuntimeReplayDoesNotOverwriteNewerMatchingClipboard),
                ("Runtime Replay tap defers clipboard work off the hook", RuntimeReplayTapDefersClipboardWorkOffHook),
                ("Runtime Replay tap keeps the note when the paste fails", RuntimeReplayTapKeepsNoteWhenPasteFails),
                ("Runtime empty Replay reports a failed final paste", RuntimeEmptyReplayReportsFinalPasteFailure),
                ("Runtime Replay resumes clipboard when enabled", RuntimeReplayResumesClipboardWhenEnabled),
                ("Runtime Replay keeps last paste when disabled", RuntimeReplayKeepsLastPasteWhenDisabled),
                ("Runtime logged fire-and-forget records async failures", RuntimeRunLoggedRecordsAsyncFailure),
                ("Runtime logged fire-and-forget records delayed async failures", RuntimeRunLoggedRecordsDelayedAsyncFailure),
                ("Runtime Pop tap removes matching note", RuntimePopTapRemovesMatchingNote),
                ("Runtime Pop removes matching image slip", RuntimePopRemovesMatchingImageSlip),
                ("Runtime copy hold creates note request", RuntimeCopyHoldCreatesNoteRequest),
                ("Runtime empty copy hold opens Board", RuntimeEmptyCopyHoldOpensBoard),
                ("Runtime cut hold defaults to today's journal bucket", RuntimeCutHoldDefaultsToTodaysJournalBucket),
                ("Runtime select-all hold captures the selection", RuntimeSelectAllHoldCapturesTheSelection),
                ("Runtime select-all shift hold captures the selection", RuntimeSelectAllShiftHoldCapturesTheSelection),
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
                ("Runtime formatted compile stages rich clipboard", RuntimeFormattedCompileStagesRichClipboard),
                ("Runtime compile does not paste when the clipboard write fails", RuntimeCompileDoesNotPasteWhenClipboardWriteFails),
                ("Runtime compile surfaces an uncertain clipboard rollback", RuntimeCompileSurfacesUncertainClipboardRollback),
                ("Runtime reports rejected compiled paste", RuntimeReportsRejectedCompiledPaste),
                ("Runtime parity scenario writes a reloadable snapshot", RuntimeParityScenarioWritesSnapshot)
        };

        foreach (var t in tests)
        {
            yield return new object[] { t.Name, t.Test };
        }
    }

    [Theory]
    [MemberData(nameof(GetTests))]
    public void RunPortableTest(string name, Action test)
    {
        Assert.NotNull(name);
        test();
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
            WaitForHold(holds, "Hold callback should fire once.");
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: false, isKeyUp: true), "Held paste key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held paste should not dispatch paste.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void PasteHoldSuppressesRepeatsAfterCtrlKeyUp()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_V, isKeyDown: true, isKeyUp: false), "Paste key down should suppress.");
            WaitForHold(holds, "Hold callback should fire once.");
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
            WaitForHold(holds, "Hold callback should fire once.");
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
            WaitForHold(holds, "Hold callback should fire once.");
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
            WaitForHold(holds, "Hold callback should fire once.");
            AssertEqual("Ctrl+P", holds[0].Name, "Hold should use Pop toggle chord.");
            AssertTrue(processor.HandleKeyEvent(VK_P, isKeyDown: false, isKeyUp: true), "Held Pop toggle key up should suppress.");
            AssertEqual(0, dispatched.Count, "Held Pop toggle should not dispatch the pop key.");
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: false, isKeyUp: true);
        }

        private static void ReplayToggleTapDispatchesOnKeyUp()
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

        private static void ReplayToggleHoldDoesNotDispatch()
        {
            using var processor = CreateProcessor(out var dispatched, out _, out _, out var holds);
            processor.HandleKeyEvent(VK_CONTROL, isKeyDown: true, isKeyUp: false);
            AssertTrue(processor.HandleKeyEvent(VK_R, isKeyDown: true, isKeyUp: false), "Replay toggle key down should suppress.");
            WaitForHold(holds, "Hold callback should fire once.");
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
            WaitForHold(holds, "Hold callback should fire once.");
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
            WaitForHold(holds, "Hold should fire after Shift restart threshold.");
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
            WaitForHold(holds, "Shift autorepeat should not postpone the restarted hold.");
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
            WaitForHold(holds, "Initial hold should fire once.");
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
                null,
                ZetlSyntheticModifier.SelectInjection(
                    leftDown: true,
                    rightDown: false,
                    VK_LCONTROL,
                    VK_RCONTROL),
                "With left held, no synthetic modifier is needed.");
            AssertEqual<int?>(
                null,
                ZetlSyntheticModifier.SelectInjection(
                    leftDown: false,
                    rightDown: true,
                    VK_LCONTROL,
                    VK_RCONTROL),
                "With right held, no synthetic modifier is needed.");
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

        private static void StateKeepsActiveTemporaryConsumables()
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

        private static void StateDisposesTemporaryConsumablesWhenLaneClears()
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

        private static void StateDisposesAbandonedTemporaryConsumablesOnLoad()
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
            AssertTrue(
                store.TryPopLastMatchingActiveNote(
                    "alpha",
                    shifted: false,
                    out var poppedBucket,
                    out var poppedNote,
                    out var reviewBucket,
                    out var reviewNote),
                "Pop should return durable undo details.");
            AssertEqual(bucket.Id, poppedBucket?.Id, "Pop should report the source bucket.");
            AssertEqual("alpha", poppedNote?.Text, "Pop should report the removed note.");
            AssertEqual("Inbox Pop Review", reviewBucket?.Name, "Pop should report its recovery bucket.");
            store.RestorePoppedNote(poppedBucket!, poppedNote!, reviewBucket, reviewNote?.Id);
            AssertEqual("alpha", bucket.Notes.Single().Text, "Restore should put popped note back.");
        }

        private static void StatePopRecoversTextAcrossRestart()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var source = store.ActiveBucket!;
            store.SetBucketPopMode(source, true);
            store.AddNote(source, "durable text", "copy");

            AssertTrue(
                store.TryPopLastMatchingActiveNote("durable text", false, out _, out _, out var review, out _),
                "Text Pop should move the slip to review.");
            AssertEqual("Inbox Pop Review", review?.Name, "Pop review should name its source bucket.");

            var reloaded = new ZetlStateStore(temp.Path);
            var loadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
            var loadedSource = loadedProject.Buckets.Single(item => item.Id == source.Id);
            var loadedReview = loadedProject.Buckets.Single(item => item.Id == loadedSource.Settings.PopReviewBucketId);
            AssertEqual(0, loadedSource.Notes.Count, "The source should remain consumed after restart.");
            AssertEqual("durable text", loadedReview.Notes.Single().Text, "Popped text should remain recoverable after restart.");
            AssertEqual("pop-recovery", loadedReview.Notes.Single().Source, "Recovered slips should identify their Pop origin.");
        }

        private static void StatePopRecoversImageAcrossRestart()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var source = store.ActiveBucket!;
            store.SetBucketPopMode(source, true);
            var bytes = new byte[] { 8, 6, 7, 5, 3, 0, 9 };
            var image = store.AddImageNote(
                project,
                source,
                new ZetlClipboardImage(bytes, 7, 1),
                "copy");

            AssertTrue(
                store.TryPopLastMatchingActiveImage(image.Image!.Sha256, false, out _, out _, out _, out _),
                "Image Pop should move the slip to review.");

            var reloaded = new ZetlStateStore(temp.Path);
            var loadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
            var loadedSource = loadedProject.Buckets.Single(item => item.Id == source.Id);
            var recovered = loadedProject.Buckets
                .Single(item => item.Id == loadedSource.Settings.PopReviewBucketId)
                .Notes.Single();
            AssertTrue(recovered.IsImage, "Popped image type should survive restart.");
            AssertEqual(7, recovered.Image?.Width, "Popped image metadata should survive restart.");
            AssertEqual(bytes.Length, reloaded.ReadImageAsset(loadedProject, recovered)?.Length, "Popped image bytes should remain readable after restart.");
        }

        private static void StatePopRecoversMixedSlipAcrossRestart()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var source = store.ActiveBucket!;
            store.SetBucketPopMode(source, true);
            var bytes = new byte[] { 1, 3, 3, 7 };
            var html = "<p><strong>mixed</strong></p>";
            var mixed = store.AddImageNote(
                project,
                source,
                new ZetlClipboardImage(bytes, 2, 2),
                "copy",
                caption: "mixed",
                preferTextContent: true,
                richHtml: html,
                replayFormats: [new ZetlClipboardFormatData(42, [4, 2], "Native Test")]);
            store.UpdateNote(
                mixed,
                mixed.Text,
                align: "right",
                bold: true,
                fontFamily: "Aptos",
                fontSize: 18,
                textColor: "#cc0000");

            AssertTrue(
                store.TryPopLastMatchingActiveImage(mixed.Image!.Sha256, false, out _, out _, out _, out _),
                "Mixed Pop should move the complete slip to review.");

            var reloaded = new ZetlStateStore(temp.Path);
            var loadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
            var loadedSource = loadedProject.Buckets.Single(item => item.Id == source.Id);
            var recovered = loadedProject.Buckets
                .Single(item => item.Id == loadedSource.Settings.PopReviewBucketId)
                .Notes.Single();
            AssertEqual("mixed", recovered.Text, "Mixed Pop should retain text after restart.");
            AssertTrue(recovered.Image is not null, "Mixed Pop should retain its attached image after restart.");
            AssertEqual(html, recovered.RichHtml, "Mixed Pop should retain rich clipboard content after restart.");
            AssertEqual("Native Test", recovered.ReplayFormats?.Single().RegisteredName, "Mixed Pop should retain native replay formats after restart.");
            AssertEqual("right", recovered.Align, "Mixed Pop should retain block alignment after restart.");
            AssertTrue(recovered.Bold, "Mixed Pop should retain emphasis after restart.");
            AssertEqual("Aptos", recovered.FontFamily, "Mixed Pop should retain its font after restart.");
            AssertEqual(18, recovered.FontSize, "Mixed Pop should retain its font size after restart.");
            AssertEqual("#CC0000", recovered.TextColor, "Mixed Pop should retain its normalized color after restart.");
            AssertEqual(bytes.Length, reloaded.ReadImageAsset(loadedProject, recovered)?.Length, "Mixed Pop should retain readable image bytes after restart.");
        }

        private static void StateFindsMostRecentlyWrittenProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var older = store.CreateProject("Older", ["Inbox"], "Inbox");
            var newer = store.CreateProject("Newer", ["Inbox"], "Inbox");
            var olderNote = store.AddNote(older.Buckets.First(), "old", "copy");
            var newerNote = store.AddNote(newer.Buckets.First(), "new", "copy");
            olderNote.CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            newerNote.CreatedAtUtc = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

            AssertEqual("Newer", store.GetMostRecentlyWrittenProject()?.Name, "The project with the latest note should win.");

            // The Zetl Logs infra project is appended to constantly but must
            // never be chosen as the last-written project.
            store.AppendLogNotes(["log line"], maxDayBuckets: 14, maxNotesPerBucket: 2000);
            AssertEqual("Newer", store.GetMostRecentlyWrittenProject()?.Name, "Zetl Logs must be excluded from the last-written project.");
        }

        private static void CaptureOriginRespectsPrivacyDetail()
        {
            var full = ZetlCaptureOrigin.Create(
                "Browser",
                "browser",
                "Private document title",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle);
            AssertEqual("Browser", full?.ApplicationName, "Full origin should retain the application name.");
            AssertEqual("browser", full?.ProcessName, "Full origin should retain the process name.");
            AssertEqual("Private document title", full?.WindowTitle, "Full origin should retain the window title.");

            var applicationOnly = ZetlCaptureOrigin.Create(
                "Browser",
                "browser",
                "Private document title",
                ZetlCaptureOriginDetail.ApplicationOnly);
            AssertEqual("Browser", applicationOnly?.ApplicationName, "Application-only origin should retain the application.");
            AssertEqual<string?>(null, applicationOnly?.WindowTitle, "Application-only origin should omit the window title.");

            AssertEqual<ZetlCaptureOrigin?>(
                null,
                ZetlCaptureOrigin.Create(
                    "Browser",
                    "browser",
                    "Private document title",
                    ZetlCaptureOriginDetail.Off),
                "Disabled origin capture should produce no metadata envelope.");
        }

        private static void CaptureOriginRoundTripsWithNotes()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            store.AddNote(
                project.Buckets[0],
                "captured text",
                "copy",
                ZetlCaptureOrigin.Create(
                    "Browser",
                    "browser",
                    "Research — Browser",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle));

            var reloaded = new ZetlStateStore(temp.Path);
            var note = reloaded.State.Projects
                .Single(project => project.Name == "Demo")
                .Buckets.Single(bucket => bucket.Name == "Inbox")
                .Notes.Single();
            AssertEqual("Browser", note.CaptureOrigin?.ApplicationName, "Application name should persist with the note.");
            AssertEqual("browser", note.CaptureOrigin?.ProcessName, "Process name should persist with the note.");
            AssertEqual("Research — Browser", note.CaptureOrigin?.WindowTitle, "Window title should persist with the note.");
            AssertTrue(note.HasCaptureOrigin, "A persisted origin should remain displayable.");
        }

        private static void CleanExportStripsCaptureOrigin()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var note = store.AddNote(
                project.Buckets[0],
                "captured text",
                "copy",
                captureOrigin: ZetlCaptureOrigin.Create(
                    "Editor",
                    "editor",
                    "Sensitive customer name",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle),
                richHtml: "<p data-private=\"producer-metadata\"><strong>captured text</strong></p>",
                replayFormats:
                [
                    new ZetlClipboardFormatData(
                        50002,
                        [1, 2, 3],
                        "Star Embed Source (XML)")
                ]);

            var clean = ZetlProjectExportSnapshot.Create(project, includeCaptureOrigins: false);
            var archive = ZetlProjectExportSnapshot.Create(project, includeCaptureOrigins: true);

            AssertEqual<ZetlCaptureOrigin?>(
                null,
                clean.Buckets.SelectMany(bucket => bucket.Notes).Single().CaptureOrigin,
                "A clean export snapshot should remove the complete capture-origin envelope.");
            AssertEqual<string?>(
                null,
                clean.Buckets.SelectMany(bucket => bucket.Notes).Single().RichHtml,
                "A clean export snapshot should remove hidden source HTML.");
            AssertEqual(
                "Sensitive customer name",
                archive.Buckets.SelectMany(bucket => bucket.Notes).Single().CaptureOrigin?.WindowTitle,
                "An archive export snapshot should preserve capture origin.");
            AssertTrue(
                archive.Buckets.SelectMany(bucket => bucket.Notes).Single().RichHtml
                    ?.Contains("producer-metadata", StringComparison.Ordinal) == true,
                "An archive export snapshot should preserve Replay's source HTML.");
            AssertEqual<List<ZetlClipboardFormatData>?>(
                null,
                clean.Buckets.SelectMany(bucket => bucket.Notes).Single().ReplayFormats,
                "A clean export snapshot should remove native Replay formats.");
            AssertTrue(
                archive.Buckets.SelectMany(bucket => bucket.Notes).Single().ReplayFormats
                    ?.Any(item => item.RegisteredName == "Star Embed Source (XML)") == true,
                "An archive export snapshot should preserve native Replay formats.");
            AssertTrue(note.CaptureOrigin is not null, "Sanitizing an export snapshot must not modify the live project.");
        }

        private static void ProjectPackagesSeparateProvenance()
        {
            using var temp = new TempStateFile();
            var root = System.IO.Path.GetDirectoryName(temp.Path)!;
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Share Me", ["Inbox"], "Inbox");
            var liveNote = store.AddNote(
                project.Buckets[0],
                "captured text",
                "copy",
                ZetlCaptureOrigin.Create(
                    "Browser",
                    "browser",
                    "Private account title",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
            var cleanPath = System.IO.Path.Combine(root, "clean.zetl.zip");
            var archivePath = System.IO.Path.Combine(root, "archive.zetl.zip");
            var exportedAt = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

            ZetlProjectExportPackage.Write(
                cleanPath,
                project,
                includeCaptureOrigins: false,
                exportedAtUtc: exportedAt);
            ZetlProjectExportPackage.Write(
                archivePath,
                project,
                includeCaptureOrigins: true,
                exportedAtUtc: exportedAt);

            (ZetlProjectExportManifest Manifest, ZetlProject Project) ReadPackage(string path)
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(path);
                AssertEqual(2, archive.Entries.Count, "A text-only package should contain only its manifest and project snapshot.");
                var manifestEntry = archive.GetEntry(ZetlProjectExportPackage.ManifestEntryName)
                    ?? throw new InvalidOperationException("Missing export manifest.");
                var projectEntry = archive.GetEntry(ZetlProjectExportPackage.ProjectEntryName)
                    ?? throw new InvalidOperationException("Missing project snapshot.");
                using var manifestStream = manifestEntry.Open();
                var manifest = System.Text.Json.JsonSerializer.Deserialize<ZetlProjectExportManifest>(
                    manifestStream,
                    JsonFile.Options)
                    ?? throw new InvalidOperationException("Invalid export manifest.");
                using var projectStream = projectEntry.Open();
                var packagedProject = System.Text.Json.JsonSerializer.Deserialize<ZetlProject>(
                    projectStream,
                    JsonFile.Options)
                    ?? throw new InvalidOperationException("Invalid project snapshot.");
                return (manifest, packagedProject);
            }

            var clean = ReadPackage(cleanPath);
            var archiveCopy = ReadPackage(archivePath);
            AssertEqual("zetl-project", clean.Manifest.Format, "The package manifest should identify the format.");
            AssertEqual(exportedAt, clean.Manifest.ExportedAtUtc, "The manifest should record export time.");
            AssertFalse(clean.Manifest.CaptureOriginsIncluded, "Clean package manifest should declare stripped provenance.");
            AssertEqual<ZetlCaptureOrigin?>(
                null,
                clean.Project.Buckets.SelectMany(bucket => bucket.Notes).Single().CaptureOrigin,
                "Clean package should not contain capture provenance.");
            AssertTrue(archiveCopy.Manifest.CaptureOriginsIncluded, "Archive package manifest should declare retained provenance.");
            AssertEqual(
                "Private account title",
                archiveCopy.Project.Buckets.SelectMany(bucket => bucket.Notes).Single().CaptureOrigin?.WindowTitle,
                "Archive package should retain capture provenance.");
            AssertTrue(liveNote.CaptureOrigin is not null, "Writing either package must leave live state untouched.");
            AssertEqual(
                0,
                Directory.GetFiles(root, "*.tmp").Length,
                "Successful package writes should not leave temporary files behind.");
        }

        private static void ImageSlipsStoreDeduplicatedAssets()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Images", ["Inbox"], "Inbox");
            var bucket = project.Buckets.Single(item => item.Name == "Inbox");
            var image = new ZetlClipboardImage([1, 2, 3, 4, 5], 20, 10);

            var first = store.AddImageNote(
                project,
                bucket,
                image,
                "copy",
                ZetlCaptureOrigin.Create(
                    "Snipping Tool",
                    "SnippingTool",
                    "Screenshot",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle),
                "Diagram");
            var second = store.AddImageNote(project, bucket, image, "copy");

            AssertTrue(first.IsImage, "An image slip should identify its typed content.");
            AssertEqual("Diagram", first.Text, "An image caption should remain separate from its asset bytes.");
            AssertEqual(first.Image?.RelativePath, second.Image?.RelativePath, "Equal image content should deduplicate by hash.");
            AssertEqual(1, store.GetProjectAssets(project).Count, "Deduplicated image content should create one asset file.");
            AssertEqual(5, store.ReadImageAsset(project, first)?.Length, "Stored image bytes should be readable through the project store.");
            var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket, first);
            AssertEqual(
                ZETL.Contracts.ZetlSlipType.Picture,
                snapshot.Type,
                "Kastn snapshots should retain the image slip type.");
            AssertEqual("Diagram", snapshot.Text, "The image caption should be available to Kastn.");
            AssertEqual(20, snapshot.Picture?.Width, "Kastn snapshots should expose image dimensions.");
            AssertEqual(
                "Screenshot",
                snapshot.CaptureOrigin?.WindowTitle,
                "Kastn snapshots should expose private capture provenance locally.");

            store.DeleteNote(bucket, first.Id);
            AssertEqual(1, store.GetProjectAssets(project).Count, "Deleting a slip should retain its asset for undo safety.");
            store.RestoreNote(bucket, first);

            var reloaded = new ZetlStateStore(temp.Path);
            var loadedProject = reloaded.State.Projects.Single(item => item.Name == "Images");
            var loadedImage = loadedProject.Buckets
                .Single(item => item.Name == "Inbox")
                .Notes.First();
            AssertTrue(loadedImage.IsImage, "Image slip type should survive persistence.");
            AssertEqual(20, loadedImage.Image?.Width, "Image dimensions should survive persistence.");
        }

        private static void ImageProjectPackagesIncludeAssets()
        {
            using var temp = new TempStateFile();
            var root = System.IO.Path.GetDirectoryName(temp.Path)!;
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Images", ["Inbox"], "Inbox");
            var bytes = new byte[] { 9, 8, 7, 6 };
            var note = store.AddImageNote(
                project,
                project.Buckets.Single(item => item.Name == "Inbox"),
                new ZetlClipboardImage(bytes, 2, 2),
                "copy",
                sourceUrl: "https://private.example/image.png?token=secret");
            var exportPath = System.IO.Path.Combine(root, "images.zetl.zip");

            ZetlProjectExportPackage.Write(
                exportPath,
                project,
                includeCaptureOrigins: false,
                assets: store.GetProjectAssets(project));

            using var archive = System.IO.Compression.ZipFile.OpenRead(exportPath);
            AssertEqual(3, archive.Entries.Count, "An image package should include manifest, project, and asset entries.");
            var assetEntry = archive.GetEntry(note.Image!.RelativePath)
                ?? throw new InvalidOperationException("Missing packaged image asset.");
            using var assetStream = assetEntry.Open();
            using var copied = new MemoryStream();
            assetStream.CopyTo(copied);
            AssertEqual(bytes.Length, copied.ToArray().Length, "The package should preserve normalized image bytes.");

            var manifestEntry = archive.GetEntry(ZetlProjectExportPackage.ManifestEntryName)!;
            using var manifestStream = manifestEntry.Open();
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ZetlProjectExportManifest>(
                manifestStream,
                JsonFile.Options)!;
            AssertEqual(1, manifest.AssetCount, "The package manifest should report included assets.");
            AssertFalse(manifest.SourceUrlsIncluded, "A clean package should declare stripped image source URLs.");

            var projectEntry = archive.GetEntry(ZetlProjectExportPackage.ProjectEntryName)!;
            using var projectStream = projectEntry.Open();
            var packagedProject = System.Text.Json.JsonSerializer.Deserialize<ZetlProject>(
                projectStream,
                JsonFile.Options)!;
            AssertEqual<string?>(
                null,
                packagedProject.Buckets.SelectMany(bucket => bucket.Notes).Single().Image?.SourceUrl,
                "A clean package should strip private image source URLs.");
            AssertTrue(note.Image?.SourceUrl is not null, "Clean export must not modify the live image source URL.");
        }

        private static void StateCreatesJournalDefaultProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.GetOrCreateDefaultProject();
            AssertEqual(store.DefaultProjectName(), project.Name, "The default capture home is the Journal.");
            AssertTrue(project.JournalMode, "The default home is journal-mode.");
            AssertEqual(ZetlStateStore.JournalBucketName(DateTime.Now, 0), store.ActiveBucket?.Name, "The journal highlights today's day bucket.");

            // A fresh weekly journal seeds the whole Mon–Sun week up front as empty
            // day-parent slots (children stay lazy), so future days are ready to hold
            // reminder notes.
            var monday = DateTime.Now.AddDays(-(((int)DateTime.Now.DayOfWeek + 6) % 7)).Date;
            for (var i = 0; i < 7; i++)
            {
                var dayName = ZetlStateStore.JournalBucketName(monday.AddDays(i), 0);
                var dayBucket = project.Buckets.SingleOrDefault(bucket => bucket.ParentBucketId is null
                    && string.Equals(bucket.Name, dayName, StringComparison.OrdinalIgnoreCase));
                AssertTrue(dayBucket is not null, $"The weekly journal seeds a day bucket for {dayName}.");
                AssertEqual(0, dayBucket!.Notes.Count, $"Seeded day bucket {dayName} starts empty.");
            }
            AssertTrue(
                project.Buckets.All(bucket => !string.Equals(bucket.Name, ZetlStateStore.JournalCaptureBucketName, StringComparison.OrdinalIgnoreCase)),
                "Capture children are not created until a capture happens.");
            AssertTrue(
                project.Buckets.All(bucket => !string.Equals(bucket.Name, "Scratch", StringComparison.OrdinalIgnoreCase)),
                "A journal has no Scratch bucket.");

            // Renaming the journal keeps it the default (it is tracked by id), and
            // compile reflects the new name.
            store.UpdateProjectName(project, "Renamed");
            AssertEqual("Renamed", store.CompilePlainText(project, [store.ActiveBucket!]).Split(Environment.NewLine)[0], "Compile should use the updated project name.");
            store.ClearActiveProject();
            AssertEqual(project.Id, store.GetOrCreateDefaultProject().Id, "The renamed journal is still the default home.");
        }

        private static void StateJournalIntervalConfiguresAndRolls()
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
            var dailyProject = store.GetOrCreateDefaultProject();
            AssertEqual(store.DefaultProjectName(), dailyProject.Name, "Daily project has correct name");

            // Change interval to weekly. Next default project call should roll to a new project because the expected name doesn't match daily project's name.
            store.Defaults = store.Defaults with { JournalInterval = "Weekly", DayStartHour = 0 };
            var weeklyProject = store.GetOrCreateDefaultProject();
            AssertTrue(dailyProject.Id != weeklyProject.Id, "Changing interval rolls to a new default project");
            AssertEqual(store.DefaultProjectName(), weeklyProject.Name, "Weekly project has correct name");

            // Verify rolling via manual name change (simulating a calendar rollover)
            // Rename active weekly project to simulate an older week
            store.UpdateProjectName(weeklyProject, "Journal Week 01 2020");
            var rolledProject = store.GetOrCreateDefaultProject();
            AssertTrue(weeklyProject.Id != rolledProject.Id, "Old journal project name triggers rollover and mints fresh project");
            AssertEqual(store.DefaultProjectName(), rolledProject.Name, "Rolled project has current week's name");
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
            AssertEqual(store.DefaultProjectName(shifted: true), shifted.Name, "Shift default project should be named distinctly.");
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

        private static void StateFinishStartsFreshJournal()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);

            var first = store.GetOrCreateDefaultProject();
            AssertTrue(first.JournalMode, "The default home is the Journal.");

            var finished = store.FinishActiveProject();
            AssertEqual(first.Id, finished?.Id, "Finish should return the sealed journal.");
            AssertEqual("Finished", first.Status, "Finish should mark the journal Finished.");
            AssertEqual<ZetlProject?>(null, store.ActiveProject, "Finish should clear the lane.");

            // The Journal is never reopened once sealed (a non-Active project is never
            // lane-active); capture mints a fresh journal instead.
            var second = store.GetOrCreateDefaultProject();
            AssertTrue(second.Id != first.Id, "Capture after finishing the journal starts a fresh one.");
            AssertTrue(second.JournalMode, "The fresh journal is journal-mode.");
            AssertEqual("Active", second.Status, "The fresh journal starts Active.");
            AssertTrue(
                store.State.Projects.Any(project => project.Id == first.Id && project.Status == "Finished"),
                "The finished journal is retained, not deleted.");
        }

        private static void StateFinishWithNoActiveProjectIsNoop()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.GetOrCreateDefaultProject();
            store.ClearActiveProject();

            var result = store.FinishActiveProject();
            AssertEqual<ZetlProject?>(null, result, "Finishing an empty lane should be a no-op.");
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

        private static void DefaultConfigParses()
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
            AssertEqual("Deleted", deleted.Settings.Kind, "Deleted bucket should use a protected kind.");
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
            AssertEqual("Deleted", deleted.Settings.Kind, "Deleted should not change kind.");
            AssertFalse(deleted.Settings.PopMode, "Deleted should not enable Pop.");
            AssertTrue(project.Buckets.Any(bucket => bucket.Id == deleted.Id), "Deleted should not be deletable.");

            var loaded = new ZetlStateStore(temp.Path);
            var loadedProject = loaded.State.Projects.Single(project => project.Name == "Demo");
            var loadedDeleted = loadedProject.Buckets.Single(bucket => bucket.Id == deleted.Id);
            AssertEqual("Deleted", loadedDeleted.Name, "Deleted name should persist.");
            AssertEqual("Deleted", loadedDeleted.Settings.Kind, "Deleted kind should persist.");
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
            AssertTrue(ZetlStateStore.IsReplayBucket(bucket), "Bucket settings should set the current kind.");
            AssertFalse(bucket.Settings.PopMode, "Replay bucket settings should disable pop mode.");
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

        private static void StateReplayResumesQueuedSlipsAcrossRestart()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path, "replay-session");
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "one", "copy");
            store.AddNote(queue, "two", "copy");

            AssertTrue(store.TryPeekNextReplayNote(queue, out var first), "Replay bucket should expose its first slip.");
            AssertEqual("one", first?.Text, "Replay should start with the oldest current-session slip.");
            AssertTrue(store.TryConsumeReplayNote(queue, first!.Id), "Replay should consume the first slip.");

            var reloaded = new ZetlStateStore(temp.Path, "new-session");
            var loadedQueue = reloaded.State.Projects.Single().Buckets.Single(bucket => bucket.Name == "Queue");
            AssertTrue(reloaded.TryPeekNextReplayNote(loadedQueue, out var second), "Replay should resume a visible queued slip after restart.");
            AssertEqual("two", second?.Text, "Replay should resume at the first unconsumed slip.");
            AssertTrue(reloaded.TryConsumeReplayNote(loadedQueue, second!.Id), "Replay should consume a prior-session slip.");
            AssertFalse(reloaded.TryPeekNextReplayNote(loadedQueue, out _), "Replay should be empty after its last slip is consumed.");
            AssertEqual(0, loadedQueue.Notes.Count, "Consumed Replay slips should stay consumed.");
        }

        private static void StateReplayArchivesConsumedSlipsForReview()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path, "replay-session");
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "posted", "copy");

            AssertTrue(store.TryPeekNextReplayNote(queue, out var note), "Replay bucket should expose a slip.");
            AssertTrue(store.TryConsumeReplayNoteToReview(project, queue, note!.Id, out var reviewBucket), "Replay should consume into review.");

            AssertTrue(reviewBucket is not null, "Replay consume should create a review bucket.");
            AssertEqual(queue.Id, project.ActiveBucketId, "Review archive should not steal the active bucket.");
            AssertEqual("Queue Review", reviewBucket!.Name, "Review bucket should be named from the Replay bucket.");
            AssertEqual("Standard", reviewBucket.Settings.Kind, "Review bucket should stay standard.");
            AssertEqual("posted", reviewBucket.Notes.Single().Text, "Review bucket should keep consumed text.");
            AssertEqual("replay", reviewBucket.Notes.Single().Source, "Review note should be tagged as replay.");
            AssertFalse(store.TryPeekNextReplayNote(queue, out _), "Consumed Replay slip should leave the queue.");

            store.AddNote(queue, "posted again", "copy");
            AssertTrue(store.TryPeekNextReplayNote(queue, out var second), "Replay bucket should expose another slip.");
            AssertTrue(store.TryConsumeReplayNoteToReview(project, queue, second!.Id, out var sameReviewBucket), "Replay should consume into the same review bucket.");
            AssertTrue(sameReviewBucket is not null, "Replay consume should return the reused review bucket.");
            AssertEqual(reviewBucket.Id, sameReviewBucket!.Id, "Review bucket should be reused.");
            AssertEqual(2, sameReviewBucket.Notes.Count, "Review bucket should accumulate consumed Replay slips.");
        }

        private static void StateReplayRestoresConsumedSlipsFromReview()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path, "replay-session");
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "posted", "copy");

            AssertTrue(store.TryPeekNextReplayNote(queue, out var note), "Replay bucket should expose a slip.");
            AssertTrue(
                store.TryConsumeReplayNoteToReview(project, queue, note!.Id, out var reviewBucket, out var consumedNote, out var reviewNote),
                "Replay should consume with undo details.");
            AssertTrue(consumedNote is not null, "Replay consume should return the consumed slip.");
            AssertTrue(reviewBucket is not null, "Replay consume should return the review bucket.");
            AssertTrue(reviewNote is not null, "Replay consume should return the review slip.");

            store.SetBucketKind(queue, "Standard");
            store.RestoreReplayConsumedNote(queue, consumedNote!, reviewBucket, reviewNote?.Id);

            AssertTrue(ZetlStateStore.IsReplayBucket(queue), "Replay undo should restore Replay kind.");
            AssertEqual("posted", queue.Notes.Single().Text, "Replay undo should restore the consumed slip.");
            AssertEqual(0, reviewBucket!.Notes.Count, "Replay undo should remove the review copy.");
        }

        private static void StateReplayDisablesPopMode()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.ActiveBucket!;

            store.SetBucketPopMode(queue, true);
            AssertTrue(queue.Settings.PopMode, "Standard bucket should accept pop mode.");
            store.SetBucketKind(queue, "Replay");
            AssertFalse(queue.Settings.PopMode, "Switching to Replay should turn pop mode off.");
            store.SetBucketPopMode(queue, true);
            AssertFalse(queue.Settings.PopMode, "Replay bucket should reject pop mode.");
        }

        private static void StateMapsLegacyFifoKindToReplay()
        {
            using var temp = new TempStateFile();
            // A state file written by an older build that used the "Fifo" kind.
            var legacyJson =
                """
                { "version": 1, "activeProjectId": "p1", "projects": [ { "id": "p1", "name": "Demo", "activeBucketId": "b1", "buckets": [ { "id": "b1", "name": "Queue", "kind": "Fifo", "defaultKind": "Fifo", "fifoReviewBucketId": "b2", "notes": [] }, { "id": "b2", "name": "Queue Review", "kind": "Standard", "defaultKind": "Standard", "notes": [] } ] } ] }
                """;
            System.IO.File.WriteAllText(temp.Path, legacyJson);

            var store = new ZetlStateStore(temp.Path);
            var queue = store.ActiveBucket!;
            AssertEqual("Queue", queue.Name, "Legacy bucket should load.");
            AssertEqual("Replay", queue.Settings.Kind, "Legacy Fifo kind should load as Replay.");
            AssertEqual("Replay", queue.Settings.DefaultKind, "Legacy Fifo default kind should load as Replay.");
            AssertTrue(ZetlStateStore.IsReplayBucket(queue), "Legacy Fifo bucket should still be a Replay bucket.");
            AssertEqual("b2", queue.Settings.ReplayReviewBucketId, "Legacy Replay review links should load.");

            store.SetBucketKind(queue, queue.Settings.Kind);
            var projectPath = Directory.GetFiles(
                System.IO.Path.GetDirectoryName(temp.Path)!,
                "project.json",
                SearchOption.AllDirectories).Single();
            var savedJson = System.IO.File.ReadAllText(projectPath);
            AssertTrue(
                savedJson.Contains("\"fifoReviewBucketId\": \"b2\"", StringComparison.Ordinal),
                "Replay review links should retain their historical JSON field name.");
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

            var projectPath = Directory.GetFiles(
                System.IO.Path.GetDirectoryName(temp.Path)!,
                "project.json",
                SearchOption.AllDirectories).Single();
            var projectJson = System.IO.File.ReadAllText(projectPath);
            AssertTrue(projectJson.Contains("\"notes\":", StringComparison.Ordinal), "Slip storage should retain the historical notes field.");
            AssertFalse(projectJson.Contains("\"slips\":", StringComparison.Ordinal), "Slip storage should not introduce a second serialized collection.");
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
            store.Settings.KastnTemporaryTemplateLaneDefault = ZetlStateStore.ShiftLane;
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
                ZetlStateStore.ShiftLane,
                loaded.Settings.KastnTemporaryTemplateLaneDefault,
                "Kastn temporary template lane default should round-trip.");
            AssertTrue(
                loaded.Settings.KastnPreferSlipKindOverBucketKind,
                "Kastn slip-kind preference should round-trip.");
        }

        private static void KastnStateRoundTripsLastProject()
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

        private static void JournalBucketRollsAtDayStartHour()
        {
            // Day parents are named "ddd MM-dd" (2026-06-24 is a Wednesday, 06-23 a Tuesday).
            // Midnight boundary: every clock hour maps to its own calendar day.
            AssertEqual(
                "Wed 06-24",
                ZetlStateStore.JournalBucketName(new DateTime(2026, 6, 24, 0, 30, 0), 0),
                "Midnight start: 00:30 belongs to that day.");
            AssertEqual(
                "Wed 06-24",
                ZetlStateStore.JournalBucketName(new DateTime(2026, 6, 24, 23, 59, 0), 0),
                "Midnight start: 23:59 belongs to that day.");

            // 4am start: captures before 04:00 belong to the previous day.
            AssertEqual(
                "Tue 06-23",
                ZetlStateStore.JournalBucketName(new DateTime(2026, 6, 24, 1, 0, 0), 4),
                "4am start: 01:00 rolls back to the previous day.");
            AssertEqual(
                "Tue 06-23",
                ZetlStateStore.JournalBucketName(new DateTime(2026, 6, 24, 3, 59, 0), 4),
                "4am start: 03:59 is still the previous day.");
            AssertEqual(
                "Wed 06-24",
                ZetlStateStore.JournalBucketName(new DateTime(2026, 6, 24, 4, 0, 0), 4),
                "4am start: 04:00 begins the new day.");

            // Out-of-range hours clamp rather than throw.
            AssertEqual(
                "Wed 06-24",
                ZetlStateStore.JournalBucketName(new DateTime(2026, 6, 24, 23, 30, 0), 99),
                "An out-of-range day-start hour clamps to 23.");
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

            // The default capture home is the rolling Journal (its buckets are days),
            // so the configured project-bucket names apply to deliberately created
            // projects, not here. Today's bucket still takes the compile defaults.
            var project = store.GetOrCreateDefaultProject();
            AssertTrue(project.JournalMode, "The default capture home is journal-mode.");
            var today = store.ActiveBucket!;
            AssertEqual("TSV", today.Settings.DefaultCompileMode, "Today's bucket should take the default compile mode.");
            AssertEqual(4, today.Settings.DefaultTsvRowLength, "Today's bucket should take the default TSV row length.");

            var added = store.AddBucket(project, "Extra");
            AssertEqual("TSV", added.Settings.DefaultCompileMode, "Added bucket should take the default compile mode.");
            AssertEqual(4, added.Settings.DefaultTsvRowLength, "Added bucket should take the default TSV row length.");
        }

        private static void JournalModeRollsIntoDatedBuckets()
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
            AssertEqual(ZetlStateStore.JournalCaptureBucketName, first!.Name, "The copy-capture roll targets the Capture child.");
            AssertEqual(first.Id, project.ActiveBucketId, "Today's Capture bucket becomes active.");
            var day1Parent = project.Buckets.Single(bucket => bucket.Id == first.ParentBucketId);
            AssertEqual("Tue 06-23", day1Parent.Name, "The Capture child sits under today's day parent.");
            AssertTrue(day1Parent.ParentBucketId is null, "The day parent is a top-level bucket.");

            var sameDay = store.RollJournalBucket(project, day1);
            AssertEqual(first.Id, sameDay!.Id, "A second capture the same day reuses the Capture bucket.");

            // Quick notes route to the same day parent's separate "Quick Note" child,
            // without stealing the active bucket from the copy target.
            var quickNote = store.ResolveJournalQuickNoteBucket(project, day1);
            AssertEqual(ZetlStateStore.JournalQuickNoteBucketName, quickNote!.Name, "Quick notes target the Quick Note child.");
            AssertEqual(day1Parent.Id, quickNote.ParentBucketId, "The Quick Note child shares the day parent.");
            AssertTrue(quickNote.Id != first.Id, "Capture and Quick Note are distinct children.");
            AssertEqual(first.Id, project.ActiveBucketId, "Quick notes do not steal the active bucket.");

            var nextDay = store.RollJournalBucket(project, day2);
            AssertEqual(ZetlStateStore.JournalCaptureBucketName, nextDay!.Name, "A new day creates its own Capture child.");
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

        private static void JournalAutoReturnsFromQuietProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path)
            {
                Defaults = ZetlBucketDefaults.Standard with { JournalAutoReturnHours = 2 }
            };

            var work = store.CreateProject("Work", new[] { "Notes" }, "Notes");
            AssertEqual(work.Id, store.GetActiveProject()?.Id, "A new deliberate project is active.");
            AssertEqual(work.Id, store.GetOrCreateDefaultProject().Id, "A fresh active project keeps capture.");

            // Simulate the project going quiet past the window.
            work.LastActiveUtc = DateTime.UtcNow.AddHours(-3);
            var resolved = store.GetOrCreateDefaultProject();
            AssertTrue(resolved.JournalMode, "A quiet deliberate project auto-returns capture to the Journal.");
            AssertEqual(resolved.Id, store.GetActiveProject()?.Id, "The Journal becomes the active project.");
            AssertTrue(resolved.Id != work.Id, "Capture left the quiet deliberate project.");
        }

        private static void JournalAutoReturnOffKeepsProject()
        {
            using var temp = new TempStateFile();
            // JournalAutoReturnHours defaults to 0 (off).
            var store = new ZetlStateStore(temp.Path);
            var work = store.CreateProject("Work", new[] { "Notes" }, "Notes");
            work.LastActiveUtc = DateTime.UtcNow.AddHours(-10);
            AssertEqual(
                work.Id,
                store.GetOrCreateDefaultProject().Id,
                "With auto-return off, even a long-quiet project keeps capture.");
        }

        private static void ToggleActiveProjectSwitchesBetweenJournalAndLastProject()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);

            // The Journal is the home, with no deliberate project used yet.
            store.GetOrCreateDefaultProject();
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

        private static void UrlSlipsAreDerivedFromContent()
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

            var origin = ZetlCaptureOrigin.Create(
                "Editor",
                "editor",
                "Draft",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle);
            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                origin).GetAwaiter().GetResult();

            var note = store.GetActiveBucket()!.Notes.Single();
            AssertEqual("copied text", note.Text, "Auto-capture should trim and save copied text.");
            AssertEqual("copy", note.Source, "Auto-capture should mark the copy source.");
            AssertEqual("Editor", note.CaptureOrigin?.ApplicationName, "Auto-capture should retain its keydown origin.");
            AssertEqual(
                "Captured to Inbox in Demo.",
                notifications.Messages.Single(),
                "Auto-capture should report its destination.");
            AssertEqual(project.Id, store.GetActiveProject()!.Id, "Auto-capture should keep the active project.");
        }

        private static void RuntimeAutoCapturesAndReplaysRichText()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            var richHtml =
                "<table><tr><td style=\"font-weight:bold;text-decoration:underline;text-align:right\">A1</td></tr></table>";
            var clipboard = new FakeClipboard(null, changeToken: 2);
            clipboard.SetMixedState("A1", richHtml, image: null, changeToken: 2);
            clipboard.NativeReplayFormats =
            [
                new ZetlClipboardFormatData(
                    13,
                    System.Text.Encoding.Unicode.GetBytes("A1\0")),
                new ZetlClipboardFormatData(
                    50001,
                    System.Text.Encoding.UTF8.GetBytes(richHtml),
                    "HTML Format"),
                new ZetlClipboardFormatData(
                    50002,
                    [1, 2, 3, 4],
                    "Star Embed Source (XML)"),
                new ZetlClipboardFormatData(
                    50003,
                    [5, 6, 7, 8],
                    "Star Object Descriptor (XML)")
            ];
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _,
                replayResumeClipboard: false);

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                captureOrigin: null).GetAwaiter().GetResult();

            var captured = queue.Notes.Single();
            AssertEqual(richHtml, captured.RichHtml, "Auto-capture should retain the source HTML fragment.");
            AssertTrue(
                captured.ReplayFormats?.Any(item =>
                    item.RegisteredName == "Star Embed Source (XML)") == true,
                "Auto-capture should retain Calc's native source representation.");
            store.SetBucketKind(queue, "Replay");
            clipboard.SetState("ordinary user clipboard", changeToken: 3);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Rich Replay should suppress the physical paste.");
            AssertEqual(1, keyboard.PasteCount, "Rich Replay should send one synthetic paste.");
            AssertEqual("A1", clipboard.Text, "Rich Replay should keep the plain-text fallback.");
            AssertEqual(richHtml, clipboard.RichHtml, "Rich Replay should stage the captured HTML fragment.");
            AssertTrue(
                clipboard.LastRestoredRawFormats?.Any(item =>
                    item.RegisteredName == "Star Embed Source (XML)") == true,
                "Rich Replay should prefer Calc's native representation over HTML import.");
            var review = store.GetActiveProject()!.Buckets
                .Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
            AssertEqual(richHtml, review.Notes.Single().RichHtml, "Replay review should retain the rich representation.");
        }

        private static void RuntimeAutoCapturesCopiedImages()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var clipboard = new FakeClipboard(null, changeToken: 2)
            {
                Image = new ZetlClipboardImage([1, 2, 3], 30, 20)
            };
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out _,
                out _);

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                ZetlCaptureOrigin.Create(
                    "Image Editor",
                    "editor",
                    "Canvas",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle))
                .GetAwaiter().GetResult();

            var note = store.GetActiveBucket()!.Notes.Single();
            AssertTrue(note.IsImage, "An image clipboard should create an image slip.");
            AssertEqual(30, note.Image?.Width, "Captured image width should persist.");
            AssertEqual("Canvas", note.CaptureOrigin?.WindowTitle, "Image capture should retain keydown provenance.");
            AssertEqual(1, store.GetProjectAssets(project).Count, "Image auto-capture should write one project asset.");
            AssertEqual(
                "Captured image to Inbox in Demo.",
                notifications.Messages.Single(),
                "Image capture should report its destination clearly.");
        }

        private static void RuntimeAutoCapturesDualClipboardAsText()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            // A spreadsheet copy: the clipboard carries the cell text and a
            // bitmap rendering at the same time.
            var richHtml = "<table><tr><td><strong>A1</strong></td><td>B1</td></tr></table>";
            var clipboard = new FakeClipboard(null, changeToken: 2);
            clipboard.SetMixedState(
                "A1\tB1\nA2\tB2",
                richHtml,
                new ZetlClipboardImage([1, 2, 3], 30, 20),
                changeToken: 2);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out _,
                out _);

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                captureOrigin: null)
                .GetAwaiter().GetResult();

            var note = store.GetActiveBucket()!.Notes.Single();
            AssertFalse(note.IsImage, "A dual capture should present as text, not as a picture.");
            AssertEqual("A1\tB1\nA2\tB2", note.Text, "A dual capture should keep the clipboard text as content.");
            AssertTrue(note.Image is not null, "A dual capture should retain the clipboard picture.");
            AssertEqual(richHtml, note.RichHtml, "A dual capture should retain the rich HTML representation.");
            AssertEqual(1, store.GetProjectAssets(project).Count, "A dual capture should write its picture asset.");
            AssertEqual(
                "Captured to Inbox in Demo.",
                notifications.Messages.Single(),
                "A dual capture should report as an ordinary text capture.");
        }

        private static void RuntimeClipboardCaptureRetriesChangedGeneration()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var clipboard = new GenerationChangingClipboard();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _);

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                captureOrigin: null)
                .GetAwaiter().GetResult();

            var note = store.GetActiveBucket()!.Notes.Single();
            AssertTrue(
                clipboard.TextReadCount >= 2,
                "Capture should retry after the token changes between format reads.");
            AssertEqual("generation two", note.Text, "Capture must discard text from the superseded generation.");
            AssertEqual(
                "<p><strong>generation two</strong></p>",
                note.RichHtml,
                "HTML must come from the same generation as the committed text.");
            AssertTrue(note.Image is not null, "The coherent generation should retain its companion image.");
            AssertEqual(
                (byte)2,
                store.ReadImageAsset(project, note)?.Single(),
                "Image bytes must come from the same generation as the committed text.");
            AssertEqual(
                (byte)2,
                note.ReplayFormats?.Single().Data.Single(),
                "Native Replay data must come from the same generation as the committed text.");
        }

        private static void RuntimeClipboardCaptureRefusesUnstableGenerations()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            var clipboard = new GenerationChangingClipboard(changeEveryTextRead: true);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out _);

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                captureOrigin: null)
                .GetAwaiter().GetResult();

            AssertEqual(
                0,
                store.GetActiveBucket()!.Notes.Count,
                "Auto-capture must commit nothing when no retry observes one complete generation.");
        }

        private static void DualSlipSurvivesPersistence()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.GetActiveBucket()!;
            store.AddImageNote(
                project,
                bucket,
                new ZetlClipboardImage([9, 9, 9], 4, 4),
                "copy",
                caption: "A1\tB1",
                preferTextContent: true,
                richHtml: "<table><tr><td><strong>A1</strong></td><td>B1</td></tr></table>");

            var reloaded = new ZetlStateStore(temp.Path);

            var note = reloaded.State.Projects
                .Single(item => item.Name == "Demo")
                .Buckets.Single(item => item.Name == "Inbox")
                .Slips.Single();
            AssertFalse(note.IsImage, "A reloaded dual slip should stay text-preferred.");
            AssertEqual("A1\tB1", note.Text, "A reloaded dual slip should keep its text content.");
            AssertTrue(note.Image is not null, "A reloaded dual slip should keep its attached picture.");
            AssertTrue(
                note.RichHtml?.Contains("<strong>A1</strong>", StringComparison.Ordinal) == true,
                "A reloaded dual slip should keep its rich HTML representation.");
        }

        private static void RuntimePopRemovesDualSlipByImageHash()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.GetActiveBucket()!;
            store.SetBucketPopMode(bucket, true);
            var bytes = new byte[] { 3, 1, 4 };
            store.AddImageNote(
                project,
                bucket,
                new ZetlClipboardImage(bytes, 3, 1),
                "copy",
                caption: "A1\tB1",
                preferTextContent: true);
            // The paste re-offers both formats, exactly as the original copy did.
            var clipboard = new FakeClipboard("A1\tB1", changeToken: 1)
            {
                Image = new ZetlClipboardImage(bytes, 3, 1)
            };
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertFalse(handled, "Dual Pop should allow the physical paste through.");
            AssertEqual(0, bucket.Notes.Count, "Pop should remove the matching dual slip via its image hash.");
            AssertTrue(undo.TryPop(false, out _), "Popped dual slip should be undoable.");
        }

        private static void RuntimeReplayPastesDualSlipAsText()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddImageNote(
                project,
                queue,
                new ZetlClipboardImage([5, 5, 5], 2, 2),
                "copy",
                caption: "A1\tB1",
                preferTextContent: true);
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _,
                replayResumeClipboard: false);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Dual Replay should suppress the physical paste.");
            AssertEqual(1, keyboard.PasteCount, "Dual Replay should send one synthetic paste.");
            AssertEqual("A1\tB1", clipboard.Text, "Dual Replay should paste the preferred text representation.");
            AssertEqual(0, queue.Notes.Count, "Dual Replay should consume the queued slip.");
            var review = project.Buckets.Single(item => item.Id == queue.Settings.ReplayReviewBucketId);
            var reviewNote = review.Notes.Single();
            AssertFalse(reviewNote.IsImage, "The review copy should stay text-preferred.");
            AssertTrue(reviewNote.Image is not null, "The review copy should retain the attached picture.");
        }

        private static void RuntimeHeldCopyCapturesImagesDirectly()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 2),
                notifications,
                out _,
                out _);
            var context = ShortcutContext(VK_C, clipboardSequenceNumber: 1);
            var pending = new ZetlPendingShortcut(
                VK_C,
                shiftLane: false,
                clipboardSequenceNumber: 1,
                captureOrigin: ZetlCaptureOrigin.Create(
                    "Snipping Tool",
                    "snippingtool",
                    "Screenshot",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
            pending.SetObservedClipboardContent(
                null,
                new ZetlClipboardImage([7, 8, 9], 3, 2));

            var request = coordinator.HandleClaimedHoldAsync(context, pending)
                .GetAwaiter().GetResult();

            AssertTrue(request is ZetlNoteCaptureRequest, "Held image copy should open the shared capture dialog.");
            var capture = (ZetlNoteCaptureRequest)request!;
            AssertTrue(capture.Image is not null, "The capture request should carry the normalized image.");
            coordinator.CompleteNoteCapture(
                capture,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "Annotated screenshot",
                    StartProject: true,
                    CreateNewProject: false,
                    ProjectName: capture.Project.Name,
                    SelectedBucketName: capture.PreferredBucket!.Name,
                    SelectedBucket: capture.PreferredBucket));

            var project = store.GetActiveProject()!;
            var note = project.Buckets.SelectMany(bucket => bucket.Notes).Single();
            AssertTrue(note.IsImage, "Held image copy should create an image slip.");
            AssertEqual("Annotated screenshot", note.Text, "The image caption should be stored as slip text.");
            AssertEqual("Screenshot", note.CaptureOrigin?.WindowTitle, "Held image copy should retain its origin.");
            AssertTrue(
                notifications.Messages.Single().StartsWith("Saved image to", StringComparison.Ordinal),
                "Committed image capture should report its destination.");
        }

        private static void RuntimeHeldCopyDualSavesTextPreferred()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 2),
                notifications,
                out _,
                out _);
            var pending = new ZetlPendingShortcut(
                VK_C,
                shiftLane: false,
                clipboardSequenceNumber: 1,
                captureOrigin: null);
            pending.SetObservedClipboardContent(
                "A1\tB1",
                new ZetlClipboardImage([7, 8, 9], 3, 2));

            var request = coordinator.HandleClaimedHoldAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                pending).GetAwaiter().GetResult();

            AssertTrue(request is ZetlNoteCaptureRequest, "Held dual copy should open the capture dialog.");
            var capture = (ZetlNoteCaptureRequest)request!;
            AssertEqual("A1\tB1", capture.Text, "The dialog should receive the clipboard text as content.");
            AssertTrue(capture.Image is not null, "The dialog request should carry the clipboard picture.");
            coordinator.CompleteNoteCapture(
                capture,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "A1\tB1 edited",
                    StartProject: true,
                    CreateNewProject: false,
                    ProjectName: capture.Project.Name,
                    SelectedBucketName: capture.PreferredBucket!.Name,
                    SelectedBucket: capture.PreferredBucket));

            var note = store.GetActiveProject()!
                .Buckets.SelectMany(bucket => bucket.Notes).Single();
            AssertFalse(note.IsImage, "A held dual capture should save text-preferred.");
            AssertEqual("A1\tB1 edited", note.Text, "The edited dialog text should be the slip content.");
            AssertTrue(note.Image is not null, "The held dual capture should retain the picture.");
            AssertTrue(
                notifications.Messages.Single().StartsWith("Saved to", StringComparison.Ordinal),
                "A text-preferred dual save should report as an ordinary note.");
        }

        private static void RuntimeHeldCopyDualClearedTextSavesPicture()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(null, changeToken: 2),
                notifications,
                out _,
                out _);
            var pending = new ZetlPendingShortcut(
                VK_C,
                shiftLane: false,
                clipboardSequenceNumber: 1,
                captureOrigin: null);
            pending.SetObservedClipboardContent(
                "A1\tB1",
                new ZetlClipboardImage([7, 8, 9], 3, 2));

            var request = coordinator.HandleClaimedHoldAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1),
                pending).GetAwaiter().GetResult();
            var capture = (ZetlNoteCaptureRequest)request!;
            coordinator.CompleteNoteCapture(
                capture,
                new ZetlNoteCaptureResult(
                    Committed: true,
                    NoteText: "",
                    StartProject: true,
                    CreateNewProject: false,
                    ProjectName: capture.Project.Name,
                    SelectedBucketName: capture.PreferredBucket!.Name,
                    SelectedBucket: capture.PreferredBucket));

            var note = store.GetActiveProject()!
                .Buckets.SelectMany(bucket => bucket.Notes).Single();
            AssertTrue(note.IsImage, "Clearing the dialog text should fall back to a picture slip.");
            AssertTrue(
                notifications.Messages.Single().StartsWith("Saved image to", StringComparison.Ordinal),
                "A picture fallback save should report as an image.");
        }

        private static void RuntimeAutoCapturesCopiedImageUrls()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("https://images.example/photo", changeToken: 2),
                new FakeNotificationSink(),
                out _,
                out _,
                imageUrlResolver: new FakeImageUrlResolver(
                    new ZetlResolvedImageUrl(
                        new ZetlClipboardImage([4, 5, 6], 40, 30),
                        "https://cdn.example/photo.png")));

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1))
                .GetAwaiter().GetResult();

            var note = store.GetActiveBucket()!.Notes.Single();
            AssertTrue(note.IsImage, "An image URL should become an image slip.");
            AssertEqual(
                "https://cdn.example/photo.png",
                note.Image?.SourceUrl,
                "The final downloaded image URL should remain attached to the asset descriptor.");
            AssertEqual(1, store.GetProjectAssets(project).Count, "A downloaded image URL should write one asset.");
        }

        private static void RuntimeKeepsNonImageUrlsAsText()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Inbox"], "Inbox");
            const string url = "https://example.com/article";
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard(url, changeToken: 2),
                new FakeNotificationSink(),
                out _,
                out _,
                imageUrlResolver: new FakeImageUrlResolver(null));

            coordinator.OnPhysicalShortcutPassedThroughAsync(
                ShortcutContext(VK_C, clipboardSequenceNumber: 1))
                .GetAwaiter().GetResult();

            var note = store.GetActiveBucket()!.Notes.Single();
            AssertFalse(note.IsImage, "A URL that does not resolve as an image should remain text.");
            AssertEqual(url, note.Text, "Failed image resolution must preserve the copied URL.");
        }

        private static void RuntimeHeldCopyCapturesImageUrls()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var coordinator = CreateShortcutCoordinator(
                store,
                new FakeClipboard("https://images.example/photo", changeToken: 2),
                new FakeNotificationSink(),
                out _,
                out _,
                imageUrlResolver: new FakeImageUrlResolver(
                    new ZetlResolvedImageUrl(
                        new ZetlClipboardImage([7, 8, 9], 3, 2),
                        "https://images.example/photo.png")));
            var context = ShortcutContext(VK_C, clipboardSequenceNumber: 1);
            var pending = new ZetlPendingShortcut(VK_C, false, 1);
            pending.SetObservedClipboardContent("https://images.example/photo", null);

            var request = coordinator.HandleClaimedHoldAsync(context, pending)
                .GetAwaiter().GetResult() as ZetlNoteCaptureRequest;

            AssertTrue(request?.Image is not null, "Held copy should preview a downloaded image URL.");
            AssertEqual(
                "https://images.example/photo.png",
                request?.ImageSourceUrl,
                "Held capture should retain the downloaded image URL.");
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
                context,
                ZetlCaptureOrigin.Create(
                    "Browser",
                    "browser",
                    "Held copy source",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
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
            AssertEqual(
                "Held copy source",
                ((ZetlNoteCaptureRequest)holdTask.Result!).CaptureOrigin?.WindowTitle,
                "The hold request should retain the keydown origin while clipboard observation finishes.");
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
            var review = project.Buckets.Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
            AssertEqual("queued value", review.Notes.Single().Text, "Replay should archive the consumed note.");
            AssertEqual("Standard", queue.Settings.Kind, "An empty Replay bucket should return to Standard.");
            AssertTrue(undo.TryPop(false, out _), "Replay consumption should be undoable.");
        }

        private static void RuntimeReplayClipboardSessionReportsRestoreOutcomes()
        {
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var session = new ZetlReplayClipboardSession(clipboard);
            var item = ZetlClipboardSnapshot.FromText("replay item");

            AssertEqual(
                ZetlClipboardRestoreOutcome.NoBackup,
                session.RestoreOriginalIfOwned(shifted: false),
                "A fresh Replay lane should report that it has no user backup.");
            AssertTrue(
                session.TryPreserveUserClipboard(shifted: false, out var failureReason),
                $"Replay should preserve the initial clipboard: {failureReason}");
            AssertTrue(
                session.TryStage(shifted: false, item, out var injectedToken),
                "Replay should stage its item through the content policy.");
            AssertEqual(
                ZetlClipboardRestoreOutcome.Restored,
                session.RestoreIfOwned(shifted: false, item, injectedToken),
                "An unchanged staged clipboard should restore successfully.");
            AssertEqual(
                "user clipboard",
                clipboard.Text,
                "A successful restore should put back the preserved content.");

            AssertTrue(
                session.TryStage(shifted: false, item, out injectedToken),
                "Replay should stage another item after a successful restore.");
            AssertTrue(
                clipboard.SetRichText("new user copy", "<strong>new user copy</strong>"),
                "The test should replace the staged clipboard with newer content.");
            AssertEqual(
                ZetlClipboardRestoreOutcome.OwnershipLost,
                session.RestoreIfOwned(shifted: false, item, injectedToken),
                "A newer clipboard generation should cancel restoration.");
            AssertEqual(
                "<strong>new user copy</strong>",
                clipboard.RichHtml,
                "Ownership loss must leave the newer rich clipboard intact.");

            session.Reset(shifted: false);
            AssertTrue(
                session.TryPreserveUserClipboard(shifted: false, out failureReason),
                $"Replay should preserve the newer user clipboard: {failureReason}");
            AssertTrue(
                session.TryStage(shifted: false, item, out injectedToken),
                "Replay should stage before the injected restore failure.");
            clipboard.SetTextSucceeds = false;
            AssertEqual(
                ZetlClipboardRestoreOutcome.Failed,
                session.RestoreIfOwned(shifted: false, item, injectedToken),
                "A backend restore rejection should have an explicit failed outcome.");

            clipboard.SetTextSucceeds = true;
            clipboard.WriteResultOverride = null;
            session.Reset(shifted: false);
            AssertTrue(
                session.TryPreserveUserClipboard(shifted: false, out failureReason),
                $"Replay should preserve before testing an uncertain rollback: {failureReason}");
            AssertTrue(
                session.TryStage(shifted: false, item, out injectedToken),
                "Replay should stage before an uncertain restore failure.");
            clipboard.WriteResultOverride = new(
                ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
                FailureReason: "injected partial rollback");
            AssertEqual(
                ZetlClipboardRestoreOutcome.FailedClipboardUncertain,
                session.RestoreIfOwned(shifted: false, item, injectedToken),
                "Replay must distinguish a failed restore whose rollback was also partial.");
        }

        private static void RuntimeClipboardContentWriterChoosesRichestRepresentation()
        {
            var clipboard = new FakeClipboard("before", changeToken: 1);
            var nativeFormats = new[]
            {
                new ZetlClipboardFormatData(
                    13,
                    System.Text.Encoding.Unicode.GetBytes("native text\0"))
            };

            AssertTrue(
                ZetlClipboardContentWriter.TryWrite(
                    clipboard,
                    "plain fallback",
                    "<strong>HTML fallback</strong>",
                    nativeFormats),
                "Native Replay formats should be writable through the content policy.");
            AssertTrue(
                ReferenceEquals(nativeFormats, clipboard.LastRestoredRawFormats),
                "Native formats should take priority over HTML and plain text.");

            AssertTrue(
                ZetlClipboardContentWriter.TryWrite(
                    clipboard,
                    "rich fallback",
                    "<em>rich fallback</em>",
                    replayFormats: null),
                "HTML should be used when no native representation exists.");
            AssertEqual(
                "<em>rich fallback</em>",
                clipboard.RichHtml,
                "The rich representation should reach the backend.");

            AssertTrue(
                ZetlClipboardContentWriter.TryWrite(
                    clipboard,
                    "plain only",
                    html: null,
                    replayFormats: null),
                "Plain text should remain the final fallback.");
            AssertEqual("plain only", clipboard.Text, "The plain fallback should reach the backend.");
        }

        private static void RuntimeReplayResumesVisibleItemsAfterRestart()
        {
            using var temp = new TempStateFile();
            var firstSession = new ZetlStateStore(temp.Path, "first-session");
            firstSession.CreateProject("Demo", ["Queue"], "Queue");
            var originalQueue = firstSession.GetActiveBucket()!;
            firstSession.SetBucketKind(originalQueue, "Replay");
            firstSession.AddNote(originalQueue, "first queued value", "copy");
            firstSession.AddNote(originalQueue, "second queued value", "copy");

            var store = new ZetlStateStore(temp.Path, "restarted-session");
            var queue = store.GetActiveBucket()!;
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay should handle a visible prior-session queue item.");
            AssertEqual(1, keyboard.PasteCount, "Replay should paste rather than pass through an apparently empty queue.");
            AssertEqual("user clipboard", clipboard.Text, "Replay should restore the user's clipboard after restart.");
            AssertEqual(1, queue.Notes.Count, "Replay should consume exactly the first visible queued item.");
            AssertEqual("second queued value", queue.Notes.Single().Text, "Replay should leave the next item queued.");
            AssertEqual("Replay", queue.Settings.Kind, "Replay should remain enabled while a visible item remains.");
        }

        private static void RuntimeRapidReplayTapsConsumeDistinctSlips()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "first queued value", "copy");
            store.AddNote(queue, "second queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out _,
                replayResumeClipboard: false);
            var firstPaste = keyboard.DeferNextPaste();

            var firstHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V));
            var secondHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(firstHandled && secondHandled, "Both rapid Replay taps should be handled.");
            AssertEqual(1, keyboard.PasteCount, "The second same-lane tap must wait for the first paste outcome.");
            AssertEqual(2, queue.Notes.Count, "No slip should be consumed before the first paste succeeds.");

            firstPaste.SetResult(true);
            AssertTrue(
                notifications.WaitForCount(2, TimeSpan.FromSeconds(5)),
                "Both serialized Replay taps should persist and report completion within the timeout.");
            AssertEqual(2, keyboard.PasteCount, "Both serialized Replay taps should send a paste.");
            AssertEqual(0, queue.Notes.Count, "Both serialized Replay taps should consume their slips.");
            var review = project.Buckets.Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
            AssertEqual(2, review.Notes.Count, "Rapid taps should archive two distinct slips, not paste one twice.");
            AssertEqual(2, review.Notes.Select(note => note.Id).Distinct().Count(), "Each consumed Replay slip should remain distinct.");
        }

        private static void RuntimeReplayLanesProgressIndependently()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Main", ["Queue"], "Queue");
            var mainQueue = store.GetActiveBucket()!;
            store.SetBucketKind(mainQueue, "Replay");
            store.AddNote(mainQueue, "main queued value", "copy");
            store.CreateProject("Alternate", ["Queue"], "Queue", shifted: true);
            var shiftQueue = store.GetActiveBucket(true)!;
            store.SetBucketKind(shiftQueue, "Replay");
            store.AddNote(shiftQueue, "alternate queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out _,
                replayResumeClipboard: false);
            var mainPaste = keyboard.DeferNextPaste();
            var shiftPaste = keyboard.DeferNextPaste();

            var mainHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V));
            var shiftHandled = coordinator.OnTapDispatched(ShortcutContext(VK_V, shifted: true));

            AssertTrue(mainHandled && shiftHandled, "Replay taps in both lanes should be handled.");
            AssertEqual(1, keyboard.PasteCount, "The shared clipboard must remain staged for Main until its paste lands.");

            mainPaste.SetResult(true);
            AssertTrue(
                notifications.WaitForCount(1, TimeSpan.FromSeconds(5)),
                "Main Replay should persist and report its result within the timeout.");
            AssertTrue(
                SpinWait.SpinUntil(() => keyboard.PasteCount == 2, TimeSpan.FromSeconds(5)),
                "Alternate Replay should begin after Main releases the shared clipboard.");
            shiftPaste.SetResult(true);
            AssertTrue(
                notifications.WaitForCount(2, TimeSpan.FromSeconds(5)),
                "Alternate Replay should persist and report its result within the timeout.");
            AssertEqual(0, mainQueue.Notes.Count, "Main Replay should consume its slip.");
            AssertEqual(0, shiftQueue.Notes.Count, "Alternate Replay should consume its slip.");
        }

        private static void RuntimeReplaySuppressesTapDuringFinalRestore()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "final queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var dispatcher = new QueuingDispatcher();
            var restoreDelay = new ManualDelay();
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out _,
                delay: restoreDelay,
                dispatcher: dispatcher);

            AssertTrue(
                coordinator.OnTapDispatched(ShortcutContext(VK_V)),
                "The final Replay tap should be handled.");
            AssertTrue(
                dispatcher.RunUntil(
                    () => keyboard.PasteCount == 1 && queue.Notes.Count == 0,
                    TimeSpan.FromSeconds(5)),
                "Replay should consume its final item before the restore delay settles.");

            AssertEqual("Replay", queue.Settings.Kind, "The bucket must remain Replay while its clipboard is restoring.");
            AssertFalse(
                notifications.Messages.Exists(message => message.Contains("complete", StringComparison.OrdinalIgnoreCase)),
                "Replay must not announce completion before clipboard restoration settles.");
            AssertTrue(
                coordinator.OnTapDispatched(ShortcutContext(VK_V)),
                "A tap during final restoration should remain suppressed.");
            dispatcher.RunAll();
            AssertEqual(1, keyboard.PasteCount, "A suppressed tap must not paste the staged final item again.");

            restoreDelay.Release();
            AssertTrue(
                dispatcher.RunUntil(
                    () => queue.Settings.Kind == "Standard"
                        && notifications.Messages.Exists(message =>
                            message.Contains("replay complete", StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(5)),
                "Replay should finalize only after the delayed clipboard restoration settles.");
            AssertEqual("user clipboard", clipboard.Text, "Finalization should restore the original clipboard.");
            AssertEqual(1, keyboard.PasteCount, "Finalization must not inject another paste.");
        }

        private static void RuntimeReplayFinalRestoreRemainsLaneLocal()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Main", ["Queue"], "Queue");
            var mainQueue = store.GetActiveBucket()!;
            store.SetBucketKind(mainQueue, "Replay");
            store.AddNote(mainQueue, "main final value", "copy");
            store.CreateProject("Alternate", ["Queue"], "Queue", shifted: true);
            var alternateQueue = store.GetActiveBucket(true)!;
            store.SetBucketKind(alternateQueue, "Replay");
            store.AddNote(alternateQueue, "alternate final value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var dispatcher = new QueuingDispatcher();
            var restoreDelay = new FirstWaitManualDelay();
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out _,
                delay: restoreDelay,
                dispatcher: dispatcher);

            AssertTrue(
                coordinator.OnTapDispatched(ShortcutContext(VK_V)),
                "Main Replay should handle its final tap.");
            AssertTrue(
                dispatcher.RunUntil(
                    () => mainQueue.Notes.Count == 0 && keyboard.PasteCount == 1,
                    TimeSpan.FromSeconds(5)),
                "Main Replay should enter its delayed final restoration.");
            AssertEqual("Replay", mainQueue.Settings.Kind, "Main should remain in its restoring state.");

            AssertTrue(
                coordinator.OnTapDispatched(ShortcutContext(VK_V, shifted: true)),
                "Alternate Replay should still handle a tap while Main restores.");
            AssertTrue(
                dispatcher.RunUntil(
                    () => alternateQueue.Settings.Kind == "Standard"
                        && keyboard.PasteCount == 2,
                    TimeSpan.FromSeconds(5)),
                "Alternate should safely paste and finalize without waiting for Main's restore delay.");
            AssertEqual("Replay", mainQueue.Settings.Kind, "Alternate completion must not finalize Main early.");
            AssertTrue(
                coordinator.OnTapDispatched(ShortcutContext(VK_V)),
                "Main taps should remain suppressed while only Main is restoring.");
            dispatcher.RunAll();
            AssertEqual(2, keyboard.PasteCount, "The suppressed Main tap must not duplicate either lane's final item.");

            restoreDelay.ReleaseFirst();
            AssertTrue(
                dispatcher.RunUntil(
                    () => mainQueue.Settings.Kind == "Standard",
                    TimeSpan.FromSeconds(5)),
                "Main should finalize once its own restore delay settles.");
            AssertEqual("user clipboard", clipboard.Text, "The lanes should converge on the original user clipboard.");
        }

        private static void RuntimeReplayFinalRestoreFailureCompletesVisibly()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "final queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var dispatcher = new QueuingDispatcher();
            var restoreDelay = new ManualDelay();
            var notifications = new FakeNotificationSink();
            var logMessages = new List<string>();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out _,
                delay: restoreDelay,
                dispatcher: dispatcher,
                log: logMessages.Add);

            AssertTrue(
                coordinator.OnTapDispatched(ShortcutContext(VK_V)),
                "Replay should handle the final item before the injected restore failure.");
            AssertTrue(
                dispatcher.RunUntil(
                    () => keyboard.PasteCount == 1 && queue.Notes.Count == 0,
                    TimeSpan.FromSeconds(5)),
                "Replay should reach final restoration before failure injection.");
            clipboard.WriteResultOverride = new(
                ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
                FailureReason: "injected final restore failure");

            restoreDelay.Release();
            AssertTrue(
                dispatcher.RunUntil(
                    () => queue.Settings.Kind == "Standard"
                        && notifications.Messages.Exists(message =>
                            message.Contains("clipboard may have changed", StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(5)),
                "A failed restore should still settle Replay with a visible integrity warning.");
            AssertTrue(
                logMessages.Exists(message =>
                    message.Contains("clipboard integrity is uncertain", StringComparison.OrdinalIgnoreCase)),
                "A failed final restore should also leave a diagnostic log entry.");
        }

        private static void RuntimeShiftLaneReplayTapConsumesShiftedPaste()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo Shift", ["Queue"], "Queue", shifted: true);
            var queue = store.GetActiveBucket(true)!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _);

            // Ctrl+Shift+V replays its Shift on pass-through, so the tap context
            // arrives with ReplayShift set; the Replay tap must still consume it.
            var handled = coordinator.OnTapDispatched(
                ShortcutContext(VK_V, shifted: true, replayShift: true));

            AssertTrue(handled, "Shift-lane Replay tap should suppress the physical paste.");
            AssertEqual(1, keyboard.PasteCount, "Shift-lane Replay tap should send one synthetic paste.");
            AssertEqual(0, queue.Notes.Count, "Shift-lane Replay should consume the queued note.");
        }

        private static void RuntimeReplayResumesClipboardWhenEnabled()
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
                out var undo,
                replayResumeClipboard: true);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay tap should suppress the physical paste.");
            AssertEqual("user clipboard", clipboard.Text, "Replay should restore the user's clipboard when setting is enabled.");
        }

        private static void RuntimeReplayKeepsLastPasteWhenDisabled()
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
                out var undo,
                replayResumeClipboard: false);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay tap should suppress the physical paste.");
            AssertEqual("queued value", clipboard.Text, "Replay should NOT restore the user's clipboard and keep the last paste when setting is disabled.");
        }

        private static void RuntimeReplayRestoresRichAndMixedClipboardFormats()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var userImage = new ZetlClipboardImage([1, 2, 3, 4], 2, 2);
            var clipboard = new FakeClipboard(null, changeToken: 1);
            clipboard.SetMixedState(
                "formatted user text",
                "<p><strong>formatted</strong> user text</p>",
                userImage,
                changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay tap should suppress the physical paste.");
            AssertEqual(1, keyboard.PasteCount, "Replay should paste the queued item once.");
            AssertEqual("formatted user text", clipboard.Text, "Replay should restore the plain-text format.");
            AssertEqual(
                "<p><strong>formatted</strong> user text</p>",
                clipboard.RichHtml,
                "Replay should restore the rich HTML format.");
            AssertTrue(
                clipboard.Image?.PngBytes.SequenceEqual(userImage.PngBytes) == true,
                "Replay should restore an image format carried alongside text.");
            AssertEqual(1, clipboard.BackupRestoreCount, "Replay should perform one complete clipboard restore.");
            AssertEqual(0, queue.Notes.Count, "A successfully restored Replay should consume its item.");
        }

        private static void RuntimeReplayRefusesLossyClipboardReplacement()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1)
            {
                BackupFailureReason = "CF_BITMAP cannot be restored safely"
            };
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay tap should remain handled when safe backup is impossible.");
            AssertEqual(0, keyboard.PasteCount, "Replay must not paste after an incomplete clipboard backup.");
            AssertEqual("user clipboard", clipboard.Text, "Replay must leave the user's clipboard untouched.");
            AssertEqual(1, queue.Notes.Count, "Replay must keep the queued item after refusing replacement.");
            AssertFalse(undo.TryPop(false, out _), "A refused Replay must not create an undo entry.");
            AssertTrue(
                notifications.Messages.Exists(message =>
                    message.Contains("Replay paused", StringComparison.OrdinalIgnoreCase)
                    && message.Contains("item kept", StringComparison.OrdinalIgnoreCase)),
                "Replay should explain that it paused to avoid a lossy clipboard replacement.");
        }

        private static void RuntimeReplayDoesNotPasteAfterTransactionalStageFailure()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var clipboard = new FakeClipboard("user clipboard", changeToken: 1)
            {
                WriteResultOverride = new(
                    ZetlClipboardWriteStatus.WriteFailedRolledBack,
                    FailureReason: "injected target format failure")
            };
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Replay should still suppress the physical paste.");
            AssertEqual(0, keyboard.PasteCount, "Replay must not inject paste after target staging fails.");
            AssertEqual(1, queue.Notes.Count, "The queued item must remain after staging rollback.");
            AssertFalse(undo.TryPop(false, out _), "A failed stage must not create an undo entry.");
            AssertTrue(
                notifications.Messages.Exists(message =>
                    message.Contains("clipboard was preserved", StringComparison.OrdinalIgnoreCase)),
                "Replay should report that transactional rollback preserved the clipboard.");
        }

        private static void RuntimeReplayDoesNotOverwriteNewerMatchingClipboard()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            store.AddNote(queue, "queued value", "copy");
            var clipboard = new FakeClipboard("original clipboard", changeToken: 1);
            var dispatcher = new QueuingDispatcher();
            var restoreDelay = new ManualDelay();
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out var keyboard,
                out _,
                delay: restoreDelay,
                dispatcher: dispatcher);

            AssertTrue(
                coordinator.OnTapDispatched(ShortcutContext(VK_V)),
                "Replay tap should be handled.");
            AssertTrue(
                dispatcher.RunUntil(
                    () => keyboard.PasteCount == 1 && queue.Notes.Count == 0,
                    TimeSpan.FromSeconds(5)),
                "Replay should stage, paste, and consume before the delayed restore.");

            // The user copies richer content with the same visible text as the
            // Replay item while the restore delay is pending.
            AssertTrue(
                clipboard.SetRichText("queued value", "<p><em>new user copy</em></p>"),
                "The simulated newer clipboard write should succeed.");
            restoreDelay.Release();
            AssertTrue(
                SpinWait.SpinUntil(() => dispatcher.PendingCount > 0, TimeSpan.FromSeconds(5)),
                "The delayed restore should return to the dispatcher.");
            AssertTrue(
                dispatcher.RunUntil(
                    () => queue.Settings.Kind == "Standard"
                        && notifications.Messages.Exists(message =>
                            message.Contains("newer clipboard", StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(5)),
                "Replay should settle as complete after detecting newer clipboard ownership.");

            AssertEqual("queued value", clipboard.Text, "Replay should leave the newer clipboard text intact.");
            AssertEqual(
                "<p><em>new user copy</em></p>",
                clipboard.RichHtml,
                "Replay should not overwrite newer rich formats just because visible text matches.");
            AssertEqual(0, clipboard.BackupRestoreCount, "A changed clipboard token must cancel restoration.");
        }

        private static void RuntimeReplayHandlesImagesAndRestoresImageClipboard()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Queue"], "Queue");
            var queue = store.GetActiveBucket()!;
            store.SetBucketKind(queue, "Replay");
            var queuedBytes = new byte[] { 4, 5, 6 };
            store.AddImageNote(
                project,
                queue,
                new ZetlClipboardImage(queuedBytes, 3, 2),
                "copy");
            var userBytes = new byte[] { 1, 2, 3 };
            var clipboard = new FakeClipboard(null, changeToken: 1)
            {
                Image = new ZetlClipboardImage(userBytes, 1, 1)
            };
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertTrue(handled, "Image Replay should suppress the physical paste.");
            AssertEqual(1, keyboard.PasteCount, "Image Replay should send one synthetic paste.");
            AssertEqual(2, clipboard.ImageSetCount, "Image Replay should inject the item and restore the prior image.");
            AssertTrue(
                clipboard.Image?.PngBytes.SequenceEqual(userBytes) == true,
                "Image Replay should restore the user's previous image clipboard.");
            AssertEqual(0, queue.Notes.Count, "Successful image Replay should consume the queued slip.");
            var review = project.Buckets.Single(bucket => bucket.Id == queue.Settings.ReplayReviewBucketId);
            AssertTrue(review.Notes.Single().IsImage, "Replay review should preserve the image slip type.");
            AssertEqual(
                queuedBytes.Length,
                store.ReadImageAsset(project, review.Notes.Single())?.Length,
                "Replay review should retain the queued image asset.");
            AssertTrue(undo.TryPop(false, out var action), "Image Replay should be undoable.");
            action!.Undo();
            AssertTrue(queue.Notes.Single().IsImage, "Undo should restore the image slip to the Replay queue.");
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
            AssertEqual("Standard", queue.Settings.Kind, "An empty Replay bucket returns to Standard.");
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
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
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

            AssertTrue(
                dispatcher.RunUntil(
                    () => keyboard.PasteCount == 1
                        && queue.Notes.Count == 0
                        && queue.Settings.Kind == "Standard"
                        && notifications.Messages.Exists(message =>
                            message.Contains("replay complete", StringComparison.OrdinalIgnoreCase)),
                    TimeSpan.FromSeconds(5)),
                "Running queued work should paste, consume, restore, and finalize Replay off the hook thread.");
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
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out _,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertFalse(handled, "Pop tap should allow the physical paste through.");
            AssertEqual(0, bucket.Notes.Count, "Pop tap should remove the matching note.");
            AssertEqual(
                "Popped item from Inbox to Inbox Pop Review.",
                notifications.Messages.Single(),
                "Pop should name both its source and durable recovery destination.");
            AssertTrue(undo.TryPop(false, out var action), "Popped note should be undoable.");
            AssertEqual(
                "Restored popped item to Inbox from Inbox Pop Review.",
                action?.Message,
                "Pop undo should name both recovery endpoints.");
        }

        private static void RuntimePopRemovesMatchingImageSlip()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
            var bucket = store.GetActiveBucket()!;
            store.SetBucketPopMode(bucket, true);
            var bytes = new byte[] { 3, 1, 4, 1, 5 };
            store.AddImageNote(
                project,
                bucket,
                new ZetlClipboardImage(bytes, 5, 1),
                "copy");
            var clipboard = new FakeClipboard(null, changeToken: 1)
            {
                Image = new ZetlClipboardImage(bytes, 5, 1)
            };
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out _,
                out var undo);

            var handled = coordinator.OnTapDispatched(ShortcutContext(VK_V));

            AssertFalse(handled, "Image Pop should allow the physical paste through.");
            AssertEqual(0, bucket.Notes.Count, "Image Pop should remove the matching image slip.");
            AssertTrue(undo.TryPop(false, out var action), "Popped image should be undoable.");
            action!.Undo();
            AssertTrue(bucket.Notes.Single().IsImage, "Undo should restore the popped image slip.");
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

        private static void RuntimeCutHoldDefaultsToTodaysJournalBucket()
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
            AssertEqual(
                ZetlStateStore.JournalQuickNoteBucketName,
                note.PreferredBucket!.Name,
                "First quick note defaults to today's Quick Note child.");
            AssertEqual(
                ZetlStateStore.JournalBucketName(DateTime.Now, 0),
                note.Project.Buckets.Single(bucket => bucket.Id == note.PreferredBucket!.ParentBucketId).Name,
                "The Quick Note child sits under today's day parent.");
            AssertTrue(note.ShowStartProjectToggle, "First quick note should offer project activation.");
            AssertFalse(note.StartProjectDefault, "Quick note should not activate the project by default.");
        }

        private static void RuntimeSelectAllHoldCapturesTheSelection()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var clipboard = new FakeClipboard(null, changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _);
            // Holding Ctrl+A injects a copy; simulate the app copying the selected field.
            keyboard.OnSendChord = () => clipboard.SetText("the whole field");

            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_A, clipboardSequenceNumber: 1)).GetAwaiter().GetResult();

            AssertTrue(request is ZetlNoteCaptureRequest, "Holding Ctrl+A opens a capture.");
            AssertEqual(
                "the whole field",
                ((ZetlNoteCaptureRequest)request!).Text,
                "It captures the freshly-copied selection.");
        }

        private static void RuntimeSelectAllShiftHoldCapturesTheSelection()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var clipboard = new FakeClipboard(null, changeToken: 1);
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                new FakeNotificationSink(),
                out var keyboard,
                out _);
            // Holding Ctrl+Shift+A injects a copy; simulate the app copying the selected field.
            keyboard.OnSendChord = () => clipboard.SetText("the whole field on shift");

            var request = coordinator.HandleHoldAsync(
                ShortcutContext(VK_A, shifted: true, clipboardSequenceNumber: 1)).GetAwaiter().GetResult();

            AssertTrue(request is ZetlNoteCaptureRequest, "Holding Ctrl+Shift+A opens a capture.");
            var note = (ZetlNoteCaptureRequest)request!;
            AssertTrue(note.Shifted, "Should target the shift lane.");
            AssertEqual(
                "the whole field on shift",
                note.Text,
                "It captures the freshly-copied selection on the shift lane.");
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
            AssertTrue(store.GetActiveBucket()!.Settings.PopMode, "Ctrl+P hold should enable Pop.");
            coordinator.HandleHoldAsync(ShortcutContext(VK_R)).GetAwaiter().GetResult();
            AssertEqual("Replay", store.GetActiveBucket()!.Settings.Kind, "Ctrl+R hold should enable Replay.");
            AssertFalse(store.GetActiveBucket()!.Settings.PopMode, "Replay should disable Pop.");

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
                ProjectNameDefault: null,
                CaptureOrigin: ZetlCaptureOrigin.Create(
                    "Editor",
                    "editor",
                    "Quick note source",
                    ZetlCaptureOriginDetail.ApplicationAndWindowTitle));

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
            AssertEqual(
                "Quick note source",
                scratch.Notes.Single().CaptureOrigin?.WindowTitle,
                "Completed note capture should save the origin carried by its request.");
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

        private static void RuntimeFormattedCompileStagesRichClipboard()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var source = store.CreateProject("Source", ["Inbox"], "Inbox");
            var task = store.AddNote(source.Buckets.First(), "compiled task", "copy", blockKind: ZetlBlockKinds.Task);
            var selected = store.GetSlipDisplayItems(source)
                .Where(item => item.Note.Id == task.Id)
                .ToList();
            var clipboard = new FakeClipboard("before", changeToken: 1);
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
                    CompiledText: "Source\r\n\r\nInbox\r\n\tcompiled task",
                    SaveToBucket: false,
                    DestinationProject: source,
                    DestinationBucketName: "Inbox",
                    Flatten: false,
                    SelectedNoteTexts: ["compiled task"],
                    PasteNow: false,
                    CompiledHtml: store.CompileHtmlFromNotes(source, selected)));

            AssertEqual(ZetlCompileOutcome.RestoreTarget, outcome, "Rich copy should restore the target.");
            AssertEqual("Source\r\n\r\nInbox\r\n\tcompiled task", clipboard.Text, "Rich copy should keep the plain fallback.");
            AssertTrue(
                (clipboard.RichHtml ?? "").Contains("☐ compiled task", StringComparison.Ordinal),
                "Rich copy should use a printable task box.");
            AssertTrue(
                !(clipboard.RichHtml ?? "").Contains("<input type=\"checkbox\"", StringComparison.Ordinal),
                "Formatted clipboard output should not rely on interactive task inputs.");
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

        private static void RuntimeCompileSurfacesUncertainClipboardRollback()
        {
            using var temp = new TempStateFile();
            var store = new ZetlStateStore(temp.Path);
            var source = store.CreateProject("Source", ["Inbox"], "Inbox");
            var clipboard = new FakeClipboard("before", changeToken: 1)
            {
                WriteResultOverride = new(
                    ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
                    FailureReason: "injected partial rollback")
            };
            var notifications = new FakeNotificationSink();
            var coordinator = CreateShortcutCoordinator(
                store,
                clipboard,
                notifications,
                out _,
                out _);

            var outcome = coordinator.CompleteCompile(
                new ZetlCompileRequest(false, source, null),
                new ZetlCompileResult(
                    Committed: true,
                    CompiledText: "uncertain compile",
                    SaveToBucket: false,
                    DestinationProject: source,
                    DestinationBucketName: "Inbox",
                    Flatten: false,
                    SelectedNoteTexts: ["uncertain compile"],
                    PasteNow: true));

            AssertEqual(
                ZetlCompileOutcome.RestoreTarget,
                outcome,
                "An uncertain clipboard must never request paste injection.");
            AssertTrue(
                notifications.Messages.Single().Contains(
                    "could not fully restore",
                    StringComparison.OrdinalIgnoreCase),
                "The user should be told that clipboard rollback was incomplete.");
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
            IClipboard clipboard,
            FakeNotificationSink notifications,
            out FakeKeyboardBackend keyboard,
            out ZetlUndoStack undo,
            IZetlDelay? delay = null,
            bool quickNoteToClipboard = false,
            bool replayResumeClipboard = true,
            IZetlDispatcher? dispatcher = null,
            IImageUrlResolver? imageUrlResolver = null,
            Action<string>? log = null)
        {
            keyboard = new FakeKeyboardBackend();
            undo = new ZetlUndoStack(100);
            var settings = new ZetlAppSettings
            {
                AutoCaptureOnCopy = true,
                QuickNoteToClipboard = quickNoteToClipboard,
                ReplayResumeClipboard = replayResumeClipboard,
                HoldDelayMs = 60
            };
            return new ZetlShortcutCoordinator(
                store,
                keyboard,
                clipboard,
                dispatcher ?? new ImmediateDispatcher(),
                delay ?? new ImmediateDelay(),
                notifications,
                undo,
                () => settings,
                log ?? (_ => { }),
                imageUrlResolver);
        }

        private static ChordlEventContext ShortcutContext(
            int keyCode,
            bool shifted = false,
            uint clipboardSequenceNumber = 0,
            bool replayShift = false)
        {
            return new ChordlEventContext(
                keyCode,
                ChordlKeys.FormatComboName(keyCode, shifted),
                ChordlDispatchMode.None,
                ReplayShift: replayShift,
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

        private static void WaitForHold(
            IReadOnlyCollection<ChordlEventContext> holds,
            string message)
        {
            AssertTrue(
                SpinWait.SpinUntil(() => holds.Count > 0, TimeSpan.FromMilliseconds(500)),
                message);
            AssertEqual(1, holds.Count, message);
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
            private readonly object gate = new();
            private readonly Queue<Action> pending = new();

            public int PendingCount
            {
                get
                {
                    lock (gate)
                    {
                        return pending.Count;
                    }
                }
            }

            public void Post(Action action)
            {
                lock (gate)
                {
                    pending.Enqueue(action);
                    Monitor.PulseAll(gate);
                }
            }

            public void RunAll()
            {
                while (true)
                {
                    Action? action;
                    lock (gate)
                    {
                        if (!pending.TryDequeue(out action))
                        {
                            return;
                        }
                    }
                    action();
                }
            }

            public bool RunUntil(Func<bool> condition, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (!condition())
                {
                    Action? action = null;
                    lock (gate)
                    {
                        if (!pending.TryDequeue(out action))
                        {
                            var remaining = deadline - DateTime.UtcNow;
                            if (remaining <= TimeSpan.Zero)
                            {
                                return condition();
                            }
                            Monitor.Wait(gate, remaining);
                            continue;
                        }
                    }
                    action();
                }

                return true;
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
            private readonly object messageGate = new();

            public List<string> Messages { get; } = new();

            public void Show(string message)
            {
                lock (messageGate)
                {
                    Messages.Add(message);
                    Monitor.PulseAll(messageGate);
                }
            }

            public bool WaitForCount(int count, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;
                lock (messageGate)
                {
                    while (Messages.Count < count)
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero || !Monitor.Wait(messageGate, remaining))
                        {
                            return Messages.Count >= count;
                        }
                    }

                    return true;
                }
            }
        }

        private sealed class FakeKeyboardBackend : IKeyboardBackend
        {
            private readonly object pasteGate = new();
            private readonly Queue<TaskCompletionSource<bool>> deferredPastes = new();
            private int pasteCount;

            public int PasteCount => Volatile.Read(ref pasteCount);

            public bool PasteSucceeds { get; set; } = true;

            // Lets a test simulate the foreground app reacting to an injected chord
            // (e.g. Ctrl+C copying the selection onto the clipboard).
            public Action? OnSendChord { get; set; }

            public TaskCompletionSource<bool> DeferNextPaste()
            {
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pasteGate)
                {
                    deferredPastes.Enqueue(completion);
                }
                return completion;
            }

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
                OnSendChord?.Invoke();
                return Task.FromResult(true);
            }

            public Task<bool> SendPaste()
            {
                Interlocked.Increment(ref pasteCount);
                lock (pasteGate)
                {
                    if (deferredPastes.TryDequeue(out var completion))
                    {
                        return completion.Task;
                    }
                }
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

            public string? RichHtml { get; private set; }

            public IReadOnlyList<ZetlClipboardFormatData>? NativeReplayFormats { get; set; }

            public IReadOnlyList<ZetlClipboardFormatData>? LastRestoredRawFormats { get; private set; }

            public uint ChangeToken { get; private set; }

            public int ImageSetCount { get; private set; }

            public int BackupRestoreCount { get; private set; }

            public bool SetTextSucceeds { get; set; } = true;

            public ZetlClipboardWriteResult? WriteResultOverride { get; set; }

            public string? BackupFailureReason { get; set; }

            public string? TryGetText()
            {
                return Text;
            }

            public string? TryGetHtml()
            {
                return RichHtml;
            }

            public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats()
            {
                return NativeReplayFormats;
            }

            public ZetlClipboardImage? Image { get; set; }

            public ZetlClipboardImage? TryGetImage()
            {
                return Image;
            }

            public bool SetText(string text)
            {
                if (!SetTextSucceeds)
                {
                    return false;
                }

                Text = text;
                RichHtml = null;
                NativeReplayFormats = null;
                Image = null;
                ChangeToken++;
                return true;
            }

            public ZetlClipboardWriteResult ReplaceText(string text) =>
                WriteResultOverride ?? ZetlClipboardWriteResult.FromLegacy(SetText(text));

            public bool SetRichText(string plainText, string html)
            {
                if (!SetTextSucceeds)
                {
                    return false;
                }

                Text = plainText;
                RichHtml = html;
                NativeReplayFormats = null;
                Image = null;
                ChangeToken++;
                return true;
            }

            public ZetlClipboardWriteResult ReplaceRichText(string plainText, string html) =>
                WriteResultOverride
                ?? ZetlClipboardWriteResult.FromLegacy(SetRichText(plainText, html));

            public bool SetImage(ZetlClipboardImage image)
            {
                if (!SetTextSucceeds)
                {
                    return false;
                }

                Image = image;
                Text = null;
                RichHtml = null;
                NativeReplayFormats = null;
                ImageSetCount++;
                ChangeToken++;
                return true;
            }

            public ZetlClipboardWriteResult ReplaceImage(ZetlClipboardImage image) =>
                WriteResultOverride ?? ZetlClipboardWriteResult.FromLegacy(SetImage(image));

            public ZetlClipboardBackup CaptureBackup()
            {
                if (BackupFailureReason is not null)
                {
                    return ZetlClipboardBackup.Incomplete(BackupFailureReason);
                }

                var image = Image is null
                    ? null
                    : new ZetlClipboardImage(
                        Image.PngBytes.ToArray(),
                        Image.Width,
                        Image.Height);
                return ZetlClipboardBackup.FromPortable(Text, RichHtml, image);
            }

            public bool RestoreBackup(ZetlClipboardBackup backup)
            {
                if (!SetTextSucceeds || !backup.IsComplete)
                {
                    return false;
                }

                if (backup.RawFormats is { } rawFormats)
                {
                    LastRestoredRawFormats = rawFormats;
                    var unicode = rawFormats.FirstOrDefault(item => item.Format == 13);
                    if (unicode is not null)
                    {
                        Text = System.Text.Encoding.Unicode.GetString(unicode.Data).TrimEnd('\0');
                    }
                    var html = rawFormats.FirstOrDefault(item =>
                        item.RegisteredName == "HTML Format");
                    RichHtml = html is null
                        ? null
                        : System.Text.Encoding.UTF8.GetString(html.Data);
                    NativeReplayFormats = rawFormats;
                    Image = null;
                    BackupRestoreCount++;
                    ChangeToken++;
                    return true;
                }

                Text = backup.Text;
                RichHtml = backup.Html;
                Image = backup.Image is null
                    ? null
                    : new ZetlClipboardImage(
                        backup.Image.PngBytes.ToArray(),
                        backup.Image.Width,
                        backup.Image.Height);
                if (Image is not null)
                {
                    ImageSetCount++;
                }
                BackupRestoreCount++;
                ChangeToken++;
                return true;
            }

            public ZetlClipboardWriteResult ReplaceWithBackup(ZetlClipboardBackup backup) =>
                WriteResultOverride
                ?? ZetlClipboardWriteResult.FromLegacy(RestoreBackup(backup));

            public uint GetChangeToken()
            {
                return ChangeToken;
            }

            public void SetState(string? text, uint changeToken)
            {
                Text = text;
                RichHtml = null;
                NativeReplayFormats = null;
                Image = null;
                ChangeToken = changeToken;
            }

            public void SetMixedState(
                string? text,
                string? html,
                ZetlClipboardImage? image,
                uint changeToken)
            {
                Text = text;
                RichHtml = html;
                NativeReplayFormats = null;
                Image = image;
                ChangeToken = changeToken;
            }
        }

        private sealed class FirstWaitManualDelay : IZetlDelay
        {
            private readonly TaskCompletionSource first = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private int waitCount;

            public Task WaitAsync(TimeSpan delay)
            {
                return Interlocked.Increment(ref waitCount) == 1
                    ? first.Task
                    : Task.CompletedTask;
            }

            public void ReleaseFirst()
            {
                first.TrySetResult();
            }
        }

        private sealed class GenerationChangingClipboard(bool changeEveryTextRead = false) : IClipboard
        {
            private int generation = 1;
            private int changeOnFirstTextRead = 1;
            private uint changeToken = 2;

            public int TextReadCount { get; private set; }

            public string? TryGetText()
            {
                TextReadCount++;
                var value = generation == 1 ? "generation one" : "generation two";
                if (changeEveryTextRead
                    || Interlocked.Exchange(ref changeOnFirstTextRead, 0) == 1)
                {
                    generation++;
                    changeToken++;
                }
                return value;
            }

            public string? TryGetHtml() => generation == 1
                ? "<p><em>generation one</em></p>"
                : "<p><strong>generation two</strong></p>";

            public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats() =>
                [new ZetlClipboardFormatData(
                    0xC001,
                    [(byte)generation],
                    "Star Embed Source (XML)")];

            public ZetlClipboardImage? TryGetImage() => new(
                [(byte)generation],
                generation,
                generation);

            public bool SetText(string text) => false;

            public bool SetRichText(string plainText, string html) => false;

            public bool SetImage(ZetlClipboardImage image) => false;

            public ZetlClipboardBackup CaptureBackup() =>
                ZetlClipboardBackup.Incomplete("test clipboard is read-only");

            public bool RestoreBackup(ZetlClipboardBackup backup) => false;

            public uint GetChangeToken() => changeToken;
        }

        private sealed class FakeImageUrlResolver(ZetlResolvedImageUrl? result) : IImageUrlResolver
        {
            public Task<ZetlResolvedImageUrl?> TryResolveAsync(string text) =>
                Task.FromResult(result);
        }
    }
