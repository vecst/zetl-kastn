using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

// Test doubles for the clipboard, keyboard, dispatcher, delay, notifications,
// and image resolver, plus a throwaway state file.

// The coordinator reached the way the app reaches it: every press, tap,
// and hold goes through the router with Zetl's default rules, exactly as
// Chordl delivers it. Dialog completions go straight to the coordinator.
internal sealed class RoutedCoordinator(
    ZetlShortcutCoordinator coordinator,
    ZetlGestureRouter router)
{
    public ZetlGestureRouter Router => router;
    public ZetlShortcutCoordinator Inner => coordinator;

    public Task OnPhysicalShortcutPassedThroughAsync(
        ChordlEventContext context,
        ZetlCaptureOrigin? captureOrigin = null) =>
        router.OnPressAsync(context, new Lazy<ZetlCaptureOrigin?>(() => captureOrigin));

    public bool OnTapDispatched(ChordlEventContext context) => router.OnTap(context);

    public Task<ZetlShortcutRequest?> HandleHoldAsync(ChordlEventContext context) =>
        router.OnHoldAsync(context, coordinator.ClaimPendingForHold(context));

    public Task<ZetlShortcutRequest?> HandleClaimedHoldAsync(
        ChordlEventContext context,
        ZetlPendingShortcut? pending) =>
        router.OnHoldAsync(context, pending);

    public ZetlPendingShortcut? ClaimPendingForHold(ChordlEventContext context) =>
        coordinator.ClaimPendingForHold(context);

    public ZetlPendingShortcut? CancelPending(int keyCode, bool shifted) =>
        coordinator.CancelPending(keyCode, shifted);

    public ZetlNoteCaptureOutcome CompleteNoteCapture(
        ZetlNoteCaptureRequest request,
        ZetlNoteCaptureResult result) =>
        coordinator.CompleteNoteCapture(request, result);

    public ZetlCompileOutcome CompleteCompile(
        ZetlCompileRequest request,
        ZetlCompileResult result) =>
        coordinator.CompleteCompile(request, result);

    public Task PasteCompiledTextAsync() => coordinator.PasteCompiledTextAsync();
}

internal sealed class TempStateFile : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZetlTests", Guid.NewGuid().ToString("N"));

    public TempStateFile()
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "state.json");
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

internal sealed class ImmediateDispatcher : IZetlDispatcher
{
    public void Post(Action action)
    {
        action();
    }
}

// Captures posted actions instead of running them, so a test can assert
// work was enqueued (deferred off the calling thread) and then run it.
internal sealed class QueuingDispatcher : IZetlDispatcher
{
    private readonly object gate = new();
    private readonly Queue<Action> pending = new();

    public int PendingCount
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    public void Post(Action action)
    {
        lock (gate)
        {
            pending.Enqueue(action);
            Monitor.PulseAll(gate);
        }
    }

    public void RunAll()
    {
        while (true)
        {
            Action? action;
            lock (gate)
            {
                if (!pending.TryDequeue(out action))
                {
                    return;
                }
            }
            action();
        }
    }

    public bool RunUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Action? action = null;
            lock (gate)
            {
                if (!pending.TryDequeue(out action))
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return condition();
                    }
                    Monitor.Wait(gate, remaining);
                    continue;
                }
            }
            action();
        }

        return true;
    }
}

internal sealed class ImmediateDelay : IZetlDelay
{
    public Task WaitAsync(TimeSpan delay)
    {
        return Task.CompletedTask;
    }
}

internal sealed class ManualDelay : IZetlDelay
{
    private readonly TaskCompletionSource completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(TimeSpan delay)
    {
        return completion.Task;
    }

    public void Release()
    {
        completion.TrySetResult();
    }
}

internal sealed class FakeNotificationSink : IZetlNotificationSink
{
    private readonly object messageGate = new();

    public List<string> Messages { get; } = new();

    public void Show(string message)
    {
        lock (messageGate)
        {
            Messages.Add(message);
            Monitor.PulseAll(messageGate);
        }
    }

    public bool WaitForCount(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (messageGate)
        {
            while (Messages.Count < count)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(messageGate, remaining))
                {
                    return Messages.Count >= count;
                }
            }

            return true;
        }
    }
}

internal sealed class FakeKeyboardBackend : IKeyboardBackend
{
    private readonly object pasteGate = new();
    private readonly Queue<TaskCompletionSource<bool>> deferredPastes = new();
    private int pasteCount;

