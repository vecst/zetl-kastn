using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal static class KastnDialogs
{
    private sealed record SlipChoice(ZetlSlipSnapshot Slip, string Label)
    {
        public override string ToString() => Label;
    }

    public static async Task<string?> PromptAsync(
        Window owner,
        string title,
        string label,
        string initial = "",
        Func<string, string?>? validate = null)
    {
        var input = new TextBox { Text = initial };
        var validation = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false
        };
        var dialog = Dialog(title, 390, 190);
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var value = input.Text?.Trim() ?? "";
            if (value.Length == 0)
            {
                validation.Text = "Enter a value first.";
                validation.IsVisible = true;
                input.Focus();
                return;
            }

            var error = validate?.Invoke(value);
            if (error is not null)
            {
                validation.Text = error;
                validation.IsVisible = true;
                input.Focus();
                input.SelectAll();
                return;
            }

            dialog.Close(value);
        };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = label },
                input,
                validation,
                Buttons(ok, cancel)
            }
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return await dialog.ShowDialog<string?>(owner);
    }

    public static async Task<bool> ConfirmAsync(
        Window owner,
        string message,
        string confirmText)
    {
        var dialog = Dialog(confirmText, 460, 190);
        var confirm = new Button
        {
            Content = confirmText,
            Width = 110,
            IsDefault = true
        };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                Buttons(confirm, cancel)
            }
        };
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task MessageAsync(
        Window owner,
        string title,
        string message)
    {
        var dialog = Dialog(title, 420, 180);
        var close = new Button
        {
            Content = "Close",
            Width = 84,
            IsDefault = true,
            IsCancel = true
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                Buttons(close)
            }
        };
        await dialog.ShowDialog(owner);
    }

    public static async Task<ZetlSlipSnapshot?> PickSlipAsync(
        Window owner,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        string initialQuery = "")
    {
        var search = new TextBox
        {
            Text = initialQuery,
            Watermark = "Search titles, bodies, or slip ids"
        };
        var list = new ListBox { MinHeight = 300 };
        var choose = new Button
        {
            Content = "Link Slip",
            Width = 96,
            IsDefault = true,
            IsEnabled = false
        };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        var dialog = Dialog("Link to Slip", 620, 520);
        dialog.CanResize = true;

        void RefreshChoices()
        {
            var query = search.Text?.Trim() ?? "";
            var choices = slips
                .Where(slip => query.Length == 0
                    || slip.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || slip.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || slip.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(slip => new SlipChoice(
                    slip,
                    $"{ZetlSlipLinks.TitleFor(slip)}  ·  {SlipPreview(slip.Text)}"))
                .OrderBy(choice => choice.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            list.ItemsSource = choices;
            list.SelectedItem = choices.FirstOrDefault();
            choose.IsEnabled = list.SelectedItem is SlipChoice;
        }

        void Accept()
        {
            if (list.SelectedItem is SlipChoice choice)
            {
                dialog.Close(choice.Slip);
            }
        }

        search.TextChanged += (_, _) => RefreshChoices();
        list.SelectionChanged += (_, _) => choose.IsEnabled = list.SelectedItem is SlipChoice;
        list.DoubleTapped += (_, _) => Accept();
        choose.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close(null);
        var buttons = Buttons(choose, cancel);
        dialog.Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,8,*,12,Auto"),
            Children =
            {
                search,
                list,
                buttons
            }
        };
        Grid.SetRow(list, 2);
        Grid.SetRow(buttons, 4);
        dialog.Opened += (_, _) =>
        {
            RefreshChoices();
            search.Focus();
            search.SelectAll();
        };
        return await dialog.ShowDialog<ZetlSlipSnapshot?>(owner);
    }

    private static string SlipPreview(string text)
    {
        var preview = text.ReplaceLineEndings(" ").Trim();
        if (preview.Length == 0)
        {
            return "No body text";
        }

        return preview.Length <= 70 ? preview : $"{preview[..67]}...";
    }

    public static async Task<EditSlipResult?> EditSlipDialogAsync(
        Window owner,
        ZetlSlipSnapshot slip,
        IReadOnlyList<ZetlBucketSnapshot> buckets)
    {
        var topGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,12,*"),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var columnCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var activeBuckets = buckets.Where(b => !KastnWorkbench.IsDeletedBucket(b)).ToList();
        var bucketItems = activeBuckets.Select(b => new BucketItem(b)).ToList();
        columnCombo.ItemsSource = bucketItems;
        columnCombo.SelectedItem = bucketItems.FirstOrDefault(item => item.Bucket.Id == slip.BucketId);

        var columnStack = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Move to Column", Classes = { "muted" }, FontSize = 11 },
                columnCombo
            }
        };
        Grid.SetColumn(columnStack, 0);

        var kindCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var kindItems = new List<BlockKindItem>
        {
            new BlockKindItem(ZetlBlockKinds.None, "Note"),
            new BlockKindItem(ZetlBlockKinds.Task, "Task / Checklist"),
            new BlockKindItem(ZetlBlockKinds.Bullet, "Bullet List"),
            new BlockKindItem(ZetlBlockKinds.Ordered, "Numbered List"),
            new BlockKindItem(ZetlBlockKinds.Heading, "Heading"),
            new BlockKindItem(ZetlBlockKinds.Quote, "Quote"),
            new BlockKindItem(ZetlBlockKinds.Code, "Code Block")
        };
        kindCombo.ItemsSource = kindItems;
        kindCombo.SelectedItem = kindItems.FirstOrDefault(item => item.Kind == slip.BlockKind) ?? kindItems[0];

        var kindStack = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Card Type", Classes = { "muted" }, FontSize = 11 },
                kindCombo
            }
        };
        Grid.SetColumn(kindStack, 2);

        topGrid.Children.Add(columnStack);
        topGrid.Children.Add(kindStack);

        var input = new TextBox
        {
            Text = slip.Text,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            AcceptsTab = true
        };
        ScrollViewer.SetVerticalScrollBarVisibility(input, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var ok = new Button { Content = "Save", Width = 84, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        var deleteBtn = new Button
        {
            Content = "Delete Slip",
            Width = 110,
            Foreground = Avalonia.Media.Brushes.OrangeRed
        };

        var dialog = Dialog($"Edit Slip ({slip.Id})", 600, 480);
        dialog.CanResize = true;

        ok.Click += (_, _) =>
        {
            var selectedBucket = (columnCombo.SelectedItem as BucketItem)?.Bucket.Id;
            var selectedKind = (kindCombo.SelectedItem as BlockKindItem)?.Kind ?? ZetlBlockKinds.None;
            dialog.Close(new EditSlipResult
            {
                Save = true,
                Text = input.Text ?? "",
                DestinationBucketId = selectedBucket,
                BlockKind = selectedKind
            });
        };

        cancel.Click += (_, _) => dialog.Close(null);

        deleteBtn.Click += (_, _) =>
        {
            dialog.Close(new EditSlipResult
            {
                Delete = true
            });
        };

        var rightButtons = Buttons(ok, cancel);
        var bottomGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children =
            {
                deleteBtn,
                rightButtons
            }
        };
        Grid.SetColumn(deleteBtn, 0);
        Grid.SetColumn(rightButtons, 2);

        dialog.Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,*,12,Auto"),
            Children =
            {
                topGrid,
                input,
                bottomGrid
            }
        };
        Grid.SetRow(topGrid, 0);
        Grid.SetRow(input, 1);
        Grid.SetRow(bottomGrid, 3);

        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.CaretIndex = input.Text?.Length ?? 0;
        };

        return await dialog.ShowDialog<EditSlipResult?>(owner);
    }

    private static Window Dialog(string title, double width, double height)
    {
        return new Window
        {
            Title = title,
            Width = width,
            Height = height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }
}

public sealed class EditSlipResult
{
    public bool Save { get; init; }
    public bool Delete { get; init; }
    public string Text { get; init; } = "";
    public string? DestinationBucketId { get; init; }
    public string BlockKind { get; init; } = "";
}

internal sealed class BucketItem(ZetlBucketSnapshot bucket)
{
    public ZetlBucketSnapshot Bucket => bucket;
    public override string ToString() => bucket.Name;
}

internal sealed class BlockKindItem(string kind, string label)
{
    public string Kind => kind;
    public string Label => label;
    public override string ToString() => label;
}
