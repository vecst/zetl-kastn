using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

/// <summary>
/// Renders a project's slips to a PDF using MigraDoc (pure managed). Built from the
/// same snapshot grouping the text views use — a read-only projection — so the PDF
/// reflects current slips and never writes them. PDF output is binary, so this lives
/// in Kastn rather than the portable renderer in Zetl.Core.
/// </summary>
internal static class KastnPdfRenderer
{
    public static byte[] Render(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        ZetlViewDocument view,
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures = null,
        bool preferSlipKindOverBucketKind = false)
    {
        EnsureFonts();
        var imageDirectory = Path.Combine(Path.GetTempPath(), $"kastn-pdf-{Guid.NewGuid():N}");
        try
        {
            var document = BuildDocument(
                project,
                slips,
                view,
                pictures,
                imageDirectory,
                preferSlipKindOverBucketKind);
            var renderer = new PdfDocumentRenderer { Document = document };
            renderer.RenderDocument();

            using var stream = new MemoryStream();
            renderer.PdfDocument.Save(stream, closeStream: false);
            return stream.ToArray();
        }
        finally
        {
            if (Directory.Exists(imageDirectory))
            {
                Directory.Delete(imageDirectory, recursive: true);
            }
        }
    }

    private static bool fontsReady;

    private static void EnsureFonts()
    {
        if (fontsReady)
        {
            return;
        }

        GlobalFontSettings.FontResolver ??= new KastnPdfFontResolver();
        fontsReady = true;
    }

