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
    private static IntPtr clientListAtom;
    private const long WindowType = 33;   // XA_WINDOW
    private const long CardinalType = 6;  // XA_CARDINAL
    private const long WmHintsAtom = 35;  // XA_WM_HINTS, also its type
    private const int PropModeReplace = 0;
    private static IntPtr userTimeAtom;
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

    /// <summary>
    /// Makes a not-yet-shown X11 window override-redirect: the window manager
    /// leaves it alone, so it goes exactly where it is put and never takes the
    /// keyboard. False when X11 is unavailable.
    /// </summary>
    public static bool MakeOverlay(IntPtr window, Action<string>? log)
    {
        lock (Gate)
        {
            if (window == IntPtr.Zero || !EnsureDisplay(log)) return false;

            // XSetWindowAttributes (64-bit layout): override_redirect is the
            // int at offset 88 of the 112-byte struct; CWOverrideRedirect = 1 << 9.
            var attributes = new byte[112];
            BitConverter.TryWriteBytes(attributes.AsSpan(88), 1);
            XChangeWindowAttributes(display, window, (IntPtr)(1L << 9), attributes);
            XFlush(display);
            return true;
        }
    }

    /// <summary>
    /// Tells the window manager never to give an X11 window the keyboard: its
    /// WM_HINTS say it takes no input, and its _NET_WM_USER_TIME of 0 says it
    /// did not come from the user, so it isn't focused when it appears. For
    /// windows nobody types into (the toast, the hold ring).
    /// </summary>
    public static void NeverFocus(IntPtr window)
    {
        lock (Gate)
        {
            if (window == IntPtr.Zero || !EnsureDisplay(log: null)) return;

            // WM_HINTS is nine 32-bit fields: flags, input, ...; InputHint = 1.
            var hints = ReadLongProperties(window, (IntPtr)WmHintsAtom, WmHintsAtom, 9) is { Length: 9 } existing
                ? existing
                : new long[9];
            hints[0] |= 1;
            hints[1] = 0;
            XChangeProperty(display, window, (IntPtr)WmHintsAtom, (IntPtr)WmHintsAtom, 32, PropModeReplace, hints, hints.Length);
            XChangeProperty(display, window, userTimeAtom, (IntPtr)CardinalType, 32, PropModeReplace, [0L], 1);
            XFlush(display);
        }
    }

    /// <summary>
    /// Moves an X11 window and waits until the server has done it, so a show
    /// that follows (on Avalonia's own connection) maps it already in place.
    /// </summary>
    public static void MoveNow(IntPtr window, int x, int y)
    {
        lock (Gate)
        {
            if (window == IntPtr.Zero || !EnsureDisplay(log: null)) return;
            XMoveWindow(display, window, x, y);
            XSync(display, discard: 0);
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
        clientListAtom = XInternAtom(display, "_NET_CLIENT_LIST", onlyIfExists: 0);
        userTimeAtom = XInternAtom(display, "_NET_WM_USER_TIME", onlyIfExists: 0);
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
            var root = XDefaultRootWindow(display);
            if (ReadLongProperties(root, activeWindowAtom, WindowType, 1) is not [var active] || active == 0)
            {
                return true;
            }

            // While a native Wayland window is active, KWin names a helper window
            // of its own; only a managed client window counts as an app window.
            // xwayland-satellite (niri) keeps no client list, and clears the
            // active window to zero while a Wayland window has focus, so with no
            // list the active window is taken as it is.
            if (ReadLongProperties(root, clientListAtom, WindowType, 4096) is not { } clients
                || clients.Contains(active))
            {
                window = (IntPtr)active;
            }

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

    private static long? ReadLongProperty(IntPtr window, IntPtr property, long type) =>
        ReadLongProperties(window, property, type, 1) is [var value] ? value : null;

    // Reads up to maxItems 32-bit-format items; Xlib returns those as C longs.
    private static long[]? ReadLongProperties(IntPtr window, IntPtr property, long type, int maxItems)
    {
        if (XGetWindowProperty(display, window, property, 0, maxItems, 0, (IntPtr)type,
                out _, out var format, out var count, out _, out var data) != 0)
        {
            return null;
        }

        try
        {
            if (data == IntPtr.Zero || format != 32) return null;
            var values = new long[(int)count];
            for (var i = 0; i < values.Length; i++) values[i] = Marshal.ReadInt64(data, i * 8);
            return values;
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
    private static extern int XChangeProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, long[] data, int count);

    [DllImport(Library)]
    private static extern int XMoveWindow(IntPtr display, IntPtr window, int x, int y);

    [DllImport(Library)]
    private static extern int XSync(IntPtr display, int discard);

    [DllImport(Library)]
    private static extern int XChangeWindowAttributes(IntPtr display, IntPtr window, IntPtr valueMask, byte[] attributes);

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
