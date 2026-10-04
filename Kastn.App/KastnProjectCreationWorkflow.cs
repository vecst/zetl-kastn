using System.Text.Json;
using ZETL;
using ZETL.Contracts;

namespace KASTN;

internal sealed record KastnProjectCreationPrompt(string TemplateName, bool AllowTemporary, bool InitialTemporary);
internal sealed record KastnProjectCreationChoice(string Name, bool Temporary);
internal sealed record KastnTemplateLanePrompt(string TemplateName, string MainLabel, string AlternateLabel);
internal sealed record KastnTemplateLaneChoice(string Lane, bool Remember);
internal sealed record KastnTemplateSeedFailure(string BucketName, string Title, string Error);

internal sealed record KastnProjectCreationResult(
    string TemplateName,
    ZetlProjectSnapshot? Project,
    string? CreationError,
    IReadOnlyList<KastnTemplateSeedFailure> SeedFailures,
    string? DefaultViewError,
    bool Interrupted = false,
    bool Cancelled = false,
    bool Minimize = false)
{
    public string Message
    {
        get
        {
            if (Project is null) return CreationError ?? "Project creation was cancelled.";
            var problems = new List<string>();
            if (SeedFailures.Count > 0) problems.Add("some template fields could not be added");
            if (DefaultViewError is not null) problems.Add($"the default view could not be assigned: {DefaultViewError.TrimEnd('.')}");
            if (Interrupted) problems.Add("setup was interrupted");
            return problems.Count == 0
                ? $"Created '{Project.Name}' from the {TemplateName} template."
                : $"Created '{Project.Name}', but {string.Join("; ", problems)}.";
        }
    }
}

// Owns one ordered template-to-project transaction at a time. Dialogs, transport,
// context validity, and final navigation/UI effects are supplied by the window.
// A confirmed project is retained in partial outcomes; setup never recreates it
// or rolls it back after a seed/view failure.
internal sealed class KastnProjectCreationWorkflow(KastnSettings settings)
{
    private enum Stage { Creation, Seed, View }
    public bool IsBusy { get; private set; }

    public async Task<KastnProjectCreationResult> RunAsync(
        ZetlTemplateDocument source,
        string? defaultViewId,
        Func<KastnProjectCreationPrompt, Task<KastnProjectCreationChoice?>> prompt,
        Func<KastnTemplateLanePrompt, Task<KastnTemplateLaneChoice?>> pickLane,
        Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute,
        Func<bool> isCurrent,
        Func<KastnProjectCreationResult, Task>? complete = null)
    {
        if (IsBusy) return new(source.Name, null, null, [], null, Cancelled: true);
        IsBusy = true;
        try
        {
            var result = await RunCoreAsync(ZetlTemplateDefaults.Clone(source), defaultViewId,
                prompt, pickLane, execute, isCurrent);
            if (complete is not null) await complete(result);
            return result;
        }
        finally { IsBusy = false; }
    }

    private async Task<KastnProjectCreationResult> RunCoreAsync(
        ZetlTemplateDocument template, string? defaultViewId,
        Func<KastnProjectCreationPrompt, Task<KastnProjectCreationChoice?>> prompt,
        Func<KastnTemplateLanePrompt, Task<KastnTemplateLaneChoice?>> pickLane,
        Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>> execute,
        Func<bool> isCurrent)
    {
        ZetlProjectSnapshot? created = null;
        var failures = new List<KastnTemplateSeedFailure>();
        string? viewError = null;
        var minimize = settings.Current.KastnMinimizeAfterTemplate;
        var stage = Stage.Creation;
        string bucketName = "", cardTitle = "";
        KastnProjectCreationResult Result(string? error = null, bool interrupted = false, bool cancelled = false) =>
            new(template.Name, created, error, failures.ToArray(), viewError, interrupted, cancelled, minimize);

        try
        {
            if (!isCurrent()) return Result(interrupted: true);
            var errors = ZetlTemplateValidator.Validate(template);
            if (errors.Count > 0) return Result(string.Join("\n", errors));
            var choice = await prompt(new(template.Name, template.IsConsumable, template.Temporary));
            if (!isCurrent()) return Result(interrupted: true);
            if (choice is null) return Result(cancelled: true);
            if (string.IsNullOrWhiteSpace(choice.Name)) return Result("A project name is required.");

            var temporary = template.IsConsumable && choice.Temporary;
            string? lane = null;
            if (temporary)
            {
                var current = settings.Current;
                lane = ZetlKastnTemplateLaneDefault.Normalize(current.KastnTemporaryTemplateLaneDefault);
                if (lane.Length == 0)
                {
                    var selected = await pickLane(new(template.Name, current.LaneLabel(false), current.LaneLabel(true)));
                    if (!isCurrent()) return Result(interrupted: true);
                    if (selected is null) return Result(cancelled: true);
                    lane = ZetlStateRules.CanonicalTemporaryLane(selected.Lane);
                    if (lane is null) return Result("A valid temporary lane is required.");
                    if (selected.Remember) settings.RememberTemporaryTemplateLane(lane);
                }
            }

            if (!isCurrent()) return Result(interrupted: true);
            var create = template.ToCreateProjectCommand(choice.Name.Trim(), lane, temporary) with
            {
                ActivateShifted = lane == ZetlStateRules.ShiftLane
            };
            var command = ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.CreateProject, create);
            var response = await execute(command);
            if (ResponseError(command, response) is { } createError) return Result(createError);
            created = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options);
            if (created is null || string.IsNullOrEmpty(created.Id) || created.MetadataRevision < 1
                || response.ProjectId != created.Id)
            {
                created = null;
                return Result("Project creation was not confirmed. Refresh before retrying.");
            }
            if (!isCurrent()) return Result(interrupted: true);

