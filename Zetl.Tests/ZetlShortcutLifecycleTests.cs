using Chordl;
using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlShortcutLifecycleTests
{
    [Fact] public void ClaimAtomicallyRemovesAndCancelsPendingShortcut()
    {
        var registry = new ZetlPendingShortcutRegistry();
        var origin = ZetlCaptureOrigin.Create(
            "Browser",
            "browser",
            "Pending source",
            ZetlCaptureOriginDetail.ApplicationAndWindowTitle);
        var pending = registry.Register(
            ChordlKeys.VK_C,
            shifted: false,
            clipboardSequenceNumber: 17,
            origin);
        pending.SetObservedClipboardText("observed copy");

        var claimed = registry.Claim(ChordlKeys.VK_C, shifted: false);

        AssertTrue(ReferenceEquals(pending, claimed), "Claim should return the registered gesture.");
        AssertTrue(claimed!.Cancelled, "Claim must cancel delayed auto-capture before returning.");
        AssertEqual("observed copy", claimed.ObservedClipboardText, "Claim should retain observed clipboard content.");
        AssertEqual("Pending source", claimed.CaptureOrigin?.WindowTitle, "Claim should retain capture origin.");
        AssertTrue(
            registry.Claim(ChordlKeys.VK_C, shifted: false) is null,
            "A pending gesture should be claimable only once.");
    }

    [Fact] public void PendingShortcutLanesRemainIndependent()
    {
        var registry = new ZetlPendingShortcutRegistry();
        var normal = registry.Register(ChordlKeys.VK_C, false, 1);
        var shifted = registry.Register(ChordlKeys.VK_C, true, 2);

        AssertTrue(
            ReferenceEquals(shifted, registry.Claim(ChordlKeys.VK_C, true)),
            "The shifted lane should claim only its pending gesture.");
        AssertTrue(!normal.Cancelled, "Claiming the shifted lane must not cancel the normal lane.");
        AssertTrue(
            ReferenceEquals(normal, registry.Claim(ChordlKeys.VK_C, false)),
            "The normal lane should remain independently claimable.");
    }
}
