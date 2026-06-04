namespace ZETL;

internal static class ZetlDialogPlacement
{
    private static readonly object DeactivatedCloseMarker = new();

    public static IWin32Window? OwnerFromHandle(IntPtr handle)
    {
        return handle == IntPtr.Zero ? null : new WindowHandleOwner(handle);
    }

    public static DialogResult ShowForegroundDialog(Form form, IWin32Window? owner = null)
    {
        var preferredForegroundWindow = owner?.Handle ?? IntPtr.Zero;
        form.ShowInTaskbar = false;
        PlaceNearTopSixth(form);
        form.Shown += (_, _) => BringToForeground(form, preferredForegroundWindow);
        return owner is null ? form.ShowDialog() : form.ShowDialog(owner);
    }

    /// <summary>
    /// Shows a transient popup non-modally so the user can click straight into
    /// another app to dismiss it (a modal dialog would flash and block
    /// instead). The popup closes on deactivation and <paramref name="onClosed"/>
    /// runs once it is closed, after which the form is disposed.
    /// </summary>
    public static void ShowForegroundPopup(
        Form form,
        Action onClosed,
        IWin32Window? owner = null,
        IntPtr activationWindow = default)
    {
        var preferredForegroundWindow = activationWindow != IntPtr.Zero
            ? activationWindow
            : owner?.Handle ?? IntPtr.Zero;
        form.ShowInTaskbar = false;
        EnableCloseOnDeactivate(form);
        PlaceNearTopSixth(form);
        form.Shown += (_, _) => BringToForeground(form, preferredForegroundWindow);
        form.FormClosed += (_, _) =>
        {
            try
            {
                onClosed();
            }
            finally
            {
                form.Dispose();
            }
        };

        if (owner is not null)
        {
            form.Show(owner);
        }
        else
        {
            form.Show();
        }
    }

    public static bool WasClosedByDeactivate(Form form)
    {
        return ReferenceEquals(form.Tag, DeactivatedCloseMarker);
    }

    public static void EnableCloseOnDeactivate(Form form, Func<bool>? canClose = null)
    {
        // Watch the foreground window rather than relying on the Deactivate
        // event. Bringing a popup to the foreground over another app is racy on
        // Windows; when the popup loses that race it never activates, so
        // Deactivate never fires and a click-off goes undetected. Polling closes
        // the popup as soon as another process owns the foreground -- but only
        // once the popup has actually held the foreground at least once, so a
        // popup that lost the show-time race is never closed out from under the
        // user (and the guard arms the moment they click into it). Our own
        // message pump keeps ticking while another app is foreground, so the
        // click-off is still detected.
        var hasHeldForeground = false;
        System.Windows.Forms.Timer? watch = null;

        form.Shown += (_, _) =>
        {
            watch = new System.Windows.Forms.Timer { Interval = 120 };
            watch.Tick += (_, _) =>
            {
                if (form.IsDisposed || !form.Visible)
                {
                    watch.Stop();
                    watch.Dispose();
                    return;
                }

                var foreground = Program.GetForegroundWindow();
                Program.GetWindowThreadProcessId(foreground, out var foregroundProcessId);
                if (foregroundProcessId == (uint)Environment.ProcessId)
                {
                    // The popup itself, a child dialog, or our tray menu owns the
                    // foreground; keep it open.
                    hasHeldForeground = true;
                    return;
                }

                if (!hasHeldForeground || (canClose is not null && !canClose()))
                {
                    return;
                }

                watch.Stop();
                watch.Dispose();
                form.Tag = DeactivatedCloseMarker;
                form.Close();
            };
            watch.Start();
        };

        form.FormClosed += (_, _) => watch?.Dispose();
    }

    public static void PlaceNearTopSixth(Form form)
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 800);
        var targetX = working.Left + Math.Max(0, (working.Width - form.Width) / 2);
        var targetY = working.Top + Math.Max(0, working.Height / 6);
        var maxY = Math.Max(working.Top, working.Bottom - form.Height - 16);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(
            targetX,
            Math.Min(targetY, maxY));
    }

    public static void BringToForeground(Form form, IntPtr preferredForegroundWindow = default)
    {
        var foregroundWindow = preferredForegroundWindow == IntPtr.Zero
            ? Program.GetForegroundWindow()
            : preferredForegroundWindow;
        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0
            : Program.GetWindowThreadProcessId(foregroundWindow, out _);
        var currentThreadId = Program.GetCurrentThreadId();
        var attached = foregroundThreadId != 0
            && foregroundThreadId != currentThreadId
            && Program.AttachThreadInput(currentThreadId, foregroundThreadId, attach: true);

        form.TopMost = true;
        if (!form.Visible)
        {
            form.Show();
        }

        form.WindowState = FormWindowState.Normal;
        try
        {
            // Attaching to the foreground thread's input queue is what lets
            // SetForegroundWindow succeed on the first try; without it the
            // initial call loses the Windows foreground-lock race and focus
            // stays on the previous app. Take the foreground synchronously here
            // so the form is the genuine foreground window before topmost is
            // released below.
            form.BringToFront();
            Program.SetForegroundWindow(form.Handle);
            Program.SetActiveWindow(form.Handle);
            form.Activate();
        }
        finally
        {
            if (attached)
            {
                Program.AttachThreadInput(currentThreadId, foregroundThreadId, attach: false);
            }
        }

        var releaseTopMostTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };
        releaseTopMostTimer.Tick += (_, _) =>
        {
            releaseTopMostTimer.Stop();
            releaseTopMostTimer.Dispose();
            if (!form.IsDisposed)
            {
                // The form already holds the foreground, so dropping topmost
                // keeps it on top instead of letting the previous window flash
                // through.
                form.TopMost = false;
                FocusFormAndActiveControl(form);
            }
        };
        releaseTopMostTimer.Start();
    }

    public static void FocusFormAndActiveControl(Form form)
    {
        form.Activate();
        if (form.ActiveControl is { IsDisposed: false } control)
        {
            control.Focus();
            Program.SetFocus(control.Handle);
        }
    }

    private sealed class WindowHandleOwner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
