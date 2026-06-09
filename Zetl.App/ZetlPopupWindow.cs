using Avalonia.Controls;
using Avalonia.Threading;

namespace ZETL;

// Shared base for Zetl's shortcut-opened popups (note capture, compile, board).
// Owns the click-away/deactivation lifecycle each window used to reimplement: a
// settle-delay arm timer, the armed flag, and the Deactivated/Closing wiring.
// Subclasses supply the dismissal action plus the guard that suppresses it
// while a completion or an owned child dialog is active.
internal abstract class ZetlPopupWindow : Window, IClickAwayDismissable
{
    private readonly DispatcherTimer armTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300)
    };
    private bool armed;

    protected ZetlPopupWindow()
    {
        Opened += (_, _) =>
        {
            // Arm after a settle delay rather than on Activated: forcing the
            // window to the foreground via Win32 does not raise Avalonia's
            // Activated, so IsActive can stay false even though the window is
            // foreground. Opened always fires, so the delay alone distinguishes
            // the spurious initial deactivation from a genuine click-away.
            if (DismissOnDeactivate)
            {
                armTimer.Start();
            }
        };
        armTimer.Tick += (_, _) =>
        {
            armTimer.Stop();
            if (!IsDismissSuppressed)
            {
                armed = true;
            }
        };
        Deactivated += (_, _) => DismissFromClickAway();
        Closing += (_, _) =>
        {
            armTimer.Stop();
            OnPopupClosing();
        };
    }

    // Set by the host: whether a click-away/deactivation dismisses this popup.
    public bool DismissOnDeactivate { get; set; }

    // True when the popup was dismissed by a click-away rather than an explicit
    // button or key.
    public bool ClosedByDeactivate { get; protected set; }

    // While true, arming and click-away dismissal are suppressed because a
    // completion is already underway or an owned child dialog holds focus.
    protected abstract bool IsDismissSuppressed { get; }

    public void DismissFromClickAway()
    {
        if (DismissOnDeactivate
            && armed
            && !IsDismissSuppressed
            && IsVisible)
        {
            ClosedByDeactivate = true;
            OnClickAwayDismiss();
        }
    }

    // Dismiss the popup (commit-and-close, or just close). ClosedByDeactivate is
    // already set when this runs.
    protected abstract void OnClickAwayDismiss();

    // Runs on Closing, after the arm timer is stopped.
    protected virtual void OnPopupClosing()
    {
    }
}
