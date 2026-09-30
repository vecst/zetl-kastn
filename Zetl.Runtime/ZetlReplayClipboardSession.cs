namespace ZETL;

/// <summary>
/// Owns Replay's clipboard content model and per-lane ownership tracking.
/// Workflow timing, paste injection, persistence, and notifications remain in
/// <see cref="ZetlShortcutCoordinator"/>.
/// </summary>
internal sealed class ZetlReplayClipboardSession(IClipboard clipboard)
{
    private readonly ReplayLaneState[] lanes = [new(), new()];

    public void Reset(bool shifted)
    {
        lanes[ZetlLanes.Index(shifted)] = new ReplayLaneState();
    }

    // Back up the user's clipboard before Replay stages over it, so it can be
    // restored afterwards. When the clipboard holds something that cannot be
    // backed up, Replay still pastes; the lane just skips restoration until the
    // user copies something new.
    public ZetlReplayBackupOutcome PreserveUserClipboard(bool shifted, out string failureReason)
    {
        var index = ZetlLanes.Index(shifted);
        var lane = lanes[index];
        var current = ReadSnapshot();
        if (MatchesTracked(lane, current))
        {
            // The clipboard still holds what this lane staged, so the decision
            // made when Replay first replaced the user's clipboard stands.
            if (lane.UserBackup is not null)
            {
                failureReason = "";
                return ZetlReplayBackupOutcome.Preserved;
            }

            if (lane.BackupFailure is { } earlierFailure)
            {
                failureReason = earlierFailure;
                return ZetlReplayBackupOutcome.StillUnavailable;
            }
        }

        var otherLane = lanes[index == 0 ? 1 : 0];
        if (MatchesTracked(otherLane, current)
            && (otherLane.UserBackup is not null || otherLane.BackupFailure is not null))
        {
            // The other Replay lane owns the current clipboard. Carry its
            // original user snapshot (or its lack of one) forward instead of
            // treating its staged Replay item as user content.
            lane.UserBackup = otherLane.UserBackup;
            lane.BackupFailure = otherLane.BackupFailure;
            failureReason = lane.BackupFailure ?? "";
            return lane.UserBackup is not null
                ? ZetlReplayBackupOutcome.Preserved
                : ZetlReplayBackupOutcome.StillUnavailable;
        }

        var backup = clipboard.CaptureBackup();
        if (!backup.IsComplete)
        {
            lane.UserBackup = null;
            lane.BackupFailure = backup.FailureReason
                ?? "the clipboard could not be backed up completely";
            failureReason = lane.BackupFailure;
            return ZetlReplayBackupOutcome.Unavailable;
        }

        lane.UserBackup = backup;
        lane.BackupFailure = null;
        failureReason = "";
        return ZetlReplayBackupOutcome.Preserved;
    }

    public bool TryStage(
        bool shifted,
        ZetlClipboardSnapshot item,
        out uint injectedToken)
    {
        return Stage(shifted, item, out injectedToken).Succeeded;
    }

    public ZetlClipboardWriteResult Stage(
        bool shifted,
        ZetlClipboardSnapshot item,
        out uint injectedToken)
    {
        injectedToken = 0;
        // Always write the queued representation. A clipboard whose visible
        // text matches the item may still carry unrelated rich formats.
        var result = WriteSnapshot(item);
        if (!result.Succeeded)
        {
            return result;
        }

        var lane = lanes[ZetlLanes.Index(shifted)];
        injectedToken = clipboard.GetChangeToken();
        lane.Injected = item;
        lane.InjectedToken = injectedToken;
        return result;
    }

    public ZetlClipboardRestoreOutcome RestoreOriginalIfOwned(bool shifted)
    {
        var lane = lanes[ZetlLanes.Index(shifted)];
        if (lane.UserBackup is null)
        {
            return ZetlClipboardRestoreOutcome.NoBackup;
        }

        if (lane.Injected is null || !MatchesTracked(lane, ReadSnapshot()))
        {
            return ZetlClipboardRestoreOutcome.OwnershipLost;
        }

        return Restore(lane);
    }

    public ZetlClipboardRestoreOutcome RestoreIfOwned(
        bool shifted,
        ZetlClipboardSnapshot injected,
        uint injectedToken)
    {
        var lane = lanes[ZetlLanes.Index(shifted)];
        if (lane.UserBackup is null)
        {
            return ZetlClipboardRestoreOutcome.NoBackup;
        }

        if (clipboard.GetChangeToken() != injectedToken
            || !ZetlClipboardSnapshot.ContentEquals(ReadSnapshot(), injected)
            || clipboard.GetChangeToken() != injectedToken)
        {
            return ZetlClipboardRestoreOutcome.OwnershipLost;
        }

        return Restore(lane);
    }

