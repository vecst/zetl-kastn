namespace ZETL;

internal sealed partial class FirstRunForm : ZetlPopupForm
{
    public FirstRunForm()
    {
        InitializeComponent();
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
