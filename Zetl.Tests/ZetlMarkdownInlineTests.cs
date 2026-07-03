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
}