    private ZetlClipboardRestoreOutcome Restore(ReplayLaneState lane)
    {
        var result = clipboard.ReplaceWithBackup(lane.UserBackup!);
        if (!result.Succeeded)
        {
            return result.ClipboardPreserved
                ? ZetlClipboardRestoreOutcome.Failed
                : ZetlClipboardRestoreOutcome.FailedClipboardUncertain;
        }

        // Track what is actually present after a successful restore. This
        // allows the other Replay lane to inherit the original user backup.
        lane.Injected = ReadSnapshot();
        lane.InjectedToken = clipboard.GetChangeToken();
        return ZetlClipboardRestoreOutcome.Restored;
    }

    private bool MatchesTracked(
        ReplayLaneState lane,
        ZetlClipboardSnapshot? current)
    {
        var token = lane.InjectedToken;
        return token is not null
            && clipboard.GetChangeToken() == token.Value
            && ZetlClipboardSnapshot.ContentEquals(current, lane.Injected)
            && clipboard.GetChangeToken() == token.Value;
    }

    private ZetlClipboardSnapshot? ReadSnapshot()
    {
        var image = clipboard.TryGetImage();
        if (image is not null)
        {
            return ZetlClipboardSnapshot.FromImage(image);
        }

        var text = clipboard.TryGetText();
        return string.IsNullOrEmpty(text) ? null : ZetlClipboardSnapshot.FromText(text);
    }

    private ZetlClipboardWriteResult WriteSnapshot(ZetlClipboardSnapshot snapshot)
    {
        return ZetlClipboardContentWriter.Write(clipboard, snapshot);
    }

    private sealed class ReplayLaneState
    {
        public ZetlClipboardBackup? UserBackup { get; set; }

        // Why the user's clipboard could not be backed up, while Replay owns it.
        public string? BackupFailure { get; set; }

        public ZetlClipboardSnapshot? Injected { get; set; }

        public uint? InjectedToken { get; set; }
    }
}

internal enum ZetlReplayBackupOutcome
{
    Preserved,
    // The user's clipboard could not be backed up just now.
    Unavailable,
    // Replay still owns a clipboard it could not back up on an earlier tap.
    StillUnavailable
}

internal enum ZetlClipboardRestoreOutcome
{
    NoBackup,
    OwnershipLost,
    Restored,
    Failed,
    FailedClipboardUncertain
}

/// <summary>
/// Chooses the richest stored representation that can be handed to a clipboard
/// backend. Native resource allocation and transactional guarantees belong to
/// the backend/transaction boundary rather than this representation policy.
/// </summary>
internal static class ZetlClipboardContentWriter
{
    public static ZetlClipboardWriteResult Write(
        IClipboard clipboard,
        ZetlClipboardSnapshot snapshot)
    {
        return snapshot.Image is not null
            ? clipboard.ReplaceImage(snapshot.Image)
            : Write(
                clipboard,
                snapshot.Text ?? "",
                snapshot.Html,
                snapshot.ReplayFormats);
    }

    public static bool TryWrite(
        IClipboard clipboard,
        ZetlClipboardSnapshot snapshot)
    {
        return Write(clipboard, snapshot).Succeeded;
    }

    public static ZetlClipboardWriteResult Write(
        IClipboard clipboard,
        string text,
        string? html,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats)
    {
        if (replayFormats is { Count: > 0 })
        {
            return clipboard.ReplaceWithBackup(ZetlClipboardBackup.FromRaw(replayFormats));
        }

        return html is not null
            ? clipboard.ReplaceRichText(text, html)
            : clipboard.ReplaceText(text);
    }

    public static bool TryWrite(
        IClipboard clipboard,
        string text,
        string? html,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats)
    {
        return Write(clipboard, text, html, replayFormats).Succeeded;
    }
}

internal sealed record ZetlClipboardSnapshot(
    string? Text,
    string? Html,
    IReadOnlyList<ZetlClipboardFormatData>? ReplayFormats,
    ZetlClipboardImage? Image,
    string Fingerprint)
{
    public static ZetlClipboardSnapshot FromText(
        string text,
        string? html = null,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats = null) =>
        // Identity deliberately follows the visible text. Clipboard change
        // tokens provide the exact overwrite guard; including HTML here would
        // make a rich staged item unequal to the plain-text read-back.
        new(text, html, replayFormats, null, $"text:{text.Trim()}");

    public static ZetlClipboardSnapshot FromImage(ZetlClipboardImage image) =>
        new(
            null,
            null,
            null,
            image,
            "image:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(image.PngBytes)));

    public static bool ContentEquals(
        ZetlClipboardSnapshot? left,
        ZetlClipboardSnapshot? right) =>
        left is null ? right is null : right is not null
            && string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal);
}
