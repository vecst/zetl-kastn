namespace ZETL;

internal static class ZetlDialogPlacement
{
    private static readonly object DeactivatedCloseMarker = new();

    public static IWin32Window? OwnerFromHandle(IntPtr handle)
    {
        return handle == IntPtr.Zero ? null : new WindowHandleOwner(handle);
    }

    public static DialogResult ShowForegroundDialog(
        Form form,
        IWin32Window? owner = null,
        bool closeOnDeactivate = false,
        IntPtr activationWindow = default)
    {
        var preferredForegroundWindow = activationWindow != IntPtr.Zero
            ? activationWindow
            : owner?.Handle ?? IntPtr.Zero;
        var modalOwner = Program.IsCodexHostWindow(preferredForegroundWindow) ? null : owner;
        form.ShowInTaskbar = false;
        if (closeOnDeactivate)
        {
            EnableCloseOnDeactivate(form);
        }

        PlaceNearTopSixth(form);
        form.Shown += (_, _) => BringToForeground(form, preferredForegroundWindow);
        return modalOwner is null ? form.ShowDialog() : form.ShowDialog(modalOwner);
    }

    public static bool WasClosedByDeactivate(Form form)
    {
        return ReferenceEquals(form.Tag, DeactivatedCloseMarker);
    }

    public static void EnableCloseOnDeactivate(Form form, Func<bool>? canClose = null)
    {
        var armed = false;
        form.Shown += (_, _) =>
        {
            var armTimer = new System.Windows.Forms.Timer { Interval = 150 };
            armTimer.Tick += (_, _) =>
            {
                armTimer.Stop();
                armTimer.Dispose();
                armed = true;
            };
            armTimer.Start();
        };

        form.Deactivate += (_, _) =>
        {
            if (!armed || form.IsDisposed || !form.Visible)
            {
                return;
            }

            form.BeginInvoke(new Action(() =>
            {
                if (form.IsDisposed || !form.Visible || form.ContainsFocus)
                {
                    return;
                }

                if (canClose is not null && !canClose())
                {
                    return;
                }

                form.Tag = DeactivatedCloseMarker;
                form.Close();
            }));
        };
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