    public int PasteCount => Volatile.Read(ref pasteCount);

    public bool PasteSucceeds { get; set; } = true;

    // Lets a test simulate the foreground app reacting to an injected chord
    // (e.g. Ctrl+C copying the selection onto the clipboard).
    public Action? OnSendChord { get; set; }

    public TaskCompletionSource<bool> DeferNextPaste()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pasteGate)
        {
            deferredPastes.Enqueue(completion);
        }
        return completion;
    }

    public bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent)
    {
        return true;
    }

    public Task<bool> SendChord(
        int vkCode,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift)
    {
        OnSendChord?.Invoke();
        return Task.FromResult(true);
    }

    public Task<bool> SendPaste()
    {
        Interlocked.Increment(ref pasteCount);
        lock (pasteGate)
        {
            if (deferredPastes.TryDequeue(out var completion))
            {
                return completion.Task;
            }
        }
        return Task.FromResult(PasteSucceeds);
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeClipboard : IClipboard
{
    public FakeClipboard(string? text, uint changeToken)
    {
        Text = text;
        ChangeToken = changeToken;
    }

    public string? Text { get; private set; }

    public string? RichHtml { get; private set; }

    public IReadOnlyList<ZetlClipboardFormatData>? NativeReplayFormats { get; set; }

    public IReadOnlyList<ZetlClipboardFormatData>? LastRestoredRawFormats { get; private set; }

    public uint ChangeToken { get; private set; }

    public int ImageSetCount { get; private set; }

    public int BackupRestoreCount { get; private set; }

    public bool SetTextSucceeds { get; set; } = true;

    public ZetlClipboardWriteResult? WriteResultOverride { get; set; }

    public string? BackupFailureReason { get; set; }

    // The source app marked the content private (a password manager).
    public bool MarkedPrivate { get; set; }

    public bool IsMarkedPrivate() => MarkedPrivate;

    // Staged pastes, like the Windows backend's: off unless a test turns
    // them on. ReadStagedPastes stands in for the target app reading
    // the item the moment it is pasted.
    public bool StagesPastes { get; set; }

    public bool ReadStagedPastes { get; set; } = true;

    public ZetlStagedPaste? LastStaged { get; private set; }

    public ZetlStagedPaste? StagePaste(
        string text,
        string? html,
        IReadOnlyList<ZetlClipboardFormatData>? replayFormats,
        ZetlClipboardImage? image)
    {
        if (!StagesPastes)
        {
            return null;
        }

        LastStaged?.MarkReplaced();
        SetText(text);
        LastStaged = new ZetlStagedPaste { ChangeToken = ChangeToken };
        if (ReadStagedPastes)
        {
            LastStaged.MarkRead();
        }

        return LastStaged;
    }

    public string? TryGetText()
    {
        return Text;
    }

    public string? TryGetHtml()
    {
        return RichHtml;
    }

    public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats()
    {
        return NativeReplayFormats;
    }

    public ZetlClipboardImage? Image { get; set; }

    public ZetlClipboardImage? TryGetImage()
    {
        return Image;
    }

    public bool SetText(string text)
    {
        if (!SetTextSucceeds)
        {
            return false;
        }

        Text = text;
        RichHtml = null;
        NativeReplayFormats = null;
        Image = null;
        ChangeToken++;
        return true;
    }

    public ZetlClipboardWriteResult ReplaceText(string text) =>
        WriteResultOverride ?? ZetlClipboardWriteResult.FromLegacy(SetText(text));

    public bool SetRichText(string plainText, string html)
    {
        if (!SetTextSucceeds)
        {
            return false;
        }

        Text = plainText;
        RichHtml = html;
        NativeReplayFormats = null;
        Image = null;
        ChangeToken++;
        return true;
    }

    public ZetlClipboardWriteResult ReplaceRichText(string plainText, string html) =>
        WriteResultOverride
        ?? ZetlClipboardWriteResult.FromLegacy(SetRichText(plainText, html));

    public bool SetImage(ZetlClipboardImage image)
    {
        if (!SetTextSucceeds)
        {
            return false;
        }

        Image = image;
        Text = null;
        RichHtml = null;
        NativeReplayFormats = null;
        ImageSetCount++;
        ChangeToken++;
        return true;
    }

    public ZetlClipboardWriteResult ReplaceImage(ZetlClipboardImage image) =>
        WriteResultOverride ?? ZetlClipboardWriteResult.FromLegacy(SetImage(image));

    public ZetlClipboardBackup CaptureBackup()
    {
        if (BackupFailureReason is not null)
        {
            return ZetlClipboardBackup.Incomplete(BackupFailureReason);
        }

        var image = Image is null
            ? null
            : new ZetlClipboardImage(
                Image.PngBytes.ToArray(),
                Image.Width,
                Image.Height);
        return ZetlClipboardBackup.FromPortable(Text, RichHtml, image);
    }

    public bool RestoreBackup(ZetlClipboardBackup backup)
    {
        if (!SetTextSucceeds || !backup.IsComplete)
        {
            return false;
        }

        if (backup.RawFormats is { } rawFormats)
        {
            LastRestoredRawFormats = rawFormats;
            var unicode = rawFormats.FirstOrDefault(item => item.Format == 13);
            if (unicode is not null)
            {
                Text = System.Text.Encoding.Unicode.GetString(unicode.Data).TrimEnd('\0');
            }
            var html = rawFormats.FirstOrDefault(item =>
                item.RegisteredName == "HTML Format");
            RichHtml = html is null
                ? null
                : System.Text.Encoding.UTF8.GetString(html.Data);
            NativeReplayFormats = rawFormats;
            Image = null;
            BackupRestoreCount++;
            ChangeToken++;
            return true;
        }

        Text = backup.Text;
        RichHtml = backup.Html;
        Image = backup.Image is null
            ? null
            : new ZetlClipboardImage(
                backup.Image.PngBytes.ToArray(),
                backup.Image.Width,
                backup.Image.Height);
        if (Image is not null)
        {
            ImageSetCount++;
        }
        BackupRestoreCount++;
        ChangeToken++;
        return true;
    }

    public ZetlClipboardWriteResult ReplaceWithBackup(ZetlClipboardBackup backup) =>
        WriteResultOverride
        ?? ZetlClipboardWriteResult.FromLegacy(RestoreBackup(backup));

    public uint GetChangeToken()
    {
        return ChangeToken;
    }

    public void SetState(string? text, uint changeToken)
    {
        Text = text;
        RichHtml = null;
        NativeReplayFormats = null;
        Image = null;
        ChangeToken = changeToken;
    }

    public void SetMixedState(
        string? text,
        string? html,
        ZetlClipboardImage? image,
        uint changeToken)
    {
        Text = text;
        RichHtml = html;
        NativeReplayFormats = null;
        Image = image;
        ChangeToken = changeToken;
    }
}

internal sealed class FirstWaitManualDelay : IZetlDelay
{
    private readonly TaskCompletionSource first = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int waitCount;

    public Task WaitAsync(TimeSpan delay)
    {
        return Interlocked.Increment(ref waitCount) == 1
            ? first.Task
            : Task.CompletedTask;
    }

    public void ReleaseFirst()
    {
        first.TrySetResult();
    }
}

internal sealed class GenerationChangingClipboard(bool changeEveryTextRead = false) : IClipboard
{
    private int generation = 1;
    private int changeOnFirstTextRead = 1;
    private uint changeToken = 2;

    public int TextReadCount { get; private set; }

    public string? TryGetText()
    {
        TextReadCount++;
        var value = generation == 1 ? "generation one" : "generation two";
        if (changeEveryTextRead
            || Interlocked.Exchange(ref changeOnFirstTextRead, 0) == 1)
        {
            generation++;
            changeToken++;
        }
        return value;
    }

    public string? TryGetHtml() => generation == 1
        ? "<p><em>generation one</em></p>"
        : "<p><strong>generation two</strong></p>";

    public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats() =>
        [new ZetlClipboardFormatData(
            0xC001,
            [(byte)generation],
            "Star Embed Source (XML)")];

    public ZetlClipboardImage? TryGetImage() => new(
        [(byte)generation],
        generation,
        generation);

    public bool SetText(string text) => false;

    public bool SetRichText(string plainText, string html) => false;

    public bool SetImage(ZetlClipboardImage image) => false;

    public ZetlClipboardBackup CaptureBackup() =>
        ZetlClipboardBackup.Incomplete("test clipboard is read-only");

    public bool RestoreBackup(ZetlClipboardBackup backup) => false;

    public uint GetChangeToken() => changeToken;
}

internal sealed class FakeImageUrlResolver(ZetlResolvedImageUrl? result) : IImageUrlResolver
{
    public Task<ZetlResolvedImageUrl?> TryResolveAsync(string text) =>
        Task.FromResult(result);
}
