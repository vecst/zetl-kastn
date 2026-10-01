using Chordl;
using ZETL;

using Xunit;
using static Chordl.ChordlKeys;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlGestureRouterTests
{
    private static ChordlEventContext Chord(int keyCode, bool shifted = false) =>
        new(keyCode, ChordlKeys.FormatComboName(keyCode, shifted), ChordlDispatchMode.None, false, shifted, 0);

    [Fact] public void DefaultRulesKeepZetlsKeyAssignments()
    {
        var router = new ZetlGestureRouter(ZetlGestureRules.Defaults);
        var expected = new (ZetlGestureKind Kind, int Key, string Action)[]
        {
            (ZetlGestureKind.Press, VK_C, ZetlGestureActions.ObserveCopy),
            (ZetlGestureKind.Press, VK_X, ZetlGestureActions.ObserveCut),
            (ZetlGestureKind.Tap, VK_V, ZetlGestureActions.PasteQueue),
            (ZetlGestureKind.Hold, VK_A, ZetlGestureActions.CaptureSelectAll),
            (ZetlGestureKind.Hold, VK_B, ZetlGestureActions.Board),
            (ZetlGestureKind.Hold, VK_C, ZetlGestureActions.CaptureCopy),
            (ZetlGestureKind.Hold, VK_J, ZetlGestureActions.ToggleProject),
            (ZetlGestureKind.Hold, VK_P, ZetlGestureActions.TogglePop),
            (ZetlGestureKind.Hold, VK_R, ZetlGestureActions.ToggleReplay),
            (ZetlGestureKind.Hold, VK_T, ZetlGestureActions.TemplatePicker),
            (ZetlGestureKind.Hold, VK_V, ZetlGestureActions.Compile),
            (ZetlGestureKind.Hold, VK_X, ZetlGestureActions.QuickNote),
            (ZetlGestureKind.Hold, VK_Z, ZetlGestureActions.Undo),
        };

        foreach (var (kind, key, action) in expected)
        {
            AssertEqual(action, router.Resolve(kind, key), $"{kind} {ChordlKeys.FormatComboName(key, false)}");
        }

        AssertEqual(ZetlGestureActions.Native, router.Resolve(ZetlGestureKind.Tap, VK_B), "A tap with no rule behaves natively.");
        AssertEqual(ZetlGestureActions.Native, router.Resolve(ZetlGestureKind.Press, VK_A), "A press with no rule is left alone.");
    }

    [Fact] public void FocusIsCheckedOnlyForRulesThatNeedItAndOnlyOnce()
    {
        var checks = 0;
        var fileView = true;
        var router = new ZetlGestureRouter(
            ZetlGestureRules.Defaults,
            () =>
            {
                checks++;
                return fileView;
            });

        AssertEqual(ZetlGestureActions.Native, router.Resolve(ZetlGestureKind.Tap, VK_V), "A paste into a file view goes through.");
        AssertEqual(1, checks, "The paste checked focus once.");

        fileView = false;
        AssertEqual(ZetlGestureActions.PasteQueue, router.Resolve(ZetlGestureKind.Tap, VK_V), "A paste elsewhere reaches Replay and Pop.");
        AssertEqual(2, checks, "Each paste checks focus once.");

        router.Resolve(ZetlGestureKind.Hold, VK_C);
        router.Resolve(ZetlGestureKind.Press, VK_X);
        AssertEqual(2, checks, "Gestures with no focus rule never look up focus.");
    }

    [Fact] public async Task MoreSpecificRuleRunsItsRegisteredActionWhateverTheOrder()
    {
        var calls = new List<string>();
        var router = new ZetlGestureRouter(
            [
                new(ZetlGestureKind.Hold, VK_X, "text.note"),
                new(ZetlGestureKind.Hold, VK_X, "files.note", ZetlGestureFocus.FileView),
            ],
            () => true);
        router.RegisterHold("files.note", (context, _) =>
        {
            calls.Add($"files:{context.ShiftLane}");
            return Task.FromResult<ZetlShortcutRequest?>(new ZetlBoardRequest(context.ShiftLane));
        });
        router.RegisterHold("text.note", (_, _) =>
        {
            calls.Add("text");
            return Task.FromResult<ZetlShortcutRequest?>(null);
        });

        var request = await router.OnHoldAsync(Chord(VK_X, shifted: true), pending: null);

        AssertEqual("files:True", string.Join(",", calls), "The file-list rule wins even listed second, and gets the lane.");
        AssertTrue(request is ZetlBoardRequest { Shifted: true }, "The action's request comes back to the host.");
    }

    [Fact] public void SavedChoicesApplyOnlyToKnownRulesAndAllowedActions()
    {
        var rules = ZetlGestureRules.Apply(
        [
            new() { Kind = "Hold", Key = "B", Focus = "Any", Action = ZetlGestureActions.Compile },
            new() { Kind = "tap", Key = "v", Focus = "fileview", Action = ZetlGestureActions.PasteQueue },
            // Ignored: a key Zetl does not watch, an action a hold cannot run,
            // and the internal press rules.
            new() { Kind = "Hold", Key = "Q", Focus = "Any", Action = ZetlGestureActions.Board },
            new() { Kind = "Hold", Key = "Z", Focus = "Any", Action = ZetlGestureActions.PasteQueue },
            new() { Kind = "Press", Key = "C", Focus = "Any", Action = ZetlGestureActions.Native },
        ]);
        var router = new ZetlGestureRouter(rules, () => true);

        AssertEqual(ZetlGestureActions.Compile, router.Resolve(ZetlGestureKind.Hold, VK_B), "A saved choice replaces the default action.");
        AssertEqual(ZetlGestureActions.PasteQueue, router.Resolve(ZetlGestureKind.Tap, VK_V), "Names match regardless of case.");
        AssertEqual(ZetlGestureActions.Native, router.Resolve(ZetlGestureKind.Hold, (int)'Q'), "No rule is added for an unwatched key.");
        AssertEqual(ZetlGestureActions.Undo, router.Resolve(ZetlGestureKind.Hold, VK_Z), "An action the gesture cannot take is ignored.");
        AssertEqual(ZetlGestureActions.ObserveCopy, router.Resolve(ZetlGestureKind.Press, VK_C), "Press rules cannot be changed.");
        AssertEqual(ZetlGestureRules.Defaults.Count, rules.Count, "Applying settings never adds or removes rules.");
    }

    [Fact] public void OnlyChangedRulesAreSavedAndTheyRoundTrip()
    {
        AssertEqual(0, ZetlGestureRules.Overrides(ZetlGestureRules.Defaults).Count, "Untouched defaults save nothing.");

        var edited = ZetlGestureRules.Defaults
            .Select(rule => rule.Kind == ZetlGestureKind.Hold && rule.KeyCode == VK_J
                ? rule with { ActionId = ZetlGestureActions.Native }
                : rule)
            .ToList();
        var saved = ZetlGestureRules.Overrides(edited);

        AssertEqual(1, saved.Count, "Only the changed rule is saved.");
        AssertEqual("Hold/J/Any/native", $"{saved[0].Kind}/{saved[0].Key}/{saved[0].Focus}/{saved[0].Action}", "It is saved by name.");
        AssertEqual(
            ZetlGestureActions.Native,
            new ZetlGestureRouter(ZetlGestureRules.Apply(saved)).Resolve(ZetlGestureKind.Hold, VK_J),
            "Loading the saved choice restores it.");
    }

    [Fact] public void ReplacedRulesTakeEffectForTheNextGesture()
    {
        var router = new ZetlGestureRouter(ZetlGestureRules.Defaults);
        router.SetRules(ZetlGestureRules.Apply(
        [
            new() { Kind = "Hold", Key = "T", Focus = "Any", Action = ZetlGestureActions.Board }
        ]));

        AssertEqual(ZetlGestureActions.Board, router.Resolve(ZetlGestureKind.Hold, VK_T), "Saving settings swaps the rules in place.");
    }

    [Fact] public async Task ARuleWithoutARegisteredActionBehavesNatively()
    {
        var router = new ZetlGestureRouter(
            [
                new(ZetlGestureKind.Tap, VK_V, "missing.program"),
                new(ZetlGestureKind.Hold, VK_V, "missing.program"),
                new(ZetlGestureKind.Press, VK_C, "missing.program"),
            ]);

        AssertFalse(router.OnTap(Chord(VK_V)), "An unhandled tap lets the physical key through.");
        AssertTrue(await router.OnHoldAsync(Chord(VK_V), pending: null) is null, "An unhandled hold opens nothing.");
        AssertTrue(router.OnPressAsync(Chord(VK_C)).IsCompleted, "An unhandled press does nothing.");
    }

    [Fact] public void TapActionDecidesWhetherThePhysicalKeyIsSuppressed()
    {
        var router = new ZetlGestureRouter([new(ZetlGestureKind.Tap, VK_V, "queue")]);
        var handled = true;
        router.RegisterTap("queue", _ => handled);

        AssertTrue(router.OnTap(Chord(VK_V)), "A handling action keeps the physical key suppressed.");
        handled = false;
        AssertFalse(router.OnTap(Chord(VK_V)), "A declining action lets the physical key through.");
    }
}
