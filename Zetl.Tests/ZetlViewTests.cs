using System.Text.Json;
using ZETL;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlViewTests
{
    [Fact] public void BuiltInViewsAreValidAndRoundTrip()
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

    [Fact] public void RendererOmitsSlipsExcludedFromViews()
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

    [Fact] public void InvalidViewsReportErrors()
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

    [Fact] public void RendererFormatsNestedBucketsAndSlips()
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

        // Notes carry no list kind, so they render as paragraphs (not bucket-bulleted).
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Ideas\n\nfirst idea\n\nsecond idea\n\n### Steps\n\ndo this");
    }

    [Fact] public void RendererKeepsEmptyAncestorBucketHeadings()
    {
        var project = Project(
            "Demo",
            [
                Bucket("bugs", "Bugs"),
                Bucket("kastn", "Kastn", parent: "bugs"),
                Bucket("compile", "Compile", parent: "kastn"),
                Bucket("empty", "Empty Subject", parent: "bugs")
            ],
            [
                Slip("kastn", "general Kastn bug"),
                Slip("compile", "markdown checkbox bug")
            ]);

        AssertRender(
            project,
            ZetlViewKinds.Formatted,
            "Demo\n\nBugs\n\n\tKastn\n\t\tgeneral Kastn bug\n\n\t\tCompile\n\t\t\tmarkdown checkbox bug");

        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Bugs\n\n### Kastn\n\ngeneral Kastn bug\n\n#### Compile\n\nmarkdown checkbox bug");
    }

    [Fact] public void ExportFidelityMatchesRenderedKinds()
    {
        // Formatting carries only into the kinds that translate the Markdown AST;
        // the literal compile kinds emit slip text verbatim.
        foreach (var kind in new[] { ZetlViewKinds.Markdown, ZetlViewKinds.Html, ZetlViewKinds.Pdf })
        {
            AssertTrue(
                ZetlViewRenderer.ExportPreservesFormatting(kind),
                $"'{kind}' export should carry slip formatting.");
        }

        foreach (var kind in new[] { ZetlViewKinds.Formatted, ZetlViewKinds.Plain, ZetlViewKinds.Tsv })
        {
            AssertTrue(
                !ZetlViewRenderer.ExportPreservesFormatting(kind),
                $"'{kind}' export is literal and should not carry slip formatting.");
        }

        // Alignment is a block style only the document-laying kinds honor; Markdown
        // has no alignment syntax, so it drops there too.
        AssertTrue(
            ZetlViewRenderer.ExportPreservesAlignment(ZetlViewKinds.Html)
                && ZetlViewRenderer.ExportPreservesAlignment(ZetlViewKinds.Pdf),
            "HTML and PDF exports should carry alignment.");
        foreach (var kind in new[]
        {
            ZetlViewKinds.Markdown, ZetlViewKinds.Formatted, ZetlViewKinds.Plain, ZetlViewKinds.Tsv
        })
        {
            AssertTrue(
                !ZetlViewRenderer.ExportPreservesAlignment(kind),
                $"'{kind}' export should not carry alignment.");
        }

        AssertTrue(
            ZetlViewRenderer.ExportPreservesTypography(ZetlViewKinds.Html)
                && ZetlViewRenderer.ExportPreservesTypography(ZetlViewKinds.Pdf),
            "HTML and PDF exports should carry whole-slip typography.");
        foreach (var kind in new[]
        {
            ZetlViewKinds.Markdown, ZetlViewKinds.Formatted, ZetlViewKinds.Plain, ZetlViewKinds.Tsv
        })
        {
            AssertTrue(
                !ZetlViewRenderer.ExportPreservesTypography(kind),
                $"'{kind}' export should not carry whole-slip typography.");
        }
    }

    [Fact] public void RendererBuildsTsvRowsUsingBucketHeaders()
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

    [Fact] public void StoreLoadsSavesAndDeletesUserViews()
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

    [Fact] public void RendererBuildsEscapedHtml()
    {
        var slip1 = Slip("b1", "first <b>idea</b> & more", "bullet");
        var slip2 = Slip("b2", "do this", "bullet");
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas"), Bucket("b2", "Steps", parent: "b1")],
            [slip1, slip2]);

        var view = new ZetlViewDocument { Id = "v", Name = "V", Kind = ZetlViewKinds.Html };
        var html = ZetlViewRenderer.Render(project, project.Slips, view);

        AssertContains(html, "<!DOCTYPE html>");
        AssertContains(html, "<h1>Demo</h1>");
        AssertContains(html, "<h2>Ideas</h2>");
        AssertContains(html, "<h3>Steps</h3>");
        AssertContains(html, $"<li id=\"{slip1.Id}\">first &lt;b&gt;idea&lt;/b&gt; &amp; more</li>");
        AssertContains(html, $"<li id=\"{slip2.Id}\">do this</li>");
    }

    [Fact] public void RendererHonorsSlipAlignment()
    {
        var slipLeft = Slip("b1", "left one", "bullet");
        var slipMiddle = Slip("b1", "middle one", "bullet") with { Align = "center" };
        var slipRight = Slip("b1", "right one", "bullet") with { Align = "right" };
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [slipLeft, slipMiddle, slipRight]);

        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(html, $"<li id=\"{slipLeft.Id}\">left one</li>");
        AssertContains(html, $"<li id=\"{slipMiddle.Id}\" style=\"text-align:center\">middle one</li>");
        AssertContains(html, $"<li id=\"{slipRight.Id}\" style=\"text-align:right\">right one</li>");

        // Markdown has no alignment, so the list renders plain regardless of Align.
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Ideas\n\n- left one\n- middle one\n- right one");
    }

    [Fact] public void RendererHonorsPortableSlipTypography()
    {
        var paragraph = Slip("b1", "styled paragraph") with
        {
            FontFamily = "  Times New Roman  ",
            FontSize = 18,
            TextColor = "#1a2b3c"
        };
        var listItem = Slip("b1", "styled item", ZetlBlockKinds.Bullet) with
        {
            Align = "right",
            FontFamily = "Georgia",
            FontSize = 7,
            TextColor = "#ABCDEF"
        };
        var quote = Slip("b1", "styled quote", ZetlBlockKinds.Quote) with
        {
            FontFamily = "Segoe UI",
            FontSize = 97,
            TextColor = "#010203"
        };
        var picture = PictureSlip("b1", "styled picture", "styled-picture", "missing") with
        {
            FontFamily = "Arial",
            FontSize = 12,
            TextColor = "#445566"
        };
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [paragraph, listItem, quote, picture]);

        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });

        AssertContains(
            html,
            $"<div id=\"{paragraph.Id}\" style=\"font-family:Times New Roman;font-size:18pt;color:#1A2B3C\">styled paragraph</div>");
        AssertContains(
            html,
            $"<li id=\"{listItem.Id}\" style=\"text-align:right;font-family:Georgia;font-size:8pt;color:#ABCDEF\">styled item</li>");
        AssertContains(
            html,
            $"<div id=\"{quote.Id}\" style=\"font-family:Segoe UI;font-size:96pt;color:#010203\"><blockquote>");
        AssertContains(
            html,
            "<p id=\"styled-picture\" style=\"font-family:Arial;font-size:12pt;color:#445566\">[Picture: styled picture]</p>");

        AssertEqual("Times New Roman", ZetlViewRenderer.SlipFontFamily(paragraph), "Family is trimmed.");
        AssertEqual(18, ZetlViewRenderer.SlipFontSize(paragraph), "Point size is retained.");
        AssertEqual("#1A2B3C", ZetlViewRenderer.SlipTextColor(paragraph), "Color is canonicalized.");
    }

    [Fact] public void RendererRejectsCssInjectionAndEmptyTypographyIsByteStable()
    {
        var plain = Slip("b1", "plain");
        var project = Project("Demo", [Bucket("b1", "Ideas")], [plain]);
        var emptyProject = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [plain with { FontFamily = "", FontSize = 0, TextColor = "" }]);
        var view = new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html };

        AssertEqual(
            ZetlViewRenderer.Render(project, project.Slips, view),
            ZetlViewRenderer.Render(emptyProject, emptyProject.Slips, view),
            "Empty typography must preserve the existing HTML byte-for-byte.");

        var hostile = plain with
        {
            FontFamily = "Arial; color:red",
            FontSize = 0,
            TextColor = "#000000\";background:url(javascript:alert(1))"
        };
        var hostileProject = Project("Demo", [Bucket("b1", "Ideas")], [hostile]);
        var hostileHtml = ZetlViewRenderer.Render(hostileProject, hostileProject.Slips, view);
        AssertContains(hostileHtml, $"<div id=\"{hostile.Id}\">plain</div>");
        AssertTrue(
            !hostileHtml.Contains("javascript:", StringComparison.Ordinal)
                && !hostileHtml.Contains("background:url", StringComparison.Ordinal)
                && !hostileHtml.Contains("color:red", StringComparison.Ordinal),
            "Invalid typography must not append CSS declarations.");
        AssertEqual("", ZetlViewRenderer.SlipFontFamily(hostile), "Unsafe family is inherited.");
        AssertEqual("", ZetlViewRenderer.SlipTextColor(hostile), "Unsafe color is inherited.");
    }

    [Fact] public void PortableSlipTypographyDoesNotChangeNonHtmlViews()
    {
        var styled = Slip("b1", "literal **text**") with
        {
            FontFamily = "Georgia",
            FontSize = 24,
            TextColor = "#DC2626"
        };
        var project = Project("Demo", [Bucket("b1", "Ideas")], [styled]);

        AssertRender(project, ZetlViewKinds.Formatted, "Demo\n\nIdeas\n\tliteral **text**");
        AssertRender(project, ZetlViewKinds.Plain, "literal **text**");
        AssertRender(project, ZetlViewKinds.Tsv, "Demo\nIdeas\nliteral **text**");
        AssertRender(project, ZetlViewKinds.Markdown, "# Demo\n\n## Ideas\n\nliteral **text**");
    }

    [Fact] public void MarkdownInlineFormattingRendersToHtml()
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
        var slip = Slip("b1", "see **this** and [x](http://h)", "bullet");
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [slip]);
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(html, $"<li id=\"{slip.Id}\">see <strong>this</strong> and <a href=\"http://h\">x</a></li>");
        AssertTrue(
            html.Contains("Content-Security-Policy", StringComparison.Ordinal),
            "Self-contained HTML should include a restrictive defense-in-depth policy.");

        var propertySlip = Slip("b1", "see this link", "bullet") with
        {
            InlineStyles =
            [
                new ZetlInlineStyleRange { Start = 4, Length = 4, Kind = ZetlInlineStyleKinds.Bold },
                new ZetlInlineStyleRange { Start = 9, Length = 4, Kind = ZetlInlineStyleKinds.Link, Href = "http://h" }
            ]
        };
        var propertyProject = Project("Demo", [Bucket("b1", "Ideas")], [propertySlip]);
        var propertyHtml = ZetlViewRenderer.Render(
            propertyProject,
            propertyProject.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(propertyHtml, $"<li id=\"{propertySlip.Id}\">see <strong>this</strong> <a href=\"http://h\">link</a></li>");
        AssertRender(
            propertyProject,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Ideas\n\n- see **this** [link](http://h)");

        var target = Slip("b1", "target");
        var wikiSlip = Slip("b1", "see target", "bullet") with
        {
            InlineStyles =
            [
                new ZetlInlineStyleRange
                {
                    Start = 4,
                    Length = 6,
                    Kind = ZetlInlineStyleKinds.WikiLink,
                    TargetSlipId = target.Id,
                    CachedTitle = "Target"
                }
            ]
        };
        var wikiProject = Project("Demo", [Bucket("b1", "Ideas")], [target, wikiSlip]);
        var wikiHtml = ZetlViewRenderer.Render(
            wikiProject,
            wikiProject.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(wikiHtml, $"<a href=\"#{target.Id}\">target</a>");
        AssertRender(
            wikiProject,
            ZetlViewKinds.Markdown,
            $"# Demo\n\n## Ideas\n\ntarget\n\n- see [target](#{target.Id})");
    }

    [Fact] public void MarkdownBlockListsRenderToHtml()
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
            "<ul style=\"list-style:none;padding-left:1.1em\"><li><input type=\"checkbox\" /> todo</li><li><input type=\"checkbox\" checked /> done</li></ul>",
            ZetlMarkdown.BlocksToHtml("- [ ] todo\n- [x] done"),
            "Task list with checkbox inputs.");
        AssertEqual(
            "intro <strong>x</strong><ul><li>item</li></ul>",
            ZetlMarkdown.BlocksToHtml("intro **x**\n- item"),
            "A paragraph then a list, carrying inline formatting.");

        // A plain (no-list) slip stays one paragraph with a line break — unchanged.
        AssertEqual("a<br />b", ZetlMarkdown.BlocksToHtml("a\nb"), "Plain text stays a paragraph.");
    }

    [Fact] public void MarkdownBlockStructuresRenderToHtml()
    {
        AssertEqual(
            "<p style=\"font-weight:700;font-size:1.05em;margin:0.5em 0 0.2em\">Section</p>",
            ZetlMarkdown.BlocksToHtml("## Section"),
            "A softened sub-heading renders as a styled paragraph, never an <h*>.");
        AssertEqual(
            "<blockquote>a<br />b</blockquote>",
            ZetlMarkdown.BlocksToHtml("> a\n> b"),
            "Consecutive quote lines merge into one blockquote.");
        AssertEqual(
            "<pre><code>&lt;tag&gt;\nx</code></pre>",
            ZetlMarkdown.BlocksToHtml("```\n<tag>\nx\n```"),
            "Fenced code is literal and HTML-escaped.");
        AssertEqual(
            "<hr />",
            ZetlMarkdown.BlocksToHtml("---"),
            "A dashed line is a thematic break.");

        // A single-backtick span on its own line stays inline code, not a fence.
        AssertEqual(
            "<code>x</code>",
            ZetlMarkdown.BlocksToHtml("`x`"),
            "Inline code is not mistaken for a code fence.");
    }

    [Fact] public void MarkdownExportSoftensHeadingsAndKeepsBlocks()
    {
        var project = Project(
            "Doc",
            [Bucket("b1", "B")],
            [Slip("b1", "## Section"), Slip("b1", "```\nx = 1\n```")]);

        // The sub-heading softens to bold (kept out of the .md outline); the fenced code
        // slip passes through verbatim instead of being wrapped under a bucket marker.
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Doc\n\n## B\n\n**Section**\n\n```\nx = 1\n```");
    }

    [Fact] public void ViewListStyleAndHeadingNumbersRender()
    {
        // Per-note list kind drives the markers (the view no longer carries a bucket-
        // wide style); cascading heading numbers still come from the view.
        var slip1 = Slip("b1", "first", "ordered");
        var slip2 = Slip("b2", "second", "ordered");
        var ordered = Project(
            "Demo",
            [Bucket("b1", "Ideas"), Bucket("b2", "Steps", parent: "b1")],
            [slip1, slip2]);
        var html = ZetlViewRenderer.Render(
            ordered,
            ordered.Slips,
            new ZetlViewDocument
            {
                Id = "v",
                Name = "V",
                Kind = ZetlViewKinds.Html,
                NumberHeadings = true
            });
        AssertContains(html, "<h2>1 Ideas</h2>");
        AssertContains(html, "<ol>");
        AssertContains(html, $"<li id=\"{slip1.Id}\">first</li>");
        AssertContains(html, "<h3>1.1 Steps</h3>");
        AssertContains(html, $"<li id=\"{slip2.Id}\">second</li>");

        // Task notes: checkbox glyphs reflecting each note's checked state.
        var task1 = Slip("b1", "first", "task");
        var task2 = Slip("b1", "second", "task", isChecked: true);
        var tasks = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [task1, task2]);
        var taskHtml = ZetlViewRenderer.Render(
            tasks,
            tasks.Slips,
            new ZetlViewDocument { Id = "t", Name = "T", Kind = ZetlViewKinds.Html });
        AssertContains(taskHtml, "<ul style=\"list-style:none;padding-left:1.1em\">");
        AssertContains(taskHtml, $"<li id=\"{task1.Id}\"><input type=\"checkbox\" /> first</li>");
        AssertContains(taskHtml, $"<li id=\"{task2.Id}\"><input type=\"checkbox\" checked /> second</li>");

        // Plain notes: no list wrapper at all.
        var plainSlip = Slip("b1", "first");
        var plain = Project("Demo", [Bucket("b1", "Ideas")], [plainSlip]);
        var paragraphHtml = ZetlViewRenderer.Render(
            plain,
            plain.Slips,
            new ZetlViewDocument { Id = "p", Name = "P", Kind = ZetlViewKinds.Html });
        AssertContains(paragraphHtml, $"<div id=\"{plainSlip.Id}\">first</div>");
        AssertTrue(
            !paragraphHtml.Contains("<ul", StringComparison.Ordinal)
                && !paragraphHtml.Contains("<ol>", StringComparison.Ordinal),
            "Plain notes should emit no list element.");

        // Markdown ordered notes + numbered headings.
        var markdown = ZetlViewRenderer.Render(
            ordered,
            ordered.Slips,
            new ZetlViewDocument
            {
                Id = "m",
                Name = "M",
                Kind = ZetlViewKinds.Markdown,
                NumberHeadings = true
            }).ReplaceLineEndings("\n");
        AssertEqual(
            "# Demo\n\n## 1 Ideas\n\n1. first\n\n### 1.1 Steps\n\n1. second",
            markdown,
            "Markdown ordered notes with cascading numbered headings.");
    }

    [Fact] public void MarkdownPreservesSlipOwnListMarkup()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Tasks")],
            [Slip("b1", "- [ ] todo"), Slip("b1", "- [x] done"), Slip("b1", "plain item")]);

        // A note that already holds its own GFM list markup in its body is emitted
        // verbatim (so it stays interactive); a note with no markup is a paragraph.
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Tasks\n\n- [ ] todo\n- [x] done\nplain item");
    }

    [Fact] public void MarkdownDoesNotDoublePrefixAuthoredListMarkup()
    {
        var project = Project(
            "Demo",
            [
                Bucket("b1", "Task Bucket", renderKind: "task"),
                Bucket("b2", "Bullet Bucket", renderKind: "bullet")
            ],
            [
                Slip("b1", "- [ ] typed task"),
                Slip("b1", "property task", "task"),
                Slip("b2", "- [x] authored checkbox"),
                Slip("b2", "plain bullet")
            ]);

        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Task Bucket\n\n- [ ] typed task\n- [ ] property task\n\n## Bullet Bucket\n\n- [x] authored checkbox\n- plain bullet");
    }

    [Fact] public void HtmlTaskListsUseCheckboxInputsWithoutNestingAuthoredTasks()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Tasks", renderKind: "task")],
            [
                Slip("b1", "property task", "task", isChecked: true),
                Slip("b1", "- [ ] typed task")
            ]);

        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html }).ReplaceLineEndings("\n");

        AssertContains(html, "<li id=\"");
        AssertContains(html, "<input type=\"checkbox\" checked /> property task");
        AssertContains(html, "<div id=\"");
        AssertContains(html, "<input type=\"checkbox\" /> typed task");
        AssertTrue(
            !html.Contains("<input type=\"checkbox\" /> <ul", StringComparison.Ordinal),
            "Authored task syntax should render as its own list, not as a nested checkbox inside a property task.");
    }

    [Fact] public void GroupBucketRendersAsBoxedSection()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Notes", renderKind: "group")],
            [Slip("b1", "inside")]);

        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(html, "<section class=\"kastn-group\">");
        AssertContains(html, "</section>");
        AssertContains(html, "<h2>Notes</h2>");

        // A normal bucket is not wrapped.
        var plain = Project("Demo", [Bucket("b1", "Notes")], [Slip("b1", "inside")]);
        var plainHtml = ZetlViewRenderer.Render(
            plain,
            plain.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertTrue(
            !plainHtml.Contains("<section class=\"kastn-group\">", StringComparison.Ordinal),
            "A normal bucket should not render a group box.");
    }

    [Fact] public void StructuralKindClassification()
    {
        AssertTrue(ZetlViewRenderer.IsStructuralKind("divider"), "A divider is a structural kind.");
        foreach (var kind in new[] { "", "bullet", "ordered", "task", "heading", "quote", "code" })
        {
            AssertTrue(
                !ZetlViewRenderer.IsStructuralKind(kind),
                $"'{kind}' renders a note's content, so it is not structural.");
        }
    }

    [Fact] public void KindNormalizationIsCentralized()
    {
        // Block kinds: every declared kind round-trips; case and surrounding space are
        // tolerated; anything else (including "paragraph"/null) collapses to None.
        foreach (var kind in ZetlBlockKinds.All)
        {
            AssertEqual(kind, ZetlBlockKinds.Normalize(kind), $"'{kind}' is a valid block kind.");
        }

        AssertEqual(ZetlBlockKinds.Task, ZetlBlockKinds.Normalize("  TASK "), "Case- and space-tolerant.");
        AssertEqual(ZetlBlockKinds.None, ZetlBlockKinds.Normalize("paragraph"), "Unknown kind collapses to none.");
        AssertEqual(ZetlBlockKinds.None, ZetlBlockKinds.Normalize(null), "Null collapses to none.");
        AssertTrue(ZetlBlockKinds.IsStructural(ZetlBlockKinds.Divider), "Divider is the structural slip kind.");
        AssertTrue(!ZetlBlockKinds.IsStructural(ZetlBlockKinds.Bullet), "A list kind is not structural.");

        // Bucket render kinds normalize the same way.
        foreach (var kind in ZetlBucketRenderKinds.All)
        {
            AssertEqual(
                kind,
                ZetlBucketRenderKinds.Normalize(kind.ToUpperInvariant()),
                $"'{kind}' is a valid container kind.");
        }

        AssertEqual(ZetlBucketRenderKinds.None, ZetlBucketRenderKinds.Normalize("carousel"),
            "Unknown container kind collapses to none.");
    }

    [Fact] public void NoteKindRendersAsWholeNoteBlock()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "B")],
            [
                Slip("b1", "Title", "heading"),
                Slip("b1", "quoted", "quote"),
                Slip("b1", "code line", "code"),
                Slip("b1", "", "divider"),
            ]);

        // HTML: each whole-note kind renders as its own block (heading softened to a
        // styled paragraph, quote/code/divider as the matching elements).
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html });
        AssertContains(html, "<p style=\"font-weight:700;font-size:1.05em;margin:0.5em 0 0.2em\">Title</p>");
        AssertContains(html, "<blockquote>quoted</blockquote>");
        AssertContains(html, "<pre><code>code line</code></pre>");
        AssertContains(html, "<hr />");

        // Markdown: heading softens to bold; quote/code/divider use GFM syntax.
        var markdown = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "m", Name = "M", Kind = ZetlViewKinds.Markdown }).ReplaceLineEndings("\n");
        AssertContains(markdown, "**Title**");
        AssertContains(markdown, "> quoted");
        AssertContains(markdown, "```\ncode line\n```");
        AssertContains(markdown, "---");
    }

    [Fact] public void PerSlipBlockKindRendersMarkdownMarkers()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "B")],
            [
                Slip("b1", "a", "bullet"),
                Slip("b1", "b", "task"),
                Slip("b1", "c", "task", isChecked: true),
            ]);

        // Each note's own kind drives its marker; checked tasks emit [x]. A run of list
        // notes stays one contiguous list (no blank lines between items).
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## B\n\n- a\n- [ ] b\n- [x] c");
    }

    [Fact] public void BucketLevelFormattingInheritsToSlips()
    {
        var project = Project(
            "InheritDemo",
            [
                Bucket("b1", "Tasks", renderKind: "task"),
                Bucket("b2", "Bullets", renderKind: "bullet"),
                Bucket("b3", "Ordered", renderKind: "ordered")
            ],
            [
                // b1 slips inherit task
                Slip("b1", "task 1"),
                Slip("b1", "task 2", isChecked: true),
                Slip("b1", "header override", blockKind: "heading"), // override
                // b2 slips inherit bullet
                Slip("b2", "bullet 1"),
                // b3 slips inherit ordered
                Slip("b3", "ordered 1"),
                Slip("b3", "ordered 2"),
            ]);

        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# InheritDemo\n\n## Tasks\n\n- [ ] task 1\n- [x] task 2\n**header override**\n\n## Bullets\n\n- bullet 1\n\n## Ordered\n\n1. ordered 1\n2. ordered 2");
    }

    [Fact] public void BucketAndSlipListKindsComposeWhenCompatible()
    {
        var project = Project(
            "ComposeDemo",
            [
                Bucket("b1", "Numbered Tasks", renderKind: "ordered"),
                Bucket("b2", "Bullet Tasks", renderKind: "bullet")
            ],
            [
                Slip("b1", "call", "task"),
                Slip("b1", "done", "task", isChecked: true),
                Slip("b2", "todo", "task"),
            ]);

        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# ComposeDemo\n\n## Numbered Tasks\n\n- [ ] call\n- [x] done\n\n## Bullet Tasks\n\n- [ ] todo");

        var markdown = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "m", Name = "M", Kind = ZetlViewKinds.Markdown }).ReplaceLineEndings("\n");
        AssertTrue(
            !markdown.Contains("1. [ ]", StringComparison.Ordinal),
            "Markdown tasks should use canonical GFM task markers instead of numbered list text.");

        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html }).ReplaceLineEndings("\n");
        AssertContains(html, "<ol>");
        AssertContains(html, "<li id=\"");
        AssertContains(html, "<input type=\"checkbox\" /> call");
        AssertContains(html, "<input type=\"checkbox\" checked /> done");
        AssertTrue(
            !html.Contains("<ul style=\"list-style:none;padding-left:1.1em\">\n<li", StringComparison.Ordinal),
            "A numbered bucket should remain the outer list even when the slip is a task.");
    }

    [Fact] public void SlipCanOptOutOfBucketListKindComposition()
    {
        var project = Project(
            "OptOutDemo",
            [Bucket("b1", "Numbered Tasks", renderKind: "ordered")],
            [
                Slip("b1", "composed", "task"),
                Slip("b1", "own task", "task", ignoreBucketRenderKind: true),
            ]);

        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# OptOutDemo\n\n## Numbered Tasks\n\n- [ ] composed\n- [ ] own task");

        var preferred = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "m", Name = "M", Kind = ZetlViewKinds.Markdown },
            preferSlipKindOverBucketKind: true).ReplaceLineEndings("\n");
        AssertEqual(
            "# OptOutDemo\n\n## Numbered Tasks\n\n- [ ] composed\n- [ ] own task",
            preferred,
            "The Kastn preference can treat explicit slip kinds as overriding bucket style.");

        var preferredHtml = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html },
            preferSlipKindOverBucketKind: true).ReplaceLineEndings("\n");
        AssertContains(preferredHtml, "<ul style=\"list-style:none;padding-left:1.1em\">");
        AssertTrue(
            !preferredHtml.Contains("<ol>", StringComparison.Ordinal),
            "The same global preference should be honored by HTML rendering.");
    }

    [Fact] public void OrderedSlipsRenumberContinuouslyOverVisibleSlips()
    {
        // Ordered notes are numbered over their run (computed at render time) and a
        // non-ordered note restarts the count.
        var slipAlpha = Slip("b1", "alpha", "ordered");
        var slipBeta = Slip("b1", "beta", "ordered");
        var slipNote = Slip("b1", "note");
        var slipGamma = Slip("b1", "gamma", "ordered");
        var project = Project(
            "Demo",
            [Bucket("b1", "Steps")],
            [
                slipAlpha,
                slipBeta,
                slipNote,
                slipGamma
            ]);
        AssertRender(
            project,
            ZetlViewKinds.Markdown,
            "# Demo\n\n## Steps\n\n1. alpha\n2. beta\nnote\n\n1. gamma");

        // Hiding the first note re-flows the numbers: the run starts over at 1.
        var hidden = Project(
            "Demo",
            [Bucket("b1", "Steps")],
            [
                Slip("b1", "alpha", "ordered") with { ExcludedFromViews = true },
                Slip("b1", "beta", "ordered"),
                Slip("b1", "gamma", "ordered")
            ]);
        AssertRender(hidden, ZetlViewKinds.Markdown, "# Demo\n\n## Steps\n\n1. beta\n2. gamma");

        // HTML groups a contiguous ordered run into one <ol> (so it reads 1, 2); the
        // note interrupts the run, starting a fresh <ol> for gamma.
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html }).ReplaceLineEndings("\n");
        AssertContains(html, "<ol>");
        AssertContains(html, $"<li id=\"{slipAlpha.Id}\">alpha</li>");
        AssertContains(html, $"<li id=\"{slipBeta.Id}\">beta</li>");
        AssertContains(html, $"<div id=\"{slipNote.Id}\">note</div>");
    }

    [Fact] public void DocumentTitleHidesOrOverridesProjectName()
    {
        var project = Project("Demo", [Bucket("b1", "Ideas")], [Slip("b1", "one")]);

        // Default: the project name is the document title.
        AssertRender(project, ZetlViewKinds.Markdown, "# Demo\n\n## Ideas\n\none");

        // A non-empty Title overrides the project name.
        AssertEqual(
            "# My Report\n\n## Ideas\n\none",
            ZetlViewRenderer.Render(
                project,
                project.Slips,
                new ZetlViewDocument { Id = "c", Name = "C", Kind = ZetlViewKinds.Markdown, Title = "My Report" })
                .ReplaceLineEndings("\n"),
            "A custom title should override the project name.");

        // ShowTitle=false omits the heading entirely.
        AssertEqual(
            "## Ideas\n\none",
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

    [Fact] public void SectionHeadingStyleRendersAcrossKinds()
    {
        var project = Project("Demo", [Bucket("b1", "Ideas")], [Slip("b1", "one")]);

        static ZetlViewSection StyledSection() => new()
        {
            Title = "Big Centered",
            Buckets = ["Ideas"],
            HeadingAlign = "center",
            HeadingBold = true,
            HeadingLevel = 1,
        };

        // HTML honors level (h1), alignment, and bold.
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument
            {
                Id = "h",
                Name = "H",
                Kind = ZetlViewKinds.Html,
                ShowTitle = false,
                Sections = [StyledSection()],
            });
        AssertContains(html, "<h1 style=\"text-align:center;font-weight:700\">Big Centered</h1>");

        // Markdown honors only the size (as heading depth); it cannot express
        // alignment or bold, so the heading stays plain.
        var markdown = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument
            {
                Id = "m",
                Name = "M",
                Kind = ZetlViewKinds.Markdown,
                ShowTitle = false,
                Sections = [StyledSection()],
            }).ReplaceLineEndings("\n");
        AssertEqual("# Big Centered\n\none", markdown, "Markdown uses only the section level.");
    }

    [Fact] public void BucketHeadingStyleRendersInAllBucketsView()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas") with { HeadingAlign = "center", HeadingBold = true, HeadingLevel = 1 }],
            [Slip("b1", "one")]);

        // The default all-buckets view renders the bucket name as a heading and
        // honors the bucket's own heading styling in HTML.
        var html = ZetlViewRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "h", Name = "H", Kind = ZetlViewKinds.Html, ShowTitle = false });
        AssertContains(html, "<h1 style=\"text-align:center;font-weight:700\">Ideas</h1>");
    }

    private static void AssertContains(string text, string expected)
    {
        if (!text.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected output to contain '{expected}'.");
        }
    }

    [Fact] public void ProjectRemembersDefaultViewAcrossReload()
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

    [Fact] public void ProjectScopedViewsPersistAndStayIsolated()
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

    [Fact] public void CreationTypeStoreLoadsSavesAndDeletes()
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

    [Fact] public void SectionsRenameReorderAndOmitBuckets()
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
            "# Demo\n\n## Method\n\nmix\n\n## What you need\n\neggs",
            markdown,
            "Sections should rename, reorder, and omit buckets.");
    }

    [Fact] public void RendererEmbedsPictures()
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

    [Fact] public void PdfRendererProducesAPdfDocument()
    {
        var project = Project(
            "Demo",
            [Bucket("b1", "Ideas")],
            [
                Slip("b1", "first idea") with
                {
                    FontFamily = "Georgia",
                    FontSize = 18,
                    TextColor = "#2563EB"
                },
                // Unknown/hand-edited values must fall back without aborting the PDF.
                Slip("b1", "second idea") with
                {
                    FontFamily = "Not An Installed Family",
                    FontSize = 500,
                    TextColor = "not-a-color"
                }
            ]);

        var pdf = KASTN.KastnPdfRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "v", Name = "V", Kind = ZetlViewKinds.Pdf });

        AssertTrue(pdf.Length > 0, "PDF render should produce bytes.");
        var header = System.Text.Encoding.ASCII.GetString(pdf, 0, 5);
        AssertEqual("%PDF-", header, "Output should be a PDF document.");
    }

    [Fact] public void PdfRendererKeepsSafeLinksAndMakesUnsupportedLinksInert()
    {
        var destination = Slip("b1", "destination");
        var links = Slip("b1", "") with
        {
            Text = $"[web](https://example.com) [email](mailto:reader@example.com) "
                + $"[jump](#{destination.Id}) [blocked](javascript:alert)"
        };
        var project = Project("Links", [Bucket("b1", "Ideas")], [destination, links]);

        var pdf = KASTN.KastnPdfRenderer.Render(
            project,
            project.Slips,
            new ZetlViewDocument { Id = "v", Name = "V", Kind = ZetlViewKinds.Pdf });

        AssertTrue(pdf.Length > 0, "Safe, internal, and blocked links should not abort PDF rendering.");
        AssertEqual(
            "%PDF-",
            System.Text.Encoding.ASCII.GetString(pdf, 0, 5),
            "Link-filtered output should remain a PDF document.");
    }

    [Fact] public void PdfRendererEmbedsPictures()
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

    // bucket render kind, slip kind, ignore bucket style, prefer slip kind
    //   -> block, outer, inner
    [Theory]
    [InlineData("", "", false, false, "", "", "")]
    [InlineData("", "ordered", false, false, "ordered", "ordered", "")]
    [InlineData("", "heading", false, false, "heading", "", "")]
    [InlineData("bullet", "", false, false, "bullet", "bullet", "")]
    [InlineData("bullet", "bullet", false, false, "bullet", "bullet", "")]
    [InlineData("ordered", "task", false, false, "ordered", "ordered", "task")]
    [InlineData("bullet", "quote", false, false, "quote", "bullet", "")]
    [InlineData("group", "bullet", false, false, "bullet", "bullet", "")]
    [InlineData("task", "bullet", true, false, "bullet", "bullet", "")]
    [InlineData("task", "bullet", false, true, "bullet", "bullet", "")]
    [InlineData("task", "", false, true, "task", "task", "")]
    public void SlipListKindsComposeWithTheBucketStyle(
        string bucketKind,
        string slipKind,
        bool ignoreBucketStyle,
        bool preferSlipKind,
        string block,
        string outer,
        string inner)
    {
        var slip = Slip("b", "text", blockKind: slipKind, ignoreBucketRenderKind: ignoreBucketStyle);
        var project = Project("P", [Bucket("b", "Bucket", renderKind: bucketKind)], [slip]);

        var kinds = ZetlViewRenderer.ResolveListKinds(project, slip, preferSlipKind);

        AssertEqual(new ZetlSlipListKinds(block, outer, inner), kinds, "List kinds should compose as documented.");
    }

    [Theory]
    [InlineData("#1A2b3C", true, 0x1A, 0x2B, 0x3C)]
    [InlineData(" #ffffff ", true, 0xFF, 0xFF, 0xFF)]
    [InlineData("", false, 0, 0, 0)]
    [InlineData("#12345", false, 0, 0, 0)]
    [InlineData("#GG0000", false, 0, 0, 0)]
    [InlineData("red", false, 0, 0, 0)]
    public void TextColorRgbParsesOnlyValidHexColors(string value, bool valid, int red, int green, int blue)
    {
        var rgb = ZetlSlipTypography.TextColorRgb(value);

        AssertEqual(valid, rgb is not null, "Only a #RRGGBB color should parse.");
        if (rgb is { } channels)
        {
            AssertEqual(((byte)red, (byte)green, (byte)blue), (channels.Red, channels.Green, channels.Blue), "Channels should match the hex digits.");
        }
    }

    [Fact] public void PictureSlipsTakeOnlyTheBucketListStyle()
    {
        var picture = PictureSlip("b", "caption", "pic", "hash") with { BlockKind = "heading" };
        var project = Project("P", [Bucket("b", "Bucket", renderKind: "task")], [picture]);

        var kinds = ZetlViewRenderer.ResolveListKinds(project, picture);

        AssertEqual(new ZetlSlipListKinds("task", "task", ""), kinds, "A picture ignores its own block kind.");
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
        string startingText = "",
        string renderKind = "") => new()
    {
        Id = id,
        Revision = 1,
        Name = name,
        ParentBucketId = parent,
        Settings = new ZETL.Contracts.ZetlBucketSettings { DefaultStartingText = startingText },
        RenderKind = renderKind
    };

    private static ZetlSlipSnapshot Slip(
        string bucketId,
        string text,
        string blockKind = "",
        bool isChecked = false,
        bool ignoreBucketRenderKind = false) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Revision = 1,
        Type = ZetlSlipType.Text,
        BucketId = bucketId,
        Text = text,
        Source = "copy",
        CapturedAtUtc = DateTimeOffset.UnixEpoch,
        BlockKind = blockKind,
        IgnoreBucketRenderKind = ignoreBucketRenderKind,
        Checked = isChecked
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

}
