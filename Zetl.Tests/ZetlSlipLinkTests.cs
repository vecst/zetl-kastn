using ZETL;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlSlipLinkTests
{
    [Fact] public void ParsesAndResolvesStableIdLinks()
    {
        var project = Project(
            Slip("source", "Source", "See [[target|Old title]] and [[missing|Remember me]].", "a"),
            Slip("target", "Current title", "Target body", "b"));

        var parsed = ZetlSlipLinks.Parse(project.Slips[0].Text);
        AssertEqual(2, parsed.Count, "Both valid tokens should parse.");
        AssertEqual("target", parsed[0].TargetId, "The stable id should be authoritative.");
        AssertEqual("Old title", parsed[0].CachedTitle, "The raw cached title should parse.");

        var resolved = ZetlSlipLinks.Resolve(project.Slips[0].Text, project);
        AssertTrue(resolved[0].IsResolved, "An existing target should resolve by id.");
        AssertEqual("Current title", resolved[0].DisplayTitle, "Resolution should use the current title.");
        AssertTrue(!resolved[1].IsResolved, "A missing target should remain unresolved.");
        AssertEqual("Remember me", resolved[1].DisplayTitle, "An unresolved link should show its cache.");
        AssertEqual("target", ZetlSlipLinks.FindAt(project.Slips[0].Text, 8)?.TargetId,
            "A cursor inside a token should find that token for re-linking.");
        AssertEqual("[[target|Current title]]", ZetlSlipLinks.Format("target", "Current title"),
            "The picker should serialize the stable id with a readable cache.");
    }

    [Fact] public void RefreshesCachesWithoutDamagingUnresolvedOrMalformedText()
    {
        var project = Project(Slip("target", "Renamed target", "Body", "moved-bucket"));
        var source = "Broken [[no pipe]] nested [[oops [[target|Stale]]; "
            + "missing [[gone|Keep this]] and [[target|Old]].";

        var refreshed = ZetlSlipLinks.RefreshCachedTitles(source, project);
        AssertContains(refreshed, "Broken [[no pipe]]", "Malformed text should remain literal.");
        AssertContains(refreshed, "[[gone|Keep this]]", "Unresolved tokens must remain byte-for-byte.");
        AssertContains(refreshed, "[[target|Renamed target]]", "Resolved caches should refresh after rename.");
        AssertEqual(3, ZetlSlipLinks.Parse(source).Count, "A nested malformed opener should not hide later links.");
    }

    [Fact] public void ComputesBacklinksWithoutPersistedReverseEdges()
    {
        var project = Project(
            Slip("one", "One", "[[target|T]] and again [[target|T]]", "a"),
            Slip("two", "", "Second source links [[target|T]].", "b"),
            Slip("target", "Target", "", "deleted"));

        var index = ZetlSlipLinks.BuildBacklinkIndex(project);
        AssertEqual(2, index["target"].Count, "Each source should contribute one backlink per target.");
        AssertEqual("One", index["target"][0].SourceTitle, "Backlinks should carry current source titles.");
        AssertEqual(
            "Second source links [[target|T]].",
            index["target"][1].SourceTitle,
            "A title-less source should derive a readable title from its note.");
        AssertTrue(!index.ContainsKey("missing"), "Unknown targets should not create backlink entries.");
    }

    [Fact] public void ParsesWikiLinksAsInlinesAndRendersThem()
    {
        var project = Project(
            Slip("source", "Source", "See [[target|Target slip]] and [[missing|Stale stub]].", "a"),
            Slip("target", "Target Title", "Target body", "b"));

        // 1. Parse Inlines
        var inlines = ZetlMarkdown.ParseInlines(project.Slips[0].Text);
        AssertEqual(5, inlines.Count, "Should parse into text and wiki-link inline runs.");
        AssertTrue(inlines[1] is ZetlWikiLink, "First wiki-link should parse as ZetlWikiLink.");
        var wiki1 = (ZetlWikiLink)inlines[1];
        AssertEqual("target", wiki1.TargetId, "Target ID should be correct.");
        AssertEqual("Target slip", wiki1.CachedTitle, "Cached title should be correct.");

        AssertTrue(inlines[3] is ZetlWikiLink, "Second wiki-link should parse as ZetlWikiLink.");
        var wiki2 = (ZetlWikiLink)inlines[3];
        AssertEqual("missing", wiki2.TargetId, "Target ID should be correct.");
        AssertEqual("Stale stub", wiki2.CachedTitle, "Cached title should be correct.");

        // 2. HTML output
        Func<string, bool> isResolved = id => project.Slips.Any(s => s.Id == id);
        var html = ZetlMarkdown.BlocksToHtml(project.Slips[0].Text, isResolved);
        AssertContains(html, "<a href=\"#target\">Target slip</a>", "Resolved wiki-link should generate local HTML anchor.");
        AssertContains(html, "<span class=\"kastn-unresolved-link\"", "Unresolved wiki-link should render as stub.");
        AssertContains(html, "Stale stub", "Unresolved wiki-link stub should display cached title.");

        // 3. Markdown round-trip/resolve
        var resolvedMarkdown = ZetlMarkdown.ResolveWikiLinksInNote(project.Slips[0].Text, isResolved);
        AssertEqual("See [Target slip](#target) and Stale stub.", resolvedMarkdown, "Markdown export should resolve wiki-links.");
    }

    private static ZetlProjectSnapshot Project(params ZetlSlipSnapshot[] slips) => new()
    {
        Id = "project",
        Name = "Links",
        MetadataRevision = 1,
        ChangeSequence = 1,
        Buckets =
        [
            new ZetlBucketSnapshot { Id = "a", Revision = 1, Name = "A" },
            new ZetlBucketSnapshot { Id = "b", Revision = 1, Name = "B" },
            new ZetlBucketSnapshot { Id = "moved-bucket", Revision = 1, Name = "Moved" },
            new ZetlBucketSnapshot { Id = "deleted", Revision = 1, Name = "Deleted" }
        ],
        Slips = slips
    };

    private static ZetlSlipSnapshot Slip(string id, string title, string text, string bucketId) => new()
    {
        Id = id,
        Revision = 1,
        Type = ZetlSlipType.Text,
        BucketId = bucketId,
        Title = title,
        Text = text,
        Source = "test",
        CapturedAtUtc = DateTimeOffset.UtcNow
    };

    private static void AssertContains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}' in '{actual}'.");
        }
    }


}
