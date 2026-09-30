using Chordl;
using static Chordl.ChordlKeys;

namespace ZETL;

// What Chordl reports about a configured chord.
internal enum ZetlGestureKind
{
    // The first press of a key whose native action goes through at once
    // (Ctrl+C, Ctrl+X): Zetl may observe it but never replaces it.
    Press,
    // A quick press and release of a key Chordl holds back until release
    // (Ctrl+V): the action decides whether the physical key still goes through.
    Tap,
    // The key held past the hold delay.
    Hold
}

// Where keyboard focus must be for a rule to apply.
internal enum ZetlGestureFocus
{
    Any,
    // A shell file list: File Explorer, the desktop, an Open/Save dialog.
    FileView
}

// One routing rule: a gesture on a key, under a focus condition, runs the action
// with this id. Rules are plain data so they can later be listed and edited.
// A rule applies to both lanes; the action receives the lane in its context.
internal sealed record ZetlGestureRule(
    ZetlGestureKind Kind,
    int KeyCode,
    string ActionId,
    ZetlGestureFocus Focus = ZetlGestureFocus.Any);

internal static class ZetlGestureActions
{
    // Let the key do what it does without Zetl.
    public const string Native = "native";

    public const string ObserveCopy = "zetl.observe-copy";
    public const string ObserveCut = "zetl.observe-cut";
    public const string PasteQueue = "zetl.paste-queue";
    public const string CaptureSelectAll = "zetl.capture-select-all";
    public const string ToggleProject = "zetl.toggle-project";
    public const string Board = "zetl.board";
    public const string CaptureCopy = "zetl.capture-copy";
    public const string TogglePop = "zetl.toggle-pop";
    public const string ToggleReplay = "zetl.toggle-replay";
    public const string TemplatePicker = "zetl.template-picker";
    public const string QuickNote = "zetl.quick-note";
    public const string Compile = "zetl.compile";
    public const string Undo = "zetl.undo";
}

internal static class ZetlGestureRules
{
    // Zetl's own behavior. Order matters: the first matching rule wins, so a
    // narrower rule (a paste into a file view) comes before the general one.
    public static IReadOnlyList<ZetlGestureRule> Defaults { get; } =
    [
        new(ZetlGestureKind.Press, VK_C, ZetlGestureActions.ObserveCopy),
        new(ZetlGestureKind.Press, VK_X, ZetlGestureActions.ObserveCut),

        // A paste into a file list pastes files; Replay and Pop work on text and
        // pictures, so the physical paste goes through untouched.
        new(ZetlGestureKind.Tap, VK_V, ZetlGestureActions.Native, ZetlGestureFocus.FileView),
        new(ZetlGestureKind.Tap, VK_V, ZetlGestureActions.PasteQueue),

        new(ZetlGestureKind.Hold, VK_A, ZetlGestureActions.CaptureSelectAll),
        new(ZetlGestureKind.Hold, VK_B, ZetlGestureActions.Board),
        new(ZetlGestureKind.Hold, VK_C, ZetlGestureActions.CaptureCopy),
        new(ZetlGestureKind.Hold, VK_J, ZetlGestureActions.ToggleProject),
        new(ZetlGestureKind.Hold, VK_P, ZetlGestureActions.TogglePop),
        new(ZetlGestureKind.Hold, VK_R, ZetlGestureActions.ToggleReplay),
        new(ZetlGestureKind.Hold, VK_T, ZetlGestureActions.TemplatePicker),
        new(ZetlGestureKind.Hold, VK_V, ZetlGestureActions.Compile),
        new(ZetlGestureKind.Hold, VK_X, ZetlGestureActions.QuickNote),
        new(ZetlGestureKind.Hold, VK_Z, ZetlGestureActions.Undo)
    ];
}

// Sits between Chordl and the programs that act on gestures. Chordl reports a
// press, tap, or hold; the router matches it against the rules and runs the
// action registered under the winning rule's id. No matching rule, or an id
// with no registered action, means Native: the key behaves as if Zetl were not
// there. Actions are registered once at startup and only read afterwards.
internal sealed class ZetlGestureRouter
{
    private readonly IReadOnlyList<ZetlGestureRule> rules;
    private readonly Func<bool> isFileViewFocused;
    private readonly Dictionary<string, Func<ChordlEventContext, Lazy<ZetlCaptureOrigin?>?, Task>> pressActions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<ChordlEventContext, bool>> tapActions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<ChordlEventContext, ZetlPendingShortcut?, Task<ZetlShortcutRequest?>>> holdActions =
        new(StringComparer.Ordinal);

    public ZetlGestureRouter(
        IReadOnlyList<ZetlGestureRule> rules,
        Func<bool>? isFileViewFocused = null)
    {
        this.rules = rules;
        this.isFileViewFocused = isFileViewFocused ?? (() => false);
    }

    public void RegisterPress(string actionId, Func<ChordlEventContext, Lazy<ZetlCaptureOrigin?>?, Task> action) =>
        pressActions.Add(actionId, action);

    public void RegisterTap(string actionId, Func<ChordlEventContext, bool> action) =>
        tapActions.Add(actionId, action);

    public void RegisterHold(
        string actionId,
        Func<ChordlEventContext, ZetlPendingShortcut?, Task<ZetlShortcutRequest?>> action) =>
        holdActions.Add(actionId, action);

    // The action a gesture resolves to. Focus is looked up only when a rule for
    // this key asks for it, and at most once per gesture.
    public string Resolve(ZetlGestureKind kind, int keyCode)
    {
        bool? fileViewFocused = null;
        foreach (var rule in rules)
        {
            if (rule.Kind != kind || rule.KeyCode != keyCode)
            {
                continue;
            }

            if (rule.Focus == ZetlGestureFocus.FileView
                && !(fileViewFocused ??= isFileViewFocused()))
            {
                continue;
            }

            return rule.ActionId;
        }

        return ZetlGestureActions.Native;
    }

    // The capture origin is deferred: the hook thread records only cheap facts,
    // and the slower metadata resolves when a capture first needs it.
    public Task OnPressAsync(ChordlEventContext context, Lazy<ZetlCaptureOrigin?>? captureOrigin = null) =>
        pressActions.TryGetValue(Resolve(ZetlGestureKind.Press, context.KeyCode), out var action)
            ? action(context, captureOrigin)
            : Task.CompletedTask;

    // Runs on the keyboard hook thread and must return at once. True means the
    // tap was handled and the held-back physical key is not sent.
    public bool OnTap(ChordlEventContext context) =>
        tapActions.TryGetValue(Resolve(ZetlGestureKind.Tap, context.KeyCode), out var action)
        && action(context);

    public Task<ZetlShortcutRequest?> OnHoldAsync(
        ChordlEventContext context,
        ZetlPendingShortcut? pending) =>
        holdActions.TryGetValue(Resolve(ZetlGestureKind.Hold, context.KeyCode), out var action)
            ? action(context, pending)
            : Task.FromResult<ZetlShortcutRequest?>(null);
}
