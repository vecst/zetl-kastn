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
// with this id. Rules are plain data, listed and edited on the Hold Actions
// settings page. A rule applies to both lanes; the action receives the lane in
// its context. Kind, key, and focus identify the rule; only the action changes.
internal sealed record ZetlGestureRule(
    ZetlGestureKind Kind,
    int KeyCode,
    string ActionId,
    ZetlGestureFocus Focus = ZetlGestureFocus.Any);

internal static class ZetlGestureActions
{
    // Let the key do what it does without Zetl.
    public const string Native = "native";

    public sealed record Choice(string Id, string Label);

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

    // The actions a rule of this kind can be set to, in menu order. Press rules
    // are internal plumbing (observing copies and cuts) and are not editable.
    public static IReadOnlyList<Choice> ChoicesFor(ZetlGestureKind kind) => kind switch
    {
        ZetlGestureKind.Tap => TapChoices,
        ZetlGestureKind.Hold => HoldChoices,
        _ => []
    };

    private static readonly Choice NormalKey = new(Native, "Normal key behavior");

    private static readonly IReadOnlyList<Choice> TapChoices =
    [
        new(PasteQueue, "Paste from Replay / Pop"),
        NormalKey
    ];

    private static readonly IReadOnlyList<Choice> HoldChoices =
    [
        new(CaptureCopy, "Capture what was copied"),
        new(QuickNote, "Quick note"),
        new(CaptureSelectAll, "Select all and capture"),
        new(Compile, "Compile"),
        new(Board, "Open the Board"),
        new(TemplatePicker, "Open templates"),
        new(ToggleProject, "Switch Journal / last project"),
        new(TogglePop, "Toggle Pop mode"),
        new(ToggleReplay, "Toggle Replay mode"),
        new(Undo, "Undo last Zetl action"),
        NormalKey
    ];
}

internal static class ZetlGestureRules
{
    // Zetl's own behavior, in the order the Hold Actions page lists it. Order
    // does not affect routing: a file-list rule beats an anywhere rule for the
    // same gesture wherever it sits.
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

    public static bool IsEditable(ZetlGestureRule rule) => rule.Kind != ZetlGestureKind.Press;

    // The defaults with the user's saved actions applied. Saved entries name a
    // rule by kind, key, and focus; ones that match no default rule, or name an
    // action that rule cannot take, are ignored, so a stale or hand-edited
    // settings file can never add keys or break a gesture.
    public static IReadOnlyList<ZetlGestureRule> Apply(IReadOnlyList<ZetlGestureRuleSetting>? saved)
    {
        if (saved is not { Count: > 0 })
        {
            return Defaults;
        }

        return Defaults
            .Select(rule => saved.LastOrDefault(setting => Identifies(setting, rule)) is { } setting
                && IsEditable(rule)
                && ZetlGestureActions.ChoicesFor(rule.Kind).Any(choice => choice.Id == setting.Action)
                    ? rule with { ActionId = setting.Action }
                    : rule)
            .ToList();
    }

    // The saved form of a rule list: only the rules whose action differs from the
    // default, so an untouched page saves nothing and new defaults still arrive.
    public static List<ZetlGestureRuleSetting> Overrides(IEnumerable<ZetlGestureRule> rules) =>
        rules
            .Where(rule => IsEditable(rule)
                && Defaults.Any(standard => SameRule(standard, rule) && standard.ActionId != rule.ActionId))
            .Select(rule => new ZetlGestureRuleSetting
            {
                Kind = rule.Kind.ToString(),
                Key = KeyName(rule.KeyCode),
                Focus = rule.Focus.ToString(),
                Action = rule.ActionId
            })
            .ToList();

    // "Hold Ctrl+X", "Tap Ctrl+V".
    public static string GestureLabel(ZetlGestureRule rule) =>
        $"{rule.Kind} Ctrl+{KeyName(rule.KeyCode)}";

    public static string FocusLabel(ZetlGestureFocus focus) => focus switch
    {
        ZetlGestureFocus.FileView => "In a file list",
        _ => "Anywhere"
    };

    private static bool SameRule(ZetlGestureRule left, ZetlGestureRule right) =>
        left.Kind == right.Kind && left.KeyCode == right.KeyCode && left.Focus == right.Focus;

    private static bool Identifies(ZetlGestureRuleSetting setting, ZetlGestureRule rule) =>
        string.Equals(setting.Kind, rule.Kind.ToString(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(setting.Key, KeyName(rule.KeyCode), StringComparison.OrdinalIgnoreCase)
        && string.Equals(setting.Focus, rule.Focus.ToString(), StringComparison.OrdinalIgnoreCase);

    // Zetl's chords are Ctrl plus a letter, whose virtual-key code is the letter.
    private static string KeyName(int keyCode) => ((char)keyCode).ToString();
}

// Sits between Chordl and the programs that act on gestures. Chordl reports a
// press, tap, or hold; the router matches it against the rules and runs the
// action registered under the winning rule's id. When several rules match, the
// more specific one wins (a file-list rule over an anywhere rule), so the
// order of the list never changes behavior. No matching rule, or an id with no
// registered action, means Native: the key behaves as if Zetl were not there.
// Actions are registered once at startup; rules can be replaced at any time.
internal sealed class ZetlGestureRouter
{
    private IReadOnlyList<ZetlGestureRule> rules;
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

    // Swap in an edited rule list. The keyboard hook reads the reference once
    // per gesture, so a gesture in flight finishes under the list it started with.
    public void SetRules(IReadOnlyList<ZetlGestureRule> updated) =>
        Volatile.Write(ref rules, updated);

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
        string? anywhere = null;
        foreach (var rule in Volatile.Read(ref rules))
        {
            if (rule.Kind != kind || rule.KeyCode != keyCode)
            {
                continue;
            }

            if (rule.Focus == ZetlGestureFocus.Any)
            {
                anywhere ??= rule.ActionId;
                continue;
            }

            if (fileViewFocused ??= isFileViewFocused())
            {
                return rule.ActionId;
            }
        }

        return anywhere ?? ZetlGestureActions.Native;
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
