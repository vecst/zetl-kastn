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
    string? ProjectNameDefault) : ZetlShortcutRequest(Shifted);

internal sealed record ZetlNoteCaptureResult(
    bool Committed,
    string NoteText,
    bool StartProject,
    bool CreateNewProject,
    string ProjectName,
    string SelectedBucketName,
    ZetlBucket SelectedBucket);

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
    bool PasteNow);

internal enum ZetlCompileOutcome
{
    None,
    RestoreTarget,
    PasteNow
}

internal sealed record ZetlNotificationEntry(DateTime CreatedAt, string Message);
