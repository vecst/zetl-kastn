using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Chordl;

namespace ZETL;

internal static class ZetlPlatformServices
{
    public static IKeyboardBackend CreateKeyboard(
        Action<string> log,
        bool allowInjectedInputForTesting)
    {
        return OperatingSystem.IsWindows()
            ? new AvaloniaWindowsKeyboardBackend(log, allowInjectedInputForTesting)
            : new UnsupportedKeyboardBackend(log);
    }

    public static IClipboard CreateClipboard(Action<string> log)
    {
        return OperatingSystem.IsWindows()
            ? new AvaloniaWindowsClipboard(log)
            : new UnsupportedClipboard(log);
    }
}

internal sealed class UnsupportedKeyboardBackend(Action<string> log) : IKeyboardBackend
{
    public bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent)
    {
        log("Global shortcuts are unavailable: no platform keyboard backend is installed.");
        return false;
    }

    public Task<bool> SendChord(
        int vkCode,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift) => Task.FromResult(false);

    public Task<bool> SendPaste() => Task.FromResult(false);

    public void Dispose()
    {
    }
}

internal sealed class UnsupportedClipboard(Action<string> log) : IClipboard
{
    public string? TryGetText() => null;

    public string? TryGetHtml() => null;

    public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats() => null;

    public ZetlClipboardImage? TryGetImage() => null;

    public bool SetImage(ZetlClipboardImage image)
    {
        log("Clipboard image write ignored: no platform clipboard backend is installed.");
        return false;
    }

    public bool SetText(string text)
    {
        log("Clipboard write ignored: no platform clipboard backend is installed.");
        return false;
    }

    public bool SetRichText(string plainText, string html)
    {
        log("Rich clipboard write ignored: no platform clipboard backend is installed.");
        return false;
    }

    public ZetlClipboardBackup CaptureBackup() =>
        ZetlClipboardBackup.Incomplete("no platform clipboard backend is installed");

    public bool RestoreBackup(ZetlClipboardBackup backup)
    {
        log("Clipboard restore ignored: no platform clipboard backend is installed.");
        return false;
    }

    public uint GetChangeToken() => 0;
}
