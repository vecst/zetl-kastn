using System.Text.Json;
using ZETL;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlTemplateDocumentTests
{
    // A copied template document must survive a JSON round-trip unchanged and
    // stay human-readable (type and bucket settings as words, not numbers).
    [Fact] public void BuiltInsRoundTripThroughJson()
    {
        foreach (var original in ZetlTemplateDefaults.CreateAll())
        {
            var json = JsonSerializer.Serialize(original, JsonFile.Options);
            var restored = JsonSerializer.Deserialize<ZetlTemplateDocument>(json, JsonFile.Options)
                ?? throw new InvalidOperationException($"'{original.Id}' did not round-trip.");

            AssertTrue(
                ZetlTemplateValidator.Validate(restored).Count == 0,
                $"Round-tripped '{original.Id}' should still be valid.");
            AssertEqual(original.Id, restored.Id, "Round-trip should preserve the id.");
            AssertEqual(original.Type, restored.Type, "Round-trip should preserve the type.");
            AssertEqual(
                original.Buckets.Count,
                restored.Buckets.Count,
                $"Round-trip should preserve '{original.Id}' bucket count.");
            for (var i = 0; i < original.Buckets.Count; i++)
            {
                AssertEqual(
                    original.Buckets[i].Name,
                    restored.Buckets[i].Name,
                    "Round-trip should preserve bucket names in order.");
                AssertEqual(
                    string.Join("|", original.Buckets[i].Seeds),
                    string.Join("|", restored.Buckets[i].Seeds),
                    $"Round-trip should preserve '{original.Buckets[i].Name}' seeds in order.");
                AssertEqual(
                    original.Buckets[i].Settings.DefaultCompileMode,
                    restored.Buckets[i].Settings.DefaultCompileMode,
                    "Round-trip should preserve bucket compile mode.");
            }
        }

        var consumableJson = JsonSerializer.Serialize(
            ZetlTemplateDefaults.FindBuiltIn("personal-info"), JsonFile.Options);
        AssertTrue(
            consumableJson.Contains("\"type\": \"Consumable\""),
            "Template type should serialize as a readable word.");
        AssertTrue(
            consumableJson.Contains("\"Full name\""),
            "Consumable seeds should serialize as readable text.");
    }

    // A document written by a newer build (higher version, fields this build does
    // not know) must load, validate, and keep its unknown fields on re-save.
    [Fact] public void FutureVersionAndUnknownFieldsArePreserved()
    {
        const string futureJson = """
        {
          "version": 999,
          "id": "future",
          "name": "Future Template",
          "category": "Test",
          "description": "From a newer build.",
          "type": "Capture",
          "futureTopLevelField": "kept",
          "buckets": [
            { "name": "Inbox", "settings": {}, "seeds": [], "futureBucketField": 7 }
          ]
        }
        """;

        var doc = JsonSerializer.Deserialize<ZetlTemplateDocument>(futureJson, JsonFile.Options)
            ?? throw new InvalidOperationException("Future document did not deserialize.");

        AssertTrue(
            ZetlTemplateValidator.Validate(doc).Count == 0,
            "A forward-compatible future document should still validate.");
        AssertTrue(
            doc.ExtensionData?.ContainsKey("futureTopLevelField") == true,
            "Unknown top-level fields should be captured, not dropped.");
        AssertTrue(
            doc.Buckets[0].ExtensionData?.ContainsKey("futureBucketField") == true,
            "Unknown bucket fields should be captured, not dropped.");

        var resaved = JsonSerializer.Serialize(doc, JsonFile.Options);
        AssertTrue(
            resaved.Contains("futureTopLevelField") && resaved.Contains("futureBucketField"),
            "Re-saving must not lose a newer build's unknown fields.");
    }

    [Fact] public void InvalidTemplatesReportActionableErrors()
    {
        AssertError(null, "missing", "A null document should report missing data.");

        var blankId = Valid();
        blankId.Id = "  ";
        AssertError(blankId, "id is required", "A blank id should be rejected.");

        var blankName = Valid();
        blankName.Name = "";
        AssertError(blankName, "name is required", "A blank name should be rejected.");

        var badType = Valid();
        badType.Type = "Mystery";
        AssertError(badType, "type must be", "An unknown template type should be rejected.");

        var noBuckets = Valid();
        noBuckets.Buckets = [];
        AssertError(noBuckets, "at least one bucket", "A bucketless template should be rejected.");

        AssertError(
            WithBuckets(Bucket("Deleted")),
            "reserved",
            "A reserved bucket name should be rejected.");
        AssertError(
            WithBuckets(Bucket("Inbox"), Bucket("Inbox")),
            "repeated",
            "Duplicate bucket names should be rejected.");
        AssertError(
            WithBuckets(Bucket("Inbox"), Bucket("Notes", parent: "Ghost")),
            "missing parent",
            "A parent that names no bucket should be rejected.");
        AssertError(
            WithBuckets(Bucket("Inbox", parent: "Inbox")),
            "its own parent",
            "A self-referential parent should be rejected.");
        AssertError(
            WithBuckets(Bucket("Inbox", settings: new ZetlBucketSettings { DefaultCompileMode = "PDF" })),
            "compile mode",
            "An invalid compile mode should be rejected.");
        AssertError(
            WithBuckets(Bucket("Inbox", settings: new ZetlBucketSettings { Kind = "Queue" })),
            "invalid kind",
            "An invalid bucket kind should be rejected.");
        AssertError(
            WithBuckets(Bucket("Inbox", settings: new ZetlBucketSettings { DefaultTsvRowLength = 0 })),
            "row length",
            "A non-positive TSV row length should be rejected.");

        var captureWithCard = WithBuckets(Bucket("Inbox"));
        captureWithCard.Buckets[0].Cards =
        [
            new ZetlTemplateSlipDocument { Title = "Question", Text = "" }
        ];
        AssertEqual(
            0,
            ZetlTemplateValidator.Validate(captureWithCard).Count,
            "A capture template should allow a labeled blank starter card.");

        var emptyCard = WithBuckets(Bucket("Inbox"));
        emptyCard.Buckets[0].Cards = [new ZetlTemplateSlipDocument()];
        AssertError(emptyCard, "empty starter card", "A starter card needs a title or note.");

        var emptyConsumable = WithBuckets(Bucket("Fields",
            settings: new ZetlBucketSettings { Kind = "Replay", DefaultKind = "Replay" }));
        emptyConsumable.Type = ZetlTemplateTypes.Consumable;
        AssertError(emptyConsumable, "must seed", "A consumable template with no seeds should be rejected.");

        var blankSeed = WithBuckets(Bucket("Fields",
            settings: new ZetlBucketSettings { Kind = "Replay", DefaultKind = "Replay" },
            seeds: ["ok", "   "]));
        blankSeed.Type = ZetlTemplateTypes.Consumable;
        AssertError(blankSeed, "empty seed", "A blank seed should be rejected.");

        var temporaryCapture = WithBuckets(Bucket("Inbox"));
        temporaryCapture.Temporary = true;
        AssertError(temporaryCapture, "must be consumable", "Only consumable templates can be temporary.");
    }

    [Fact] public void TemporaryConsumablesProjectToTemporaryCreateCommand()
    {
        var template = WithBuckets(Bucket("Fields",
            settings: new ZetlBucketSettings { Kind = "Replay", DefaultKind = "Replay" },
            seeds: ["Full name"]));
        template.Type = ZetlTemplateTypes.Consumable;
        template.Temporary = true;

        var command = template.ToCreateProjectCommand("One Shot", ZetlStateStore.NormalLane);

        AssertEqual(ZetlStateStore.TemporaryConsumableProjectKind, command.Kind, "Temporary flag should project to the create command.");
        AssertEqual(template.Id, command.SourceTemplateId, "Temporary projects should remember their source template.");
        AssertEqual(ZetlStateStore.NormalLane, command.TemporaryLane, "Temporary projects should carry their owning lane.");

        template.Temporary = false;
        var useTimeTemporary = template.ToCreateProjectCommand(
            "One Shot",
            ZetlStateStore.ShiftLane,
            temporary: true);
        AssertEqual(
            ZetlStateStore.TemporaryConsumableProjectKind,
            useTimeTemporary.Kind,
            "Use-time temporary choice should override a durable template default.");
        AssertEqual(
            ZetlStateStore.ShiftLane,
            useTimeTemporary.TemporaryLane,
            "Use-time temporary choice should keep the selected lane.");

        template.Temporary = true;
        var useTimeDurable = template.ToCreateProjectCommand(
            "Keep This",
            ZetlStateStore.NormalLane,
            temporary: false);
        AssertEqual(
            ZetlStateStore.StandardProjectKind,
            useTimeDurable.Kind,
            "Use-time durable choice should override a temporary template default.");
        AssertEqual(null, useTimeDurable.TemporaryLane, "Durable projects should not retain a temporary lane.");
    }

    private static ZetlTemplateDocument Valid() => new()
    {
        Id = "test",
        Name = "Test",
        Category = "Test",
        Description = "",
        Type = ZetlTemplateTypes.Capture,
        Buckets = [Bucket("Inbox")]
    };

    private static ZetlTemplateDocument WithBuckets(params ZetlTemplateBucketDocument[] buckets)
    {
        var doc = Valid();
        doc.Buckets = [.. buckets];
        return doc;
    }

    private static ZetlTemplateBucketDocument Bucket(
        string name,
        string? parent = null,
        ZetlBucketSettings? settings = null,
        IEnumerable<string>? seeds = null) => new()
    {
        Name = name,
        Parent = parent,
        Settings = settings ?? new ZetlBucketSettings(),
        Seeds = seeds?.ToList() ?? []
    };

    private static void AssertError(
        ZetlTemplateDocument? template,
        string expectedSubstring,
        string message)
    {
        var errors = ZetlTemplateValidator.Validate(template);
        AssertTrue(errors.Count > 0, $"{message} (expected at least one error)");
        AssertTrue(
            errors.Any(error => error.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase)),
            $"{message} Expected an error containing '{expectedSubstring}', got: "
                + string.Join("; ", errors));
    }


}