    private static Document BuildDocument(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> slips,
        ZetlViewDocument view,
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures,
        string imageDirectory,
        bool preferSlipKindOverBucketKind)
    {
        var document = new Document();
        var settings = new ZetlAppSettingsStore().Settings;
        var normal = document.Styles["Normal"]!;
        normal.Font.Name = KastnPdfFontResolver.FamilyName;
        normal.Font.Size = settings.PdfFontSize;

        var section = document.AddSection();
        section.PageSetup.PageFormat = string.Equals(settings.PdfPageFormat, "A4", StringComparison.OrdinalIgnoreCase)
            ? PageFormat.A4
            : PageFormat.Letter;

        if (ZetlViewRenderer.DocumentTitle(project, view) is { } titleText)
        {
            var title = section.AddParagraph(titleText);
            title.Format.Font.Size = 20;
            title.Format.Font.Bold = true;
            title.Format.SpaceAfter = Unit.FromPoint(12);
        }

        // One id set for the whole render: the wiki-link resolver runs per link,
        // so a per-call scan of project.Slips would go quadratic on big projects.
        var slipIds = project.Slips.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // Reuse the shared grouping so the PDF honors view sections and bucket order
        // exactly like the text/Markdown/HTML renderers.
        foreach (var group in ZetlViewRenderer.BuildGroups(project, slips, view))
        {
            var heading = section.AddParagraph(ZetlViewRenderer.HeadingText(group, view));
            heading.Format.Font.Size = Math.Max(11, 20 - (2 * group.EffectiveLevel));
            heading.Format.Font.Bold = true;
            heading.Format.Alignment = ZetlViewRenderer.NormalizeHeadingAlign(group.HeadingAlign) switch
            {
                "center" => ParagraphAlignment.Center,
                "right" => ParagraphAlignment.Right,
                _ => ParagraphAlignment.Left,
            };
            heading.Format.SpaceBefore = Unit.FromPoint(10);
            heading.Format.SpaceAfter = Unit.FromPoint(4);
            heading.Format.LeftIndent = Unit.FromPoint(group.Depth * 14);
            // A container "group" reads as a shaded, boxed header bar so it is visibly a
            // grouping rather than an ordinary section.
            if (group.RenderKind == ZetlBucketRenderKinds.Group)
            {
                heading.Format.Shading.Color = new Color(0xF2, 0xF2, 0xF2);
                heading.Format.Borders.Color = new Color(0xCC, 0xCC, 0xCC);
                heading.Format.Borders.Width = 0.75;
                heading.Format.Borders.Distance = Unit.FromPoint(3);
            }

            // Each note carries its own list kind; ordered notes count over their run
            // and any non-ordered note or picture restarts it.
            var orderedRun = 0;
            foreach (var slip in group.Slips)
            {
                if (slip.Type == ZetlSlipType.Picture)
                {
                    orderedRun = 0;
                    if (pictures?.TryGetValue(slip.Id, out var picture) == true)
                    {
                        Directory.CreateDirectory(imageDirectory);
                        var safeHash = Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(picture.Bytes))
                            .ToLowerInvariant();
                        var imagePath = Path.Combine(imageDirectory, $"{safeHash}.png");
                        if (!File.Exists(imagePath))
                        {
                            File.WriteAllBytes(imagePath, picture.Bytes);
                        }

                        var renderedImage = section.AddImage(imagePath);
                        renderedImage.LockAspectRatio = true;
                        renderedImage.Width = Unit.FromInch(Math.Clamp(picture.Width / 96d, 1, 6.25));
                        var caption = slip.Text.Trim();
                        if (caption.Length > 0)
                        {
                            var captionParagraph = section.AddParagraph(caption);
                            captionParagraph.Format.Font.Size = 9;
                            captionParagraph.Format.Font.Italic = true;
                            captionParagraph.Format.SpaceAfter = Unit.FromPoint(6);
                        }
                    }
                    else
                    {
                        var unavailable = section.AddParagraph(
                            string.IsNullOrWhiteSpace(slip.Text)
                                ? "[Picture unavailable]"
                                : $"[Picture unavailable: {slip.Text.Trim()}]");
                        unavailable.Format.LeftIndent = Unit.FromPoint((group.Depth + 1) * 14);
                    }

                    continue;
                }

                var displayText = ZetlViewRenderer.TextOrTitle(slip);
                var listKinds = ZetlViewRenderer.ResolveListKinds(project, slip, preferSlipKindOverBucketKind);
                var kind = listKinds.Block;
                // A divider note carries no text but still renders (as a rule); other
                // empty notes are skipped.
                if (kind != ZetlBlockKinds.Divider && string.IsNullOrWhiteSpace(displayText))
                {
                    continue;
                }

                var slipMarker = PdfOuterListMarker(listKinds.Outer, slip.Checked, ref orderedRun)
                    + PdfInnerListMarker(listKinds.Inner, slip.Checked);

                AppendSlipBlocks(section, slip, displayText, group.Depth, slipMarker, slipIds.Contains);
            }
        }

