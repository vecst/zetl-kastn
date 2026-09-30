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
        ZetlCaptureOrigin? captureOrigin = null) =>
        Register(
            keyCode,
            shifted,
            clipboardSequenceNumber,
            deferredCaptureOrigin: new Lazy<ZetlCaptureOrigin?>(() => captureOrigin));

    public ZetlPendingShortcut Register(
        int keyCode,
        bool shifted,
        uint clipboardSequenceNumber,
        Lazy<ZetlCaptureOrigin?> deferredCaptureOrigin)
    {
        var shortcut = new ZetlPendingShortcut(
            keyCode,
            shifted,
            clipboardSequenceNumber,
            deferredCaptureOrigin: deferredCaptureOrigin);
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
    private readonly Lazy<ZetlCaptureOrigin?> captureOrigin;

    public ZetlPendingShortcut(
        int keyCode,
        bool shiftLane,
        uint clipboardSequenceNumber,
        ZetlCaptureOrigin? captureOrigin = null)
        : this(
            keyCode,
            shiftLane,
            clipboardSequenceNumber,
            deferredCaptureOrigin: new Lazy<ZetlCaptureOrigin?>(() => captureOrigin))
    {
    }

    // The origin is resolved on first use, off the keyboard hook thread.
    public ZetlPendingShortcut(
        int keyCode,
        bool shiftLane,
        uint clipboardSequenceNumber,
        Lazy<ZetlCaptureOrigin?> deferredCaptureOrigin)
    {
        KeyCode = keyCode;
        ShiftLane = shiftLane;
        ClipboardSequenceNumber = clipboardSequenceNumber;
        captureOrigin = deferredCaptureOrigin;
    }

    public int KeyCode { get; }

    public bool ShiftLane { get; }

    public uint ClipboardSequenceNumber { get; }

    public ZetlCaptureOrigin? CaptureOrigin => captureOrigin.Value;

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
