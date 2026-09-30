using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlClipboardTransactionTests
{
    [Fact] public void EachTargetFormatFailureRestoresOriginalClipboard()
    {
        foreach (var failedFormat in new uint[] { 10, 11, 12 })
        {
            var clipboard = new Dictionary<uint, string>
            {
                [1] = "original-text",
                [2] = "original-rich"
            };
            var target = Stage((10u, "new-text"), (11u, "new-rich"), (12u, "new-native"));
            var rollback = Stage((1u, "original-text"), (2u, "original-rich"));
            var emptyCount = 0;

            var result = ZetlNativeClipboardTransaction.Execute(
                target,
                rollback,
                () =>
                {
                    emptyCount++;
                    clipboard.Clear();
                    return true;
                },
                (format, payload) =>
                {
                    if (emptyCount == 1 && format == failedFormat)
                    {
                        return false;
                    }

                    clipboard[format] = payload;
                    return true;
                });

            AssertEqual(
                ZetlClipboardWriteStatus.WriteFailedRolledBack,
                result.Status,
                $"Failure at native target format {failedFormat} should report rollback.");
            AssertTrue(result.ClipboardPreserved, "A completed rollback should report preserved content.");
            AssertEqual(2, clipboard.Count, "Rollback should restore only the original formats.");
            AssertEqual("original-text", clipboard[1], "Rollback should restore original text.");
            AssertEqual("original-rich", clipboard[2], "Rollback should restore original rich content.");
        }
    }

    [Fact] public void RollbackFormatFailureReportsClipboardUncertain()
    {
        var clipboard = new Dictionary<uint, string>
        {
            [1] = "original-text",
            [2] = "original-rich"
        };
        var target = Stage((10u, "new-text"), (11u, "new-rich"));
        var rollback = Stage((1u, "original-text"), (2u, "original-rich"));
        var emptyCount = 0;

        var result = ZetlNativeClipboardTransaction.Execute(
            target,
            rollback,
            () =>
            {
                emptyCount++;
                clipboard.Clear();
                return true;
            },
            (format, payload) =>
            {
                if ((emptyCount == 1 && format == 11)
                    || (emptyCount == 2 && format == 2))
                {
                    return false;
                }

                clipboard[format] = payload;
                return true;
            });

        AssertEqual(
            ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
            result.Status,
            "A rollback format failure must have a distinct structured outcome.");
        AssertTrue(!result.ClipboardPreserved, "Partial rollback must not claim the clipboard was preserved.");
        AssertEqual(1, clipboard.Count, "The fake should expose the partial rollback for fault verification.");
        AssertEqual("original-text", clipboard[1], "Successfully restored formats should remain present.");
    }

    [Fact] public void EmptyFailureLeavesOriginalClipboardUntouched()
    {
        var clipboard = new Dictionary<uint, string> { [1] = "original" };
        var transferCalls = 0;

        var result = ZetlNativeClipboardTransaction.Execute(
            Stage((10u, "new")),
            Stage((1u, "original")),
            () => false,
            (_, _) =>
            {
                transferCalls++;
                return true;
            });

        AssertEqual(
            ZetlClipboardWriteStatus.EmptyFailed,
            result.Status,
            "A pre-mutation empty failure should be explicit.");
        AssertTrue(result.ClipboardPreserved, "A failed initial empty should leave the clipboard intact.");
        AssertEqual(0, transferCalls, "No native format should transfer after EmptyClipboard fails.");
        AssertEqual("original", clipboard[1], "The original clipboard should remain untouched.");
    }

    [Fact] public void WriteWithoutBackupSucceedsAndReportsUncertainFailure()
    {
        // An original clipboard that could not be backed up (null rollback) must
        // not block the write; only a failed write is reported as uncertain.
        var clipboard = new Dictionary<uint, string> { [1] = "unreadable original" };
        var result = ZetlNativeClipboardTransaction.Execute(
            Stage((10u, "new")),
            rollback: null,
            () =>
            {
                clipboard.Clear();
                return true;
            },
            (format, payload) =>
            {
                clipboard[format] = payload;
                return true;
            });

        AssertEqual(ZetlClipboardWriteStatus.Success, result.Status, "A write without a backup should still succeed.");
        AssertEqual("new", clipboard[10], "The requested content should be written.");

        var failed = ZetlNativeClipboardTransaction.Execute(
            Stage((10u, "new"), (11u, "new-rich")),
            rollback: null,
            () => true,
            (format, _) => format != 11);
        AssertEqual(
            ZetlClipboardWriteStatus.WriteFailedRestoreFailed,
            failed.Status,
            "A failed write with no backup cannot claim the original was restored.");
        AssertTrue(!failed.ClipboardPreserved, "A failed write with no backup leaves the clipboard uncertain.");
    }

    [Fact] public void SuccessfulTransactionRequiresEveryTargetFormat()
    {
        var clipboard = new Dictionary<uint, string> { [1] = "original" };
        var target = Stage((10u, "new-text"), (11u, "new-rich"), (12u, "new-native"));

        var result = ZetlNativeClipboardTransaction.Execute(
            target,
            Stage((1u, "original")),
            () =>
            {
                clipboard.Clear();
                return true;
            },
            (format, payload) =>
            {
                clipboard[format] = payload;
                return true;
            });

        AssertEqual(ZetlClipboardWriteStatus.Success, result.Status, "A complete target should succeed.");
        AssertEqual(3, clipboard.Count, "Every requested target format should be present.");
        AssertTrue(target.All(item => item.Transferred), "Every target payload should transfer ownership.");
    }

    private static List<ZetlStagedClipboardFormat<string>> Stage(
        params (uint Format, string Payload)[] formats) =>
        formats.Select(item =>
            new ZetlStagedClipboardFormat<string>(item.Format, item.Payload)).ToList();
}
