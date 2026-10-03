using System.Text;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnViewExportDestination(string Name, Func<Task<Stream>> OpenWriteAsync);

// Copy/export own these inputs from the click through picture loading and writing.
// Project snapshots are replaced by refreshes; mutable view/settings and the
// filtered slip collection are captured by value before any await.
internal sealed class KastnViewExportOperation
{
    private readonly ZetlProjectSnapshot project;
    private readonly IReadOnlyList<ZetlSlipSnapshot> slips;
    private readonly ZetlViewDocument view;
    private readonly bool preferSlipKindOverBucketKind;
    private readonly KastnPdfRenderOptions pdfOptions;

    public KastnViewExportOperation(ZetlProjectSnapshot project, IReadOnlyList<ZetlSlipSnapshot> slips,
        ZetlViewDocument view, ZetlAppSettings settings)
    {
        this.project = project;
        this.slips = slips.ToArray();
        this.view = ZetlViewDefaults.Clone(view);
        preferSlipKindOverBucketKind = settings.KastnPreferSlipKindOverBucketKind;
        pdfOptions = new(settings.PdfPageFormat, settings.PdfFontSize);
    }

    public string ProjectId => project.Id;
    public string ViewName => view.Name;
    public bool IsPdf => view.Kind == ZetlViewKinds.Pdf;
    public string FileExtension => view.Kind switch
    {
        ZetlViewKinds.Markdown => "md",
        ZetlViewKinds.Html => "html",
        ZetlViewKinds.Pdf => "pdf",
        ZetlViewKinds.Tsv => "tsv",
        _ => "txt"
    };

    public string SuggestedFileName
    {
        get
        {
            var name = string.Concat(project.Name.Trim().Select(character =>
                Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '-' : character));
            return $"{(string.IsNullOrWhiteSpace(name) ? "project" : name)}.{FileExtension}";
        }
    }

    public async Task<IReadOnlyDictionary<string, ZetlPictureContent>> LoadPicturesAsync(
        Func<string, ZetlSlipSnapshot, Task<ZetlPictureContent?>> fetch)
    {
        var pictures = new Dictionary<string, ZetlPictureContent>(StringComparer.Ordinal);
        // Literal text formats never consume image bytes.
        if (view.Kind is not (ZetlViewKinds.Markdown or ZetlViewKinds.Html or ZetlViewKinds.Pdf))
        {
            return pictures;
        }
        foreach (var slip in slips.Where(slip => slip.Type == ZetlSlipType.Picture && slip.Picture is not null))
        {
            try
            {
                if (await fetch(project.Id, slip) is { } picture && picture.Bytes.Length > 0
                    && picture.Sha256 == slip.Picture!.Sha256)
                {
                    pictures[slip.Id] = picture;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                // Keep the document readable with placeholders for unavailable assets.
            }
        }
        return pictures;
    }

    public string RenderText(IReadOnlyDictionary<string, ZetlPictureContent> pictures) =>
        ZetlViewRenderer.Render(project, slips, view, pictures, preferSlipKindOverBucketKind);

    public async Task WriteAsync(Stream stream, IReadOnlyDictionary<string, ZetlPictureContent> pictures)
    {
        if (IsPdf)
        {
            var pdf = KastnPdfRenderer.Render(project, slips, view, pictures, preferSlipKindOverBucketKind, pdfOptions);
            await stream.WriteAsync(pdf);
        }
        else
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            await writer.WriteAsync(RenderText(pictures));
        }
    }
}
