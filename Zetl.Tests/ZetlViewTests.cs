using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace ZETL.Tests;

internal static class ZetlViewTests
{
    public static void BuiltInViewsAreValidAndRoundTrip()
    {
        var builtIns = ZetlViewDefaults.CreateAll();
        AssertTrue(builtIns.Count >= 5, "The catalog should ship the built-in views.");
        foreach (var view in builtIns)
        {
            AssertTrue(
                ZetlViewValidator.Validate(view).Count == 0,
                $"Built-in view '{view.Id}' should be well-formed.");

            var json = JsonSerializer.Serialize(view, JsonFile.Options);
            var restored = JsonSerializer.Deserialize<ZetlViewDocument>(json, JsonFile.Options)
                ?? throw new InvalidOperationException($"'{view.Id}' did not round-trip.");
            AssertEqual(view.Id, restored.Id, "Round-trip should preserve the view id.");
            AssertEqual(view.Kind, restored.Kind, "Round-trip should preserve the view kind.");
        }

        var markdownJson = JsonSerializer.Serialize(
            ZetlViewDefaults.FindBuiltIn("markdown"), JsonFile.Options);
        AssertTrue(
            markdownJson.Contains("\"kind\": \"Markdown\""),
            "View kind should serialize as a readable word.");
    }

