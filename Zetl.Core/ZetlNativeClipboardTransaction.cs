namespace ZETL;

/// <summary>
/// Executes a fully staged native clipboard replacement. Payload allocation and
/// release remain platform-owned; this component owns destructive write and
/// compensating-restore ordering. A null rollback means the original clipboard
/// could not be backed up; the write still proceeds (the caller is replacing the
/// clipboard on purpose), and a failed write then reports an uncertain clipboard.
/// </summary>
internal static class ZetlNativeClipboardTransaction
{
    public static ZetlClipboardWriteResult Execute<TPayload>(
        IReadOnlyList<ZetlStagedClipboardFormat<TPayload>> target,
        IReadOnlyList<ZetlStagedClipboardFormat<TPayload>>? rollback,
        Func<bool> emptyClipboard,
        Func<uint, TPayload, bool> transfer)
    {
        if (!emptyClipboard())
        {
            return new(
                ZetlClipboardWriteStatus.EmptyFailed,
                FailureReason: "the clipboard could not be emptied");
        }

        foreach (var item in target)
        {
            if (!transfer(item.Format, item.Payload))
            {
                return RollBack(
                    item.Format,
                    rollback,
                    emptyClipboard,
                    transfer);
            }

            item.MarkTransferred();
        }

        return ZetlClipboardWriteResult.Success;
    }

    private static ZetlClipboardWriteResult RollBack<TPayload>(
        uint failedFormat,
        IReadOnlyList<ZetlStagedClipboardFormat<TPayload>>? rollback,
        Func<bool> emptyClipboard,
        Func<uint, TPayload, bool> transfer)
    {
        if (rollback is null)
        {
            return new(
                ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
                failedFormat,
                "the target write failed and the original clipboard had no restorable backup");
        }

        if (!emptyClipboard())
        {
            return new(
                ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
                failedFormat,
                "the target write failed and the partial clipboard could not be cleared for rollback");
        }

        foreach (var item in rollback)
        {
            if (!transfer(item.Format, item.Payload))
            {
                return new(
                    ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
                    item.Format,
                    $"the target write failed at format {failedFormat} and rollback failed at format {item.Format}");
            }

            item.MarkTransferred();
        }

        return new(
            ZetlClipboardWriteStatus.WriteFailedRolledBack,
            failedFormat,
            "the target write failed and the original clipboard was restored");
    }
}

internal sealed class ZetlStagedClipboardFormat<TPayload>(uint format, TPayload payload)
{
    public uint Format { get; } = format;

    public TPayload Payload { get; } = payload;

    public bool Transferred { get; private set; }

    public void MarkTransferred()
    {
        Transferred = true;
    }
}
