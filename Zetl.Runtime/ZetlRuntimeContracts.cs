namespace ZETL;

internal interface IZetlDispatcher
{
    void Post(Action action);
}

internal interface IZetlDelay
{
    Task WaitAsync(TimeSpan delay);
}

internal interface IZetlNotificationSink
{
    void Show(string message);
}

internal interface IZetlDialogHost
{
    void ShowBoard(bool shifted);
}

internal interface IZetlForegroundService
{
    object? CaptureTarget();

    void RestoreTarget(object? target);
}

internal sealed class SystemZetlDelay : IZetlDelay
{
    public Task WaitAsync(TimeSpan delay)
    {
        return Task.Delay(delay);
    }
}

internal abstract record ZetlShortcutRequest(bool Shifted);

internal sealed record ZetlBoardRequest(bool Shifted) : ZetlShortcutRequest(Shifted);

// A request to open Zetl's quick template picker. Raised by a held Ctrl+T, or by a
// held Ctrl+V when no project is active and there is nothing to compile.
// FromCompileFallback distinguishes the two so the host can keep the original
// "nothing to compile" message when no templates exist.
internal sealed record ZetlTemplatePickerRequest(bool Shifted, bool FromCompileFallback)
    : ZetlShortcutRequest(Shifted);

internal sealed record ZetlNoteCaptureRequest(
    bool Shifted,
    ZetlProject Project,
    ZetlBucket? PreferredBucket,
    string Text,
    string Source,
    bool ShowStartProjectToggle,
    bool StartProjectDefault,
    bool ScratchOnlyUntilProjectStarted,
    bool CreateNewProjectToggle,
    string? ProjectToggleText,
    string? ProjectNameDefault,
    ZetlCaptureOrigin? CaptureOrigin = null,
    ZetlClipboardImage? Image = null,
    string? ImageSourceUrl = null) : ZetlShortcutRequest(Shifted);

internal sealed record ZetlNoteCaptureResult(
    bool Committed,
    string NoteText,
    bool StartProject,
    bool CreateNewProject,
    string ProjectName,
    string SelectedBucketName,
    ZetlBucket SelectedBucket,
    // The project the note should be filed into. Null falls back to the
    // request's project; the quick-note dialog sets it when the user redirects
    // the note to a different existing project.
    ZetlProject? SelectedProject = null);

internal sealed record ZetlCompileRequest(
    bool Shifted,
    ZetlProject Project,
    IReadOnlyList<ZetlBucket>? BucketScope) : ZetlShortcutRequest(Shifted);

internal sealed record ZetlCompileResult(
    bool Committed,
    string CompiledText,
    bool SaveToBucket,
    ZetlProject DestinationProject,
    string DestinationBucketName,
    bool Flatten,
    IReadOnlyList<string> SelectedNoteTexts,
    bool PasteNow,
    string? CompiledHtml = null);

internal enum ZetlCompileOutcome
{
    None,
    RestoreTarget,
    PasteNow
}

internal enum ZetlNoteCaptureOutcome
{
    None,

    // A held cut was cancelled without keeping the note. Because Ctrl+X passes
    // the physical cut through before the dialog opens, the source text is
    // already gone; the host should paste the still-on-clipboard cut text back
    // to restore it.
    PasteCutBack
}

internal sealed record ZetlNotificationEntry(DateTime CreatedAt, string Message);
