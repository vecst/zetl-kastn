using System.Text.Json;
using KASTN;
using Xunit;
using ZETL.Contracts;

namespace ZETL.Tests;

public sealed class KastnProjectCreationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KastnCreation", Guid.NewGuid().ToString("N"));
    private readonly ZetlStateStore store;
    private readonly ZetlProjectService service;
    private readonly KastnSettings settings;
    private readonly KastnProjectCreationWorkflow workflow;
    private readonly List<ZetlCommandEnvelope> commands = [];

    public KastnProjectCreationTests()
    {
        store = new(Path.Combine(directory, "state.json"), "creation-tests");
        service = new(store);
        settings = new(Path.Combine(directory, "settings.json"));
        workflow = new(settings);
    }

    private Task<ZetlResponseEnvelope> Execute(ZetlCommandEnvelope command)
    {
        commands.Add(command);
        return Task.FromResult(service.Execute(command));
    }

    private Task<KastnProjectCreationResult> Run(ZetlTemplateDocument? template = null, string? view = null,
        Func<KastnProjectCreationPrompt, Task<KastnProjectCreationChoice?>>? prompt = null,
        Func<KastnTemplateLanePrompt, Task<KastnTemplateLaneChoice?>>? lane = null,
        Func<ZetlCommandEnvelope, Task<ZetlResponseEnvelope>>? execute = null,
        Func<bool>? current = null, Func<KastnProjectCreationResult, Task>? complete = null) =>
        workflow.RunAsync(template ?? Template(), view,
            prompt ?? (_ => Task.FromResult<KastnProjectCreationChoice?>(new("My project", false))),
            lane ?? (_ => throw new Exception("Unexpected lane prompt.")),
            execute ?? Execute, current ?? (() => true), complete);

    internal static ZetlTemplateDocument Template() => new()
    {
        Id = "test-template", Name = "Starter", Type = ZetlTemplateTypes.Consumable,
        Buckets =
        [
            new() { Name = "Fields", Settings = new() { Kind = "Replay", DefaultKind = "Replay" },
                Seeds = ["first", "second"], Cards = [new() { Title = "Card title", Text = "third" }] },
            new() { Name = "Notes", Cards = [new() { Title = "Title only" }] }
        ]
    };

    [Fact]
    public async Task SeedsKeepBucketAndCardOrderThenAssignTheCapturedView()
    {
        var result = await Run(view: "chosen-view");
        Assert.Null(result.CreationError);
        Assert.Empty(result.SeedFailures);
        Assert.Null(result.DefaultViewError);
        Assert.False(result.Interrupted);
        Assert.Equal("chosen-view", result.Project!.DefaultViewId);
        var created = Assert.Single(store.State.Projects);
        var slips = ZetlProjectSnapshotMapper.ToSnapshot(created).Slips;
        Assert.Equal(new[] { "first", "second", "third", "" }, slips.Select(slip => slip.Text));
        Assert.Equal("Card title", slips[2].Title);
        Assert.Equal("Title only", slips[3].Title);
        Assert.All(slips, slip => Assert.Equal("template", slip.Source));
        Assert.Equal(new[] { ZetlCommandKind.CreateProject, ZetlCommandKind.AddSlip, ZetlCommandKind.AddSlip,
            ZetlCommandKind.AddSlip, ZetlCommandKind.AddSlip, ZetlCommandKind.SetProjectView }, commands.Select(c => c.Kind));
        Assert.All(commands.Skip(1), c => Assert.Equal(created.Id, c.ProjectId));
        Assert.Equal(commands.Count, commands.Select(c => c.CommandId).Distinct().Count());
        Assert.Contains("Created 'My project' from the Starter template", result.Message);
        Assert.False(workflow.IsBusy);
    }

    [Fact]
    public async Task TemplateAndMinimizePreferenceAreCapturedBeforeThePrompt()
    {
        var template = Template();
        var answer = new TaskCompletionSource<KastnProjectCreationChoice?>();
        var running = Run(template, "original-view", prompt: request =>
        {
            Assert.Equal("Starter", request.TemplateName);
            return answer.Task;
        });
        template.Name = "Changed";
        template.Buckets[0].Seeds[0] = "changed seed";
        template.Buckets[0].Cards[0].Text = "changed card";
        template.Buckets[0].Settings.Kind = "Standard";
        var preferences = new ZetlAppSettingsStore(settings.SettingsPath);
        preferences.Settings.KastnMinimizeAfterTemplate = false;
        preferences.Save();
        answer.SetResult(new("My project", false));
        var result = await running;
        Assert.Equal("Starter", result.TemplateName);
        Assert.True(result.Minimize);
        var project = ZetlProjectSnapshotMapper.ToSnapshot(Assert.Single(store.State.Projects));
        Assert.Equal("first", project.Slips[0].Text);
        Assert.Equal("third", project.Slips[2].Text);
        Assert.Equal("Replay", project.Buckets[0].Settings.Kind);
    }

    [Theory]
    [InlineData("Normal", false)]
    [InlineData("Shift", true)]
    public async Task RememberedTemporaryLaneSkipsThePickerAndMatchesActivation(string lane, bool shifted)
    {
        settings.RememberTemporaryTemplateLane(lane);
        var result = await Run(prompt: _ => Task.FromResult<KastnProjectCreationChoice?>(new("Temporary", true)));
        var payload = commands[0].Payload!.Value.Deserialize<CreateProjectCommand>(ZetlProtocolJson.Options)!;
        Assert.Equal(ZetlStateRules.TemporaryConsumableProjectKind, payload.Kind);
        Assert.Equal(lane, payload.TemporaryLane);
        Assert.Equal(shifted, payload.ActivateShifted);
        Assert.Equal("test-template", payload.SourceTemplateId);
        Assert.Equal(lane, result.Project!.TemporaryLane);
    }

    [Fact]
    public async Task RememberLanePreservesPreferencesChangedWhilePicking()
    {
        var answer = new TaskCompletionSource<KastnTemplateLaneChoice?>();
        var running = Run(prompt: _ => Task.FromResult<KastnProjectCreationChoice?>(new("Temporary", true)), lane: _ => answer.Task);
        JsonFile.WriteAtomic(settings.SettingsPath, new ZetlAppSettings { ThemeVariant = "Dark", KastnAutosave = false });
        answer.SetResult(new("Shift", true));
        var result = await running;
        Assert.NotNull(result.Project);
        Assert.Equal("Shift", settings.Current.KastnTemporaryTemplateLaneDefault);
        Assert.Equal("Dark", settings.Current.ThemeVariant);
        Assert.False(settings.Current.KastnAutosave);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("lane")]
    public async Task CancelledPromptsSendNoCommands(string at)
    {
        var result = await Run(prompt: _ => Task.FromResult<KastnProjectCreationChoice?>(
            at == "project" ? null : new("Temporary", true)), lane: _ => Task.FromResult<KastnTemplateLaneChoice?>(null));
        Assert.True(result.Cancelled);
        Assert.Empty(commands);
        Assert.Empty(store.State.Projects);
        Assert.False(workflow.IsBusy);
    }

    [Fact]
    public async Task CaptureTemplatesIgnoreARequestedTemporaryMode()
    {
        var template = Template();
        template.Type = ZetlTemplateTypes.Capture;
        var result = await Run(template, prompt: request =>
        {
            Assert.False(request.AllowTemporary);
            return Task.FromResult<KastnProjectCreationChoice?>(new("Capture", true));
        });
        Assert.Equal(ZetlStateRules.StandardProjectKind, result.Project!.Kind);
        Assert.Null(result.Project.TemporaryLane);
    }

    [Theory]
    [InlineData("invalid-template")]
    [InlineData("empty-name")]
    [InlineData("invalid-lane")]
    public async Task InvalidInputsStopBeforeProjectCreation(string invalid)
    {
        var template = Template();
        if (invalid == "invalid-template") template.Buckets[0].Name = "Scratch";
        var result = await Run(template, prompt: _ => Task.FromResult<KastnProjectCreationChoice?>(
            new(invalid == "empty-name" ? " " : "Temporary", invalid == "invalid-lane")),
            lane: _ => Task.FromResult<KastnTemplateLaneChoice?>(new("bogus", true)));
        Assert.NotNull(result.CreationError);
        Assert.Empty(commands);
        Assert.False(workflow.IsBusy);
    }

    [Theory]
    [InlineData("project-prompt")]
    [InlineData("lane-prompt")]
    [InlineData("create")]
    [InlineData("first-seed")]
    [InlineData("view")]
    public async Task InterruptedSessionStopsUnsentWorkAndNeverRemembersAStaleChoice(string at)
    {
        var current = true;
        var result = await Run(view: "view", current: () => current,
            prompt: _ =>
            {
                if (at == "project-prompt") current = false;
                return Task.FromResult<KastnProjectCreationChoice?>(new("Temporary", at == "lane-prompt"));
            }, lane: _ =>
            {
                current = false;
                return Task.FromResult<KastnTemplateLaneChoice?>(new("Shift", true));
            }, execute: async command =>
            {
                var response = await Execute(command);
                if (at == "create" && command.Kind == ZetlCommandKind.CreateProject
                    || at == "first-seed" && command.Kind == ZetlCommandKind.AddSlip
                    || at == "view" && command.Kind == ZetlCommandKind.SetProjectView) current = false;
                return response;
            });
        Assert.True(result.Interrupted);
        Assert.Equal(at switch { "create" => 1, "first-seed" => 2, "view" => 6, _ => 0 }, commands.Count);
        Assert.Equal("", settings.Current.KastnTemporaryTemplateLaneDefault);
        if (at is "create" or "first-seed" or "view") Assert.NotNull(result.Project);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong-command")]
    [InlineData("wrong-project")]
    public async Task UnconfirmedCreationNeverStartsSeeding(string fault)
    {
        var result = await Run(execute: async command =>
        {
            var response = await Execute(command);
            return fault switch
            {
                "rejected" => Failure(command, "Creation rejected"),
                "missing" => response with { Payload = null },
                "malformed" => response with { Payload = ZetlProtocolJson.ToElement("wrong shape") },
                "wrong-command" => response with { CommandId = "another-command" },
                _ => response with { ProjectId = "another-project" }
            };
        });
        Assert.Null(result.Project);
        Assert.NotNull(result.CreationError);
        Assert.Single(commands);
        Assert.False(workflow.IsBusy);
    }

    [Fact]
    public async Task SeedFailuresRemainPartialAndIndependentFieldsStillRun()
    {
        var result = await Run(view: "view", execute: async command =>
        {
            if (command.Kind == ZetlCommandKind.AddSlip
                && command.Payload!.Value.Deserialize<AddSlipCommand>(ZetlProtocolJson.Options)!.Text == "second")
            {
                commands.Add(command);
                return Failure(command, "Field rejected");
            }
            return await Execute(command);
        });
        var failure = Assert.Single(result.SeedFailures);
        Assert.Equal("Fields", failure.BucketName);
        Assert.Equal("Field rejected", failure.Error);
        Assert.Null(result.DefaultViewError);
        Assert.Equal("view", result.Project!.DefaultViewId);
        Assert.Equal(3, ZetlProjectSnapshotMapper.ToSnapshot(Assert.Single(store.State.Projects)).Slips.Count);
        Assert.Contains("some template fields could not be added", result.Message);
        Assert.Single(commands.Where(command => command.Kind == ZetlCommandKind.CreateProject));
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("missing")]
    [InlineData("wrong-view")]
    [InlineData("wrong-revision")]
    [InlineData("wrong-project")]
    public async Task FailedOrUnconfirmedViewAssignmentIsReportedWithoutRecreating(string fault)
    {
        var result = await Run(view: "view", execute: async command =>
        {
            var response = await Execute(command);
            if (command.Kind != ZetlCommandKind.SetProjectView) return response;
            var project = response.Payload!.Value.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)!;
            return fault switch
            {
                "conflict" => response with { Status = ZetlResponseStatus.Conflict, Payload = null },
                "missing" => response with { Payload = null },
                "wrong-view" => response with { Payload = ZetlProtocolJson.ToElement(project with { DefaultViewId = "other" }) },
                "wrong-revision" => response with { Payload = ZetlProtocolJson.ToElement(project with { MetadataRevision = project.MetadataRevision + 1 }) },
                _ => response with { ProjectId = "another-project" }
            };
        });
        Assert.NotNull(result.Project);
        Assert.NotNull(result.DefaultViewError);
        Assert.Contains("default view could not be assigned", result.Message);
        Assert.Single(store.State.Projects);
        Assert.Single(commands.Where(command => command.Kind == ZetlCommandKind.SetProjectView));
    }

    [Theory]
    [InlineData(ZetlCommandKind.CreateProject)]
    [InlineData(ZetlCommandKind.AddSlip)]
    [InlineData(ZetlCommandKind.SetProjectView)]
    public async Task TransportFailureReportsItsStageAndStopsRemainingCommands(ZetlCommandKind at)
    {
        var result = await Run(view: "view", execute: command =>
        {
            if (command.Kind == at) throw new IOException("Disconnected");
            return Execute(command);
        });
        if (at == ZetlCommandKind.CreateProject) Assert.Contains("Disconnected", result.CreationError);
        else
        {
            Assert.NotNull(result.Project);
            Assert.True(result.Interrupted);
            if (at == ZetlCommandKind.AddSlip) Assert.Contains("Disconnected", Assert.Single(result.SeedFailures).Error);
            else Assert.Contains("Disconnected", result.DefaultViewError);
        }
        Assert.False(workflow.IsBusy);
    }

    [Fact]
    public async Task MissingCreatedBucketsProduceFailuresWithoutSendingToAnotherBucket()
    {
        var result = await Run(execute: async command =>
        {
            var response = await Execute(command);
            if (command.Kind != ZetlCommandKind.CreateProject) return response;
            var project = response.Payload!.Value.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)!;
            return response with { Payload = ZetlProtocolJson.ToElement(project with
                { Buckets = project.Buckets.Where(bucket => bucket.Name != "Fields").ToArray() }) };
        });
        Assert.NotNull(result.Project);
        Assert.Equal(3, result.SeedFailures.Count);
        Assert.All(result.SeedFailures, issue => Assert.Equal("Fields", issue.BucketName));
        Assert.Single(commands.Where(command => command.Kind == ZetlCommandKind.AddSlip));
        Assert.Single(ZetlProjectSnapshotMapper.ToSnapshot(Assert.Single(store.State.Projects)).Slips);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-command")]
    [InlineData("wrong-bucket")]
    [InlineData("wrong-content")]
    [InlineData("wrong-project")]
    public async Task UnconfirmedFieldsAreReportedWithoutRepeatingThem(string fault)
    {
        var damaged = false;
        var result = await Run(execute: async command =>
        {
            var response = await Execute(command);
            if (command.Kind != ZetlCommandKind.AddSlip || damaged) return response;
            damaged = true;
            var slip = response.Payload!.Value.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)!;
            return fault switch
            {
                "missing" => response with { Payload = null },
                "wrong-command" => response with { CommandId = "another-command" },
                "wrong-bucket" => response with { Payload = ZetlProtocolJson.ToElement(slip with { BucketId = "other-bucket" }) },
                "wrong-content" => response with { Payload = ZetlProtocolJson.ToElement(slip with { Text = "other text" }) },
                _ => response with { ProjectId = "another-project" }
            };
        });
        Assert.NotNull(result.Project);
        Assert.Single(result.SeedFailures);
        Assert.Equal(4, commands.Count(command => command.Kind == ZetlCommandKind.AddSlip));
        Assert.Equal(4, ZetlProjectSnapshotMapper.ToSnapshot(Assert.Single(store.State.Projects)).Slips.Count);
    }

    [Fact]
    public async Task BusyGateIncludesFinalNavigationAndIsReleasedOnFailure()
    {
        var completing = new TaskCompletionSource();
        var arrived = new TaskCompletionSource();
        var running = Run(complete: _ => { arrived.SetResult(); return completing.Task; });
        await arrived.Task;
        Assert.True(workflow.IsBusy);
        var duplicate = await Run();
        Assert.True(duplicate.Cancelled);
        Assert.Single(store.State.Projects);
        completing.SetException(new InvalidOperationException("Navigation adapter failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        Assert.False(workflow.IsBusy);
        Assert.NotNull((await Run()).Project);
    }

    private static ZetlResponseEnvelope Failure(ZetlCommandEnvelope command, string message) => new()
    {
        CommandId = command.CommandId, ProjectId = command.ProjectId, Status = ZetlResponseStatus.ValidationError,
        Error = new() { Code = "test", Message = message }
    };

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
