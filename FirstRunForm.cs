namespace ZETL;

internal sealed partial class FirstRunForm : Form
{
    public FirstRunForm()
    {
        InitializeComponent();
        ZetlFormShortcuts.EnableCtrlEnterClose(this);
        openBoardButton.Click += (_, _) => OpenBoard();
        Shown += (_, _) => gotItButton.Focus();
    }

    public bool OpenBoardRequested { get; private set; }

    private void OpenBoard()
    {
        OpenBoardRequested = true;
        DialogResult = DialogResult.OK;
        Close();
    }
}
