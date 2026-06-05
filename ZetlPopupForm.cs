namespace ZETL;

/// <summary>
/// Base form for Zetl's popup windows. Centralizes the shared chrome:
/// no OS control box / minimize / maximize / icon (popups use explicit
/// in-app Cancel/Close buttons), Ctrl+Enter close-or-accept, and Esc cancel.
/// Size, border style, and title stay with each derived form.
/// </summary>
internal class ZetlPopupForm : Form
{
    protected ZetlPopupForm()
    {
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ZetlFormShortcuts.EnableCtrlEnterClose(this);
        ZetlFormShortcuts.EnableEscCancel(this);
    }
}