        return document;
    }

    private static string PdfOuterListMarker(string kind, bool isChecked, ref int orderedRun)
    {
        if (kind == ZetlBlockKinds.Ordered)
        {
            return $"{++orderedRun}. ";
        }

        orderedRun = 0;
        return kind switch
        {
            ZetlBlockKinds.Task => isChecked ? "☑ " : "☐ ",
            ZetlBlockKinds.Bullet => "• ",
            _ => ""
        };
    }

    private static string PdfInnerListMarker(string kind, bool isChecked) => kind switch
    {
        ZetlBlockKinds.Task => isChecked ? "☑ " : "☐ ",
        ZetlBlockKinds.Bullet => "• ",
        ZetlBlockKinds.Ordered => "1. ",
        _ => ""
    };

    private static void ApplySlipTypography(
        Paragraph paragraph,
        ZetlSlipSnapshot slip,
        bool preserveFontFamily = false)
    {
        // Empty values inherit the document defaults. Canonicalizing a non-empty
        // family here also keeps old or hand-edited project files from passing an
        // arbitrary font identifier into MigraDoc.
        var fontFamily = ZetlViewRenderer.SlipFontFamily(slip);
        if (!preserveFontFamily && fontFamily.Length > 0)
        {
            paragraph.Format.Font.Name = KastnPdfFontResolver.NormalizeFamilyName(fontFamily);
        }

        var fontSize = ZetlViewRenderer.SlipFontSize(slip);
        if (fontSize > 0)
        {
            paragraph.Format.Font.Size = fontSize;
        }

        if (ZetlSlipTypography.TextColorRgb(slip.TextColor) is { } rgb)
        {
            paragraph.Format.Font.Color = new Color(rgb.Red, rgb.Green, rgb.Blue);
        }
    }

    // Render a slip's Markdown blocks into the section: paragraphs (the first line
    // carries the slip's "•" bucket bullet) and list items (their own marker, deeper
    // indent). Alignment from the slip's Align rides on every paragraph.
    private static void AppendSlipBlocks(
        Section section,
        ZetlSlipSnapshot slip,
        string text,
        int depth,
        string slipMarker,
        Func<string, bool> isResolved)
    {
        var alignment = ZetlViewRenderer.SlipAlignment(slip) switch
        {
            "center" => ParagraphAlignment.Center,
            "right" => ParagraphAlignment.Right,
            _ => ParagraphAlignment.Left
        };
        var placedSlipMarker = false;
        var addedBookmark = false;

        void AddBookmarkIfFirst(Paragraph p)
        {
            if (!addedBookmark)
            {
                p.AddBookmark(slip.Id);
                addedBookmark = true;
            }
        }

        // A whole-note kind (heading/quote/code/divider) synthesizes its one block.
        foreach (var block in ZetlMarkdown.BlocksForNote(
            slip.BlockKind, text, ZetlViewRenderer.EffectiveInlineStyles(slip, text)))
        {
            if (block is ZetlParagraphBlock paragraphBlock)
            {
                var paragraph = section.AddParagraph();
                AddBookmarkIfFirst(paragraph);
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 1) * 14);
                paragraph.Format.SpaceAfter = Unit.FromPoint(4);
                paragraph.Format.Alignment = alignment;
                ApplySlipTypography(paragraph, slip);
                for (var line = 0; line < paragraphBlock.Lines.Count; line++)
                {
                    if (line > 0)
                    {
                        paragraph.AddLineBreak();
                    }

                    if (!placedSlipMarker)
                    {
                        if (slipMarker.Length > 0)
                        {
                            paragraph.AddText(slipMarker);
                        }

                        placedSlipMarker = true;
                    }

                    AppendInlines(paragraph.AddFormattedText(), paragraphBlock.Lines[line], isResolved);
                }
            }
            else if (block is ZetlListBlock listBlock)
            {
                var number = listBlock.Start;
                foreach (var item in listBlock.Items)
                {
                    var paragraph = section.AddParagraph();
                    AddBookmarkIfFirst(paragraph);
                    paragraph.Format.LeftIndent = Unit.FromPoint((depth + 2) * 14);
                    paragraph.Format.SpaceAfter = Unit.FromPoint(2);
                    paragraph.Format.Alignment = alignment;
                    ApplySlipTypography(paragraph, slip);
                    var marker = listBlock.Kind switch
                    {
                        "ordered" => $"{number++}. ",
                        "task" => item.Checked ? "☑ " : "☐ ",
                        _ => "• "
                    };
                    paragraph.AddText(marker);
                    AppendInlines(paragraph.AddFormattedText(), item.Inlines, isResolved);
                }

                placedSlipMarker = true;
            }
            else if (block is ZetlHeadingBlock headingBlock)
            {
                // Softened sub-heading: bold and a touch larger, not a document heading.
                var paragraph = section.AddParagraph();
                AddBookmarkIfFirst(paragraph);
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 1) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(6);
                paragraph.Format.SpaceAfter = Unit.FromPoint(2);
                paragraph.Format.Alignment = alignment;
                ApplySlipTypography(paragraph, slip);
                if (!placedSlipMarker)
                {
                    if (slipMarker.Length > 0)
                    {
                        paragraph.AddText(slipMarker);
                    }

                    placedSlipMarker = true;
                }

                var headingText = paragraph.AddFormattedText();
                headingText.Bold = true;
                var headingFontSize = ZetlViewRenderer.SlipFontSize(slip);
                headingText.Font.Size = headingFontSize > 0
                    ? headingFontSize
                    : headingBlock.Level <= 1 ? 13 : headingBlock.Level == 2 ? 12 : 11.5;
                AppendInlines(headingText, headingBlock.Inlines, isResolved);
            }
            else if (block is ZetlQuoteBlock quoteBlock)
            {
                var paragraph = section.AddParagraph();
                AddBookmarkIfFirst(paragraph);
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 2) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(2);
                paragraph.Format.SpaceAfter = Unit.FromPoint(2);
                paragraph.Format.Alignment = alignment;
                paragraph.Format.Borders.Left.Width = 2;
                paragraph.Format.Borders.Left.Color = new Color(0xBB, 0xBB, 0xBB);
                paragraph.Format.Borders.DistanceFromLeft = Unit.FromPoint(4);
                ApplySlipTypography(paragraph, slip);
                var quoteText = paragraph.AddFormattedText();
                quoteText.Italic = true;
                for (var line = 0; line < quoteBlock.Lines.Count; line++)
                {
                    if (line > 0)
                    {
                        quoteText.AddLineBreak();
                    }

                    AppendInlines(quoteText, quoteBlock.Lines[line], isResolved);
                }

                placedSlipMarker = true;
            }
            else if (block is ZetlCodeBlock codeBlock)
            {
                var paragraph = section.AddParagraph();
                AddBookmarkIfFirst(paragraph);
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 2) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(3);
                paragraph.Format.SpaceAfter = Unit.FromPoint(3);
                paragraph.Format.Font.Name = KastnPdfFontResolver.MonoFamilyName;
                paragraph.Format.Font.Size = 9.5;
                paragraph.Format.Shading.Color = new Color(0xF2, 0xF2, 0xF2);
                // Fenced code deliberately remains monospace, while an explicit
                // whole-slip size or color still applies.
                ApplySlipTypography(paragraph, slip, preserveFontFamily: true);
                var codeLines = codeBlock.Text.ReplaceLineEndings("\n").Split('\n');
                for (var line = 0; line < codeLines.Length; line++)
                {
                    if (line > 0)
                    {
                        paragraph.AddLineBreak();
                    }

                    paragraph.AddText(codeLines[line]);
                }

                placedSlipMarker = true;
            }
            else if (block is ZetlDividerBlock)
            {
                // An empty paragraph with a bottom border reads as a horizontal rule.
                var paragraph = section.AddParagraph();
                AddBookmarkIfFirst(paragraph);
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 1) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(4);
                paragraph.Format.SpaceAfter = Unit.FromPoint(4);
                paragraph.Format.Borders.Bottom.Width = 0.75;
                paragraph.Format.Borders.Bottom.Color = new Color(0xCC, 0xCC, 0xCC);
                ApplySlipTypography(paragraph, slip);
                placedSlipMarker = true;
            }
        }
    }

    // Walk the Markdown inline AST into MigraDoc formatted text. Bold/italic and web
    // hyperlinks are honored; inline code and strikethrough have no MigraDoc face, so
    // they fall back to normal text.
    private static void AppendInlines(FormattedText target, IReadOnlyList<ZetlInline> inlines, Func<string, bool> isResolved)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case ZetlTextRun run:
                    target.AddText(run.Text);
                    break;
                case ZetlCodeRun code:
                    target.AddText(code.Text);
                    break;
                case ZetlEmphasis emphasis:
                    var formatted = target.AddFormattedText();
                    if (emphasis.Kind == "bold")
                    {
                        formatted.Bold = true;
                    }
                    else if (emphasis.Kind == "italic")
                    {
                        formatted.Italic = true;
                    }
                    AppendInlines(formatted, emphasis.Children, isResolved);
                    break;
                case ZetlLink link:
                    if (ZetlLinkSafety.TryNormalizeTarget(link.Url, out var safeTarget))
                    {
                        var hyperlink = safeTarget[0] == '#'
                            ? target.AddHyperlink(safeTarget[1..], HyperlinkType.Bookmark)
                            : target.AddHyperlink(safeTarget, HyperlinkType.Web);
                        var linkText = hyperlink.AddFormattedText();
                        linkText.Font.Underline = Underline.Single;
                        linkText.Font.Color = Colors.Blue;
                        AppendInlines(linkText, link.Children, isResolved);
                    }
                    else
                    {
                        AppendInlines(target, link.Children, isResolved);
                    }
                    break;
                case ZetlWikiLink wiki:
                    var resolved = isResolved(wiki.TargetId);
                    if (resolved)
                    {
                        var wikiLink = target.AddHyperlink(wiki.TargetId, HyperlinkType.Bookmark);
                        var wikiLinkText = wikiLink.AddFormattedText();
                        wikiLinkText.Font.Underline = Underline.Single;
                        wikiLinkText.Font.Color = Colors.Blue;
                        wikiLinkText.AddText(wiki.CachedTitle);
                    }
                    else
                    {
                        var wikiLinkText = target.AddFormattedText();
                        wikiLinkText.Font.Color = Colors.Gray;
                        wikiLinkText.AddText(wiki.CachedTitle);
                    }
                    break;
            }
        }
    }
}

