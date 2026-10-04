using Avalonia.Controls;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal partial class MainWindow
{
    private (string ProjectId, string? ServerId, long Sequence, string Message)? creationNotice;

    private string? CreationStatus(KastnSessionSnapshot snapshot)
    {
        if (creationNotice is not { } notice) return null;
        bool Matches(KastnSessionSnapshot current) => current.ConnectionState == KastnConnectionState.Online
            && current.Project?.Id == notice.ProjectId && current.Project.ChangeSequence == notice.Sequence
            && current.ServerInstanceId == notice.ServerId;
        // Own-change refreshes can arrive after navigation/completion. Retain the
        // setup outcome for this unchanged project, until a mutation or navigation.
        if (!Matches(connection.Current)) { creationNotice = null; return null; }
        return Matches(snapshot) ? notice.Message : null;
    }

    private Task CreateProjectFromTemplateAsync(ZetlTemplateDocument template, string? defaultViewId = null) =>
        CreateProjectFromTemplateAsync(template, defaultViewId,
            async request =>
            {
                var answer = await KastnDialogs.PromptTemplateProjectAsync(this, $"New {request.TemplateName} Project",
                    request.TemplateName, request.AllowTemporary, request.InitialTemporary,
                    candidate => projects.Any(project => string.Equals(project.Name, candidate, StringComparison.OrdinalIgnoreCase))
                        ? "A project with that name already exists." : null);
                return answer is null ? null : new(answer.Name, answer.Temporary);
            },
            async request =>
            {
                var answer = await KastnDialogs.PickTemporaryTemplateLaneAsync(this, request.TemplateName,
                    request.MainLabel, request.AlternateLabel);
                return answer is null ? null : new(answer.Lane, answer.Remember);
            });

    private async Task CreateProjectFromTemplateAsync(ZetlTemplateDocument template, string? defaultViewId,
        Func<KastnProjectCreationPrompt, Task<KastnProjectCreationChoice?>> prompt,
        Func<KastnTemplateLanePrompt, Task<KastnTemplateLaneChoice?>> pickLane)
    {
        if (projectCreation.IsBusy || lifetime.AllowClose) return;
        if (!IsOnline)
        {
            statusText.Text = "Connect to Zetl before creating a project from a template.";
            return;
        }
        // Capture before the prerequisite save as well as the dialogs. A later
        // editor/project/server session must not be navigated away by completion.
        var captured = ZetlTemplateDefaults.Clone(template);
        var projectId = currentProject?.Id;
        var generation = editHistory.Generation;
        var context = new KastnEditorWorkflowContext(projectId ?? "", editorState);
        var hadEditor = editorState.SlipId is not null;
        var server = connection.Current.ServerInstanceId;
        var navigation = connection.NavigationVersion;
        bool SameSession() => !lifetime.AllowClose && IsOnline && editHistory.Generation == generation
            && (hadEditor ? context.IsSameSession(currentProject?.Id ?? "", editorState)
                : currentProject?.Id == projectId && editorState.SlipId is null)
            && connection.Current.Project?.Id == projectId && connection.Current.ServerInstanceId == server
            && connection.NavigationVersion == navigation;
        if (!await SaveEditorAsync() || !SameSession()) return;
        bool IsCurrent() => SameSession() && !editorState.IsDirty && editorState.ConflictCurrent is null;
        await projectCreation.RunAsync(captured, defaultViewId, prompt, pickLane,
            command => connection.ExecuteAsync(command), IsCurrent,
            async result =>
            {
                if (!IsCurrent() || result.Cancelled) return;
                if (result.Project is null || result.Interrupted)
                {
                    statusText.Text = result.Message;
                    return;
                }
                try
                {
                    await connection.NavigateToProjectAsync(result.Project.Id);
                    if (lifetime.AllowClose || connection.Current.ServerInstanceId != server
                        || connection.NavigationVersion != navigation + 1 || editorState.IsDirty) return;
                    if (connection.Current.Project?.Id != result.Project.Id)
                    {
                        statusText.Text = $"{result.Message} Could not open the created project.";
                        return;
                    }
                    ApplySnapshot(connection.Current);
                    creationNotice = (result.Project.Id, server, connection.Current.Project.ChangeSequence, result.Message);
                    statusText.Text = result.Message;
                    // Keep setup warnings visible instead of immediately minimizing.
                    if (result.Minimize && result.SeedFailures.Count == 0 && result.DefaultViewError is null)
                        WindowState = WindowState.Minimized;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                {
                    if (!lifetime.AllowClose && connection.Current.ServerInstanceId == server
                        && connection.NavigationVersion == navigation + 1 && !editorState.IsDirty)
                        statusText.Text = $"{result.Message} Could not open the created project: {ex.Message}";
                }
            });
    }
}
