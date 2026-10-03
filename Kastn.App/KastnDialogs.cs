using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal static class KastnDialogs
{
    public sealed record TemplateProjectPromptResult(string Name, bool Temporary);
    public sealed record TemporaryTemplateLaneResult(string Lane, bool Remember);
    public sealed record LinkEditResult(string? Href, bool Remove);

    public enum LinkRangeAction
    {
        Change,
        Remove
    }

    public enum UnsavedCloseAction
    {
        KeepRecovery,
        Discard,
        Cancel
    }

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

    public static async Task<TemplateProjectPromptResult?> PromptTemplateProjectAsync(
        Window owner,
        string title,
        string initialName,
        bool allowTemporary,
        bool initialTemporary,
        Func<string, string?>? validate = null)
    {
        var input = new TextBox { Text = initialName };
        var temporary = new CheckBox
        {
            Content = "Temporary project",
            IsChecked = allowTemporary && initialTemporary,
            IsVisible = allowTemporary
        };
        var temporaryHelp = new TextBlock
        {
            Text = "Delete this project when it leaves the selected lane.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = 0.72,
            IsVisible = allowTemporary
        };
        var validation = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false
        };
        var dialog = Dialog(title, 430, allowTemporary ? 260 : 190);
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

            dialog.Close(new TemplateProjectPromptResult(
                value,
                allowTemporary && temporary.IsChecked == true));
        };
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Project name" },
                input,
                temporary,
                temporaryHelp,
                validation,
                Buttons(ok, cancel)
            }
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return await dialog.ShowDialog<TemplateProjectPromptResult?>(owner);
    }

    public static async Task<LinkEditResult?> EditWebLinkAsync(
        Window owner,
        string initial = "",
        bool allowRemove = false)
    {
        var input = new TextBox { Text = initial };
        var validation = new TextBlock
        {
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = false
        };
        var dialog = Dialog(allowRemove ? "Edit Web Link" : "Web Link", 420, allowRemove ? 220 : 190);
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true };
        var remove = new Button
        {
            Content = "Remove",
            Width = 84,
            IsVisible = allowRemove
        };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var value = input.Text?.Trim() ?? "";
            if (value.Length == 0)
            {
                validation.Text = "Enter a URL first.";
                validation.IsVisible = true;
                input.Focus();
                return;
            }

            if (!ZetlLinkSafety.TryNormalizeTarget(value, out var safeTarget))
            {
                validation.Text = "Use an http, https, mailto, or #fragment link.";
                validation.IsVisible = true;
                input.Focus();
                return;
            }

            dialog.Close(new LinkEditResult(safeTarget, Remove: false));
        };
        remove.Click += (_, _) => dialog.Close(new LinkEditResult(null, Remove: true));
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Link target" },
                input,
                validation,
                Buttons(allowRemove ? [remove, ok, cancel] : [ok, cancel])
            }
        };
        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return await dialog.ShowDialog<LinkEditResult?>(owner);
    }

    public static async Task<LinkRangeAction?> PickLinkRangeActionAsync(
        Window owner,
        string title,
        string label)
    {
        var dialog = Dialog(title, 420, 180);
        var change = new Button
        {
            Content = "Change",
            Width = 84,
            IsDefault = true
        };
        var remove = new Button { Content = "Remove", Width = 84 };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        change.Click += (_, _) => dialog.Close(LinkRangeAction.Change);
        remove.Click += (_, _) => dialog.Close(LinkRangeAction.Remove);
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = label,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                Buttons(change, remove, cancel)
            }
        };
        return await dialog.ShowDialog<LinkRangeAction?>(owner);
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

    public static async Task<UnsavedCloseAction> DecideUnsavedCloseAsync(
        Window owner,
        bool recoveryStored,
        string reason)
    {
        var dialog = Dialog("Unsaved slip", 520, 240);
        var keep = new Button
        {
            Content = "Keep recovery & close",
            Width = 160,
            IsDefault = recoveryStored,
            IsEnabled = recoveryStored
        };
        var discard = new Button { Content = "Discard & close", Width = 120 };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        keep.Click += (_, _) => dialog.Close(UnsavedCloseAction.KeepRecovery);
        discard.Click += (_, _) => dialog.Close(UnsavedCloseAction.Discard);
        cancel.Click += (_, _) => dialog.Close(UnsavedCloseAction.Cancel);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = recoveryStored
                        ? $"{reason} Keep a local recovery draft for the next Kastn launch, discard it, or cancel closing."
                        : $"{reason} The local recovery draft could not be written. Cancel closing to preserve the editor, or explicitly discard it.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                Buttons(keep, discard, cancel)
            }
        };
        return await dialog.ShowDialog<UnsavedCloseAction>(owner);
    }

    public static async Task<TemporaryTemplateLaneResult?> PickTemporaryTemplateLaneAsync(
        Window owner,
        string templateName,
        string mainLaneLabel,
        string alternateLaneLabel,
        string title = "Use Temporary Template",
        string? prompt = null,
        string confirmText = "Use Template",
        bool allowRemember = true)
    {
        var main = new RadioButton
        {
            Content = mainLaneLabel,
            GroupName = "temporary-template-lane",
            IsChecked = true
        };
        var alternate = new RadioButton
        {
            Content = alternateLaneLabel,
            GroupName = "temporary-template-lane"
        };
        var remember = new CheckBox
        {
            Content = "Remember this choice",
            IsVisible = allowRemember
        };
        var dialog = Dialog(title, 440, allowRemember ? 250 : 220);
        var use = new Button
        {
            Content = confirmText,
            Width = 132,
            IsDefault = true
        };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        use.Click += (_, _) => dialog.Close(new TemporaryTemplateLaneResult(
            alternate.IsChecked == true ? ZetlStateRules.ShiftLane : ZetlStateRules.NormalLane,
            remember.IsChecked == true));
        cancel.Click += (_, _) => dialog.Close(null);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = prompt ?? $"Use '{templateName}' in which lane?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        main,
                        alternate
                    }
                },
                remember,
                Buttons(use, cancel)
            }
        };
        return await dialog.ShowDialog<TemporaryTemplateLaneResult?>(owner);
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

        var ignoreBucketStyleCheck = new CheckBox
        {
            Content = "Ignore bucket style for this slip",
            IsChecked = slip.IgnoreBucketRenderKind
        };

        var kindStack = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Card Type", Classes = { "muted" }, FontSize = 11 },
                kindCombo,
                ignoreBucketStyleCheck
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

        void Commit()
        {
            var selectedBucket = (columnCombo.SelectedItem as BucketItem)?.Bucket.Id;
            var selectedKind = (kindCombo.SelectedItem as BlockKindItem)?.Kind ?? ZetlBlockKinds.None;
            dialog.Close(new EditSlipResult
            {
                Save = true,
                Text = input.Text ?? "",
                DestinationBucketId = selectedBucket,
                BlockKind = selectedKind,
                IgnoreBucketRenderKind = ignoreBucketStyleCheck.IsChecked == true
            });
        }

        ok.Click += (_, _) => Commit();
        input.AddHandler(
            InputElement.KeyDownEvent,
            (_, args) =>
            {
                if (args.KeyModifiers.HasFlag(KeyModifiers.Control) && args.Key == Key.Enter)
                {
                    args.Handled = true;
                    Commit();
                }
            },
            RoutingStrategies.Tunnel);

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

    // The two-way choice for an undo/redo step whose record changed since it was
    // recorded: apply the step anyway (overwriting the newer change) or keep the
    // newer change and consume the entry. This is the editor conflict panel's
    // current-vs-target affordance as a dialog, so it also works in Board Mode,
    // where the docked panel is collapsed.
    public static async Task<bool> UndoConflictAsync(
        Window owner,
        string verb,
        string description,
        string currentText,
        string targetText)
    {
        var capitalizedVerb = char.ToUpperInvariant(verb[0]) + verb[1..];
        var dialog = Dialog($"{capitalizedVerb} conflict", 560, 380);
        var applyAnyway = false;
        var apply = new Button { Content = $"{capitalizedVerb} anyway", IsDefault = true };
        var keep = new Button { Content = "Keep newer change", IsCancel = true };
        apply.Click += (_, _) =>
        {
            applyAnyway = true;
            dialog.Close();
        };
        keep.Click += (_, _) => dialog.Close();

        static Control Labeled(string label, string text) => new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, FontWeight = Avalonia.Media.FontWeight.Bold },
                new ScrollViewer
                {
                    MaxHeight = 100,
                    Content = new TextBlock
                    {
                        Text = text,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    }
                }
            }
        };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"“{description}” cannot {verb} cleanly — this item changed since.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                Labeled("Newer change (kept if you decline)", currentText),
                Labeled($"After {verb}", targetText),
                Buttons(apply, keep)
            }
        };
        await dialog.ShowDialog(owner);
        return applyAnyway;
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
    public bool IgnoreBucketRenderKind { get; init; }
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
