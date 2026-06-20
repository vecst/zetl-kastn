using ZETL;
using ZETL.Contracts;

namespace ZETL.Tests;

internal static class ZetlTemplateStoreTests
{
    public static void LoadsBuiltInsWhenNoDirectory()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), "ZetlTemplateStoreTests", Guid.NewGuid().ToString("N"));
        var store = new ZetlTemplateStore(missing);

        var loaded = store.LoadAll();

        AssertEqual(
            ZetlTemplateDefaults.CreateAll().Count,
            loaded.Count,
            "With no templates directory, the catalog is exactly the built-ins.");
    }

    public static void LoadsValidUserTemplatesAndSkipsBadOnes()
    {
        using var dir = new TempDir();
        var logs = new List<string>();
        var store = new ZetlTemplateStore(dir.Path, logs.Add);

        Write(dir, "mine.json", UserTemplate("my-template", "My Template"));
        Write(dir, "collision.json", UserTemplate("blank", "Fake Blank"));
        Write(dir, "reserved.json", new ZetlTemplateDocument
        {
            Id = "reserved-bucket",
            Name = "Reserved",
            Category = "Test",
            Type = ZetlTemplateTypes.Capture,
            Buckets = [new ZetlTemplateBucketDocument { Name = "Deleted" }]
        });
        File.WriteAllText(Path.Combine(dir.Path, "broken.json"), "{ not valid json ");

        var loaded = store.LoadAll();
        var ids = loaded.Select(template => template.Id).ToList();

        AssertTrue(ids.Contains("my-template"), "A valid user template should load.");
        AssertTrue(ids.Contains("blank"), "Built-ins should remain available.");
        AssertEqual(
            1,
            ids.Count(id => id == "blank"),
            "A user file colliding with a built-in id must not duplicate the built-in.");
        AssertTrue(!ids.Contains("reserved-bucket"), "An invalid user template should be skipped.");
        AssertEqual(
            ZetlTemplateDefaults.CreateAll().Count + 1,
            loaded.Count,
            "Only the one valid user template should be added to the built-ins.");

        AssertTrue(
            Directory.GetFiles(dir.Path, "broken.json.corrupt-*").Length == 1,
            "A corrupt template file should be quarantined to a visible .corrupt-* copy.");
        AssertTrue(
            logs.Any(line => line.Contains("reserved.json")),
            "Skipping an invalid template should leave a diagnostic.");
        AssertTrue(
            logs.Any(line => line.Contains("collision.json")),
            "Skipping a built-in id collision should leave a diagnostic.");
    }

    public static void RemovingAFileDropsTheTemplateAfterRefresh()
    {
        using var dir = new TempDir();
        var store = new ZetlTemplateStore(dir.Path);
        var path = Path.Combine(dir.Path, "mine.json");
        Write(dir, "mine.json", UserTemplate("my-template", "My Template"));

        AssertTrue(
            store.LoadAll().Any(template => template.Id == "my-template"),
            "The user template should load while its file exists.");

        File.Delete(path);

        var afterDelete = store.LoadAll();
        AssertTrue(
            afterDelete.All(template => template.Id != "my-template"),
            "Removing the file should drop the template on the next load.");
        AssertEqual(
            ZetlTemplateDefaults.CreateAll().Count,
            afterDelete.Count,
            "After removal only the built-ins remain.");
    }

    public static void SaveRoundTripsAndRefusesBuiltInIds()
    {
        using var dir = new TempDir();
        var store = new ZetlTemplateStore(dir.Path);

        var draft = ZetlTemplateDefaults.CreateDraft();
        draft.Name = "My Workflow";
        draft.Id = ZetlTemplateDefaults.CreateId(draft.Name);
        store.Save(draft);

        var reloaded = store.LoadAll().SingleOrDefault(template => template.Id == draft.Id);
        AssertTrue(reloaded is not null, "A saved template should load back from the store.");
        AssertEqual("My Workflow", reloaded!.Name, "Save should persist the template name.");

        var collision = ZetlTemplateDefaults.CreateDraft();
        collision.Id = "blank";
        collision.Name = "Not Allowed";
        AssertThrows<InvalidOperationException>(
            () => store.Save(collision),
            "Saving over a built-in id should be refused.");

        var invalid = ZetlTemplateDefaults.CreateDraft();
        invalid.Id = ZetlTemplateDefaults.CreateId("invalid");
        invalid.Buckets = [];
        AssertThrows<InvalidDataException>(
            () => store.Save(invalid),
            "Saving an invalid template should be refused.");
    }

    public static void DuplicateMakesAnIndependentEditableCopy()
    {
        var builtIn = ZetlTemplateDefaults.FindBuiltIn("research-board")
            ?? throw new InvalidOperationException("research-board built-in is missing.");

        var copy = ZetlTemplateDefaults.Duplicate(builtIn);

        AssertTrue(copy.Id != builtIn.Id, "A duplicate should get a fresh id.");
        AssertTrue(!ZetlTemplateDefaults.IsBuiltIn(copy.Id), "A duplicate must not be a built-in.");
        AssertEqual("Research board copy", copy.Name, "A duplicate names itself a copy by default.");
        AssertEqual(
            builtIn.Buckets.Count,
            copy.Buckets.Count,
            "A duplicate should carry the source bucket structure.");
        AssertTrue(
            ZetlTemplateValidator.Validate(copy).Count == 0,
            "A duplicate of a built-in should be valid.");

        // Editing the copy must not disturb the original built-in document.
        copy.Buckets[0].Name = "Renamed";
        AssertEqual(
            "Sources",
            builtIn.Buckets[0].Name,
            "Editing a duplicate must not mutate the source.");
    }

    public static void DeleteRemovesUserTemplatesButNotBuiltIns()
    {
        using var dir = new TempDir();
        var store = new ZetlTemplateStore(dir.Path);
        store.Save(UserTemplate("my-template", "My Template"));

        store.Delete("my-template");
        AssertTrue(
            store.LoadAll().All(template => template.Id != "my-template"),
            "Delete should remove a user template.");

        // Idempotent: deleting again is harmless.
        store.Delete("my-template");

        AssertThrows<InvalidOperationException>(
            () => store.Delete("blank"),
            "Deleting a built-in should be refused.");
    }

    private static ZetlTemplateDocument UserTemplate(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Category = "Test",
        Description = "A user template.",
        Type = ZetlTemplateTypes.Capture,
        Buckets = [new ZetlTemplateBucketDocument { Name = "Inbox" }]
    };

    private static void Write(TempDir dir, string fileName, ZetlTemplateDocument template)
    {
        JsonFile.WriteAtomic(Path.Combine(dir.Path, fileName), template);
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

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{message} (expected {typeof(TException).Name})");
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ZetlTemplateStoreTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
