using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnViewWriteResult(bool Success, string? Error = null);

// Captured view writes own scope migration and response checks. The window
// supplies transport and decides whether a completion still belongs to its UI.
internal sealed class KastnViewPersistence(ZetlViewStore store)
{
    public bool IsBusy { get; private set; }

    public async Task<KastnViewWriteResult> SaveAsync(ZetlViewDocument draft, bool wasProjectScoped,
        ZetlProjectSnapshot? project, Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute,
        Func<bool> isCurrent)
    {
        if (IsBusy) return new(false, "Wait for the current view write to finish.");
        var view = ZetlViewDefaults.Clone(draft);
        var errors = ZetlViewValidator.Validate(view);
        if (errors.Count > 0) return new(false, string.Join("\n", errors));
        if (ZetlViewDefaults.IsBuiltIn(view.Id)) return new(false, "Built-in views must be saved as a copy.");
        if ((view.Sections.Count > 0 || wasProjectScoped) && project is null)
            return new(false, "Open the original project before saving a structured view.");
        if (wasProjectScoped && !project!.Views.Any(item => item.Id == view.Id))
            return new(false, "The project view no longer exists. Save a new copy instead.");
        IsBusy = true;
        try
        {
            if (!isCurrent()) return new(false, "The view's project changed before saving.");
            if (view.Sections.Count > 0)
            {
                var priorGlobal = !wasProjectScoped ? store.LoadAll().FirstOrDefault(item => item.Id == view.Id) : null;
                var result = await WriteProjectAsync(project!, view, delete: false, execute);
                if (!result.Success) return result;
                if (priorGlobal is not null)
                {
                    try
                    {
                        var latestGlobal = store.LoadAll().FirstOrDefault(item => item.Id == view.Id);
                        if (latestGlobal is not null && KastnViewEditorDraft.Fingerprint(latestGlobal) == KastnViewEditorDraft.Fingerprint(priorGlobal))
                            store.Delete(view.Id);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { /* The durable project copy shadows a leftover global file. */ }
                }
            }
            else
            {
                // Keep the durable global copy if removing the project copy fails.
                store.Save(view);
                if (wasProjectScoped)
                {
                    if (!isCurrent()) return new(false, "Saved a global copy; the project changed before scope migration.");
                    var result = await WriteProjectAsync(project!, view, delete: true, execute);
                    if (!result.Success) return new(false, $"Saved a global copy; {result.Error}");
                }
            }
            return new(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { return new(false, ex.Message); }
        finally { IsBusy = false; }
    }

    public async Task<KastnViewWriteResult> DeleteAsync(ZetlViewDocument captured, bool projectScoped,
        ZetlProjectSnapshot? project, Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute,
        Func<bool> isCurrent)
    {
        if (IsBusy) return new(false, "Wait for the current view write to finish.");
        var view = ZetlViewDefaults.Clone(captured);
        if (ZetlViewDefaults.IsBuiltIn(view.Id)) return new(false, "Built-in views can't be deleted.");
        IsBusy = true;
        try
        {
            if (!isCurrent()) return new(false, "The view's project changed before deletion.");
            if (projectScoped)
            {
                if (project is null) return new(false, "The original project is no longer available.");
                return await WriteProjectAsync(project, view, delete: true, execute);
            }
            // A confirmation for an old file must not delete a newer replacement.
            var current = store.LoadAll().FirstOrDefault(item => item.Id == view.Id);
            if (current is null || KastnViewEditorDraft.Fingerprint(current) != KastnViewEditorDraft.Fingerprint(view))
                return new(false, "The view changed while deletion was being confirmed.");
            store.Delete(view.Id);
            return new(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { return new(false, ex.Message); }
        finally { IsBusy = false; }
    }

    private static async Task<KastnViewWriteResult> WriteProjectAsync(ZetlProjectSnapshot project,
        ZetlViewDocument view, bool delete, Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute)
    {
        var requested = ZetlProjectSnapshotMapper.ToSnapshot(view);
        var command = ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"),
            delete ? ZetlCommandKind.DeleteProjectView : ZetlCommandKind.SaveProjectView,
            delete ? (object)new DeleteProjectViewCommand { ViewId = view.Id } : new SaveProjectViewCommand { View = requested },
            project.Id, expectedTargetRevision: project.MetadataRevision);
        var response = await execute(command);
        if (response.Status != ZetlResponseStatus.Success)
            return new(false, response.Error?.Message ?? $"View write failed: {response.Status}.");
        ZetlProjectSnapshot? saved;
        try { saved = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options); }
        catch (JsonException) { return new(false, "The view write was not confirmed. Refresh before retrying."); }
        if (response.CommandId != command.CommandId || response.ProjectId is { } id && id != project.Id
            || saved is null || saved.Id != project.Id || saved.MetadataRevision != project.MetadataRevision + 1
            || (delete ? saved.Views.Any(item => item.Id == view.Id)
                : saved.Views.FirstOrDefault(item => item.Id == view.Id) is not { } savedView
                    || JsonSerializer.Serialize(savedView, ZetlProtocolJson.Options) != JsonSerializer.Serialize(requested, ZetlProtocolJson.Options)))
            return new(false, "The view write was not confirmed. Refresh before retrying.");
        return new(true);
    }
}
