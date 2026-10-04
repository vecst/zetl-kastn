using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ZETL.Tests;

public class ZetlTrayMenuWarmupTests
{
    [AvaloniaFact]
    public void LayoutPreparationNeverShowsAWindowOrRunsMenuActions()
    {
        var menu = new NativeMenu();
        var action = new NativeMenuItem("Take a note");
        var clicks = 0;
        action.Click += (_, _) => clicks++;
        menu.Items.Add(action);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("Settings") { IsEnabled = false });
        var opened = 0;
        using var subscription = Window.WindowOpenedEvent.AddClassHandler<Window>((_, _) => opened++);
        var size = ZetlTrayMenuWarmup.Prepare(menu);
        Assert.True(size.Width > 0 && size.Height > 0);
        Assert.Equal(0, opened);
        Assert.Equal(0, clicks);
        Assert.Same(action, menu.Items[0]);
        Assert.Equal(3, menu.Items.Count);
        Assert.False(((NativeMenuItem)menu.Items[2]).IsEnabled);
    }

    [AvaloniaFact]
    public void WarmupCopiesLabelsAndStateWithoutLiveCommands()
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("Toggle") { IsChecked = true, ToggleType = NativeMenuItemToggleType.CheckBox });
        menu.Items.Add(new NativeMenuItemSeparator());
        var presenter = ZetlTrayMenuWarmup.CreatePresenter(menu);
        var item = Assert.IsType<MenuItem>(presenter.Items[0]);
        Assert.Equal("Toggle", item.Header);
        Assert.True(item.IsChecked);
        Assert.Equal(MenuItemToggleType.CheckBox, item.ToggleType);
        Assert.Null(item.Command);
        Assert.IsType<Separator>(presenter.Items[1]);
    }

    [AvaloniaFact]
    public void EmptyMenuNeedsNoWindowOrLayout()
    {
        Assert.Equal(default, ZetlTrayMenuWarmup.Prepare(new NativeMenu()));
    }
}
