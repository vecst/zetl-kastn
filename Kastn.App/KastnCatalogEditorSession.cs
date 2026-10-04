using System.Text.Json;
using ZETL;

namespace KASTN;

internal sealed record KastnCatalogSaveResult<T>(T? Saved, string? Error) where T : class;

// Common local-document policy: isolated working copy, normalized baseline,
// stable ID across retries, and acceptance only after a successful store write.
internal sealed class KastnCatalogEditorSession<T>(T source, Func<T, string> id,
    Action<T, string> setId, Func<T, string> createId, Func<T?, IReadOnlyList<string>> validate) where T : class
{
    public T Document { get; } = JsonFile.Clone(source);
    private string baseline = "";
    public static string Fingerprint(T document) => JsonSerializer.Serialize(document, JsonFile.Options);
    public bool IsDirty(T captured) => Fingerprint(captured) != baseline;
    public void SetBaseline(T captured) => baseline = Fingerprint(captured);

    public KastnCatalogSaveResult<T> Save(T captured, Action<T> persist)
    {
        var draft = JsonFile.Clone(captured);
        if (string.IsNullOrEmpty(id(Document))) setId(Document, createId(draft));
        setId(draft, id(Document));
        var errors = validate(draft);
        if (errors.Count > 0) return new(null, string.Join("\n", errors));
        try
        {
            persist(draft);
            SetBaseline(draft);
            return new(draft, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(null, ex.Message); }
    }
}
