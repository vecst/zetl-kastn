using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZETL;

/// <summary>
/// A versioned creation type: bundles one input template with default output
/// view(s), so a single choice starts a project with the right buckets and renders
/// it with the right view — e.g. a "Recipe" that ships an Ingredients/Steps/Notes
/// template and a recipe document view. A creation type only references a template
/// and views (by id); it is never the source of truth for project content.
/// </summary>
internal sealed class ZetlCreationTypeDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    public string Description { get; set; } = "";

    // The template this creation type starts a project from.
    public string TemplateId { get; set; } = "";

    // Default views for the new project, in priority order. The first becomes the
    // project's default view. Zero views is allowed (template-only creation type).
    public List<string> ViewIds { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public string? PrimaryViewId =>
        ViewIds.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
}

internal static class ZetlCreationTypeDefaults
{
    public static IReadOnlyList<ZetlCreationTypeDocument> CreateAll() =>
    [
        new ZetlCreationTypeDocument
        {
            Id = "research-report",
            Name = "Research report",
            Category = "Research",
            Description = "Start a research board and render it as a Markdown document.",
            TemplateId = "research-board",
            ViewIds = ["markdown"]
        }
    ];

    private static readonly HashSet<string> BuiltInIds =
        new(CreateAll().Select(creation => creation.Id), StringComparer.Ordinal);

    public static bool IsBuiltIn(string? id) => id is not null && BuiltInIds.Contains(id);

    public static ZetlCreationTypeDocument? FindBuiltIn(string? id) =>
        CreateAll().FirstOrDefault(creation => creation.Id == id);

    public static ZetlCreationTypeDocument Clone(ZetlCreationTypeDocument creation) =>
        JsonFile.Clone(creation);

    public static ZetlCreationTypeDocument Duplicate(
        ZetlCreationTypeDocument source,
        string? newName = null)
    {
        var copy = Clone(source);
        copy.Name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} copy" : newName.Trim();
        copy.Id = CreateId(copy.Name);
        copy.Version = ZetlCreationTypeDocument.CurrentVersion;
        return copy;
    }

    public static string CreateId(string name) => ZetlDocumentId.Create(name, "creation");
}

internal static class ZetlCreationTypeValidator
{
    public static IReadOnlyList<string> Validate(ZetlCreationTypeDocument? creation)
    {
        if (creation is null)
        {
            return ["Creation type data is missing."];
        }

        var errors = new List<string>();

        if (creation.Version < 1)
        {
            errors.Add("Creation type version must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(creation.Id))
        {
            errors.Add("Creation type id is required.");
        }

        if (string.IsNullOrWhiteSpace(creation.Name))
        {
            errors.Add("Creation type name is required.");
        }

        if (string.IsNullOrWhiteSpace(creation.TemplateId))
        {
            errors.Add("Creation type must reference a template.");
        }

        if (creation.ViewIds.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Creation type has an empty view reference.");
        }

        return errors;
    }
}

/// <summary>
/// Loads the creation-type catalog: protected built-ins plus valid user JSON under
/// the creation-types directory (default <c>%AppData%\Zetl\creation-types</c>).
/// Mirrors the template/view stores — a bad file is quarantined or skipped with a
/// diagnostic, never blocking the catalog.
/// </summary>
internal sealed class ZetlCreationTypeStore
{
    private readonly ZetlDocumentStore<ZetlCreationTypeDocument> store;

    public ZetlCreationTypeStore(string? directory = null, Action<string>? log = null) =>
        store = new(
            directory,
            "creation-types",
            "creation type",
            ZetlCreationTypeDefaults.CreateAll,
            ZetlCreationTypeValidator.Validate,
            ZetlCreationTypeDefaults.IsBuiltIn,
            creation => creation.Id,
            log);

    public string Directory => store.Directory;

    public IReadOnlyList<ZetlCreationTypeDocument> LoadAll() => store.LoadAll();

    public void Save(ZetlCreationTypeDocument creation) => store.Save(creation);

    public void Delete(string id) => store.Delete(id);
}
