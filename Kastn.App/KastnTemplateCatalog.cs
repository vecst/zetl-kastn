using ZETL.Contracts;

namespace KASTN;

/// <summary>
/// How a template is meant to be used. Capture templates create an empty project
/// to collect into. Consumable templates seed an ordered Replay queue you paste
/// through (e.g. your details into a form), then discard — the template, not the
/// project, is the durable source, so each use instantiates a fresh project.
/// </summary>
internal enum KastnTemplateType
{
    Capture,
    Consumable
}

/// <summary>
/// A built-in project template: a named, input-side scaffold. Capture templates
/// only shape the starting buckets; consumable templates also seed ordered slips.
/// Project creation still goes through Zetl's <see cref="CreateProjectCommand"/>
/// (and <c>AddSlip</c> for seeds), so Zetl remains the sole writer.
/// </summary>
internal sealed record KastnTemplate(
    string Id,
    string Name,
    string Category,
    string Description,
    KastnTemplateType Type,
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
/// project's active capture bucket. <see cref="Seeds"/> are ordered slip texts a
/// consumable template pre-loads into this bucket; capture buckets leave it empty.
/// Nesting is not represented yet: templates are flat until the create contract
/// carries parent references by name.
/// </summary>
internal sealed record KastnTemplateBucket(
    string Name,
    ZetlBucketSettings Settings,
    IReadOnlyList<string> Seeds)
{
    public KastnTemplateBucket(string name)
        : this(name, new ZetlBucketSettings(), [])
    {
    }

    public KastnTemplateBucket(string name, ZetlBucketSettings settings)
        : this(name, settings, [])
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
            KastnTemplateType.Capture,
            [new KastnTemplateBucket("Inbox")]),
        new KastnTemplate(
            "draft-stack",
            "Draft stack",
            "Writing",
            "Collect notes toward a draft or essay.",
            KastnTemplateType.Capture,
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
            KastnTemplateType.Capture,
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
            ]),
        new KastnTemplate(
            "personal-info",
            "Personal info",
            "Forms",
            "Replay your details through a form, field by field. Edit the "
                + "placeholders to your own info, then tab-paste them into any form.",
            KastnTemplateType.Consumable,
            [
                new KastnTemplateBucket(
                    "Fields",
                    new ZetlBucketSettings { Kind = "Replay", DefaultKind = "Replay" },
                    [
                        "Full name",
                        "Email",
                        "Phone",
                        "Street address",
                        "City",
                        "State",
                        "ZIP"
                    ])
            ])
    ];

    /// <summary>
    /// Validates the built-in catalog: stable unique ids, named templates with at
    /// least one bucket, no empty or reserved bucket names, and seeds that match
    /// the template type (capture templates stay empty; consumable templates seed
    /// at least one non-empty slip). Returns the first problem found, or null when
    /// the catalog is well-formed. Used by tests so built-ins can be checked
    /// without launching Kastn.
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
            var seedCount = 0;
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

                if (bucket.Seeds.Any(string.IsNullOrWhiteSpace))
                {
                    return $"Template '{template.Id}' has an empty seed in bucket '{bucket.Name}'.";
                }

                seedCount += bucket.Seeds.Count;
            }

            if (template.Type == KastnTemplateType.Capture && seedCount > 0)
            {
                return $"Capture template '{template.Id}' must not seed slips.";
            }

            if (template.Type == KastnTemplateType.Consumable && seedCount == 0)
            {
                return $"Consumable template '{template.Id}' must seed at least one slip.";
            }
        }

        return null;
    }
}
