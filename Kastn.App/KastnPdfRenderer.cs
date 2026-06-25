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
        IReadOnlyDictionary<string, ZetlPictureContent>? pictures = null)
    {
        EnsureFonts();
        var imageDirectory = Path.Combine(Path.GetTempPath(), $"kastn-pdf-{Guid.NewGuid():N}");
        try
        {
            var document = BuildDocument(project, slips, view, pictures, imageDirectory);
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
        string imageDirectory)
    {
        var document = new Document();
        var normal = document.Styles["Normal"]!;
        normal.Font.Name = KastnPdfFontResolver.FamilyName;
        normal.Font.Size = 11;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.Letter;

        if (ZetlViewRenderer.DocumentTitle(project, view) is { } titleText)
        {
            var title = section.AddParagraph(titleText);
            title.Format.Font.Size = 20;
            title.Format.Font.Bold = true;
            title.Format.SpaceAfter = Unit.FromPoint(12);
        }

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
            if (group.RenderKind == "group")
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

                var displayText = string.IsNullOrWhiteSpace(slip.Text) ? slip.Title : slip.Text;
                var kind = ZetlViewRenderer.SlipListKind(slip);
                // A divider note carries no text but still renders (as a rule); other
                // empty notes are skipped.
                if (kind != "divider" && string.IsNullOrWhiteSpace(displayText))
                {
                    continue;
                }

                var slipMarker = kind switch
                {
                    "ordered" => $"{++orderedRun}. ",
                    "task" => slip.Checked ? "☑ " : "☐ ",
                    "bullet" => "• ",
                    _ => ""
                };
                if (kind != "ordered")
                {
                    orderedRun = 0;
                }

                AppendSlipBlocks(section, slip, displayText, group.Depth, slipMarker);
            }
        }

        return document;
    }

    // Render a slip's Markdown blocks into the section: paragraphs (the first line
    // carries the slip's "•" bucket bullet) and list items (their own marker, deeper
    // indent). Alignment from the slip's Align rides on every paragraph.
    private static void AppendSlipBlocks(
        Section section,
        ZetlSlipSnapshot slip,
        string text,
        int depth,
        string slipMarker)
    {
        var alignment = ZetlViewRenderer.SlipAlignment(slip) switch
        {
            "center" => ParagraphAlignment.Center,
            "right" => ParagraphAlignment.Right,
            _ => ParagraphAlignment.Left
        };
        var placedSlipMarker = false;

        // A whole-note kind (heading/quote/code/divider) synthesizes its one block.
        foreach (var block in ZetlMarkdown.BlocksForNote(slip.ListKind, text))
        {
            if (block is ZetlParagraphBlock paragraphBlock)
            {
                var paragraph = section.AddParagraph();
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 1) * 14);
                paragraph.Format.SpaceAfter = Unit.FromPoint(4);
                paragraph.Format.Alignment = alignment;
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

                    AppendInlines(paragraph.AddFormattedText(), paragraphBlock.Lines[line]);
                }
            }
            else if (block is ZetlListBlock listBlock)
            {
                var number = listBlock.Start;
                foreach (var item in listBlock.Items)
                {
                    var paragraph = section.AddParagraph();
                    paragraph.Format.LeftIndent = Unit.FromPoint((depth + 2) * 14);
                    paragraph.Format.SpaceAfter = Unit.FromPoint(2);
                    paragraph.Format.Alignment = alignment;
                    var marker = listBlock.Kind switch
                    {
                        "ordered" => $"{number++}. ",
                        "task" => item.Checked ? "☑ " : "☐ ",
                        _ => "• "
                    };
                    paragraph.AddText(marker);
                    AppendInlines(paragraph.AddFormattedText(), item.Inlines);
                }

                placedSlipMarker = true;
            }
            else if (block is ZetlHeadingBlock headingBlock)
            {
                // Softened sub-heading: bold and a touch larger, not a document heading.
                var paragraph = section.AddParagraph();
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 1) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(6);
                paragraph.Format.SpaceAfter = Unit.FromPoint(2);
                paragraph.Format.Alignment = alignment;
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
                headingText.Font.Size = headingBlock.Level <= 1 ? 13 : headingBlock.Level == 2 ? 12 : 11.5;
                AppendInlines(headingText, headingBlock.Inlines);
            }
            else if (block is ZetlQuoteBlock quoteBlock)
            {
                var paragraph = section.AddParagraph();
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 2) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(2);
                paragraph.Format.SpaceAfter = Unit.FromPoint(2);
                paragraph.Format.Alignment = alignment;
                paragraph.Format.Borders.Left.Width = 2;
                paragraph.Format.Borders.Left.Color = new Color(0xBB, 0xBB, 0xBB);
                paragraph.Format.Borders.DistanceFromLeft = Unit.FromPoint(4);
                var quoteText = paragraph.AddFormattedText();
                quoteText.Italic = true;
                for (var line = 0; line < quoteBlock.Lines.Count; line++)
                {
                    if (line > 0)
                    {
                        quoteText.AddLineBreak();
                    }

                    AppendInlines(quoteText, quoteBlock.Lines[line]);
                }

                placedSlipMarker = true;
            }
            else if (block is ZetlCodeBlock codeBlock)
            {
                var paragraph = section.AddParagraph();
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 2) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(3);
                paragraph.Format.SpaceAfter = Unit.FromPoint(3);
                paragraph.Format.Font.Name = KastnPdfFontResolver.MonoFamilyName;
                paragraph.Format.Font.Size = 9.5;
                paragraph.Format.Shading.Color = new Color(0xF2, 0xF2, 0xF2);
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
                paragraph.Format.LeftIndent = Unit.FromPoint((depth + 1) * 14);
                paragraph.Format.SpaceBefore = Unit.FromPoint(4);
                paragraph.Format.SpaceAfter = Unit.FromPoint(4);
                paragraph.Format.Borders.Bottom.Width = 0.75;
                paragraph.Format.Borders.Bottom.Color = new Color(0xCC, 0xCC, 0xCC);
                placedSlipMarker = true;
            }
        }
    }

    // Walk the Markdown inline AST into MigraDoc formatted text. Bold/italic and web
    // hyperlinks are honored; inline code and strikethrough have no MigraDoc face, so
    // they render as plain text (their content is preserved).
    private static void AppendInlines(FormattedText target, IReadOnlyList<ZetlInline> inlines)
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
                    AppendInlines(formatted, emphasis.Children);
                    break;
                case ZetlLink link:
                    var hyperlink = target.AddHyperlink(link.Url, HyperlinkType.Web);
                    var linkText = hyperlink.AddFormattedText();
                    linkText.Font.Underline = Underline.Single;
                    linkText.Font.Color = Colors.Blue;
                    AppendInlines(linkText, link.Children);
                    break;
            }
        }
    }
}

