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
}
