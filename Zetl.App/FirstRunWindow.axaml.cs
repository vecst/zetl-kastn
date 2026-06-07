using Avalonia.Controls;

namespace ZETL;

internal partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();

        gotItButton.Click += (_, _) => Close();
        openBoardButton.Click += (_, _) =>
        {
            OpenBoardRequested = true;
            Close();
        };
        ZetlWindowShortcuts.Enable(this, Close, Close);
        Opened += (_, _) => gotItButton.Focus();
    }

    public bool OpenBoardRequested { get; private set; }
}
