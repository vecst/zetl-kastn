using System.Text.Json;
using KASTN;
using ZETL;
using ZETL.Contracts;

namespace ZETL.Tests;

internal static class KastnTemplateCatalogTests
{
    public static void BuiltInTemplatesAreValid()
    {
        var builtIns = KastnTemplateCatalog.BuiltIns;
        foreach (var template in builtIns)
        {
            var errors = ZetlTemplateValidator.Validate(template);
            AssertTrue(
                errors.Count == 0,
                $"Built-in template '{template.Id}' should be well-formed: "
                    + string.Join("; ", errors));
        }

        AssertTrue(
            builtIns.Count >= 3,
            "The catalog should ship at least the three built-in templates.");
    }

    public static void TemplateCreatesProjectThroughService()
    {
        using var temp = new TempDir();
        var store = new ZetlStateStore(temp.StatePath, "template-tests");
        var service = new ZetlProjectService(store);
        var research = KastnTemplateCatalog.BuiltIns.Single(
            template => template.Id == "research-board");

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "create-from-template",
            ZetlCommandKind.CreateProject,
            research.ToCreateProjectCommand("My Research")));
        var snapshot = response.Payload?.Deserialize<ZetlProjectSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("CreateProject returned no snapshot.");
        var sources = snapshot.Buckets.Single(bucket => bucket.Name == "Sources");
        var active = snapshot.Buckets.Single(bucket => bucket.Id == snapshot.ActiveBucketId);

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Template creation should succeed.");
        AssertEqual("My Research", snapshot.Name, "The project should take the chosen name.");
        AssertTrue(
            snapshot.Buckets.Any(bucket => bucket.Name == "Sources")
                && snapshot.Buckets.Any(bucket => bucket.Name == "Notes")
                && snapshot.Buckets.Any(bucket => bucket.Name == "Synthesis"),
            "Template buckets should be created.");
        AssertTrue(
            snapshot.Buckets.Any(bucket => bucket.Name == "Scratch"),
            "Zetl should still add the protected Scratch bucket.");
        AssertEqual("Sources", active.Name, "The first template bucket should become active.");
        AssertEqual(
            "TSV",
            sources.Settings.DefaultCompileMode,
            "Sources should carry the template's TSV compile mode.");
        AssertEqual(
            4,
            sources.Settings.DefaultTsvRowLength,
            "Sources should carry the template's TSV row length.");
        AssertTrue(
            sources.Settings.DefaultStartingText.Contains("Title")
                && sources.Settings.DefaultStartingText.Contains("Year"),
            "Sources should carry the template's TSV header starting text.");
    }

    public static void ConsumableTemplateSeedsOrderedReplayQueue()
    {
        using var temp = new TempDir();
        var store = new ZetlStateStore(temp.StatePath, "template-tests");
        var service = new ZetlProjectService(store);
        var personal = KastnTemplateCatalog.BuiltIns.Single(
            template => template.Id == "personal-info");
        var seeds = personal.Buckets.Single(bucket => bucket.Name == "Fields").Seeds;

        var snapshot = service.Execute(ZetlCommandEnvelope.Create(
            "create-consumable",
            ZetlCommandKind.CreateProject,
            personal.ToCreateProjectCommand("Me")))
            .Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("CreateProject returned no snapshot.");
        var fields = snapshot.Buckets.Single(bucket => bucket.Name == "Fields");

        // Seed in listed order, the same way Kastn's create path does.
        foreach (var text in seeds)
        {
            service.Execute(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.AddSlip,
                new AddSlipCommand { BucketId = fields.Id, Text = text, Source = "template" },
                snapshot.Id));
        }

        var refreshed = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "get-consumable",
            Kind = ZetlCommandKind.GetProject,
            ProjectId = snapshot.Id
        }).Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("GetProject returned no snapshot.");
        var fieldSlips = refreshed.Slips
            .Where(slip => slip.BucketId == fields.Id)
            .ToList();

        AssertEqual(
            ZetlTemplateTypes.Consumable,
            personal.Type,
            "Personal info should be a consumable template.");
        AssertEqual(
            "Replay",
            fields.Settings.Kind,
            "The seeded Fields bucket should be a Replay bucket.");
        AssertEqual(seeds.Count, fieldSlips.Count, "Every template field should be seeded once.");
        AssertEqual(
            "Full name",
            fieldSlips[0].Text,
            "Seeds should keep order: the first field replays first.");
        AssertEqual(
            "ZIP",
            fieldSlips[^1].Text,
            "Seeds should keep order: the last field replays last.");
    }

    public static void BlankTemplateCreatesMinimalProject()
    {
        using var temp = new TempDir();
        var store = new ZetlStateStore(temp.StatePath, "template-tests");
        var service = new ZetlProjectService(store);
        var blank = KastnTemplateCatalog.BuiltIns.Single(template => template.Id == "blank");

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "create-blank",
            ZetlCommandKind.CreateProject,
            blank.ToCreateProjectCommand("Fresh")));
        var snapshot = response.Payload?.Deserialize<ZetlProjectSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("CreateProject returned no snapshot.");

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Blank creation should succeed.");
        AssertTrue(
            snapshot.Buckets.Any(bucket => bucket.Name == "Inbox"),
            "Blank should create the Inbox bucket.");
        AssertTrue(
            snapshot.Buckets.Any(bucket => bucket.Name == "Scratch"),
            "Blank should still get the protected Scratch bucket.");
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "KastnTemplateCatalogTests",
            Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(directory);
            StatePath = Path.Combine(directory, "state.json");
        }

        public string StatePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
