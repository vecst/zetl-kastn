using System.Text;
using MigraDoc.DocumentObjectModel;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace KASTN;

/// <summary>
/// Reserves inline checkbox space during MigraDoc layout, then turns its temporary
/// link annotations into printable AcroForm widgets. Using the laid-out image's
/// rectangle keeps fields attached to their text across alignment and page breaks.
/// </summary>
internal sealed class KastnPdfCheckboxes(string imageDirectory, int defaultFontSize)
{
    private const string PlaceholderPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==";
    private readonly Dictionary<string, bool> states = new(StringComparer.Ordinal);
    private readonly string uriPrefix = $"https://kastn.invalid/checkbox/{Guid.NewGuid():N}/";
    private string? placeholderPath;

    public void AppendMarker(Paragraph paragraph, string marker)
    {
        var start = 0;
        for (var index = 0; index < marker.Length; index++)
        {
            if (marker[index] is not ('☑' or '☐'))
            {
                continue;
            }

            paragraph.AddText(marker[start..index]);
            if (placeholderPath is null)
            {
                Directory.CreateDirectory(imageDirectory);
                placeholderPath = Path.Combine(imageDirectory, "checkbox.png");
                File.WriteAllBytes(placeholderPath, Convert.FromBase64String(PlaceholderPng));
            }

            var uri = uriPrefix + states.Count;
            states.Add(uri, marker[index] == '☑');
            var link = paragraph.AddHyperlink(uri, HyperlinkType.Web);
            var image = link.AddImage(placeholderPath);
            var fontSize = paragraph.Format.Font.Size.IsEmpty
                ? defaultFontSize : paragraph.Format.Font.Size.Point;
            image.Width = image.Height = Unit.FromPoint(fontSize * 0.85);
            start = index + 1;
        }

        paragraph.AddText(marker[start..]);
    }

    public byte[] Apply(byte[] renderedPdf)
    {
        if (states.Count == 0)
        {
            return renderedPdf;
        }

        // PDFsharp writes web-link actions only during serialization. Reopen the
        // rendered PDF to inspect their public dictionaries, without private API.
        using var input = new MemoryStream(renderedPdf);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var fields = new PdfArray(document);
        var acroForm = new PdfDictionary(document);
        acroForm.Elements["/Fields"] = fields;
        acroForm.Elements.SetBoolean("/NeedAppearances", false);
        document.Internals.AddObject(acroForm);
        document.Internals.Catalog.Elements["/AcroForm"] = acroForm.Reference!;
        var off = CreateAppearance(document, isChecked: false);
        var on = CreateAppearance(document, isChecked: true);
        var replaced = 0;
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            // Snapshot the annotation list because each placeholder is removed.
            foreach (var annotation in page.Annotations.Cast<PdfSharp.Pdf.Annotations.PdfAnnotation>().ToArray())
            {
                var action = annotation.Elements.GetDictionary("/A");
                var uri = action?.Elements.GetString("/URI");
                if (uri is null || !states.TryGetValue(uri, out var isChecked))
                {
                    continue;
                }

                // PDFsharp 6 exposes existing fields but not a public checkbox
                // constructor. A merged field/widget dictionary is standard PDF.
                var field = new PdfDictionary(document);
                field.Elements.SetName("/FT", "/Btn");
                field.Elements.SetName("/Type", "/Annot");
                field.Elements.SetName("/Subtype", "/Widget");
                field.Elements.SetString("/T", $"task-{++replaced}");
                field.Elements.SetString("/TU", $"Task {replaced}");
                var bounds = annotation.Rectangle;
                var square = new PdfSharp.Drawing.XRect(bounds.X1,
                    bounds.Y1 + (bounds.Height - bounds.Width) / 2, bounds.Width, bounds.Width);
                field.Elements.SetRectangle("/Rect", new PdfRectangle(square));
                field.Elements.SetInteger("/F", 4); // Print; remains visible and editable.
                field.Elements.SetInteger("/Ff", 0);
                field.Elements["/P"] = page.Reference!;
                var state = isChecked ? "/Yes" : "/Off";
                field.Elements.SetName("/V", state);
                field.Elements.SetName("/DV", state);
                field.Elements.SetName("/AS", state);
                var normal = new PdfDictionary(document);
                normal.Elements["/Off"] = off.Reference!;
                normal.Elements["/Yes"] = on.Reference!;
                var appearance = new PdfDictionary(document);
                appearance.Elements["/N"] = normal;
                field.Elements["/AP"] = appearance;
                document.Internals.AddObject(field);

                page.Annotations.Remove(annotation);
                page.Elements.GetArray("/Annots")!.Elements.Add(field.Reference!);
                fields.Elements.Add(field.Reference!);
            }
        }

        // Never silently export missing fields or leave temporary links behind.
        if (replaced != states.Count)
        {
            throw new InvalidOperationException("PDF checkbox layout did not produce every task field.");
        }

        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

    private static PdfDictionary CreateAppearance(PdfDocument document, bool isChecked)
    {
        var appearance = new PdfDictionary(document);
        appearance.Elements.SetName("/Type", "/XObject");
        appearance.Elements.SetName("/Subtype", "/Form");
        appearance.Elements.SetInteger("/FormType", 1);
        appearance.Elements.SetRectangle("/BBox", new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, 12, 12)));
        appearance.Elements["/Resources"] = new PdfDictionary(document);
        // Both states have explicit vector appearances: no missing-font glyphs,
        // no stale mark in page content, and no viewer regeneration needed.
        var commands = "q 1 g 0 0 12 12 re f 0 G 0.8 w 0.6 0.6 10.8 10.8 re S\n";
        if (isChecked)
        {
            commands += "1.5 w 1 J 1 j 2.5 6 m 5 3.5 l 9.5 9 l S\n";
        }

        appearance.CreateStream(Encoding.ASCII.GetBytes(commands + "Q\n"));
        document.Internals.AddObject(appearance);
        return appearance;
    }
}
