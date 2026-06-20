using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ZETL.Contracts;

namespace ZETL;

// The two ways a template is used, stored as readable text the same way bucket
// Kind / DefaultCompileMode are. A capture template creates an empty project to
// collect into; a consumable template seeds an ordered Replay queue you paste
// through, then discard (the template, not the project, is the durable source).
internal static class ZetlTemplateTypes
{
    public const string Capture = "Capture";
    public const string Consumable = "Consumable";
}

/// <summary>
/// A versioned, human-readable project template document. This is the canonical
/// shape for both the built-in catalog and future user-authored templates,
/// mirroring the theme system (<see cref="ZetlThemeDocument"/>): a stable id,
/// a version, and <see cref="JsonExtensionData"/> so a newer document opened by
/// an older build keeps its unknown fields instead of losing them.
///
/// A template only describes input-side structure. Project creation (and any
/// consumable seeding) still goes through Zetl's
/// <see cref="CreateProjectCommand"/> and <c>AddSlip</c>, so Zetl stays the sole
/// writer.
/// </summary>
internal sealed class ZetlTemplateDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Category { get; set; } = "";

    public string Description { get; set; } = "";

    // ZetlTemplateTypes.Capture or .Consumable.
    public string Type { get; set; } = ZetlTemplateTypes.Capture;

    public List<ZetlTemplateBucketDocument> Buckets { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public bool IsConsumable =>
        string.Equals(Type, ZetlTemplateTypes.Consumable, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Project the template's bucket structure onto the existing create contract.
    /// Capture and consumable templates build the same buckets here; a consumable
    /// template's <see cref="ZetlTemplateBucketDocument.Seeds"/> are applied
    /// afterwards through <c>AddSlip</c> by the caller, because the create
    /// contract carries bucket structure, not slip content.
    /// </summary>
    public CreateProjectCommand ToCreateProjectCommand(string projectName)
    {
        return new CreateProjectCommand
        {
            Name = projectName,
            Buckets = Buckets
                .Select(bucket => new CreateBucketDefinition
                {
                    Name = bucket.Name,
                    Settings = bucket.Settings
                })
                .ToList()
        };
    }
}

/// <summary>
/// One bucket in a template. The first bucket of a template becomes the created
/// project's active capture bucket. <see cref="Seeds"/> are ordered slip texts a
/// consumable template pre-loads into this bucket; capture buckets leave it empty.
/// </summary>
internal sealed class ZetlTemplateBucketDocument
{
    public string Name { get; set; } = "";

    /// <summary>
    /// Parent bucket <em>name</em> within this same template, or null for a
    /// top-level bucket. Names rather than ids because template buckets have no
    /// ids until a project is created from them. Nesting is validated here;
    /// wiring nested creation through Zetl (resolving names to ids) is a later
    /// step, so today's built-ins stay flat.
    /// </summary>
    public string? Parent { get; set; }

    public ZetlBucketSettings Settings { get; set; } = new();

    public List<string> Seeds { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// The protected, built-in template catalog. Built-ins are authored in the same
/// document shape as future user templates so a copied built-in is a valid,
/// inspectable starting point for authoring.
/// </summary>
internal static partial class ZetlTemplateDefaults
{
    // Bucket names Zetl manages itself; templates must not define them. Scratch is
    // always added to a new project, and Deleted is the protected soft-delete
    // bucket.
    public static readonly IReadOnlyList<string> ReservedBucketNames = ["Scratch", "Deleted"];

    // Fresh documents each call so a caller (e.g. a future "duplicate built-in"
    // authoring path) can mutate its copy without disturbing the shared presets.
    public static IReadOnlyList<ZetlTemplateDocument> CreateAll() =>
    [
        Blank(),
        DraftStack(),
        ResearchBoard(),
        PersonalInfo()
    ];

    private static readonly HashSet<string> BuiltInIds =
        new(CreateAll().Select(template => template.Id), StringComparer.Ordinal);

    public static bool IsBuiltIn(string? id) => id is not null && BuiltInIds.Contains(id);

    public static ZetlTemplateDocument? FindBuiltIn(string? id) =>
        CreateAll().FirstOrDefault(template => template.Id == id);

    // A starter document for authoring a brand-new template: one capture bucket,
    // no id yet (the store assigns one from the name on save).
    public static ZetlTemplateDocument CreateDraft() => new()
    {
        Name = "",
        Category = "Custom",
        Description = "",
        Type = ZetlTemplateTypes.Capture,
        Buckets = [new ZetlTemplateBucketDocument { Name = "Inbox" }]
    };

    // A deep copy via the same JSON path the store persists through, so a clone
    // can be edited without disturbing the source (e.g. a built-in preset).
    public static ZetlTemplateDocument Clone(ZetlTemplateDocument template)
    {
        var json = JsonSerializer.Serialize(template, JsonFile.Options);
        return JsonSerializer.Deserialize<ZetlTemplateDocument>(json, JsonFile.Options)
            ?? CreateDraft();
    }

    // Make an independent user copy of a template (typically a built-in) with a
    // fresh id and name, so the original stays immutable.
    public static ZetlTemplateDocument Duplicate(ZetlTemplateDocument source, string? newName = null)
    {
        var copy = Clone(source);
        copy.Name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} copy" : newName.Trim();
        copy.Id = CreateId(copy.Name);
        copy.Version = ZetlTemplateDocument.CurrentVersion;
        return copy;
    }

    // A stable, filesystem-safe id from a display name plus a uniqueness suffix,
    // matching the theme id scheme so user files never collide.
    public static string CreateId(string name)
    {
        var slug = NonSlugCharacters().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "template";
        }

        slug = slug[..Math.Min(slug.Length, 30)];
        return $"{slug}-{Guid.NewGuid():N}"[..Math.Min(slug.Length + 9, 48)];
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();

    private static ZetlTemplateDocument Blank() => new()
    {
        Id = "blank",
        Name = "Blank",
        Category = "Start",
        Description = "Start with a clean bucket structure.",
        Type = ZetlTemplateTypes.Capture,
        Buckets = [Bucket("Inbox")]
    };

    private static ZetlTemplateDocument DraftStack() => new()
    {
        Id = "draft-stack",
        Name = "Draft stack",
        Category = "Writing",
        Description = "Collect notes toward a draft or essay.",
        Type = ZetlTemplateTypes.Capture,
        Buckets = [Bucket("Ideas"), Bucket("Draft"), Bucket("References")]
    };

    private static ZetlTemplateDocument ResearchBoard() => new()
    {
        Id = "research-board",
        Name = "Research board",
        Category = "Research",
        Description = "Track sources as a table, then gather notes and synthesis.",
        Type = ZetlTemplateTypes.Capture,
        Buckets =
        [
            Bucket("Sources", new ZetlBucketSettings
            {
                DefaultCompileMode = "TSV",
                DefaultStartingText = "Title\nAuthor\nURL\nYear",
                DefaultTsvRowLength = 4
            }),
            Bucket("Notes"),
            Bucket("Synthesis")
        ]
    };

    private static ZetlTemplateDocument PersonalInfo() => new()
    {
        Id = "personal-info",
        Name = "Personal info",
        Category = "Forms",
        Description = "Replay your details through a form, field by field. Edit the "
            + "placeholders to your own info, then tab-paste them into any form.",
        Type = ZetlTemplateTypes.Consumable,
        Buckets =
        [
            Bucket(
                "Fields",
                new ZetlBucketSettings { Kind = "Replay", DefaultKind = "Replay" },
                ["Full name", "Email", "Phone", "Street address", "City", "State", "ZIP"])
        ]
    };

    private static ZetlTemplateBucketDocument Bucket(
        string name,
        ZetlBucketSettings? settings = null,
        IEnumerable<string>? seeds = null) => new()
    {
        Name = name,
        Settings = settings ?? new ZetlBucketSettings(),
        Seeds = seeds?.ToList() ?? []
    };
}

/// <summary>
/// Validates a template document before it can create a project. Returns every
/// problem found (empty when well-formed) so an authoring UI or import path can
/// list actionable errors instead of failing on the first one. Used by tests so
/// built-ins can be checked without launching Kastn.
/// </summary>
internal static class ZetlTemplateValidator
{
    // Canonical bucket setting values. Kept in step with ZetlState's normalizers,
    // which store exactly these strings ("Fifo" is a legacy alias normalized to
    // "Replay" before it ever reaches a template document).
    private static readonly string[] ValidKinds = ["Standard", "Replay"];
    private static readonly string[] ValidCompileModes = ["Formatted", "Plain", "TSV"];

    public static IReadOnlyList<string> Validate(ZetlTemplateDocument? template)
    {
        if (template is null)
        {
            return ["Template data is missing."];
        }

        var errors = new List<string>();

        if (template.Version < 1)
        {
            errors.Add("Template version must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(template.Id))
        {
            errors.Add("Template id is required.");
        }

        if (string.IsNullOrWhiteSpace(template.Name))
        {
            errors.Add("Template name is required.");
        }

        var isCapture = string.Equals(
            template.Type, ZetlTemplateTypes.Capture, StringComparison.OrdinalIgnoreCase);
        if (!isCapture && !template.IsConsumable)
        {
            errors.Add(
                $"Template type must be '{ZetlTemplateTypes.Capture}' or "
                + $"'{ZetlTemplateTypes.Consumable}'.");
        }

        if (template.Buckets.Count == 0)
        {
            errors.Add("A template needs at least one bucket.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedCount = 0;
        foreach (var bucket in template.Buckets)
        {
            if (string.IsNullOrWhiteSpace(bucket.Name))
            {
                errors.Add("A bucket is missing a name.");
                continue;
            }

            if (ReservedName(bucket.Name))
            {
                errors.Add($"Bucket name '{bucket.Name}' is reserved.");
            }

            if (!names.Add(bucket.Name))
            {
                errors.Add($"Bucket name '{bucket.Name}' is repeated.");
            }

            if (bucket.Seeds.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Bucket '{bucket.Name}' has an empty seed.");
            }

            seedCount += bucket.Seeds.Count;
            ValidateSettings(bucket, errors);
        }

        // Parent references resolve only after every name is known, so check them
        // in a second pass.
        foreach (var bucket in template.Buckets)
        {
            if (string.IsNullOrWhiteSpace(bucket.Parent))
            {
                continue;
            }

            if (string.Equals(bucket.Parent, bucket.Name, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Bucket '{bucket.Name}' cannot be its own parent.");
            }
            else if (!names.Contains(bucket.Parent))
            {
                errors.Add(
                    $"Bucket '{bucket.Name}' references a missing parent '{bucket.Parent}'.");
            }
        }

        if (isCapture && seedCount > 0)
        {
            errors.Add("Capture templates must not seed slips.");
        }

        if (template.IsConsumable && seedCount == 0)
        {
            errors.Add("Consumable templates must seed at least one slip.");
        }

        return errors;
    }

    public static bool ReservedName(string name) =>
        ZetlTemplateDefaults.ReservedBucketNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void ValidateSettings(ZetlTemplateBucketDocument bucket, List<string> errors)
    {
        var settings = bucket.Settings;
        if (settings is null)
        {
            errors.Add($"Bucket '{bucket.Name}' is missing settings.");
            return;
        }

        if (!ValidKinds.Contains(settings.Kind, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Bucket '{bucket.Name}' has an invalid kind '{settings.Kind}'.");
        }

        if (!ValidKinds.Contains(settings.DefaultKind, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"Bucket '{bucket.Name}' has an invalid default kind '{settings.DefaultKind}'.");
        }

        if (!ValidCompileModes.Contains(settings.DefaultCompileMode, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Bucket '{bucket.Name}' has an invalid compile mode "
                + $"'{settings.DefaultCompileMode}'.");
        }

        if (settings.DefaultTsvRowLength < 1)
        {
            errors.Add($"Bucket '{bucket.Name}' TSV row length must be at least 1.");
        }
    }
}

/// <summary>
/// Loads the template catalog: the protected built-ins plus any valid user
/// template JSON documents under the templates directory (default
/// <c>%AppData%\Zetl\templates</c>). Mirrors <see cref="ZetlThemeStore"/>. One bad
/// file never blocks the catalog: a corrupt file is quarantined (moved aside),
/// and an invalid, id-colliding, or duplicate file is skipped with a diagnostic,
/// leaving the built-ins always available.
/// </summary>
internal sealed class ZetlTemplateStore
{
    private readonly string templateDirectory;
    private readonly Action<string>? log;

    public ZetlTemplateStore(string? templateDirectory = null, Action<string>? log = null)
    {
        this.templateDirectory = templateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "templates");
        this.log = log;
    }

    public string TemplateDirectory => templateDirectory;

    public IReadOnlyList<ZetlTemplateDocument> LoadAll()
    {
        var templates = ZetlTemplateDefaults.CreateAll().ToList();
        if (!Directory.Exists(templateDirectory))
        {
            return templates;
        }

        string[] paths;
        try
        {
            paths = Directory.GetFiles(templateDirectory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not list template directory '{templateDirectory}': {ex.Message}");
            return templates;
        }

        var seenIds = new HashSet<string>(
            templates.Select(template => template.Id), StringComparer.Ordinal);

        foreach (var path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            // ReadOrQuarantine moves a corrupt file aside (a visible .corrupt-*
            // copy) and returns null, so a damaged user file degrades to "skipped"
            // rather than aborting the load.
            var template = JsonFile.ReadOrQuarantine<ZetlTemplateDocument>(path, log);
            if (template is null)
            {
                continue;
            }

            var errors = ZetlTemplateValidator.Validate(template);
            if (errors.Count > 0)
            {
                log?.Invoke($"Ignoring invalid template '{name}': {string.Join("; ", errors)}");
                continue;
            }

            if (ZetlTemplateDefaults.IsBuiltIn(template.Id))
            {
                log?.Invoke(
                    $"Ignoring user template '{name}': id '{template.Id}' is reserved by a built-in.");
                continue;
            }

            if (!seenIds.Add(template.Id))
            {
                log?.Invoke($"Ignoring user template '{name}': duplicate id '{template.Id}'.");
                continue;
            }

            templates.Add(template);
        }

        return templates;
    }

    /// <summary>
    /// Writes a user template as JSON. The document must be valid and must not use
    /// a built-in id, so authoring can never overwrite a protected preset. The file
    /// is named from the (stable) id, so renaming a template never moves its file.
    /// </summary>
    public void Save(ZetlTemplateDocument template)
    {
        var errors = ZetlTemplateValidator.Validate(template);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }

        if (ZetlTemplateDefaults.IsBuiltIn(template.Id))
        {
            throw new InvalidOperationException("Built-in templates cannot be overwritten.");
        }

        JsonFile.WriteAtomic(PathFor(template.Id), template);
    }

    /// <summary>
    /// Removes a user template file. Built-ins are never deletable. A missing file
    /// is a no-op so a double-delete is harmless.
    /// </summary>
    public void Delete(string id)
    {
        if (ZetlTemplateDefaults.IsBuiltIn(id))
        {
            throw new InvalidOperationException("Built-in templates cannot be deleted.");
        }

        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string id)
    {
        var safeId = string.Concat(id.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-'));
        return Path.Combine(templateDirectory, $"{safeId}.json");
    }
}
