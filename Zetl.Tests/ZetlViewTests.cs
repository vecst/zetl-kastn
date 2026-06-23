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

    public static void MarkdownInlineFormattingRendersToHtml()
    {
        AssertEqual("<strong>bold</strong>", ZetlMarkdown.InlinesToHtml("**bold**"), "Bold.");
        AssertEqual("<em>it</em>", ZetlMarkdown.InlinesToHtml("*it*"), "Italic.");
        AssertEqual("<del>no</del>", ZetlMarkdown.InlinesToHtml("~~no~~"), "Strikethrough.");
        AssertEqual(
            "<code>x &lt; y</code>",
            ZetlMarkdown.InlinesToHtml("`x < y`"),
            "Inline code is literal and escaped.");
        AssertEqual(
            "<a href=\"https://e.com\">site</a>",
            ZetlMarkdown.InlinesToHtml("[site](https://e.com)"),
            "Link with text and URL.");
        AssertEqual(
            "<strong>a <em>b</em> c</strong>",
            ZetlMarkdown.InlinesToHtml("**a *b* c**"),
            "Bold wrapping italic should nest.");

        // Unmatched delimiters stay literal (and escaped), so plain text is safe.
        AssertEqual("a * b &amp; c", ZetlMarkdown.InlinesToHtml("a * b & c"), "Unmatched star is literal.");
        AssertEqual("2 ** 3", ZetlMarkdown.InlinesToHtml("2 ** 3"), "Unmatched double star is literal.");

        // The HTML view applies inline formatting inside its list items.
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [Slip("b1", "see **this** and [x](http://h)")]);
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(html, "<li>see <strong>this</strong> and <a href=\"http://h\">x</a></li>");
    }

    public static void MarkdownBlockListsRenderToHtml()
    {
        AssertEqual(
            "<ul><li>one</li><li>two</li></ul>",
            ZetlMarkdown.BlocksToHtml("- one\n- two"),
            "Bullet list.");
        AssertEqual(
            "<ol><li>a</li><li>b</li></ol>",
            ZetlMarkdown.BlocksToHtml("1. a\n2. b"),
            "Ordered list.");
        AssertEqual(
            "<ul style=\"list-style:none;padding-left:1.1em\"><li>☐ todo</li><li>☑ done</li></ul>",
            ZetlMarkdown.BlocksToHtml("- [ ] todo\n- [x] done"),
            "Task list with checkbox glyphs.");
        AssertEqual(
            "intro <strong>x</strong><ul><li>item</li></ul>",
            ZetlMarkdown.BlocksToHtml("intro **x**\n- item"),
            "A paragraph then a list, carrying inline formatting.");

        // A plain (no-list) slip stays one paragraph with a line break — unchanged.
        AssertEqual("a<br />b", ZetlMarkdown.BlocksToHtml("a\nb"), "Plain text stays a paragraph.");
    }

    public static void ViewListStyleAndHeadingNumbersRender()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas"), Bucket("b2", "Steps", parent: "b1")],
            [Slip("b1", "first"), Slip("b2", "second")]);

        // Ordered list style + cascading numbered headings (HTML).
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument
            {
                Id = "v",
                Name = "V",
                Kind = ZetlViewKinds.Html,
                ListStyle = ZetlViewListStyles.Ordered,
                NumberHeadings = true
            });
        AssertContains(html, "<h2>1 Ideas</h2>");
        AssertContains(html, "<ol>");
        AssertContains(html, "<li>first</li>");
        AssertContains(html, "<h3>1.1 Steps</h3>");
        AssertContains(html, "<li>second</li>");

        // Task style: checkbox glyphs in an unmarked list.
        var taskHtml = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "t", Name = "T", Kind = ZetlViewKinds.Html, ListStyle = ZetlViewListStyles.Task });
        AssertContains(taskHtml, "<ul style=\"list-style:none;padding-left:1.1em\">");
        AssertContains(taskHtml, "<li>☐ first</li>");

        // Paragraph style: no list wrapper at all.
        var paragraphHtml = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "p", Name = "P", Kind = ZetlViewKinds.Html, ListStyle = ZetlViewListStyles.Paragraph });
        AssertContains(paragraphHtml, "<div>first</div>");
        AssertTrue(
            !paragraphHtml.Contains("<ul", StringComparison.Ordinal)
                && !paragraphHtml.Contains("<ol>", StringComparison.Ordinal),
            "Paragraph style should emit no list element.");

        // Markdown ordered list + numbered headings.
        var markdown = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument
            {
                Id = "m",
                Name = "M",
                Kind = ZetlViewKinds.Markdown,
                ListStyle = ZetlViewListStyles.Ordered,
                NumberHeadings = true
            }).ReplaceLineEndings("\n");
        AssertEqual(
            "# Demo\n\n## 1 Ideas\n\n1. first\n\n### 1.1 Steps\n\n1. second",
            markdown,
            "Markdown ordered list with cascading numbered headings.");
    }

    public static void MarkdownPreservesSlipOwnListMarkup()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Tasks")],
            [Slip("b1", "- [ ] todo"), Slip("b1", "- [x] done"), Slip("b1", "plain item")]);

        // Default bullet view: a slip that is already a GFM task item is emitted
        // verbatim (so it stays interactive), not nested under a second "- " marker
        // ("- - [ ] todo"); a plain slip still gets the bucket bullet.
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Tasks\n\n- [ ] todo\n- [x] done\n- plain item");

        // A Task-style view also must not double-mark an existing checkbox slip;
        // only the plain slip gets the task marker.
        var taskOutput = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument
            {
                Id = "t",
                Name = "T",
                Kind = ZetlViewKinds.Markdown,
                ListStyle = ZetlViewListStyles.Task
            }).ReplaceLineEndings("\n");
        AssertEqual(
            "# Demo\n\n## Tasks\n\n- [ ] todo\n- [x] done\n- [ ] plain item",
            taskOutput,
            "Task view should keep a checkbox slip's own markup and only mark plain slips.");
    }

    public static void DocumentTitleHidesOrOverridesProjectName()
    {
        var project = Project("Demo", [Bucket("b1", "Ideas")], [Slip("b1", "one")]);

        // Default: the project name is the document title.
        AssertRender(project, ZetlViewKinds.Markdown, "# Demo\n\n## Ideas\n\n- one");

        // A non-empty Title overrides the project name.
        AssertEqual(
            "# My Report\n\n## Ideas\n\n- one",
            ZetlViewRenderer.Render(
                project,
                project.Slips,
                new ZetlViewDocument { Id = "c", Name = "C", Kind = ZetlViewKinds.Markdown, Title = "My Report" })
                .ReplaceLineEndings("\n"),
            "A custom title should override the project name.");

        // ShowTitle=false omits the heading entirely.
        AssertEqual(
            "## Ideas\n\n- one",
            ZetlViewRenderer.Render(
                project,
                project.Slips,
                new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Markdown, ShowTitle = false })
                .ReplaceLineEndings("\n"),
            "Hiding the title should omit the document heading.");

        // HTML: custom <h1>, and no <h1> when hidden (the tab <title> still falls
        // back to the project name).
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "html", Name = "H", Kind = ZetlViewKinds.Html, Title = "My Report" });
        AssertContains(html, "<h1>My Report</h1>");
        var htmlHidden = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "hh", Name = "H", Kind = ZetlViewKinds.Html, ShowTitle = false });
        AssertTrue(
            !htmlHidden.Contains("<h1>", StringComparison.Ordinal),
            "A hidden title should omit the HTML heading.");
        AssertContains(htmlHidden, "<title>Demo</title>");
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

    public static void ProjectScopedViewsPersistAndStayIsolated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ZetlProjectViewTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var statePath = Path.Combine(dir, "state.json");
        try
        {
            var service = new ZetlProjectService(new ZetlStateStore(statePath, "project-view-tests"));
            var first = service.Execute(ZetlCommandEnvelope.Create(
                    "create-first",
                    ZetlCommandKind.CreateProject,
                    new CreateProjectCommand { Name = "First" }))
                .Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("First project was not created.");
            var second = service.Execute(ZetlCommandEnvelope.Create(
                    "create-second",
                    ZetlCommandKind.CreateProject,
                    new CreateProjectCommand { Name = "Second" }))
                .Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("Second project was not created.");

            var view = new ZetlProjectViewSnapshot
            {
                Id = "first-report",
                Name = "First report",
                Kind = "Markdown",
                Sections =
                [
                    new ZetlProjectViewSectionSnapshot
                    {
                        Title = "Report",
                        Buckets = ["Inbox"]
                    }
                ],
                NumberHeadings = true
            };
            var savedResponse = service.Execute(ZetlCommandEnvelope.Create(
                "save-view",
                ZetlCommandKind.SaveProjectView,
                new SaveProjectViewCommand { View = view },
                first.Id,
                expectedTargetRevision: first.MetadataRevision));
            AssertEqual(ZetlResponseStatus.Success, savedResponse.Status, "Project view save should succeed.");
            var saved = savedResponse.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("SaveProjectView returned no snapshot.");
            AssertEqual(1, saved.Views.Count, "The owning project should expose its structured view.");
            AssertEqual("first-report", saved.Views[0].Id, "The view id should round-trip through the snapshot.");

            var stale = service.Execute(ZetlCommandEnvelope.Create(
                "save-view-stale",
                ZetlCommandKind.SaveProjectView,
                new SaveProjectViewCommand { View = view },
                first.Id,
                expectedTargetRevision: first.MetadataRevision));
            AssertEqual(ZetlResponseStatus.Conflict, stale.Status, "A stale project-view save should conflict.");

            var other = service.Execute(new ZetlCommandEnvelope
            {
                CommandId = "get-second",
                Kind = ZetlCommandKind.GetProject,
                ProjectId = second.Id
            }).Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("Second project could not be fetched.");
            AssertEqual(0, other.Views.Count, "A project view must not leak into another project.");

            var defaultResponse = service.Execute(ZetlCommandEnvelope.Create(
                "set-project-view",
                ZetlCommandKind.SetProjectView,
                new SetProjectViewCommand { ViewId = view.Id },
                first.Id,
                expectedTargetRevision: saved.MetadataRevision));
            var withDefault = defaultResponse.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("SetProjectView returned no snapshot.");

            var reloaded = new ZetlProjectService(new ZetlStateStore(statePath, "project-view-tests"));
            var fetched = reloaded.Execute(new ZetlCommandEnvelope
            {
                CommandId = "get-reloaded",
                Kind = ZetlCommandKind.GetProject,
                ProjectId = first.Id
            }).Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("Reloaded project could not be fetched.");
            AssertEqual(1, fetched.Views.Count, "The project view should persist in project.json.");
            AssertEqual(view.Id, fetched.DefaultViewId, "The project-scoped default should persist.");

            var deletedResponse = reloaded.Execute(ZetlCommandEnvelope.Create(
                "delete-project-view",
                ZetlCommandKind.DeleteProjectView,
                new DeleteProjectViewCommand { ViewId = view.Id },
                first.Id,
                expectedTargetRevision: withDefault.MetadataRevision));
            var deleted = deletedResponse.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("DeleteProjectView returned no snapshot.");
            AssertEqual(0, deleted.Views.Count, "Deleting should remove the project view.");
            AssertEqual<string?>(null, deleted.DefaultViewId, "Deleting the default view should clear the pointer.");
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
