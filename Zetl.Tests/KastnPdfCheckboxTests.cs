using KASTN;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.IO;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public class KastnPdfCheckboxTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlipStateIsVisibleEditableAndSurvivesSaving(bool isChecked)
    {
        var project = KastnViewExportTests.Project();
        project = project with { Slips = [project.Slips[0] with { Checked = isChecked }] };
        using var pdf = Render(project);
        var field = Assert.IsType<PdfCheckBoxField>(pdf.AcroForm.Fields[0]);
        Assert.Single(pdf.AcroForm.Fields.Elements.Items);
        Assert.False(field.ReadOnly);
        Assert.Equal(isChecked, field.Checked);
        Assert.Equal(isChecked ? "/Yes" : "/Off", field.Elements.GetName("/DV"));
        Assert.False(pdf.AcroForm.Elements.GetBoolean("/NeedAppearances"));
        AssertWidget(pdf.Pages[0], field, isChecked);
        // An invalid placeholder still gets a link rectangle, but MigraDoc paints
        // an error message instead of an image. Require a decoded image resource.
        var images = pdf.Pages[0].Resources.Elements.GetDictionary("/XObject")!;
        Assert.Contains(images.Elements.Keys,
            key => images.Elements.GetDictionary(key)!.Elements.GetName("/Subtype") == "/Image");

        field.Checked = !isChecked;
        using var saved = new MemoryStream();
        pdf.Save(saved, closeStream: false);
        saved.Position = 0;
        using var reopened = PdfReader.Open(saved, PdfDocumentOpenMode.Modify);
        var changed = Assert.IsType<PdfCheckBoxField>(reopened.AcroForm.Fields[0]);
        Assert.Equal(!isChecked, changed.Checked);
        AssertWidget(reopened.Pages[0], changed, !isChecked);
    }

    [Fact]
    public void AuthoredTaskListsPreserveEachItemAndOtherHyperlinks()
    {
        var project = KastnViewExportTests.Project();
        project = project with
        {
            Slips = [project.Slips[0] with
            {
                BlockKind = "", Text = "- [x] Done [reference](https://example.com)\n- [ ] To do"
            }]
        };
        using var pdf = Render(project);
        Assert.Equal(2, pdf.AcroForm.Fields.Count);
        AssertWidget(pdf.Pages[0], pdf.AcroForm.Fields[0], true);
        AssertWidget(pdf.Pages[0], pdf.AcroForm.Fields[1], false);
        var links = pdf.Pages[0].Annotations.Cast<PdfSharp.Pdf.Annotations.PdfAnnotation>()
            .Where(annotation => annotation.Elements.GetName("/Subtype") == "/Link").ToArray();
        var link = Assert.Single(links);
        Assert.Equal("https://example.com", link.Elements.GetDictionary("/A")!.Elements.GetString("/URI"));
    }

    [Fact]
    public void FieldsFollowAlignmentTypographyAndPageBreaks()
    {
        var project = KastnViewExportTests.Project();
        project = project with
        {
            Slips = Enumerable.Range(0, 90).Select(index => project.Slips[0] with
            {
                Id = $"task-{index}", Text = "Task text", Checked = index % 2 == 0,
                Align = index % 3 == 0 ? "left" : index % 3 == 1 ? "center" : "right",
                FontFamily = "Georgia", FontSize = 18
            }).ToList()
        };
        using var pdf = Render(project);
        Assert.True(pdf.PageCount > 1);
        Assert.Equal(90, pdf.AcroForm.Fields.Count);
        var left = pdf.AcroForm.Fields[0].Elements.GetRectangle("/Rect");
        var center = pdf.AcroForm.Fields[1].Elements.GetRectangle("/Rect");
        var right = pdf.AcroForm.Fields[2].Elements.GetRectangle("/Rect");
        Assert.True(left.X1 < center.X1 && center.X1 < right.X1);
        Assert.InRange(left.Width, 15.2, 15.4);
        Assert.InRange(left.Height, 15.2, 15.4);
        var seen = new HashSet<string>();
        foreach (var page in pdf.Pages.Cast<PdfPage>())
        {
            foreach (var widget in page.Annotations.Cast<PdfSharp.Pdf.Annotations.PdfAnnotation>())
            {
                Assert.Equal("/Widget", widget.Elements.GetName("/Subtype"));
                var name = widget.Elements.GetString("/T");
                Assert.True(seen.Add(name));
                var field = pdf.AcroForm.Fields[name]!;
                AssertWidget(page, field, (int.Parse(name[5..]) - 1) % 2 == 0);
                var bounds = widget.Rectangle;
                Assert.InRange(bounds.X1, 0, page.Width.Point - bounds.Width);
                Assert.InRange(bounds.Y1, 0, page.Height.Point - bounds.Height);
            }
        }

        Assert.Equal(90, seen.Count);
    }

    [Fact]
    public void BucketTasksBecomeFieldsAndPlainNotesDoNot()
    {
        var project = KastnViewExportTests.Project();
        project = project with
        {
            Buckets = [project.Buckets[0] with { RenderKind = ZetlBlockKinds.Task }],
            Slips = [project.Slips[0] with { BlockKind = "", Checked = true }]
        };
        using (var pdf = Render(project))
        {
            Assert.Single(pdf.AcroForm.Fields.Elements.Items);
            AssertWidget(pdf.Pages[0], pdf.AcroForm.Fields[0], true);
        }

        project = project with { Buckets = [project.Buckets[0] with { RenderKind = "" }] };
        using var plain = Render(project);
        Assert.Null(plain.Internals.Catalog.Elements["/AcroForm"]);
        Assert.Empty(plain.Pages[0].Annotations);
    }

    private static PdfDocument Render(ZetlProjectSnapshot project)
    {
        var bytes = KastnPdfRenderer.Render(project, project.Slips,
            new() { Name = "PDF", Kind = ZetlViewKinds.Pdf }, options: new("A4", 11));
        using var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
    }

    private static void AssertWidget(PdfPage page, PdfAcroField field, bool isChecked)
    {
        var widget = Assert.Single(page.Annotations.Cast<PdfSharp.Pdf.Annotations.PdfAnnotation>()
            .Where(annotation => annotation.Elements.GetString("/T") == field.Name));
        Assert.Equal(field.Reference!.ObjectID, widget.Reference!.ObjectID);
        Assert.Equal("/Widget", widget.Elements.GetName("/Subtype"));
        Assert.Equal(4, widget.Elements.GetInteger("/F"));
        Assert.Equal(isChecked ? "/Yes" : "/Off", field.Elements.GetName("/V"));
        Assert.Equal(field.Elements.GetName("/V"), widget.Elements.GetName("/AS"));
        var normal = widget.Elements.GetDictionary("/AP")!.Elements.GetDictionary("/N")!;
        foreach (var state in new[] { "/Off", "/Yes" })
        {
            var appearance = normal.Elements.GetDictionary(state)!;
            Assert.Equal("/Form", appearance.Elements.GetName("/Subtype"));
            Assert.NotEmpty(appearance.Stream.UnfilteredValue);
        }

        Assert.NotEqual(normal.Elements.GetDictionary("/Off")!.Stream.UnfilteredValue,
            normal.Elements.GetDictionary("/Yes")!.Stream.UnfilteredValue);
    }
}
