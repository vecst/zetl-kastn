using ZETL;
using ZETL.Contracts;

namespace KASTN;

/// <summary>
/// The one editor draft Kastn can have open at a time. Baseline content travels
/// with the draft so recovery can detect a remote edit instead of silently rebasing
/// deliberate local writing onto a newer authoritative revision.
/// </summary>
internal sealed class KastnDraftDocument
{
    public string ProjectId { get; set; } = "";
    public string SlipId { get; set; } = "";
    public long BaselineRevision { get; set; }
    public string BaselineText { get; set; } = "";
    public List<ZetlInlineStyleRange> BaselineInlineStyles { get; set; } = [];
    public string DraftText { get; set; } = "";
    public List<ZetlInlineStyleRange> DraftInlineStyles { get; set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>
/// Kastn-owned crash-recovery state. It is separate from authoritative projects
/// and from settings, and contains at most one slip because the workbench exposes
/// only one editor at a time.
/// </summary>
internal sealed class KastnDraftStore
{
    public static string? DefaultPathOverride { get; set; }

    private readonly string path;
    private readonly Action<string>? log;

    public KastnDraftStore(string? path = null, Action<string>? log = null)
    {
        this.path = path ?? DefaultPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Zetl",
            "kastn-draft.json");
        this.log = log;
        Draft = Normalize(JsonFile.ReadOrQuarantine<KastnDraftDocument>(this.path, log));
    }

    public KastnDraftDocument? Draft { get; private set; }

    public bool Save(KastnDraftDocument draft)
    {
        Draft = Normalize(draft);
        if (Draft is null)
        {
            return false;
        }

        try
        {
            JsonFile.WriteAtomic(path, Draft);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not save the Kastn recovery draft: {ex.Message}");
            return false;
        }
    }

    public bool Clear()
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            Draft = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Could not clear the Kastn recovery draft: {ex.Message}");
            return false;
        }
    }

    private static KastnDraftDocument? Normalize(KastnDraftDocument? draft)
    {
        if (draft is null
            || string.IsNullOrWhiteSpace(draft.ProjectId)
            || string.IsNullOrWhiteSpace(draft.SlipId))
        {
            return null;
        }

        draft.ProjectId = draft.ProjectId.Trim();
        draft.SlipId = draft.SlipId.Trim();
        draft.BaselineText ??= "";
        draft.DraftText ??= "";
        draft.BaselineInlineStyles = ZetlInlineStyles.Normalize(
            draft.BaselineText,
            draft.BaselineInlineStyles);
        draft.DraftInlineStyles = ZetlInlineStyles.Normalize(
            draft.DraftText,
            draft.DraftInlineStyles);
        return draft;
    }
}
