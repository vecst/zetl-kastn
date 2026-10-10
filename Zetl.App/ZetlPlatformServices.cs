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
        if (OperatingSystem.IsWindows())
        {
            return new AvaloniaWindowsKeyboardBackend(log, allowInjectedInputForTesting);
        }

        return OperatingSystem.IsLinux()
            ? new LinuxKeyboardBackend(log, allowInjectedInputForTesting)
            : new UnsupportedKeyboardBackend(log);
    }

    public static IClipboard CreateClipboard(Action<string> log)
    {
        if (OperatingSystem.IsWindows())
        {
            return new AvaloniaWindowsClipboard(log);
        }

        return OperatingSystem.IsLinux() && ConnectLinuxSelection(log) is { } selection
            ? new LinuxClipboard(selection, ZetlClipboardImages.Normalize, log)
            : new UnsupportedClipboard(log);
    }

    // Wayland's clipboard-manager protocol where the compositor has one (KDE,
    // wlroots); otherwise X11's CLIPBOARD selection, which covers X11 sessions and
    // GNOME (Mutter mirrors every copy into Xwayland). ZETL_LINUX_CLIPBOARD=x11
    // skips Wayland, to test the X11 path on a desktop that has both.
    internal static ILinuxSelection? ConnectLinuxSelection(Action<string> log)
    {
        var forceX11 = string.Equals(
            Environment.GetEnvironmentVariable("ZETL_LINUX_CLIPBOARD"), "x11", StringComparison.OrdinalIgnoreCase);
        ILinuxSelection? selection = forceX11 ? null : WaylandDataControl.TryConnect(log);
        selection ??= X11Selection.TryConnect(log);
        if (selection is null)
        {
            log("Linux clipboard: no clipboard Zetl can watch; clipboard capture is off.");
        }

        return selection;
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
