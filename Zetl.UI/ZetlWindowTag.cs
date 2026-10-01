using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ZETL;

// Marks Zetl's and Kastn's own windows with a window property, so Zetl's
// keyboard hook can tell from the foreground window alone whether the user is
// typing in one of them, and whether it is a Zetl popup, in either process.
internal static class ZetlWindowTag
{
    public const string PropertyName = "Zetl.OwnWindow";
    public const int None = 0;
    public const int OwnWindow = 1;
    public const int OwnPopup = 2;

    // The property is set under a global atom, so tagging a window adds nothing
    // the window has to clean up when it is destroyed.
    private static ushort atom;

    // Tags every window the app opens from here on; isPopup picks the kind.
    public static void TagAllWindows(Func<Window, bool> isPopup)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        atom = GlobalAddAtom(PropertyName);
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (atom != 0 && window.TryGetPlatformHandle()?.Handle is { } handle)
            {
                SetProp(handle, (IntPtr)atom, (IntPtr)(isPopup(window) ? OwnPopup : OwnWindow));
            }
        });
    }

    // The tag on the foreground window, or None when it belongs to neither app.
    // Called from the keyboard hook thread; two cheap window-manager reads.
    public static int ReadForeground()
    {
        if (!OperatingSystem.IsWindows())
        {
            return None;
        }

        var foreground = GetForegroundWindow();
        return foreground == IntPtr.Zero
            ? None
            : (int)GetProp(foreground, PropertyName);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPropW")]
    private static extern IntPtr GetProp(IntPtr window, string name);

    [DllImport("user32.dll", EntryPoint = "SetPropW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr window, IntPtr atom, IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GlobalAddAtomW")]
    private static extern ushort GlobalAddAtom(string name);
}