    public static void RendererOmitsSlipsExcludedFromViews()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas"), Bucket("b2", "Drafts")],
            [
                Slip("b1", "kept idea"),
                Slip("b1", "hidden idea") with { ExcludedFromViews = true },
                Slip("b2", "hidden draft") with { ExcludedFromViews = true },
            ]);

        // The excluded idea drops out; the Drafts bucket, left with only an
        // excluded slip, is omitted entirely like any empty group.
        AssertRender(project, ZetlViewKinds.Formatted, "Demo\n\nIdeas\n\tkept idea");
        AssertRender(project, ZetlViewKinds.Plain, "kept idea");
    }

    public static void InvalidViewsReportErrors()
    {
        AssertTrue(ZetlViewValidator.Validate(null).Count > 0, "Null view is invalid.");

        var badKind = new ZetlViewDocument { Id = "x", Name = "X", Kind = "DOCX" };
        AssertTrue(
            ZetlViewValidator.Validate(badKind).Any(error => error.Contains("kind")),
            "An unknown kind should be rejected.");

        var badRow = new ZetlViewDocument { Id = "x", Name = "X", Kind = ZetlViewKinds.Tsv, TsvRowLength = 0 };
        AssertTrue(
            ZetlViewValidator.Validate(badRow).Any(error => error.Contains("row length")),
            "A non-positive TSV row length should be rejected.");
    }

    public static void RendererFormatsNestedBucketsAndSlips()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas"), Bucket("b2", "Steps", parent: "b1")],
            [Slip("b1", "first idea"), Slip("b1", "second idea"), Slip("b2", "do this")]);

        AssertRender(
            project,
            ZetlViewKinds.Formatted,
            "Demo\n\nIdeas\n\tfirst idea\n\tsecond idea\n\n\tSteps\n\t\tdo this");

        AssertRender(
            project,
            ZetlViewKinds.Plain,
            "first idea\nsecond idea\ndo this");

        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Ideas\n\n- first idea\n- second idea\n\n### Steps\n\n- do this");
    }

    public static void RendererBuildsTsvRowsUsingBucketHeaders()
    {
        var project = Project(
            "Cat",
            [Bucket("b1", "Rows", startingText: "Name\nNumber")],
            [Slip("b1", "x"), Slip("b1", "1"), Slip("b1", "y"), Slip("b1", "2")]);

        AssertRender(
            project,
            ZetlViewKinds.Tsv,
            "Cat\nRows\nName\tNumber\nx\t1\ny\t2");
    }

    public static void StoreLoadsSavesAndDeletesUserViews()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ZetlViewTests", Guid.NewGuid().ToString("N"));
        try
        {
            var logs = new List<string>();
            var store = new ZetlViewStore(dir, logs.Add);

            AssertEqual(
                ZetlViewDefaults.CreateAll().Count,
                store.LoadAll().Count,
                "With no directory the catalog is exactly the built-ins.");

            var mine = new ZetlViewDocument
            {
                Id = ZetlViewDefaults.CreateId("My View"),
                Name = "My View",
                Kind = ZetlViewKinds.Markdown
            };
            store.Save(mine);
            AssertTrue(
                store.LoadAll().Any(view => view.Id == mine.Id),
                "A saved user view should load back.");

            var collision = new ZetlViewDocument { Id = "markdown", Name = "Fake", Kind = ZetlViewKinds.Markdown };
            AssertThrows<InvalidOperationException>(
                () => store.Save(collision),
                "Saving over a built-in id should be refused.");

            var invalid = new ZetlViewDocument
            {
                Id = ZetlViewDefaults.CreateId("bad"),
                Name = "Bad",
                Kind = "DOCX"
            };
            AssertThrows<InvalidDataException>(
                () => store.Save(invalid),
                "Saving an invalid view should be refused.");

            store.Delete(mine.Id);
            AssertTrue(
                store.LoadAll().All(view => view.Id != mine.Id),
                "Delete should remove a user view.");
            AssertThrows<InvalidOperationException>(
                () => store.Delete("markdown"),
                "Deleting a built-in should be refused.");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    public static void RendererBuildsEscapedHtml()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas"), Bucket("b2", "Steps", parent: "b1")],
            [Slip("b1", "first <b>idea</b> & more"), Slip("b2", "do this")]);

        var view = new ZetlViewDocument { Id = "v", Name = "V", Kind = ZetlViewKinds.Html };
        var html = ZetlViewRenderer.Render(project, project.Slips, view);

        AssertContains(html, "<!DOCTYPE html>");
        AssertContains(html, "<h1>Demo</h1>");
        AssertContains(html, "<h2>Ideas</h2>");
        AssertContains(html, "<h3>Steps</h3>");
        AssertContains(html, "<li>first &lt;b&gt;idea&lt;/b&gt; &amp; more</li>");
        AssertContains(html, "<li>do this</li>");
    }

    public static void RendererHonorsSlipAlignment()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [
                Slip("b1", "left one"),
                Slip("b1", "middle one") with { Align = "center" },
                Slip("b1", "right one") with { Align = "right" },
            ]);

        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(html, "<li>left one</li>");
        AssertContains(html, "<li style=\"text-align:center\">middle one</li>");
        AssertContains(html, "<li style=\"text-align:right\">right one</li>");

        // Markdown has no alignment, so the list renders plain regardless of Align.
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Ideas\n\n- left one\n- middle one\n- right one");
    }

    private static void AssertContains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected output to contain '{expected}'.");
        }
    }

    public static void ProjectRemembersDefaultViewAcrossReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ZetlViewTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.json");
        try
        {
            var store = new ZetlStateStore(statePath, "view-tests");
            var service = new ZetlProjectService(store);
            var created = service.Execute(ZetlCommandEnvelope.Create(
                    "create",
                    ZetlCommandKind.CreateProject,
                    new CreateProjectCommand { Name = "Doc" }))
                .Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("CreateProject returned no snapshot.");

            var response = service.Execute(ZetlCommandEnvelope.Create(
                "set-view",
                ZetlCommandKind.SetProjectView,
                new SetProjectViewCommand { ViewId = "markdown" },
                created.Id,
                expectedTargetRevision: created.MetadataRevision));
            AssertEqual(ZetlResponseStatus.Success, response.Status, "Setting the default view should succeed.");
            var updated = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("SetProjectView returned no snapshot.");
            AssertEqual("markdown", updated.DefaultViewId, "The snapshot should carry the default view.");

            // A stale revision is rejected.
            var stale = service.Execute(ZetlCommandEnvelope.Create(
                "set-view-stale",
                ZetlCommandKind.SetProjectView,
                new SetProjectViewCommand { ViewId = "plain" },
                created.Id,
                expectedTargetRevision: created.MetadataRevision));
            AssertEqual(ZetlResponseStatus.Conflict, stale.Status, "A stale default-view edit should conflict.");

            // Reload from disk: the default view persisted.
            var reloaded = new ZetlProjectService(new ZetlStateStore(statePath, "view-tests"));
            var fetched = reloaded.Execute(new ZetlCommandEnvelope
            {
                CommandId = "get",
                Kind = ZetlCommandKind.GetProject,
                ProjectId = created.Id
            }).Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("GetProject returned no snapshot.");
            AssertEqual("markdown", fetched.DefaultViewId, "The default view should persist across reload.");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    public static void CreationTypeStoreLoadsSavesAndDeletes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ZetlCreationTypeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ZetlCreationTypeStore(dir);
            AssertTrue(
                store.LoadAll().Any(creation => creation.Id == "research-report"),
                "The built-in creation type should be present.");

            var mine = new ZetlCreationTypeDocument
            {
                Id = ZetlCreationTypeDefaults.CreateId("Recipe"),
                Name = "Recipe",
                TemplateId = "blank",
                ViewIds = ["markdown"]
            };
            store.Save(mine);
            AssertTrue(
                store.LoadAll().Any(creation => creation.Id == mine.Id),
                "A saved creation type should load back.");

            var collision = new ZetlCreationTypeDocument
            {
                Id = "research-report",
                Name = "Fake",
                TemplateId = "blank"
            };
            AssertThrows<InvalidOperationException>(
                () => store.Save(collision),
                "Saving over a built-in id should be refused.");

            var invalid = new ZetlCreationTypeDocument
            {
                Id = ZetlCreationTypeDefaults.CreateId("bad"),
                Name = "Bad",
                TemplateId = ""
            };
            AssertThrows<InvalidDataException>(
                () => store.Save(invalid),
                "A creation type without a template should be refused.");

            store.Delete(mine.Id);
            AssertTrue(
                store.LoadAll().All(creation => creation.Id != mine.Id),
                "Delete should remove a user creation type.");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    public static void SectionsRenameReorderAndOmitBuckets()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ingredients"), Bucket("b2", "Steps"), Bucket("b3", "Notes")],
            [Slip("b1", "eggs"), Slip("b2", "mix"), Slip("b3", "secret")]);

        var view = new ZetlViewDocument
        {
            Id = "recipe",
            Name = "Recipe",
            Kind = ZetlViewKinds.Markdown,
            // Reorder (Steps first), rename (Steps -> Method), and omit Notes.
            Sections =
            [
                new ZetlViewSection { Title = "Method", Buckets = ["Steps"] },
                new ZetlViewSection { Title = "What you need", Buckets = ["Ingredients"] }
            ]
        };

        var markdown = ZetlViewRenderer.Render(project, project.Slips, view).ReplaceLineEndings("\n");
        AssertEqual(
            "# Demo\n\n## Method\n\n- mix\n\n## What you need\n\n- eggs",
            markdown,
            "Sections should rename, reorder, and omit buckets.");
    }

    public static void RendererEmbedsPictures()
    {
        var picture = PictureSlip("b1", "Diagram", "picture-1", "hash-1");
        var project = Project("Demo", [Bucket("b1", "Ideas")], [picture]);
        var content = new ZetlPictureContent
        {
            SlipId = picture.Id,
            Sha256 = "hash-1",
            Width = 1,
            Height = 1,
            Bytes = [1, 2, 3]
        };
        var pictures = new Dictionary<string, ZetlPictureContent> { [picture.Id] = content };

        var markdown = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "md", Name = "Markdown", Kind = ZetlViewKinds.Markdown },
            pictures);
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "html", Name = "HTML", Kind = ZetlViewKinds.Html },
            pictures);
        var formatted = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "text", Name = "Text", Kind = ZetlViewKinds.Formatted });

        AssertTrue(
            markdown.Contains("![Diagram](data:image/png;base64,AQID)", StringComparison.Ordinal),
            "Markdown should embed picture bytes as a self-contained data URI.");
        AssertTrue(
            html.Contains("<img src=\"data:image/png;base64,AQID\" alt=\"Diagram\"", StringComparison.Ordinal),
            "HTML should embed picture bytes as a self-contained image.");
        AssertTrue(
            formatted.Contains("[Picture: Diagram]", StringComparison.Ordinal),
            "Text-only views should retain a readable picture marker.");
    }

    public static void PdfRendererProducesAPdfDocument()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [Slip("b1", "first idea"), Slip("b1", "second idea")]);

        var pdf = KASTN.KastnPdfRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "v", Name = "V", Kind = ZetlViewKinds.Pdf });

        AssertTrue(pdf.Length > 0, "PDF render should produce bytes.");
        var header = System.Text.Encoding.ASCII.GetString(pdf, 0, 5);
        AssertEqual("%PDF-", header, "Output should be a PDF document.");
    }

    public static void PdfRendererEmbedsPictures()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var picture = PictureSlip("b1", "Diagram", "picture-pdf", "hash-pdf");
        var project = Project("Demo", [Bucket("b1", "Ideas")], [picture]);
        var pdf = KASTN.KastnPdfRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "v", Name = "V", Kind = ZetlViewKinds.Pdf },
            new Dictionary<string, ZetlPictureContent>
            {
                [picture.Id] = new ZetlPictureContent
                {
                    SlipId = picture.Id,
                    Sha256 = "hash-pdf",
                    Width = 1,
                    Height = 1,
                    Bytes = png
                }
            });

        var pdfText = System.Text.Encoding.ASCII.GetString(pdf);
        AssertTrue(pdfText.StartsWith("%PDF-", StringComparison.Ordinal), "Picture PDF should be valid.");
        AssertTrue(
            pdfText.Contains("/Subtype/Image", StringComparison.Ordinal)
                || pdfText.Contains("/Subtype /Image", StringComparison.Ordinal),
            "Picture PDF should contain an embedded image object.");
    }

    private static void AssertRender(ZetlProjectSnapshot project, string kind, string expected)
    {
        var view = new ZetlViewDocument { Id = "v", Name = "V", Kind = kind };
        var actual = ZetlViewRenderer.Render(project, project.Slips, view).ReplaceLineEndings("\n");
        AssertEqual(expected, actual, $"{kind} render mismatch.");
    }

    private static ZetlProjectSnapshot Project(
        string name,
        IReadOnlyList<ZetlBucketSnapshot> buckets,
        IReadOnlyList<ZetlSlipSnapshot> slips) => new()
    {
        Id = "p",
        Name = name,
        MetadataRevision = 1,
        ChangeSequence = 1,
        Buckets = buckets,
        Slips = slips
    };

    private static ZetlBucketSnapshot Bucket(
        string id,
        string name,
        string? parent = null,
        string startingText = "") => new()
    {
        Id = id,
        Revision = 1,
        Name = name,
        ParentBucketId = parent,
        Settings = new ZetlBucketSettings { DefaultStartingText = startingText }
    };

    private static ZetlSlipSnapshot Slip(string bucketId, string text) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Revision = 1,
        Type = ZetlSlipType.Text,
        BucketId = bucketId,
        Text = text,
        Source = "copy",
        CapturedAtUtc = DateTimeOffset.UnixEpoch
    };

    private static ZetlSlipSnapshot PictureSlip(
        string bucketId,
        string caption,
        string id,
        string hash) => new()
    {
        Id = id,
        Revision = 1,
        Type = ZetlSlipType.Picture,
        BucketId = bucketId,
        Text = caption,
        Picture = new ZetlPictureSnapshot
        {
            MimeType = "image/png",
            Width = 1,
            Height = 1,
            ByteLength = 3,
            Sha256 = hash
        },
        Source = "copy",
        CapturedAtUtc = DateTimeOffset.UnixEpoch
    };

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
}
