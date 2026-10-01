using System.Runtime.InteropServices;
using System.Text;

namespace ZETL;

// Detects when keyboard focus is in a Windows shell file view: File Explorer's
// item list, the desktop, or the file list in an Open/Save dialog. All of them
// host the shell's SHELLDLL_DefView, and a Ctrl+V there pastes files, not text.
// Called from the keyboard hook thread, so it only reads window state.
internal static class ZetlShellFileView
{
    private const string ShellViewClass = "SHELLDLL_DefView";
    private const uint GaParent = 1;
    private const int MaxDepth = 32;

    public static bool HasFocus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var foreground = Win32Interop.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var threadId = Win32Interop.GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info))
        {
            return false;
        }

        var window = info.Focus;
        for (var depth = 0; window != IntPtr.Zero && depth < MaxDepth; depth++)
        {
            if (HasClass(window, ShellViewClass))
            {
                return true;
            }

            if (window == foreground)
            {
                break;
            }

            window = GetAncestor(window, GaParent);
        }

        return false;
    }

    // Window class names are at most 256 characters. A StringBuilder is
    // marshalled as a real buffer of its capacity, so GetClassName can never
    // write past what it was given.
    private static bool HasClass(IntPtr window, string className)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(window, buffer, buffer.Capacity);
        return length > 0 && string.Equals(buffer.ToString(), className, StringComparison.Ordinal);
    }

    // For the Windows self-tests: the class name the check reads for a window.
    internal static string? ReadClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
}
