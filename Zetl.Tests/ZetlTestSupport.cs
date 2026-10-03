using Chordl;
using static Chordl.ChordlKeys;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

// Helpers shared by the Zetl test classes: builders for the store, the
// coordinator, and Chordl, and the message-carrying asserts they use.
internal static class ZetlTestSupport
{
    // A left or right Shift, the keys synthetic injection sends; not the generic VK_SHIFT.
    internal static bool IsSidedShiftKey(int virtualKey)
    {
        return virtualKey == VK_LSHIFT || virtualKey == VK_RSHIFT;
    }

    internal static bool PassThrough(ZetlStateStore store, string text) =>
        store.TryPassThroughLatestCopy(text, null, shifted: false, out _, out _, out _, out _);

    internal static void AssertConfigRejected(string json, string expectedFragment, string because)
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

    // A committed save of <request> into its own project and preferred bucket.
    internal static ZetlNoteCaptureResult SavedNote(
        ZetlNoteCaptureRequest request,
        string text,
        bool startProject) => new(
            Committed: true,
            NoteText: text,
            StartProject: startProject,
            CreateNewProject: false,
            ProjectName: request.Project.Name,
            SelectedBucketName: request.PreferredBucket!.Name,
            SelectedBucket: request.PreferredBucket!,
            SelectedProject: request.Project);

    internal static (ZetlStateStore Store, ZetlBucket Queue, FakeClipboard Clipboard, FakeNotificationSink Notifications, FakeKeyboardBackend Keyboard, RoutedCoordinator Coordinator)
        StagedReplay(TempStateFile temp, bool appReads)
    {
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Demo", ["Queue"], "Queue");
        var queue = store.GetActiveBucket()!;
        store.SetBucketKind(queue, "Replay");
        foreach (var value in new[] { "one", "two", "three" })
        {
            store.AddSlip(queue, value, "copy");
        }

        var clipboard = new FakeClipboard("user clipboard", changeToken: 1)
        {
            StagesPastes = true,
            ReadStagedPastes = appReads
        };
        var notifications = new FakeNotificationSink();
        var coordinator = CreateShortcutCoordinator(store, clipboard, notifications, out var keyboard, out _);
        return (store, queue, clipboard, notifications, keyboard, coordinator);
    }

    internal static RoutedCoordinator CreateShortcutCoordinator(
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
        Action<string>? log = null,
        Func<bool>? isFileViewFocused = null,
        bool passThrough = false,
        Func<DateTimeOffset>? clock = null)
    {
        keyboard = new FakeKeyboardBackend();
        undo = new ZetlUndoStack(100);
        var settings = new ZetlAppSettings
        {
            AutoCaptureOnCopy = true,
            QuickNoteToClipboard = quickNoteToClipboard,
            ReplayResumeClipboard = replayResumeClipboard,
            PassThrough = passThrough,
            HoldDelayMs = 60
        };
        var coordinator = new ZetlShortcutCoordinator(
            store,
            keyboard,
            clipboard,
            dispatcher ?? new ImmediateDispatcher(),
            delay ?? new ImmediateDelay(),
            notifications,
            undo,
            () => settings,
            log ?? (_ => { }),
            imageUrlResolver,
            clock);
        var router = new ZetlGestureRouter(ZetlGestureRules.Defaults, isFileViewFocused);
        coordinator.RegisterActions(router);
        return new RoutedCoordinator(coordinator, router);
    }

    internal static ChordlEventContext ShortcutContext(
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

    internal static ChordlProcessor CreateProcessor(
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

    internal static void AssertTrue(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void AssertFalse(bool value, string message)
    {
        if (value)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void WaitForHold(
        IReadOnlyCollection<ChordlEventContext> holds,
        string message)
    {
        AssertTrue(
            SpinWait.SpinUntil(() => holds.Count > 0, TimeSpan.FromMilliseconds(500)),
            message);
        AssertEqual(1, holds.Count, message);
    }

    internal static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }
}
