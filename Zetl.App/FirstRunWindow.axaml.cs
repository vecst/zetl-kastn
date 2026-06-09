using Avalonia.Controls;

namespace ZETL;

internal partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
        // How Zetl Works is large; on a small display leave a margin instead of
        // filling the screen.
        ZetlWindowPlacement.Track(this, compactWidth: 175, compactHeight: 150);

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
