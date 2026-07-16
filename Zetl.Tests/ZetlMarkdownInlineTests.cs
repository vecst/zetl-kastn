using ZETL;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlMarkdownInlineTests
{
    private static ZetlInlineStyleRange Range(string kind, int start, int length) => new()
    {
        Kind = kind,
        Start = start,
        Length = length
    };

    [Fact] public void BoldAndItalicOnTheSameRangeRenderNested()
    {
        // Bold + italic on one range emit adjacent same-char markers
        // (`***note***`); the parser must treat the triple run as bold wrapping
        // italic instead of leaking a literal `*note*` into the output.
        var styled = ZetlMarkdown.ApplyInlineStyleMarkers(
            "note",
            [Range(ZetlInlineStyleKinds.Bold, 0, 4), Range(ZetlInlineStyleKinds.Italic, 0, 4)]);
        AssertEqual("***note***", styled, "Both styles wrap the same text.");

        var html = ZetlMarkdown.InlinesToHtml(styled);
        AssertEqual(
            "<strong><em>note</em></strong>",
            html,
            "The triple-asterisk run should nest bold around italic.");
        AssertTrue(!html.Contains('*'), "No literal markers may leak into the rendering.");
    }

    [Fact] public void BoldItalicAndStrikeTogetherStayNested()
    {
        var styled = ZetlMarkdown.ApplyInlineStyleMarkers(
            "note",
            [
                Range(ZetlInlineStyleKinds.Bold, 0, 4),
                Range(ZetlInlineStyleKinds.Italic, 0, 4),
                Range(ZetlInlineStyleKinds.Strike, 0, 4)
            ]);

        var html = ZetlMarkdown.InlinesToHtml(styled);
        AssertEqual(
            "<strong><em><del>note</del></em></strong>",
            html,
            "All three styles should nest without leaking markers.");
    }

    [Fact] public void TripleRunWithInnerItalicSpanKeepsBothLevels()
    {
        // `***a*b**` — the run of three opens bold+italic, but the italic closes
        // early: bold(italic(a) b), matching how the styles would be authored.
        var html = ZetlMarkdown.InlinesToHtml("***a*b**");
        AssertEqual(
            "<strong><em>a</em>b</strong>",
            html,
            "An early inner close should still nest under the outer bold.");
    }

    private static ZetlSlipSnapshot StyledSlip(
        string text,
        bool bold = false,
        bool italic = false,
        bool strike = false,
        string blockKind = "") => new()
    {
        Id = "s1",
        Revision = 1,
        Type = ZetlSlipType.Text,
        BucketId = "b1",
        Text = text,
        BlockKind = blockKind,
        Bold = bold,
        Italic = italic,
        Strike = strike,
        Source = "kastn",
        CapturedAtUtc = DateTimeOffset.UnixEpoch
    };

    private static string RenderWholeSlip(ZetlSlipSnapshot slip)
    {
        var styles = ZetlViewRenderer.EffectiveInlineStyles(slip, slip.Text);
        return ZetlMarkdown.InlinesToHtml(ZetlMarkdown.ApplyInlineStyleMarkers(slip.Text, styles));
    }

    [Fact] public void WholeSlipBoldAndItalicRenderNestedWithoutMarkers()
    {
        var html = RenderWholeSlip(StyledSlip("note", bold: true, italic: true));
        AssertEqual(
            "<strong><em>note</em></strong>",
            html,
            "Whole-slip flags render through the same nested emphasis pipeline.");
    }

    [Fact] public void WholeSlipStylesApplyPerLineAndSkipBlockMarkers()
    {
        var slip = StyledSlip("- item\nplain line", bold: true);
        var styles = ZetlViewRenderer.EffectiveInlineStyles(slip, slip.Text);
        var styled = ZetlMarkdown.ApplyInlineStyleMarkers(slip.Text, styles);
        AssertEqual(
            "- **item**\n**plain line**",
            styled,
            "Styling starts after a typed list marker and never spans a line break.");

        var html = ZetlMarkdown.BlocksToHtml(styled);
        AssertTrue(
            html.Contains("<li><strong>item</strong></li>", StringComparison.Ordinal),
            "The list line stays a list item with bold content.");
        AssertTrue(!html.Contains('*'), "No literal markers may leak into the rendering.");
    }

    [Fact] public void WholeSlipStylesLeaveCodeAlone()
    {
        var codeSlip = StyledSlip("var x = 1;", bold: true, blockKind: ZetlBlockKinds.Code);
        AssertEqual(
            0,
            ZetlViewRenderer.EffectiveInlineStyles(codeSlip, codeSlip.Text).Count,
            "A code-kind slip takes no whole-slip styling — its body is literal.");

        var fenced = StyledSlip("before\n```\nlet y = 2;\n```", bold: true);
        var styled = ZetlMarkdown.ApplyInlineStyleMarkers(
            fenced.Text,
            ZetlViewRenderer.EffectiveInlineStyles(fenced, fenced.Text));
        AssertEqual(
            "**before**\n```\nlet y = 2;\n```",
            styled,
            "Fenced lines inside the body stay literal.");
    }

    [Fact] public void PlainDoubledEmphasisIsUnchanged()
    {
        AssertEqual(
            "<strong>bold</strong> and <em>italic</em>",
            ZetlMarkdown.InlinesToHtml("**bold** and *italic*"),
            "Ordinary bold and italic parsing must not change.");
        AssertEqual(
            "<strong>a</strong>*",
            ZetlMarkdown.InlinesToHtml("**a***"),
            "A stray trailing marker after a plain bold stays literal.");
        AssertEqual(
            "******",
            ZetlMarkdown.InlinesToHtml("******"),
            "A bare marker run with no content stays literal.");
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("mailto:reader@example.com")]
    [InlineData("#slip-123")]
    public void SafeAuthoredLinkTargetsRemainActive(string target)
    {
        AssertTrue(
            ZetlLinkSafety.TryNormalizeTarget($"  {target}  ", out var normalized),
            "The shared policy should accept supported navigation targets.");
        AssertEqual(target, normalized, "Accepted targets should be trimmed without being rewritten.");
        AssertEqual(
            $"<a href=\"{target.Replace("&", "&amp;")}\">open</a>",
            ZetlMarkdown.InlinesToHtml($"[open]({target})"),
            "Safe targets should remain active in HTML output.");
    }

    [Theory]
    [InlineData("javascript:alert")]
    [InlineData("data:text/html,alert")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("ftp://example.com/file")]
    [InlineData("relative/page.html")]
    [InlineData("#fragment with spaces")]
    public void UnsafeAuthoredLinkTargetsRenderInert(string target)
    {
        AssertFalse(
            ZetlLinkSafety.TryNormalizeTarget(target, out _),
            "Unsupported targets must fail the shared navigation policy.");

        var html = ZetlMarkdown.InlinesToHtml($"[open]({target})");
        AssertEqual(
            "<span class=\"kastn-blocked-link\" title=\"Link target omitted: unsupported scheme\">open</span>",
            html,
            "HTML should preserve the label but omit the active target.");
        AssertFalse(html.Contains(target, StringComparison.Ordinal), "Blocked output must not retain the target.");

        var markdown = ZetlMarkdown.ResolveWikiLinksInNote($"[open]({target})", _ => false);
        AssertEqual("open", markdown, "Translated Markdown should retain only the inert label.");

        var styled = ZetlMarkdown.ApplyInlineStyleMarkers(
            "open",
            [
                new ZetlInlineStyleRange
                {
                    Kind = ZetlInlineStyleKinds.Link,
                    Start = 0,
                    Length = 4,
                    Href = target
                }
            ]);
        AssertEqual("open", styled, "Structured unsafe links should not become Markdown links.");
    }
}
