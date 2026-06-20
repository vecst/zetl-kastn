using ZETL.Contracts;

namespace KASTN;

/// <summary>
/// A built-in project template: a named, input-side scaffold that creates a new,
/// empty project with a useful bucket structure and bucket behaviors. Templates
/// never copy sample slips; they only shape the starting buckets. Project
/// creation still goes through Zetl's <see cref="CreateProjectCommand"/>, so Zetl
/// remains the sole writer.
/// </summary>
internal sealed record KastnTemplate(
    string Id,
    string Name,
    string Category,
    string Description,
    IReadOnlyList<KastnTemplateBucket> Buckets)
{
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
/// project's active capture bucket. Nesting is not represented yet: templates are
/// flat until the create contract carries parent references by name.
/// </summary>
internal sealed record KastnTemplateBucket(string Name, ZetlBucketSettings Settings)
{
    public KastnTemplateBucket(string name)
        : this(name, new ZetlBucketSettings())
    {
    }
}

internal static class KastnTemplateCatalog
{
    // Reserved bucket names Zetl manages itself; templates must not define them.
    // Scratch is always added to a new project, and Deleted is the protected
    // soft-delete bucket.
    private static readonly string[] ReservedBucketNames = ["Scratch", "Deleted"];

    public static IReadOnlyList<KastnTemplate> BuiltIns { get; } =
    [
        new KastnTemplate(
            "blank",
            "Blank",
            "Start",
            "Start with a clean bucket structure.",
            [new KastnTemplateBucket("Inbox")]),
        new KastnTemplate(
            "draft-stack",
            "Draft stack",
            "Writing",
            "Collect notes toward a draft or essay.",
            [
                new KastnTemplateBucket("Ideas"),
                new KastnTemplateBucket("Draft"),
                new KastnTemplateBucket("References")
            ]),
        new KastnTemplate(
            "research-board",
            "Research board",
            "Research",
            "Track sources as a table, then gather notes and synthesis.",
            [
                new KastnTemplateBucket(
                    "Sources",
                    new ZetlBucketSettings
                    {
                        DefaultCompileMode = "TSV",
                        DefaultStartingText = "Title\nAuthor\nURL\nYear",
                        DefaultTsvRowLength = 4
                    }),
                new KastnTemplateBucket("Notes"),
                new KastnTemplateBucket("Synthesis")
            ])
    ];

    /// <summary>
    /// Validates the built-in catalog: stable unique ids, named templates with at
    /// least one bucket, and no empty or reserved bucket names. Returns the first
    /// problem found, or null when the catalog is well-formed. Used by tests so
    /// built-ins can be checked without launching Kastn.
    /// </summary>
    public static string? Validate(IReadOnlyList<KastnTemplate> templates)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in templates)
        {
            if (string.IsNullOrWhiteSpace(template.Id))
            {
                return "A template is missing an id.";
            }

            if (!seenIds.Add(template.Id))
            {
                return $"Duplicate template id '{template.Id}'.";
            }

            if (string.IsNullOrWhiteSpace(template.Name))
            {
                return $"Template '{template.Id}' is missing a name.";
            }

            if (template.Buckets.Count == 0)
            {
                return $"Template '{template.Id}' has no buckets.";
            }

            var seenBuckets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bucket in template.Buckets)
            {
                if (string.IsNullOrWhiteSpace(bucket.Name))
                {
                    return $"Template '{template.Id}' has a bucket without a name.";
                }

                if (ReservedBucketNames.Contains(bucket.Name, StringComparer.OrdinalIgnoreCase))
                {
                    return $"Template '{template.Id}' uses reserved bucket name '{bucket.Name}'.";
                }

                if (!seenBuckets.Add(bucket.Name))
                {
                    return $"Template '{template.Id}' repeats bucket name '{bucket.Name}'.";
                }
            }
        }

        return null;
    }
}
