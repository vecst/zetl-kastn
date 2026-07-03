using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZETL;

// How a view renders a project's slips. Stored as readable text (like bucket Kind
// / compile mode) so a copied view document is understandable without a legend.
// The first three mirror Zetl's hardcoded compile formats; Markdown is the first
// document-oriented view.
internal static class ZetlViewKinds
{
    public const string Formatted = "Formatted";
    public const string Plain = "Plain";
    public const string Tsv = "TSV";
    public const string Markdown = "Markdown";
    public const string Html = "HTML";
    // PDF output is binary, so Kastn produces it with
    // MigraDoc from the same snapshot model); the portable text renderer only
    // returns a note for it.
    public const string Pdf = "PDF";
}

// A note's own block kind in Kastn's rendered views (the slip's BlockKind, persisted as
// "listKind"): a list item (Bullet/Ordered/Task), an inline-content block (Heading/
// Quote/Code), or the Divider structural leaf. None ("") renders as a plain paragraph.
// This is the single source of truth for valid kinds — normalize through it everywhere.
internal static class ZetlBlockKinds
{
    public const string None = "";
    public const string Bullet = "bullet";
    public const string Ordered = "ordered";
    public const string Task = "task";
    public const string Heading = "heading";
    public const string Quote = "quote";
    public const string Code = "code";
    public const string Divider = "divider";

    public static readonly string[] All = [Bullet, Ordered, Task, Heading, Quote, Code, Divider];

    // Structural slip kinds: content-less elements Zetl ignores and Kastn renders.
    public static readonly string[] Structural = [Divider];

    public static string Normalize(string? value)
    {
        var lowered = value?.Trim().ToLowerInvariant();
        return lowered is not null && All.Contains(lowered) ? lowered : None;
    }

    public static bool IsStructural(string? value) =>
        Structural.Contains((value ?? "").Trim().ToLowerInvariant());
}

// How Kastn renders a container bucket's contents (the bucket's RenderKind): a boxed
// Group, a Table, or a LaTeX block. None ("") is an ordinary section. Zetl ignores this;
// the bucket is otherwise a normal bucket.
internal static class ZetlBucketRenderKinds
{
    public const string None = "";
    public const string Group = "group";
    public const string Table = "table";
    public const string Latex = "latex";
    public const string Bullet = "bullet";
    public const string Ordered = "ordered";
    public const string Task = "task";

    public static readonly string[] All = [Group, Table, Latex, Bullet, Ordered, Task];

    public static string Normalize(string? value)
    {
        var lowered = value?.Trim().ToLowerInvariant();
        return lowered is not null && All.Contains(lowered) ? lowered : None;
    }
}

// How a view arranges each bucket's slips in the document kinds. Stored as readable
// text like the other view settings.
internal static class ZetlViewListStyles
{
    public const string Bullet = "bullet";
    public const string Ordered = "ordered";
    public const string Task = "task";
    public const string Paragraph = "paragraph";

    public static readonly string[] All = [Bullet, Ordered, Task, Paragraph];

    public static string Normalize(string? value)
    {
        var lowered = value?.Trim().ToLowerInvariant();
        return All.Contains(lowered) ? lowered! : Bullet;
    }
}

