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
/// in the head rather than the portable renderer in Zetl.Core.
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

        var title = section.AddParagraph(project.Name.Trim());
        title.Format.Font.Size = 20;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(12);

        // Reuse the shared grouping so the PDF honors view sections and bucket order
        // exactly like the text/Markdown/HTML renderers.
        var listStyle = ZetlViewListStyles.Normalize(view.ListStyle);
        foreach (var group in ZetlViewRenderer.BuildGroups(project, slips, view))
        {
            var heading = section.AddParagraph(ZetlViewRenderer.HeadingText(group, view));
            heading.Format.Font.Size = Math.Max(12, 16 - group.Depth);
            heading.Format.Font.Bold = true;
            heading.Format.SpaceBefore = Unit.FromPoint(10);
            heading.Format.SpaceAfter = Unit.FromPoint(4);
            heading.Format.LeftIndent = Unit.FromPoint(group.Depth * 14);

            var itemNumber = 1;
            foreach (var slip in group.Slips)
            {
                if (slip.Type == ZetlSlipType.Picture)
                {
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
                if (string.IsNullOrWhiteSpace(displayText))
                {
                    continue;
                }

                var slipMarker = listStyle switch
                {
                    ZetlViewListStyles.Ordered => $"{itemNumber++}. ",
                    ZetlViewListStyles.Task => "☐ ",
                    ZetlViewListStyles.Paragraph => "",
                    _ => "• "
                };
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

        foreach (var block in ZetlMarkdown.ParseBlocks(text))
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
                var number = 1;
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

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var face = (bold, italic) switch
        {
            (true, true) => "z-bi",
            (true, false) => "z-b",
            (false, true) => "z-i",
            _ => "z-r"
        };
        return new FontResolverInfo(face);
    }

    public byte[]? GetFont(string faceName)
    {
        var file = faceName switch
        {
            "z-b" => "arialbd.ttf",
            "z-i" => "ariali.ttf",
            "z-bi" => "arialbi.ttf",
            _ => "arial.ttf"
        };
        return LoadSystemFont(file) ?? LoadSystemFont("arial.ttf");
    }

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