/// <summary>
/// Supplies font bytes to MigraDoc without System.Drawing, by reading system TTFs
/// (Windows). All requested families map to one sans family with bold/italic faces,
/// which is plenty for the document layout. The Linux port would point these at its
/// own font paths or a bundled font.
/// </summary>
internal sealed class KastnPdfFontResolver : IFontResolver
{
    public const string FamilyName = "Zetl Sans";

    // A monospace family for fenced code blocks. Backed by Consolas, falling back to
    // Courier New and finally the sans face, so a missing font never aborts the render.
    public const string MonoFamilyName = "Zetl Mono";

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var mono = familyName == MonoFamilyName;
        var face = (mono, bold, italic) switch
        {
            (true, true, true) => "zm-bi",
            (true, true, false) => "zm-b",
            (true, false, true) => "zm-i",
            (true, false, false) => "zm-r",
            (false, true, true) => "z-bi",
            (false, true, false) => "z-b",
            (false, false, true) => "z-i",
            _ => "z-r"
        };
        return new FontResolverInfo(face);
    }

    public byte[]? GetFont(string faceName)
    {
        return faceName switch
        {
            "z-b" => LoadSystemFont("arialbd.ttf") ?? LoadSystemFont("arial.ttf"),
            "z-i" => LoadSystemFont("ariali.ttf") ?? LoadSystemFont("arial.ttf"),
            "z-bi" => LoadSystemFont("arialbi.ttf") ?? LoadSystemFont("arial.ttf"),
            "zm-b" => LoadMonoFont("consolab.ttf", "courbd.ttf"),
            "zm-i" => LoadMonoFont("consolai.ttf", "couri.ttf"),
            "zm-bi" => LoadMonoFont("consolaz.ttf", "courbi.ttf"),
            "zm-r" => LoadMonoFont("consola.ttf", "cour.ttf"),
            _ => LoadSystemFont("arial.ttf")
        };
    }

    // Prefer Consolas, then Courier New, then the sans fallback so code still renders
    // (just not monospaced) on a machine missing both monospace faces.
    private static byte[]? LoadMonoFont(string consolas, string courier) =>
        LoadSystemFont(consolas) ?? LoadSystemFont(courier) ?? LoadSystemFont("arial.ttf");

    private static byte[]? LoadSystemFont(string file)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
