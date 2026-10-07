using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// Asks the window manager to activate one of Zetl's X11 windows (Avalonia
/// runs on X11, under Xwayland on Wayland desktops). Zetl reads the keyboard
/// below the desktop, so the window manager never sees the shortcut as input
/// to Zetl, and its focus-stealing prevention refuses an ordinary activation
/// from a background app: the popup opens behind the active window. The
/// request is sent as a pager/tool request (source 2), which window managers
/// treat as the user's own choice, the way task switchers and xdotool do.
/// </summary>
internal static class ZetlX11Activation
{
    private const string Library = "libX11.so.6";
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1L << 19;
    private const long SubstructureRedirectMask = 1L << 20;
    private const long SourcePagerOrTool = 2;
    private static readonly object Gate = new();
    private static IntPtr display;
    private static IntPtr activeWindowAtom;
    private static IntPtr pidAtom;
    private const long WindowType = 33;   // XA_WINDOW
    private const long CardinalType = 6;  // XA_CARDINAL
    private static bool unavailable;

    /// <summary>Requests activation of an X11 window; false when X11 is unavailable.</summary>
    public static bool RequestActivation(IntPtr window, Action<string>? log)
    {
        lock (Gate)
        {
            if (!EnsureDisplay(log)) return false;

            // XClientMessageEvent inside a 192-byte XEvent (64-bit layout):
            // type@0, serial@8, send_event@16, display@24, window@32,
            // message_type@40, format@48, data.l[5]@56.
            var xevent = new byte[192];
            BitConverter.TryWriteBytes(xevent.AsSpan(0), ClientMessage);
            BitConverter.TryWriteBytes(xevent.AsSpan(16), 1);
            BitConverter.TryWriteBytes(xevent.AsSpan(24), display.ToInt64());
            BitConverter.TryWriteBytes(xevent.AsSpan(32), window.ToInt64());
            BitConverter.TryWriteBytes(xevent.AsSpan(40), activeWindowAtom.ToInt64());
            BitConverter.TryWriteBytes(xevent.AsSpan(48), 32);
            BitConverter.TryWriteBytes(xevent.AsSpan(56), SourcePagerOrTool);
            // data.l[1] = CurrentTime (0); data.l[2] = no currently active window.
            var sent = XSendEvent(
                display,
                XDefaultRootWindow(display),
                propagate: 0,
                (IntPtr)(SubstructureRedirectMask | SubstructureNotifyMask),
                xevent);
            XFlush(display);
            return sent != 0;
        }
    }

    private static bool EnsureDisplay(Action<string>? log)
    {
        if (display != IntPtr.Zero) return true;
        if (unavailable) return false;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            display = IntPtr.Zero;
        }

        if (display == IntPtr.Zero)
        {
            unavailable = true;
            log?.Invoke("X11 activation unavailable; popups rely on the window manager's own focus.");
            return false;
        }

        activeWindowAtom = XInternAtom(display, "_NET_ACTIVE_WINDOW", onlyIfExists: 0);
        pidAtom = XInternAtom(display, "_NET_WM_PID", onlyIfExists: 0);
        return true;
    }

    /// <summary>
    /// The window manager's active X11 window (zero when a native Wayland window
    /// or nothing is active). This is the authoritative answer: Avalonia's own
    /// activation tracking can miss an activation it did not request itself.
    /// </summary>
    public static bool TryGetActiveWindow(out IntPtr window)
    {
        lock (Gate)
        {
            window = IntPtr.Zero;
            if (!EnsureDisplay(log: null)) return false;
            window = ReadLongProperty(XDefaultRootWindow(display), activeWindowAtom, WindowType) is { } value
                ? (IntPtr)value
                : IntPtr.Zero;
            return true;
        }
    }

    /// <summary>
    /// True when X11 keyboard focus is in a window of the given process. When a
    /// native Wayland surface (another app, the desktop) takes the keyboard,
    /// Xwayland moves X focus off every X11 window, so this turns false.
    /// </summary>
    public static bool? FocusIsInProcess(long processId)
    {
        lock (Gate)
        {
            if (!EnsureDisplay(log: null)) return null;
            XGetInputFocus(display, out var focus, out _);
            // None (0) and PointerRoot (1) mean no X11 window has focus.
            var root = XDefaultRootWindow(display);
            for (var window = focus; window != IntPtr.Zero && window != (IntPtr)1 && window != root;)
            {
                if (ReadLongProperty(window, pidAtom, CardinalType) is { } pid) return pid == processId;
                if (XQueryTree(display, window, out _, out var parent, out var children, out _) == 0) break;
                if (children != IntPtr.Zero) XFree(children);
                window = parent;
            }

            return false;
        }
    }

    /// <summary>The process that owns an X11 window (its _NET_WM_PID), or null if unknown.</summary>
    public static long? GetProcessId(IntPtr window)
    {
        lock (Gate)
        {
            return window != IntPtr.Zero && EnsureDisplay(log: null)
                ? ReadLongProperty(window, pidAtom, CardinalType)
                : null;
        }
    }

    // Reads the first 32-bit-format item of a property; Xlib returns those as C longs.
    private static long? ReadLongProperty(IntPtr window, IntPtr property, long type)
    {
        if (XGetWindowProperty(display, window, property, 0, 1, 0, (IntPtr)type,
                out _, out var format, out var count, out _, out var data) != 0)
        {
            return null;
        }

        try
        {
            return data != IntPtr.Zero && format == 32 && count != 0 ? Marshal.ReadInt64(data) : null;
        }
        finally
        {
            if (data != IntPtr.Zero) XFree(data);
        }
    }

    [DllImport(Library)]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport(Library)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(Library)]
    private static extern IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);

    [DllImport(Library)]
    private static extern int XSendEvent(IntPtr display, IntPtr window, int propagate, IntPtr eventMask, byte[] xevent);

    [DllImport(Library)]
    private static extern int XFlush(IntPtr display);

    [DllImport(Library)]
    private static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property, long offset, long length, int delete, IntPtr requestedType,
        out IntPtr actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out IntPtr data);

    [DllImport(Library)]
    private static extern int XFree(IntPtr data);

    [DllImport(Library)]
    private static extern int XGetInputFocus(IntPtr display, out IntPtr focus, out int revertTo);

    [DllImport(Library)]
    private static extern int XQueryTree(
        IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint childCount);
}