/// <summary>
/// Supplies font bytes to MigraDoc without System.Drawing, by reading a curated set
/// of Windows system TTFs. Unsupported or unavailable families fall back to Arial;
/// this keeps arbitrary values from older or hand-edited projects safe.
/// </summary>
internal sealed class KastnPdfFontResolver : IFontResolver
{
    public const string FamilyName = "Zetl Sans";
    public const string MonoFamilyName = "Zetl Mono";

    private const string FacePrefix = "kastn:";

    private enum FaceStyle
    {
        Regular,
        Bold,
        Italic,
        BoldItalic
    }

    private sealed record FontFamilyFiles(
        string Key,
        string DisplayName,
        string Regular,
        string Bold,
        string Italic,
        string BoldItalic)
    {
        public string FileFor(FaceStyle style) => style switch
        {
            FaceStyle.Bold => Bold,
            FaceStyle.Italic => Italic,
            FaceStyle.BoldItalic => BoldItalic,
            _ => Regular
        };
    }

    private static readonly FontFamilyFiles ArialFamily = new(
        "arial", "Arial", "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf");
    private static readonly FontFamilyFiles CalibriFamily = new(
        "calibri", "Calibri", "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf");
    private static readonly FontFamilyFiles GeorgiaFamily = new(
        "georgia", "Georgia", "georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf");
    private static readonly FontFamilyFiles SegoeUiFamily = new(
        "segoeui", "Segoe UI", "segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf");
    private static readonly FontFamilyFiles TimesNewRomanFamily = new(
        "times", "Times New Roman", "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf");
    private static readonly FontFamilyFiles VerdanaFamily = new(
        "verdana", "Verdana", "verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf");
    private static readonly FontFamilyFiles ConsolasFamily = new(
        "consolas", "Consolas", "consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf");
    private static readonly FontFamilyFiles CourierNewFamily = new(
        "courier", "Courier New", "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf");

