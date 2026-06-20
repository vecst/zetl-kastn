using System.Text.Json;
using KASTN;
using ZETL.Contracts;

namespace ZETL.Tests;

internal static class KastnTemplateCatalogTests
{
    public static void BuiltInTemplatesAreValid()
    {
        AssertTrue(
            KastnTemplateCatalog.Validate(KastnTemplateCatalog.BuiltIns) is null,
            "Built-in templates should be well-formed.");
        AssertTrue(
            KastnTemplateCatalog.BuiltIns.Count >= 3,
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
