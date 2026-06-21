using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace ZETL;

// Zetl's quick template picker: a small popup listing templates with a
// Capture/Consumable switcher, so a held Ctrl+T (defaulting to Capture) or a held
// Ctrl+V with no active project and nothing to compile (defaulting to Consumable)
// can start a fresh project from one. Arrow keys move the selection, Ctrl+Enter
// starts the highlighted template, and Escape cancels. Choosing a template sets
// SelectedTemplate and the host creates the project.
internal sealed class TemplatePickerWindow : ZetlPopupWindow
{
    private readonly IReadOnlyList<ZetlTemplateDocument> templates;
    private readonly ListBox list;
    private readonly Button captureButton;
    private readonly Button consumableButton;
    private readonly TextBlock emptyHint;
    private bool showConsumable;
    private bool completionDecided;

    public TemplatePickerWindow(
        IReadOnlyList<ZetlTemplateDocument> templates,
        bool initialConsumable)
    {
        this.templates = templates;
        showConsumable = initialConsumable;

        Title = "Start a template";
        Width = 460;
        Height = 460;
        CanResize = false;
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;

        captureButton = new Button { Content = "Capture", Padding = new Thickness(16, 5) };
        consumableButton = new Button { Content = "Consumable", Padding = new Thickness(16, 5) };
        captureButton.Click += (_, _) => SetType(consumable: false);
        consumableButton.Click += (_, _) => SetType(consumable: true);

        list = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<TemplateChoice>(
                (choice, _) => new StackPanel
                {
                    Margin = new Thickness(2),
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = choice.Title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = choice.Detail, Opacity = 0.7, FontSize = 12 }
                    }
                },
                supportsRecycling: true)
        };
        list.DoubleTapped += (_, _) => UseSelected();

        emptyHint = new TextBlock
        {
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false
        };

        var use = new Button { Content = "Start", Width = 96, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 96 };
        use.Click += (_, _) => UseSelected();
        cancel.Click += (_, _) => CancelAndClose();

        Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        }.With(
            Place(new TextBlock { Text = "Start a template", FontSize = 18, FontWeight = FontWeight.SemiBold }, 0),
            Place(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 10, 0, 10),
                Children = { captureButton, consumableButton }
            }, 1),
            Place(new Panel { Children = { list, emptyHint } }, 2),
            Place(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0),
                Children = { use, cancel }
            }, 3));

        ZetlWindowShortcuts.Enable(this, UseSelected, CancelAndClose);
        Filter();
        Opened += (_, _) => list.Focus();
    }

    // The template the user chose, or null when cancelled / dismissed.
    public ZetlTemplateDocument? SelectedTemplate { get; private set; }

    protected override bool IsDismissSuppressed => completionDecided;

    protected override void OnClickAwayDismiss() => CancelAndClose();

    protected override void OnPopupClosing() => completionDecided = true;

    private void SetType(bool consumable)
    {
        showConsumable = consumable;
        Filter();
        list.Focus();
    }

    private void Filter()
    {
        // Disable the active switcher button so the current type reads as selected.
        captureButton.IsEnabled = showConsumable;
        consumableButton.IsEnabled = !showConsumable;

        var choices = templates
            .Where(template => template.IsConsumable == showConsumable)
            .Select(TemplateChoice.From)
            .ToList();
        list.ItemsSource = choices;
        list.SelectedIndex = choices.Count > 0 ? 0 : -1;

        var hasChoices = choices.Count > 0;
        list.IsVisible = hasChoices;
        emptyHint.IsVisible = !hasChoices;
        emptyHint.Text = showConsumable
            ? "No consumable templates yet. Create one in Kastn."
            : "No capture templates yet. Create one in Kastn.";
    }

    private void UseSelected()
    {
        if (list.SelectedItem is TemplateChoice choice)
        {
            SelectedTemplate = choice.Template;
            completionDecided = true;
            Close();
        }
    }

    private void CancelAndClose()
    {
        completionDecided = true;
        Close();
    }

    private static Control Place(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }

    private sealed record TemplateChoice(ZetlTemplateDocument Template, string Title, string Detail)
    {
        public static TemplateChoice From(ZetlTemplateDocument template)
        {
            var cardCount = template.Buckets.Sum(
                bucket => bucket.Seeds.Count + bucket.Cards.Count);
            var detail = cardCount > 0
                ? $"{template.Category} · {template.Buckets.Count} buckets · {cardCount} cards"
                : $"{template.Category} · {template.Buckets.Count} buckets";
            return new TemplateChoice(template, template.Name, detail);
        }
    }
}

internal static class GridContentExtensions
{
    // Add children and return the grid, so a Grid can be built inline.
    public static Grid With(this Grid grid, params Control[] children)
    {
        foreach (var child in children)
        {
            grid.Children.Add(child);
        }

        return grid;
    }
}