    private static readonly IReadOnlyDictionary<string, FontFamilyFiles> FamiliesByName =
        new Dictionary<string, FontFamilyFiles>(StringComparer.OrdinalIgnoreCase)
        {
            [FamilyName] = ArialFamily,
            [MonoFamilyName] = ConsolasFamily,
            [ArialFamily.DisplayName] = ArialFamily,
            [CalibriFamily.DisplayName] = CalibriFamily,
            [GeorgiaFamily.DisplayName] = GeorgiaFamily,
            [SegoeUiFamily.DisplayName] = SegoeUiFamily,
            [TimesNewRomanFamily.DisplayName] = TimesNewRomanFamily,
            [VerdanaFamily.DisplayName] = VerdanaFamily,
            [ConsolasFamily.DisplayName] = ConsolasFamily,
            [CourierNewFamily.DisplayName] = CourierNewFamily
        };

    private static readonly IReadOnlyDictionary<string, FontFamilyFiles> FamiliesByKey =
        new[]
        {
            ArialFamily,
            CalibriFamily,
            GeorgiaFamily,
            SegoeUiFamily,
            TimesNewRomanFamily,
            VerdanaFamily,
            ConsolasFamily,
            CourierNewFamily
        }.ToDictionary(family => family.Key, StringComparer.Ordinal);

