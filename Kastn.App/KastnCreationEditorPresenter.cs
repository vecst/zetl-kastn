using Avalonia.Controls;
using ZETL;

namespace KASTN;

internal sealed record KastnCreationEditorControls(TextBlock Title, TextBox Name, TextBox Category,
    TextBox Description, ComboBox Template, ComboBox View);

internal sealed class KastnCreationEditorPresenter(KastnCreationEditorControls controls)
{
    public KastnCatalogEditorSession<ZetlCreationTypeDocument>? State { get; private set; }
    private string initialViewId = "";

    public void Open(ZetlCreationTypeDocument source, bool isNew,
        IReadOnlyList<ZetlTemplateDocument> templates, IReadOnlyList<ZetlViewDocument> views, ZetlViewDocument noView)
    {
        State = new(source, doc => doc.Id, (doc, id) => doc.Id = id,
            doc => ZetlCreationTypeDefaults.CreateId(doc.Name), ZetlCreationTypeValidator.Validate);
        var document = State.Document;
        var templateChoices = templates.ToList();
        var selectedTemplate = templateChoices.FirstOrDefault(item => item.Id == document.TemplateId);
        if (selectedTemplate is null && document.TemplateId.Length > 0)
        {
            selectedTemplate = new() { Id = document.TemplateId, Name = $"Unavailable: {document.TemplateId}" };
            templateChoices.Add(selectedTemplate);
        }
        selectedTemplate ??= templateChoices.FirstOrDefault();
        var viewChoices = new List<ZetlViewDocument> { noView };
        viewChoices.AddRange(views);
        var selectedView = viewChoices.FirstOrDefault(item => item.Id == document.PrimaryViewId);
        if (selectedView is null && document.PrimaryViewId is { } missingView)
        {
            selectedView = new() { Id = missingView, Name = $"Unavailable: {missingView}" };
            viewChoices.Add(selectedView);
        }
        selectedView ??= noView;
        initialViewId = selectedView.Id;
        controls.Title.Text = isNew ? "New Creation Type" : $"Edit Creation Type — {document.Name}";
        controls.Name.Text = document.Name;
        controls.Category.Text = document.Category;
        controls.Description.Text = document.Description;
        controls.Template.ItemsSource = templateChoices;
        controls.Template.SelectedItem = selectedTemplate;
        controls.View.ItemsSource = viewChoices;
        controls.View.SelectedItem = selectedView;
        State.SetBaseline(Capture()!);
    }

    public ZetlCreationTypeDocument? Capture()
    {
        if (State is null) return null;
        var result = JsonFile.Clone(State.Document);
        result.Name = controls.Name.Text?.Trim() ?? "";
        result.Category = string.IsNullOrWhiteSpace(controls.Category.Text) ? "Custom" : controls.Category.Text.Trim();
        result.Description = controls.Description.Text?.Trim() ?? "";
        result.TemplateId = (controls.Template.SelectedItem as ZetlTemplateDocument)?.Id ?? "";
        var viewId = (controls.View.SelectedItem as ZetlViewDocument)?.Id ?? "";
        // The single-view editor must not erase an existing priority list when
        // only metadata changes. An explicit view choice replaces that list.
        if (viewId != initialViewId) result.ViewIds = viewId.Length == 0 ? [] : [viewId];
        return result;
    }

    public void Close() => State = null;
}
