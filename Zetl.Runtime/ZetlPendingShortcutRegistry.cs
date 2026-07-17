namespace ZETL;

/// <summary>
/// Owns pending copy/cut gestures by chord lane. Claiming is atomic with
/// cancellation so the delayed auto-capture path cannot commit the same gesture.
/// </summary>
internal sealed class ZetlPendingShortcutRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<(int KeyCode, bool Shifted), ZetlPendingShortcut> pending = new();

    public ZetlPendingShortcut Register(
        int keyCode,
        bool shifted,
        uint clipboardSequenceNumber,
        ZetlCaptureOrigin? captureOrigin = null)
    {
        var shortcut = new ZetlPendingShortcut(
            keyCode,
            shifted,
            clipboardSequenceNumber,
            captureOrigin);
        lock (gate)
        {
            pending[(keyCode, shifted)] = shortcut;
        }

        return shortcut;
    }

    public ZetlPendingShortcut? Claim(int keyCode, bool shifted)
    {
        lock (gate)
        {
            if (!pending.Remove((keyCode, shifted), out var shortcut))
            {
                return null;
            }

            shortcut.Cancel();
            return shortcut;
        }
    }
}

internal sealed class ZetlPendingShortcut
{
    private readonly object gate = new();
    private ZetlClipboardCaptureSnapshot? observedClipboardContent;
    private bool cancelled;

    public ZetlPendingShortcut(
        int keyCode,
        bool shiftLane,
        uint clipboardSequenceNumber,
        ZetlCaptureOrigin? captureOrigin = null)
    {
        KeyCode = keyCode;
        ShiftLane = shiftLane;
        ClipboardSequenceNumber = clipboardSequenceNumber;
        CaptureOrigin = captureOrigin;
    }

    public int KeyCode { get; }

    public bool ShiftLane { get; }

    public uint ClipboardSequenceNumber { get; }

    public ZetlCaptureOrigin? CaptureOrigin { get; }

    public string? ObservedClipboardText
    {
        get
        {
            lock (gate)
            {
                return observedClipboardContent?.Text;
            }
        }
    }

    public ZetlClipboardImage? ObservedClipboardImage
    {
        get
        {
            lock (gate)
            {
                return observedClipboardContent?.Image;
            }
        }
    }

    public string? ObservedClipboardHtml
    {
        get
        {
            lock (gate)
            {
                return observedClipboardContent?.Html;
            }
        }
    }

    public IReadOnlyList<ZetlClipboardFormatData>? ObservedReplayFormats
    {
        get
        {
            lock (gate)
            {
                return observedClipboardContent?.ReplayFormats;
            }
        }
    }

    public bool Cancelled
    {
        get
        {
            lock (gate)
            {
                return cancelled;
            }
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            cancelled = true;
        }
    }

    public void SetObservedClipboardText(string? text)
    {
        lock (gate)
        {
            observedClipboardContent = new ZetlClipboardCaptureSnapshot(
                ClipboardSequenceNumber,
                text,
                null,
                null,
                null);
        }
    }

    public ZetlClipboardCaptureSnapshot? GetObservedClipboardContent()
    {
        lock (gate)
        {
            return observedClipboardContent;
        }
    }

    public void SetObservedClipboardContent(ZetlClipboardCaptureSnapshot content)
    {
        lock (gate)
        {
            observedClipboardContent = content;
        }
    }

    public void SetObservedClipboardContent(
        string? text,
        ZetlClipboardImage? image,
        string? html = null,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats = null)
    {
        lock (gate)
        {
            observedClipboardContent = new ZetlClipboardCaptureSnapshot(
                ClipboardSequenceNumber,
                text,
                html,
                replayFormats,
                image);
        }
    }
}