    public static string NormalizeFamilyName(string? familyName) =>
        FindFamily(familyName).DisplayName;

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var family = FindFamily(familyName);
        var style = (bold, italic) switch
        {
            (true, true) => FaceStyle.BoldItalic,
            (true, false) => FaceStyle.Bold,
            (false, true) => FaceStyle.Italic,
            _ => FaceStyle.Regular
        };
        return new FontResolverInfo($"{FacePrefix}{family.Key}:{StyleKey(style)}");
    }

    public byte[]? GetFont(string faceName)
    {
        if (!TryParseFaceName(faceName, out var family, out var style))
        {
            return null;
        }

        var exact = LoadSystemFont(family.FileFor(style));
        if (exact is not null)
        {
            return exact;
        }

        // Keep a mono request mono when one of the two Windows mono families is
        // absent, before using the general Arial fallback.
        if (family == ConsolasFamily || family == CourierNewFamily)
        {
            var alternateMono = family == ConsolasFamily ? CourierNewFamily : ConsolasFamily;
            var alternate = LoadSystemFont(alternateMono.FileFor(style));
            if (alternate is not null)
            {
                return alternate;
            }
        }

        // A regular face is preferable to failing outright when a particular style
        // file is missing. Arial remains the final family fallback for every request.
        if (style != FaceStyle.Regular && LoadSystemFont(family.Regular) is { } regular)
        {
            return regular;
        }

        return LoadSystemFont(ArialFamily.FileFor(style))
            ?? LoadSystemFont(ArialFamily.Regular);
    }

    private static FontFamilyFiles FindFamily(string? familyName)
    {
        if (!string.IsNullOrWhiteSpace(familyName)
            && FamiliesByName.TryGetValue(familyName.Trim(), out var family))
        {
            return family;
        }

        return ArialFamily;
    }

    private static string StyleKey(FaceStyle style) => style switch
    {
        FaceStyle.Bold => "b",
        FaceStyle.Italic => "i",
        FaceStyle.BoldItalic => "bi",
        _ => "r"
    };

    private static bool TryParseFaceName(
        string faceName,
        out FontFamilyFiles family,
        out FaceStyle style)
    {
        family = ArialFamily;
        style = FaceStyle.Regular;
        if (!faceName.StartsWith(FacePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = faceName.IndexOf(':', FacePrefix.Length);
        if (separator < 0
            || !FamiliesByKey.TryGetValue(faceName[FacePrefix.Length..separator], out family!))
        {
            family = ArialFamily;
            return false;
        }

        style = faceName[(separator + 1)..] switch
        {
            "b" => FaceStyle.Bold,
            "i" => FaceStyle.Italic,
            "bi" => FaceStyle.BoldItalic,
            "r" => FaceStyle.Regular,
            _ => (FaceStyle)(-1)
        };
        return style >= FaceStyle.Regular && style <= FaceStyle.BoldItalic;
    }

    private static byte[]? LoadSystemFont(string file)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }
}
