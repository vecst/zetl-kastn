using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace ZETL;

internal static class ZetlWindowActivation
{
    public static void Show(
        Window window,
        Window? owner = null,
        object? activationTarget = null,
        Action<string>? log = null)
    {
        if (!window.IsVisible)
        {
            if (owner is not null)
            {
                window.Show(owner);
            }
            else
            {
                window.Show();
            }
        }

        window.WindowState = WindowState.Normal;

        if (OperatingSystem.IsLinux())
        {
            ActivateX11Window(window, log);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            window.Activate();
            return;
        }

        ActivateWindowsForeground(window, activationTarget, log);
    }

    // The Linux counterpart of the Windows foreground steal: keep the popup
    // above while activation is contested, and ask the window manager to
    // activate it the way a task switcher would, retrying until it holds focus.
    private static void ActivateX11Window(Window window, Action<string>? log)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        window.Topmost = true;
        window.Activate();
        if (handle == IntPtr.Zero || !ZetlX11Activation.RequestActivation(handle, log))
        {
            window.Topmost = false;
            return;
        }

        var attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        window.Closing += (_, _) =>
        {
            window.Topmost = false;
            timer.Stop();
        };
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (!window.IsVisible)
            {
                timer.Stop();
                return;
            }

            var active = ZetlX11Activation.TryGetActiveWindow(out var current) && current == handle;
            if (active || attempts >= 6)
            {
                window.Topmost = false;
                timer.Stop();
                if (active)
                {
                    EnsureKeyboardFocus(window, log);
                }

                log?.Invoke($"Activation settled: attempts={attempts}, active={active}.");
                return;
            }

            ZetlX11Activation.RequestActivation(handle, log);
        };
        timer.Start();
    }

    // An X11 activation never raises Avalonia's Activated, so nothing restores
    // or sets keyboard focus inside the window, and Avalonia delivers keys only
    // to the focused control, which is one control for the whole app. Seen on
    // GNOME: Esc in the Board did nothing until a click, and with the tour open
    // the tour's text box kept the Board's keys. Give the window its starting
    // control unless focus is already inside it.
    internal static void EnsureKeyboardFocus(Window window, Action<string>? log = null)
    {
        var before = window.FocusManager?.GetFocusedElement();
        if (before is Visual focused && ReferenceEquals(TopLevel.GetTopLevel(focused), window))
        {
            return;
        }

        // A list takes focus through its rows, not itself.
        var preferred = (window as ZetlPopupWindow)?.InitialFocusElement;
        IInputElement? row = preferred is ListBox list
            ? list.ContainerFromIndex(Math.Max(list.SelectedIndex, 0))
            : null;
        IInputElement?[] candidates =
            [row, preferred, KeyboardNavigationHandler.GetNext(window, NavigationDirection.Next)];
        var target = candidates.FirstOrDefault(candidate => candidate?.Focus() == true);
        log?.Invoke($"Keyboard focus: {Describe(before)} -> {Describe(target)} in {window.Title}.");
    }

    private static string Describe(IInputElement? element) => element switch
    {
        null => "nothing",
        Control { Name: { Length: > 0 } name } control => $"{control.GetType().Name} '{name}'"
            + (TopLevel.GetTopLevel(control) is Window { Title: { } title } ? $" in '{title}'" : ""),
        Visual visual => visual.GetType().Name
            + (TopLevel.GetTopLevel(visual) is Window { Title: { } title } ? $" in '{title}'" : ""),
        _ => element.GetType().Name,
    };

    private static void ActivateWindowsForeground(
        Window window,
        object? activationTarget,
        Action<string>? log)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // The window we are taking the foreground from: the captured shortcut
        // target if we have one, otherwise whatever is foreground right now.
        var outgoing = ZetlForegroundService.GetWindowsHandle(activationTarget)
            is { } target && target != IntPtr.Zero
            ? target
            : Win32Interop.GetForegroundWindow();

        // Force Z-order so the popup is visible even while the foreground steal
        // is contested. Topmost is an Avalonia property (SetWindowPos with
        // SWP_NOACTIVATE), so it does not perturb activation state.
        window.Topmost = true;
        TryActivate(window, outgoing);

        // Windows can deny SetForegroundWindow to a background process on the
        // first try. Retry the activation through Avalonia (never raw Win32, so
        // its focus manager stays in sync and IsActive matches reality) until
        // the window actually holds the foreground.
        var attempts = 0;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        // Stop re-asserting the foreground the instant the popup starts closing.
        // Otherwise a late tick can re-grab the foreground for the closing popup
        // right after its Closed handler restored the caller's window, leaving
        // focus on whatever Windows picks under the popup.
        window.Closing += (_, _) =>
        {
            window.Topmost = false;
            timer.Stop();
        };
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (!window.IsVisible)
            {
                timer.Stop();
                return;
            }

            var foreground = Win32Interop.GetForegroundWindow();
            if (foreground == handle || attempts >= 6)
            {
                window.Topmost = false;
                timer.Stop();
                log?.Invoke(
                    $"Activation settled: attempts={attempts}, "
                    + $"foreground=0x{foreground.ToInt64():X}, "
                    + $"window=0x{handle.ToInt64():X}, isActive={window.IsActive}.");
                return;
            }

            TryActivate(window, outgoing);
        };
        timer.Start();
    }

    private static void TryActivate(Window window, IntPtr outgoing)
    {
        var outgoingThreadId = outgoing == IntPtr.Zero
            ? 0
            : Win32Interop.GetWindowThreadProcessId(outgoing, out _);
        var currentThreadId = Win32Interop.GetCurrentThreadId();
        // Attaching our input queue to the outgoing foreground thread lets a
        // background process win SetForegroundWindow. Avalonia's Activate drives
        // the actual activation so the focus manager engages.
        var attached = outgoingThreadId != 0
            && outgoingThreadId != currentThreadId
            && Win32Interop.AttachThreadInput(currentThreadId, outgoingThreadId, attach: true);
        try
        {
            window.Activate();
        }
        finally
        {
            if (attached)
            {
                Win32Interop.AttachThreadInput(currentThreadId, outgoingThreadId, attach: false);
            }
        }
    }
}
