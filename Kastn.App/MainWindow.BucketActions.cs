using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private Task AddBucketAsync() => AddBucketAsync(() => KastnDialogs.PromptAsync(this, "New Bucket", "Bucket name"));
    private Task AddBucketAsync(Func<Task<string?>> prompt) => CreateBucketAsync(group: false, prompt);
    private Task InsertGroupBucketAsync() => InsertGroupBucketAsync(() => KastnDialogs.PromptAsync(this, "New Group", "Section label"));
    private Task InsertGroupBucketAsync(Func<Task<string?>> prompt) => CreateBucketAsync(group: true, prompt);

    private async Task CreateBucketAsync(bool group, Func<Task<string?>> prompt)
    {
        if (!IsOnline || currentProject is null || showingDeleted) return;
        var projectId = currentProject.Id;
        var parent = SelectedBucket is { } bucket && !KastnWorkbench.IsDeletedBucket(bucket) ? bucket : null;
        var current = CaptureNativeAction();
        bool CanSend() => current() && (parent is null || CurrentBucketTarget(parent));
        var name = await prompt();
        if (string.IsNullOrWhiteSpace(name) || !CanSend()) return;
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.AddBucket,
            new AddBucketCommand { Name = name.Trim(), ParentBucketId = parent?.Id, RenderKind = group ? ZetlBucketRenderKinds.Group : "" },
            projectId), group ? $"Group '{name.Trim()}' added — drag slips or buckets into it." : $"Bucket '{name.Trim()}' created.", CanSend,
            response => navigation.RequestBucket(response.Payload?.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)?.Id));
    }

    private async Task SaveBucketAsync()
    {
        if (!IsOnline || currentProject is null || SelectedBucket is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket)) return;
        var name = bucketNameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { statusText.Text = "A bucket name is required."; return; }
        var current = CaptureNativeAction();
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand { Name = name, ParentBucketId = (parentBucketBox.SelectedItem as KastnBucketItem)?.Id,
                Settings = bucket.Settings, RenderKind = (bucketRenderKindBox.SelectedItem as KastnRenderKindItem)?.Value ?? "" },
            currentProject.Id, bucket.Id, bucket.Revision), "Bucket saved.", () => current() && CurrentBucketTarget(bucket));
    }

    private Task DeleteBucketAsync() => DeleteBucketAsync(bucket => KastnDialogs.ConfirmAsync(this,
        $"Delete '{bucket.Name}' and all of its child buckets and slips?", "Delete Bucket"));

    private async Task DeleteBucketAsync(Func<ZetlBucketSnapshot, Task<bool>> confirm)
    {
        if (!IsOnline || currentProject is null || SelectedBucket is not { } bucket || KastnWorkbench.IsDeletedBucket(bucket)) return;
        var projectId = currentProject.Id;
        var session = CaptureNativeAction(trackEditor: false);
        if (!await SaveEditorAsync() || !session() || editorState.IsDirty || !CurrentBucketTarget(bucket)) return;
        var current = CaptureNativeAction();
        bool CanSend() => current() && CurrentBucketTarget(bucket);
        if (!await confirm(bucket) || !CanSend()) return;
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.DeleteBucket,
            new DeleteBucketCommand(), projectId, bucket.Id, bucket.Revision), "Bucket deleted.", CanSend);
    }
}
