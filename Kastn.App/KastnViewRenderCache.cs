using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

// Selection is deliberately absent: highlighting a different slip does not
// change either the document or its export. Capture mutable view configuration
// by value so editing a view under the same id still invalidates the render.
internal sealed record KastnViewRenderKey(
    string ProjectId,
    long ChangeSequence,
    long MetadataRevision,
    string ViewDefinition,
    string VisibleSlipIds,
    bool PreferSlipKindOverBucketKind,
    string UntitledSlipTitle)
{
    public static KastnViewRenderKey Create(
        ZetlProjectSnapshot project,
        IReadOnlyList<ZetlSlipSnapshot> visible,
        ZetlViewDocument view,
        ZetlAppSettings settings) => new(
            project.Id,
            project.ChangeSequence,
            project.MetadataRevision,
            JsonSerializer.Serialize(view, ZetlProtocolJson.Options),
            JsonSerializer.Serialize(visible.Select(slip => slip.Id), ZetlProtocolJson.Options),
            settings.KastnPreferSlipKindOverBucketKind,
            settings.UntitledSlipTitle);
}

internal sealed class KastnViewRenderCache
{
    private KastnViewRenderKey? key;
    private string text = "";

    public string GetText(KastnViewRenderKey inputs, Func<string> render)
    {
        if (key != inputs)
        {
            // Advance the key only after rendering succeeds so a failed render
            // can be retried instead of serving the previous view's output.
            text = render();
            key = inputs;
        }

        return text;
    }

    public void Clear()
    {
        key = null;
        text = "";
    }
}