            stage = Stage.Seed;
            foreach (var bucket in template.Buckets)
            {
                var cards = bucket.Seeds.Select(text => new ZetlTemplateSlipDocument { Text = text }).Concat(bucket.Cards);
                var target = created.Buckets.FirstOrDefault(item => string.Equals(item.Name, bucket.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var card in cards)
                {
                    if (!isCurrent()) return Result(interrupted: true);
                    bucketName = bucket.Name;
                    cardTitle = card.Title;
                    if (target is null)
                    {
                        failures.Add(new(bucket.Name, card.Title, "The created bucket is missing."));
                        continue;
                    }
                    command = ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.AddSlip,
                        new AddSlipCommand { BucketId = target.Id, Title = card.Title, Text = card.Text, Source = "template" }, created.Id);
                    response = await execute(command);
                    var seedError = ResponseError(command, response);
                    if (seedError is null)
                    {
                        var saved = response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);
                        if (saved is null || string.IsNullOrEmpty(saved.Id) || saved.Revision < 1
                            || saved.BucketId != target.Id || saved.Source != "template"
                            || saved.Title != card.Title.Trim() || saved.Text != card.Text.Trim())
                            seedError = "The template field was not confirmed. Refresh before retrying.";
                    }
                    if (seedError is not null)
                        failures.Add(new(bucket.Name, card.Title, seedError));
                    if (!isCurrent()) return Result(interrupted: true);
                }
            }

            if (!string.IsNullOrEmpty(defaultViewId))
            {
                stage = Stage.View;
                if (!isCurrent()) return Result(interrupted: true);
                command = ZetlCommandEnvelope.Create(Guid.NewGuid().ToString("N"), ZetlCommandKind.SetProjectView,
                    new SetProjectViewCommand { ViewId = defaultViewId }, created.Id,
                    expectedTargetRevision: created.MetadataRevision);
                response = await execute(command);
                viewError = ResponseError(command, response);
                if (viewError is null)
                {
                    var saved = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options);
                    if (saved is null || saved.Id != created.Id
                        || saved.MetadataRevision != created.MetadataRevision + 1 || saved.DefaultViewId != defaultViewId)
                        viewError = "The view assignment was not confirmed. Refresh before retrying.";
                    else created = saved;
                }
                if (!isCurrent()) return Result(interrupted: true);
            }
            return Result();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException or JsonException)
        {
            if (created is null) return Result($"Project creation could not be confirmed: {ex.Message}");
            if (stage == Stage.Seed) failures.Add(new(bucketName, cardTitle, ex.Message));
            else if (stage == Stage.View) viewError = ex.Message;
            return Result(interrupted: true);
        }
    }

    private static string? ResponseError(ZetlCommandEnvelope command, ZetlResponseEnvelope response)
    {
        if (response.CommandId != command.CommandId
            || command.ProjectId is { } projectId && response.ProjectId is { } actualId && actualId != projectId)
            return "The command response did not match the request. Refresh before retrying.";
        return response.Status == ZetlResponseStatus.Success ? null
            : response.Error?.Message ?? $"Operation failed: {response.Status}.";
    }
}
