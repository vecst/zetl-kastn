using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private async Task DeleteProjectAsync()
    {
        if (!IsOnline || currentProject is null)
        {
            return;
        }

        await DeleteProjectAsync(new KastnProjectCard(
            currentProject.Id,
            currentProject.Name,
            currentProject.MetadataRevision,
            "",
            "",
            "",
            null,
            currentProject.Status,
            currentProject.Slips.Count,
            "",
            "",
            false,
            string.Equals(
                currentProject.Kind,
                ZetlStateRules.TemporaryConsumableProjectKind,
                StringComparison.Ordinal)));
    }

    private Task RenameProjectAsync(KastnProjectCard project) => RenameProjectAsync(project,
        () => KastnDialogs.PromptAsync(this, "Rename Project", "Project name", project.Name));

    private async Task RenameProjectAsync(KastnProjectCard project, Func<Task<string?>> prompt)
    {
        if (!CurrentProjectTarget(project)) return;
        var session = CaptureNativeAction(trackEditor: false);
        if (!await SaveEditorAsync() || !session() || editorState.IsDirty || !CurrentProjectTarget(project)) return;
        var current = CaptureNativeAction();
        var name = await prompt();
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == project.Name) return;
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.RenameProject,
            new RenameProjectCommand { Name = name.Trim() }, project.Id, project.Id, project.MetadataRevision),
            $"Project renamed to '{name.Trim()}'.", () => current() && CurrentProjectTarget(project));
    }

    private async Task ToggleJournalModeAsync()
    {
        if (!IsOnline || NativeProject is not { } project) return;
        var current = CaptureNativeAction();
        var turningOn = !project.JournalMode;
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.SetJournalMode,
            new SetJournalModeCommand { JournalMode = turningOn }, project.Id, project.Id, project.MetadataRevision),
            turningOn ? "Journal mode on — capture rolls into a dated bucket each day." : "Journal mode off.",
            () => current() && NativeProject?.MetadataRevision == project.MetadataRevision);
    }

    private async Task SetProjectStatusAsync(KastnProjectCard project, string status)
    {
        var current = CaptureNativeAction();
        var verb = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) ? "reactivated" : status.ToLowerInvariant();
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.SetProjectStatus,
            new SetProjectStatusCommand { Status = status }, project.Id, project.Id, project.MetadataRevision),
            $"Project {verb}.", () => current() && CurrentProjectTarget(project));
    }

    private async Task SetActiveProjectAsync(KastnProjectCard project, bool shifted)
    {
        var current = CaptureNativeAction();
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.SetActiveProject,
            new SetActiveProjectCommand { ActivateShifted = shifted }, project.Id),
            shifted ? "Project set as Shift." : "Project set as Main.", () => current() && CurrentProjectTarget(project));
    }

    private Task CreateTemporaryProjectFromReplayAsync(KastnProjectCard project) => CreateTemporaryProjectFromReplayAsync(project,
        async () => (await KastnDialogs.PickTemporaryTemplateLaneAsync(this, project.Name,
            LaneLabel(ZetlStateRules.NormalLane), LaneLabel(ZetlStateRules.ShiftLane), title: "Use Temporarily",
            prompt: $"Create a temporary project from '{project.Name}' in which lane?",
            confirmText: "Create Temporary", allowRemember: false))?.Lane,
        () => KastnDialogs.PromptAsync(this, "Temporary Project", "Project name", $"{project.Name} Temporary",
            candidate => landing.ProjectNameExists(candidate) ? "A project with that name already exists." : null));

    private async Task CreateTemporaryProjectFromReplayAsync(KastnProjectCard project,
        Func<Task<string?>> chooseLane, Func<Task<string?>> prompt)
    {
        if (!CurrentProjectTarget(project) || !project.CanUseTemporarily) return;
        var session = CaptureNativeAction(trackEditor: false);
        if (!await SaveEditorAsync() || !session() || editorState.IsDirty || !CurrentProjectTarget(project)) return;
        var current = CaptureNativeAction();
        bool CanSend() => current() && CurrentProjectTarget(project);
        var chosenLane = await chooseLane();
        if (chosenLane is null || !CanSend()) return;
        var lane = ZetlStateRules.CanonicalTemporaryLane(chosenLane) ?? ZetlStateRules.NormalLane;
        var name = await prompt();
        if (string.IsNullOrWhiteSpace(name) || !CanSend()) return;
        var response = await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"),
            ZetlCommandKind.CreateTemporaryProjectFromReplay, new CreateTemporaryProjectFromReplayCommand
            { Name = name.Trim(), TemporaryLane = lane, ActivateShifted = lane == ZetlStateRules.ShiftLane }, project.Id),
            "Created temporary project.", CanSend);
        if (response?.Status != ZetlResponseStatus.Success || !current() || editorState.IsDirty) return;
        var created = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options);
        if (created is null) return;
        var result = await navigation.NavigateProjectAsync(created.Id);
        if (!lifetime.IsRetired && !editorState.IsDirty && result == KastnProjectNavigationStatus.Completed)
            statusText.Text = $"Created temporary project '{created.Name}'.";
    }

    private Task DeleteProjectAsync(KastnProjectCard project) => DeleteProjectAsync(project,
        () => KastnDialogs.ConfirmAsync(this,
            $"Delete project '{project.Name}' and all of its buckets and slips? This cannot be undone.", "Delete Project"));

    private async Task DeleteProjectAsync(KastnProjectCard project, Func<Task<bool>> confirm)
    {
        if (!CurrentProjectTarget(project)) return;
        var session = CaptureNativeAction(trackEditor: false);
        if (!await SaveEditorAsync() || !session() || editorState.IsDirty || !CurrentProjectTarget(project)) return;
        var current = CaptureNativeAction();
        if (!await confirm()) return;
        await RunNativeCommandAsync(ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.DeleteProject,
            new DeleteProjectCommand(), project.Id, project.Id, project.MetadataRevision),
            "Project deleted.", () => current() && CurrentProjectTarget(project));
    }
}
