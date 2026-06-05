namespace ZETL;

internal static class ZetlFormShortcuts
{
    public static void EnableCtrlEnterClose(Form form)
    {
        form.KeyPreview = true;
        form.KeyDown += (_, args) =>
        {
            if (!args.Control || args.KeyCode != Keys.Enter)
            {
                return;
            }

            args.Handled = true;
            args.SuppressKeyPress = true;
            if (form.AcceptButton is Button acceptButton && acceptButton.Enabled && acceptButton.Visible)
            {
                acceptButton.PerformClick();
                return;
            }

            form.Close();
        };
    }

    // Esc cancels the popup without committing. Popups shown over another app
    // don't reliably get WinForms' built-in Esc/CancelButton dialog-key handling
    // (which is why Ctrl+Enter is wired through KeyPreview too), so route Esc the
    // same way. Honor the form's CancelButton if it has one, so any cancel-
    // specific cleanup runs; otherwise close with a Cancel result.
    public static void EnableEscCancel(Form form)
    {
        form.KeyPreview = true;
        form.KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Escape || args.Control || args.Alt || args.Shift)
            {
                return;
            }

            args.Handled = true;
            args.SuppressKeyPress = true;
            if (form.CancelButton is Button cancelButton && cancelButton.Enabled && cancelButton.Visible)
            {
                cancelButton.PerformClick();
                return;
            }

            form.DialogResult = DialogResult.Cancel;
            form.Close();
        };
    }
}