/// <summary>
/// A versioned, human-readable view document: a named renderer that projects an
/// existing project's slips into an artifact. This is the output-side counterpart
/// to <see cref="ZetlTemplateDocument"/>, mirroring the theme/template systems
/// (stable id, version, <see cref="JsonExtensionData"/> for forward compatibility).
///
/// A view never owns content — it renders slips and is discarded. Slips remain the
/// only source of truth, so re-rendering always reflects current slips.
/// </summary>
internal sealed class ZetlViewDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    public string Description { get; set; } = "";

    // One of ZetlViewKinds.
    public string Kind { get; set; } = ZetlViewKinds.Formatted;

    // Slips per row for the TSV kind; ignored by other kinds. A bucket's own header
    // lines still override this at render time, matching today's compile behavior.
    public int TsvRowLength { get; set; } = 5;

    // Optional named output sections mapping buckets to headings. When empty, the
    // view renders every bucket under its own name (today's behavior). When set,
    // the view renders exactly these sections, in order — letting a view rename,
    // reorder, merge, or omit buckets.
    public List<ZetlViewSection> Sections { get; set; } = [];

    // How each bucket's slips are arranged in the document kinds (Markdown / HTML /
    // PDF) and the on-screen reading view. One of ZetlViewListStyles; default bullet.
    // The literal Formatted / Plain / TSV kinds ignore it.
    public string ListStyle { get; set; } = ZetlViewListStyles.Bullet;

    // When true, document views prefix each bucket/section heading with a cascading
    // outline number (1, 1.1, 1.1.1). The literal kinds ignore it.
    public bool NumberHeadings { get; set; }

    // Document title control for the rendered artifacts (Markdown / HTML / PDF).
    // ShowTitle=false omits the title heading entirely; a non-empty Title overrides
    // the project name. The literal Formatted / Plain / TSV kinds keep the project
    // name for compile parity.
    public bool ShowTitle { get; set; } = true;

    public string Title { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// One named output section: a heading plus the bucket names whose slips it gathers
/// (in order). Bucket names are matched case-insensitively against the project at
/// render time, so a view stays project-agnostic.
/// </summary>
internal sealed class ZetlViewSection
{
    public string Title { get; set; } = "";

    public List<string> Buckets { get; set; } = [];

    // Heading styling for the rich kinds (HTML / PDF) and the on-screen view. Align
    // is "left" (default) / "center" / "right"; Bold makes the heading heavier; Level
    // 1/2/3 sizes it large/normal/small (0 = automatic from depth). Markdown honors
    // the level (heading depth) but ignores align/bold, which it cannot express.
    public string HeadingAlign { get; set; } = "";

    public bool HeadingBold { get; set; }

    public int HeadingLevel { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// The protected, built-in view catalog. The first three reproduce Zetl's compile
/// formats so the existing fast workflows are expressible as views; Markdown is the
/// first new artifact kind.
/// </summary>
internal static class ZetlViewDefaults
{
    public static IReadOnlyList<ZetlViewDocument> CreateAll() =>
    [
        new ZetlViewDocument
        {
            Id = "formatted",
            Name = "Formatted",
            Category = "Compile",
            Description = "Group slips under project and bucket headings.",
            Kind = ZetlViewKinds.Formatted
        },
        new ZetlViewDocument
        {
            Id = "plain",
            Name = "Plain",
            Category = "Compile",
            Description = "One slip per line, without headings.",
            Kind = ZetlViewKinds.Plain
        },
        new ZetlViewDocument
        {
            Id = "tsv",
            Name = "TSV",
            Category = "Compile",
            Description = "Spreadsheet rows from slips, with optional bucket headers.",
            Kind = ZetlViewKinds.Tsv
        },
        new ZetlViewDocument
        {
            Id = "markdown",
            Name = "Markdown",
            Category = "Document",
            Description = "A Markdown document: project title, bucket sections, and slip bullets.",
            Kind = ZetlViewKinds.Markdown
        },
        new ZetlViewDocument
        {
            Id = "html",
            Name = "HTML",
            Category = "Document",
            Description = "A self-contained HTML document: project title, bucket headings, and slip lists.",
            Kind = ZetlViewKinds.Html
        },
        new ZetlViewDocument
        {
            Id = "pdf",
            Name = "PDF",
            Category = "Document",
            Description = "A PDF document built from the project: title, bucket headings, and slip bullets.",
            Kind = ZetlViewKinds.Pdf
        }
    ];

    private static readonly HashSet<string> BuiltInIds =
        new(CreateAll().Select(view => view.Id), StringComparer.Ordinal);

    public static bool IsBuiltIn(string? id) => id is not null && BuiltInIds.Contains(id);

    public static ZetlViewDocument? FindBuiltIn(string? id) =>
        CreateAll().FirstOrDefault(view => view.Id == id);

    public static ZetlViewDocument Clone(ZetlViewDocument view) => JsonFile.Clone(view);

    // An independent user copy of a view (typically a built-in) with a fresh id and
    // name, so the original stays immutable.
    public static ZetlViewDocument Duplicate(ZetlViewDocument source, string? newName = null)
    {
        var copy = Clone(source);
        copy.Name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} copy" : newName.Trim();
        copy.Id = CreateId(copy.Name);
        copy.Version = ZetlViewDocument.CurrentVersion;
        return copy;
    }

    public static string CreateId(string name) => ZetlDocumentId.Create(name, "view");
}

/// <summary>
/// Validates a view document. Returns every problem found (empty when well-formed)
/// so an authoring/import path can list actionable errors. Used by tests so
/// built-ins can be checked without launching the app.
/// </summary>
internal static class ZetlViewValidator
{
    private static readonly string[] ValidKinds =
    [
        ZetlViewKinds.Formatted,
        ZetlViewKinds.Plain,
        ZetlViewKinds.Tsv,
        ZetlViewKinds.Markdown,
        ZetlViewKinds.Html,
        ZetlViewKinds.Pdf
    ];

    public static IReadOnlyList<string> Validate(ZetlViewDocument? view)
    {
        if (view is null)
        {
            return ["View data is missing."];
        }

        var errors = new List<string>();

        if (view.Version < 1)
        {
            errors.Add("View version must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(view.Id))
        {
            errors.Add("View id is required.");
        }

        if (string.IsNullOrWhiteSpace(view.Name))
        {
            errors.Add("View name is required.");
        }

        if (!ValidKinds.Contains(view.Kind, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"View kind must be one of: {string.Join(", ", ValidKinds)}.");
        }

        if (view.TsvRowLength < 1)
        {
            errors.Add("TSV row length must be at least 1.");
        }

        if (!ZetlViewListStyles.All.Contains(view.ListStyle, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"View list style must be one of: {string.Join(", ", ZetlViewListStyles.All)}.");
        }

        foreach (var section in view.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Title))
            {
                errors.Add("A section is missing a title.");
                continue;
            }

            if (section.Buckets.Count == 0 || section.Buckets.All(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Section '{section.Title}' references no buckets.");
            }
        }

        return errors;
    }
}

/// <summary>
/// Loads the view catalog: the protected built-ins plus any valid user view JSON
/// documents under the views directory (default <c>%AppData%\Zetl\views</c>).
/// Mirrors <see cref="ZetlTemplateStore"/> / <see cref="ZetlThemeStore"/>: one bad
/// file never blocks the catalog — a corrupt file is quarantined and an invalid,
/// id-colliding, or duplicate file is skipped with a diagnostic.
/// </summary>
internal sealed class ZetlViewStore
{
    private readonly ZetlDocumentStore<ZetlViewDocument> store;

    public ZetlViewStore(string? viewDirectory = null, Action<string>? log = null) =>
        store = new(
            viewDirectory,
            "views",
            "view",
            ZetlViewDefaults.CreateAll,
            ZetlViewValidator.Validate,
            ZetlViewDefaults.IsBuiltIn,
            view => view.Id,
            log);

    public string ViewDirectory => store.Directory;

    public IReadOnlyList<ZetlViewDocument> LoadAll() => store.LoadAll();

    public void Save(ZetlViewDocument view) => store.Save(view);

    public void Delete(string id) => store.Delete(id);
}
