using System.Collections.Concurrent;
using System.Text.Json;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlProjectServiceTests
{
    [Fact] public void RetriedAddDoesNotDuplicate()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);
        var command = AddSlipCommand(
            "add-once",
            project.Id,
            bucket.Id,
            "one captured slip");

        var first = service.Execute(command);
        var retry = service.Execute(command);

        AssertEqual(ZetlResponseStatus.Success, first.Status, "Initial add should succeed.");
        AssertTrue(ReferenceEquals(first, retry), "A duplicate command ID should return the cached response.");
        AssertEqual(1, bucket.Slips.Count, "Retrying an add must not duplicate the slip.");
    }

    [Fact] public void StaleEditReturnsCurrentSlip()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "original", "copy");
        var service = new ZetlProjectService(store);

        var firstEdit = ZetlCommandEnvelope.Create(
            "edit-current",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = "newer" },
            project.Id,
            note.Id,
            expectedTargetRevision: 1);
        var firstResponse = service.Execute(firstEdit);
        var staleEdit = ZetlCommandEnvelope.Create(
            "edit-stale",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = "older overwrite" },
            project.Id,
            note.Id,
            expectedTargetRevision: 1);
        var staleResponse = service.Execute(staleEdit);
        var current = staleResponse.Conflict?.Current.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options);

        AssertEqual(ZetlResponseStatus.Success, firstResponse.Status, "The current edit should succeed.");
        AssertEqual(ZetlResponseStatus.Conflict, staleResponse.Status, "The stale edit should conflict.");
        AssertEqual(2L, staleResponse.Conflict?.ActualRevision, "Conflict should report the latest revision.");
        AssertEqual("newer", current?.Text, "Conflict should return the current slip.");
        AssertEqual("newer", note.Text, "A stale edit must not overwrite the slip.");
    }

    [Fact] public void UnrelatedCaptureDoesNotConflictWithRename()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);
        var metadataRevision = project.MetadataRevision;

        var addResponse = service.Execute(AddSlipCommand(
            "add-before-rename",
            project.Id,
            bucket.Id,
            "unrelated capture"));
        var renameResponse = service.Execute(ZetlCommandEnvelope.Create(
            "rename-after-capture",
            ZetlCommandKind.RenameProject,
            new RenameProjectCommand { Name = "Renamed" },
            project.Id,
            expectedTargetRevision: metadataRevision));

        AssertEqual(ZetlResponseStatus.Success, addResponse.Status, "Capture should succeed.");
        AssertEqual(ZetlResponseStatus.Success, renameResponse.Status, "Unrelated capture should not conflict with rename.");
        AssertEqual("Renamed", project.Name, "Rename should update the project.");
        AssertEqual(metadataRevision + 1, project.MetadataRevision, "Rename should advance only metadata revision.");
    }

    [Fact] public void ConcurrentAddsAreSerialized()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);
        var responses = new ConcurrentBag<ZetlResponseEnvelope>();

        Parallel.For(
            0,
            40,
            index =>
            {
                responses.Add(service.Execute(AddSlipCommand(
                    $"parallel-{index:D2}",
                    project.Id,
                    bucket.Id,
                    $"note {index:D2}")));
            });

        AssertEqual(40, responses.Count, "Every concurrent command should return.");
        AssertTrue(
            responses.All(response => response.Status == ZetlResponseStatus.Success),
            "Every concurrent add should succeed.");
        AssertEqual(40, bucket.Slips.Count, "Serialized adds should preserve every slip.");
        AssertEqual(
            40,
            bucket.Slips.Select(note => note.Id).Distinct(StringComparer.Ordinal).Count(),
            "Every added slip should have a unique ID.");

        var reloaded = new ZetlStateStore(temp.StatePath);
        AssertEqual(
            40,
            reloaded.State.Projects.Single().Buckets.Single(item => item.Name == "Inbox").Slips.Count,
            "Every acknowledged add should be durable.");
    }

    [Fact] public void DirectAndServiceMutationsShareOneWriter()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);

        Parallel.Invoke(
            () =>
            {
                for (var index = 0; index < 20; index++)
                {
                    store.AddNote(bucket, $"direct {index:D2}", "copy");
                }
            },
            () =>
            {
                for (var index = 0; index < 20; index++)
                {
                    var response = service.Execute(AddSlipCommand(
                        $"mixed-{index:D2}",
                        project.Id,
                        bucket.Id,
                        $"service {index:D2}"));
                    AssertEqual(
                        ZetlResponseStatus.Success,
                        response.Status,
                        "Service mutation should succeed beside direct mutations.");
                }
            });

        AssertEqual(40, bucket.Slips.Count, "Direct and service mutations should preserve every slip.");
        var reloaded = new ZetlStateStore(temp.StatePath);
        AssertEqual(
            40,
            reloaded.State.Projects.Single().Buckets.Single(item => item.Name == "Inbox").Slips.Count,
            "The shared writer monitor should make every mixed mutation durable.");
    }

    [Fact] public void CreateProjectCanMarkNormalLaneTemporaryConsumable()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "create-temp",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "One Shot",
                Kind = ZetlStateStore.TemporaryConsumableProjectKind,
                SourceTemplateId = "template",
                TemporaryLane = ZetlStateStore.NormalLane,
                Buckets = [new CreateBucketDefinition { Name = "Queue" }]
            }));
        var snapshot = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Create did not return a project snapshot.");

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Normal-lane temporary creation should succeed.");
        AssertEqual(ZetlStateStore.TemporaryConsumableProjectKind, snapshot.Kind, "Snapshot should expose the project kind.");
        AssertEqual("template", snapshot.SourceTemplateId, "Snapshot should expose the source template id.");
        AssertEqual(ZetlStateStore.NormalLane, snapshot.TemporaryLane, "Snapshot should expose the owning lane.");
        AssertEqual(snapshot.Id, store.ActiveProject?.Id, "The created temporary project should be normal-lane active.");
    }

    [Fact] public void ListProjectsShowsUnderlyingLaneBehindTemporaryConsumable()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath);
        var service = new ZetlProjectService(store);

        var durable = service.Execute(ZetlCommandEnvelope.Create(
            "create-durable",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "Durable",
                Buckets = [new CreateBucketDefinition { Name = "Inbox" }]
            })).Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Durable create did not return a project snapshot.");

        var temporary = service.Execute(ZetlCommandEnvelope.Create(
            "create-temporary",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "Temporary",
                Kind = ZetlStateStore.TemporaryConsumableProjectKind,
                TemporaryLane = ZetlStateStore.NormalLane,
                Buckets = [new CreateBucketDefinition { Name = "Queue" }]
            })).Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Temporary create did not return a project snapshot.");

        var list = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "list",
            Kind = ZetlCommandKind.ListProjects
        }).Payload?.Deserialize<List<ZetlProjectSummary>>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("List did not return project summaries.");

        AssertEqual(
            ZetlStateStore.NormalLane,
            list.Single(project => project.Id == temporary.Id).ActiveLane,
            "The temporary project should be the active normal-lane overlay.");
        AssertEqual(
            ZetlStateStore.NormalLane,
            list.Single(project => project.Id == durable.Id).UnderlyingLane,
            "The previous durable project should be shown behind the temporary normal lane.");
    }

    [Fact] public void SetActiveProjectAssignsRequestedLane()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath);
        var service = new ZetlProjectService(store);

        var project = service.Execute(ZetlCommandEnvelope.Create(
            "create-shift-project",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "Lane Target",
                Buckets = [new CreateBucketDefinition { Name = "Inbox" }]
            })).Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Create did not return a project snapshot.");
        service.Execute(ZetlCommandEnvelope.Create(
            "create-normal-project",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "Main Target",
                Buckets = [new CreateBucketDefinition { Name = "Inbox" }]
            }));

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "set-normal-active",
            ZetlCommandKind.SetActiveProject,
            new SetActiveProjectCommand { ActivateShifted = false },
            project.Id));
        AssertEqual(ZetlResponseStatus.Success, response.Status, "Setting the normal lane should succeed.");

        response = service.Execute(ZetlCommandEnvelope.Create(
            "set-shift-active",
            ZetlCommandKind.SetActiveProject,
            new SetActiveProjectCommand { ActivateShifted = true },
            project.Id));
        var list = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "list-after-active",
            Kind = ZetlCommandKind.ListProjects
        }).Payload?.Deserialize<List<ZetlProjectSummary>>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("List did not return project summaries.");

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Setting the active lane should succeed.");
        AssertEqual(
            ZetlStateStore.ShiftLane,
            list.Single(item => item.Id == project.Id).ActiveLane,
            "The project summary should report the requested active lane after moving lanes.");
    }

    [Fact] public void CreateProjectCanMarkShiftLaneTemporaryConsumable()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "create-temp-shift",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "One Shot",
                Kind = ZetlStateStore.TemporaryConsumableProjectKind,
                SourceTemplateId = "template",
                TemporaryLane = ZetlStateStore.ShiftLane,
                ActivateShifted = true,
                Buckets = [new CreateBucketDefinition { Name = "Queue" }]
            }));
        var snapshot = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Create did not return a project snapshot.");

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Shift-lane temporary creation should succeed when activated in Shift.");
        AssertEqual(ZetlStateStore.TemporaryConsumableProjectKind, snapshot.Kind, "Snapshot should expose the project kind.");
        AssertEqual(ZetlStateStore.ShiftLane, snapshot.TemporaryLane, "Snapshot should expose the owning lane.");
        AssertEqual(snapshot.Id, store.ShiftActiveProject?.Id, "The created temporary project should be Shift-lane active.");
        AssertEqual<ZetlProject?>(null, store.ActiveProject, "Creating in Shift should not activate the normal lane.");
    }

    [Fact] public void CreateProjectRejectsTemporaryLaneMismatch()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "create-temp-mismatch",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "One Shot",
                Kind = ZetlStateStore.TemporaryConsumableProjectKind,
                SourceTemplateId = "template",
                TemporaryLane = ZetlStateStore.ShiftLane,
                Buckets = [new CreateBucketDefinition { Name = "Queue" }]
            }));

        AssertEqual(ZetlResponseStatus.ValidationError, response.Status, "Temporary lane metadata must match activation.");
        AssertEqual("temporary_lane_mismatch", response.Error?.Code, "Lane mismatch should fail explicitly.");
        AssertEqual(0, store.State.Projects.Count, "Rejected creation should not leave a project behind.");
    }

    [Fact] public void CreateTemporaryProjectFromReplayUsesReviewWithoutCopyingReviewBucket()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath, "service-session");
        var source = store.CreateProject("Archived Source", ["Queue"], "Queue");
        var queue = source.Buckets.Single(bucket => bucket.Name == "Queue");
        store.SetBucketKind(queue, "Replay");
        var first = store.AddNote(queue, "first", "copy");
        store.AddNote(queue, "second", "copy");
        AssertTrue(
            store.TryConsumeReplayNoteToReview(source, queue, first.Id, out var reviewBucket),
            "Replay consume should create a review bucket.");
        store.SetBucketKind(queue, "Standard");
        store.SetProjectStatus(source, ZetlStateStore.ArchivedStatus);
        var service = new ZetlProjectService(store);

        var list = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "list-replay-source",
            Kind = ZetlCommandKind.ListProjects
        });
        var sourceSummary = list.Payload?.Deserialize<IReadOnlyList<ZetlProjectSummary>>(
            ZetlProtocolJson.Options)?.Single(project => project.Id == source.Id);
        var create = service.Execute(ZetlCommandEnvelope.Create(
            "create-temp-from-replay",
            ZetlCommandKind.CreateTemporaryProjectFromReplay,
            new CreateTemporaryProjectFromReplayCommand
            {
                Name = "Temporary Replay",
                TemporaryLane = ZetlStateStore.NormalLane,
                ActivateShifted = false
            },
            source.Id));
        var created = create.Payload?.Deserialize<ZetlProjectSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Temporary replay creation returned no snapshot.");
        var targetQueue = created.Buckets.Single(bucket => bucket.Name == "Queue");
        var targetTexts = created.Slips
            .Where(slip => slip.BucketId == targetQueue.Id)
            .Select(slip => slip.Text)
            .ToList();

        AssertTrue(
            sourceSummary?.CanCreateTemporaryFromReplay == true,
            "Archived projects with only a replay review link should be eligible.");
        AssertEqual(ZetlResponseStatus.Success, create.Status, "Replay source should create a temporary project.");
        AssertEqual(ZetlStateStore.TemporaryConsumableProjectKind, created.Kind, "Created project should be temporary.");
        AssertEqual(ZetlStateStore.NormalLane, created.TemporaryLane, "Created project should use the requested lane.");
        AssertEqual(created.Id, store.State.ActiveProjectId, "Created temporary project should occupy the requested lane.");
        AssertEqual("Replay", targetQueue.Settings.Kind, "Recovered queue should be replayable.");
        AssertTrue(
            targetQueue.Settings.ReplayReviewBucketId is null,
            "Recovered queue should create its own review bucket when consumed.");
        AssertFalse(
            created.Buckets.Any(bucket => bucket.Name == reviewBucket?.Name),
            "The source review bucket should not be copied into the temporary project.");
        AssertEqual(2, targetTexts.Count, "Review plus remaining queue slips should be recovered.");
        AssertEqual("first", targetTexts[0], "Consumed review slips should be restored first.");
        AssertEqual("second", targetTexts[1], "Remaining queue slips should follow review slips.");
        AssertTrue(
            created.Slips.All(slip => string.Equals(slip.SessionId, "service-session", StringComparison.Ordinal)),
            "Recovered slips need the current session id so Replay can consume them immediately.");
        AssertEqual(ZetlStateStore.ArchivedStatus, source.Status, "The source archived project should remain archived.");
    }

    [Fact] public void SlipInclusionToggleRoundTrips()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "keepable", "copy");
        var service = new ZetlProjectService(store);

        var exclude = service.Execute(ZetlCommandEnvelope.Create(
            "slip-exclude",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = note.Text, ExcludedFromViews = true },
            project.Id,
            note.Id,
            note.Revision));
        var excluded = exclude.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Exclude update did not return a slip.");
        AssertEqual(ZetlResponseStatus.Success, exclude.Status, "Toggling inclusion should succeed.");
        AssertTrue(excluded.ExcludedFromViews, "The snapshot should report the slip excluded.");
        AssertTrue(note.ExcludedFromViews, "The stored note should be excluded.");

        // A later text edit that omits the flag preserves the exclusion.
        var editText = service.Execute(ZetlCommandEnvelope.Create(
            "slip-edit-text",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = "edited" },
            project.Id,
            note.Id,
            excluded.Revision));
        var afterEdit = editText.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Text edit did not return a slip.");
        AssertTrue(afterEdit.ExcludedFromViews, "Omitting the flag should preserve exclusion.");

        // Re-including clears it.
        var include = service.Execute(ZetlCommandEnvelope.Create(
            "slip-include",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = afterEdit.Text, ExcludedFromViews = false },
            project.Id,
            note.Id,
            afterEdit.Revision));
        var included = include.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Include update did not return a slip.");
        AssertTrue(!included.ExcludedFromViews, "Re-including should clear exclusion.");
        AssertTrue(!note.ExcludedFromViews, "The stored note should be included again.");
    }

    [Fact] public void SlipAlignmentRoundTrips()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "centerable", "copy");
        var service = new ZetlProjectService(store);

        var center = service.Execute(ZetlCommandEnvelope.Create(
            "slip-center",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = note.Text, Align = "center" },
            project.Id,
            note.Id,
            note.Revision));
        var centered = center.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Align update did not return a slip.");
        AssertEqual(ZetlResponseStatus.Success, center.Status, "Setting alignment should succeed.");
        AssertEqual("center", centered.Align, "The snapshot should report the alignment.");
        AssertEqual("center", note.Align, "The stored note should carry the alignment.");

        // A later text edit that omits alignment preserves it.
        var editText = service.Execute(ZetlCommandEnvelope.Create(
            "slip-edit-text",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = "edited" },
            project.Id,
            note.Id,
            centered.Revision));
        var afterEdit = editText.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Text edit did not return a slip.");
        AssertEqual("center", afterEdit.Align, "Omitting alignment should preserve it.");

        // Left normalizes back to the default (null), keeping the JSON clean.
        var left = service.Execute(ZetlCommandEnvelope.Create(
            "slip-left",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = afterEdit.Text, Align = "left" },
            project.Id,
            note.Id,
            afterEdit.Revision));
        var lefted = left.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Left update did not return a slip.");
        AssertTrue(lefted.Align is null, "Left should normalize to the default (no alignment).");
        AssertTrue(note.Align is null, "The stored note should carry no alignment for left.");
    }

    [Fact] public void SlipBlockKindAndCheckedRoundTrip()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "todo", "copy");
        var service = new ZetlProjectService(store);

        ZetlSlipSnapshot Update(string id, UpdateSlipCommand command, long revision) =>
            service.Execute(ZetlCommandEnvelope.Create(
                id, ZetlCommandKind.UpdateSlip, command, project.Id, note.Id, revision))
                .Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException($"{id} did not return a slip.");

        // Become a task, then check it.
        var task = Update("to-task", new UpdateSlipCommand { Text = note.Text, BlockKind = "task" }, note.Revision);
        AssertEqual("task", task.BlockKind, "The snapshot should report the task kind.");
        var checkedSlip = Update("check", new UpdateSlipCommand { Text = task.Text, Checked = true }, task.Revision);
        AssertTrue(checkedSlip.Checked, "Checking a task should persist.");

        // Omitting the kind on a text edit preserves both kind and checked.
        var edited = Update("edit", new UpdateSlipCommand { Text = "done soon" }, checkedSlip.Revision);
        AssertEqual("task", edited.BlockKind, "Omitting the kind preserves it.");
        AssertTrue(edited.Checked, "Omitting checked preserves it.");

        // Switching to a non-task kind clears the now-meaningless checked flag.
        var bulleted = Update("to-bullet", new UpdateSlipCommand { Text = edited.Text, BlockKind = "bullet" }, edited.Revision);
        AssertEqual("bullet", bulleted.BlockKind, "The kind should become bullet.");
        AssertTrue(!bulleted.Checked, "Leaving task should clear the checked flag.");

        // An unknown kind normalizes to a plain paragraph ("").
        var plain = Update("to-plain", new UpdateSlipCommand { Text = bulleted.Text, BlockKind = "paragraph" }, bulleted.Revision);
        AssertEqual("", plain.BlockKind, "An unknown kind normalizes to no marker.");
        AssertEqual("", note.BlockKind, "The stored note carries no marker for an unknown kind.");

        // Plain slips can still carry checked state when their bucket renders as a
        // checklist; the effective task kind is inherited at render time.
        var inheritedChecked = Update("plain-check", new UpdateSlipCommand { Text = plain.Text, Checked = true }, plain.Revision);
        AssertTrue(inheritedChecked.Checked, "Checking an inherited bucket-task slip should persist.");

        // Toggling a kind off sends "" explicitly through the editor's toggle-to-clear path.
        var reBulleted = Update("re-bullet", new UpdateSlipCommand { Text = inheritedChecked.Text, BlockKind = "bullet" }, inheritedChecked.Revision);
        var composedChecked = Update(
            "composed-check",
            new UpdateSlipCommand { Text = reBulleted.Text, Checked = true, IgnoreBucketRenderKind = true },
            reBulleted.Revision);
        AssertTrue(composedChecked.Checked, "Checking a composed bucket-task/non-task slip should persist.");
        AssertTrue(composedChecked.IgnoreBucketRenderKind, "Slip bucket-style opt-out should round-trip.");

        var cleared = Update("clear", new UpdateSlipCommand { Text = composedChecked.Text, BlockKind = "" }, composedChecked.Revision);
        AssertEqual("", cleared.BlockKind, "An explicit empty kind clears the marker (toggle off).");
    }

    [Fact] public void SlipInlineStylesRoundTripAndNormalize()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "hello world", "copy");
        var service = new ZetlProjectService(store);

        ZetlSlipSnapshot Update(string id, UpdateSlipCommand command, long revision) =>
            service.Execute(ZetlCommandEnvelope.Create(
                id, ZetlCommandKind.UpdateSlip, command, project.Id, note.Id, revision))
                .Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException($"{id} did not return a slip.");

        var styled = Update(
            "style",
            new UpdateSlipCommand
            {
                Text = note.Text,
                InlineStyles =
                [
                    new ZetlInlineStyleRange { Start = 6, Length = 5, Kind = " BOLD " },
                    new ZetlInlineStyleRange { Start = 0, Length = 5, Kind = "link", Href = " https://example.com " },
                    new ZetlInlineStyleRange { Start = 6, Length = 50, Kind = "italic", Href = "ignored" },
                    new ZetlInlineStyleRange { Start = 6, Length = 5, Kind = "bold" },
                    new ZetlInlineStyleRange { Start = 0, Length = 5, Kind = "missing-link" },
                    new ZetlInlineStyleRange { Start = 4, Length = 0, Kind = "strike" }
                ]
            },
            note.Revision);

        AssertEqual(3, styled.InlineStyles.Count, "Valid inline ranges should persist after normalization.");
        AssertEqual(ZetlInlineStyleKinds.Link, styled.InlineStyles[0].Kind, "The link range should sort first.");
        AssertEqual("https://example.com", styled.InlineStyles[0].Href, "Link hrefs should be trimmed.");
        AssertEqual(ZetlInlineStyleKinds.Bold, styled.InlineStyles[1].Kind, "Bold should normalize.");
        AssertEqual(ZetlInlineStyleKinds.Italic, styled.InlineStyles[2].Kind, "Italic should normalize.");
        AssertEqual(5, styled.InlineStyles[2].Length, "Ranges should clamp to the current text length.");
        AssertEqual(3, note.InlineStyles.Count, "The stored note should carry normalized ranges.");

        var shortened = Update(
            "shorten",
            new UpdateSlipCommand { Text = "hello" },
            styled.Revision);
        AssertEqual(1, shortened.InlineStyles.Count, "Omitting inline styles preserves and reclamps existing ranges.");
        AssertEqual(ZetlInlineStyleKinds.Link, shortened.InlineStyles[0].Kind, "Only the still-valid range should remain.");

        var cleared = Update(
            "clear-inline",
            new UpdateSlipCommand { Text = shortened.Text, InlineStyles = [] },
            shortened.Revision);
        AssertEqual(0, cleared.InlineStyles.Count, "An explicit empty style list clears inline styling.");
    }

    [Fact] public void WholeSlipStylesRoundTripAndPreserveWhenOmitted()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "styled note", "copy");
        var service = new ZetlProjectService(store);

        ZetlSlipSnapshot Update(string id, UpdateSlipCommand command, long revision) =>
            service.Execute(ZetlCommandEnvelope.Create(
                id, ZetlCommandKind.UpdateSlip, command, project.Id, note.Id, revision))
                .Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException($"{id} did not return a slip.");

        var styled = Update(
            "style-on",
            new UpdateSlipCommand { Text = note.Text, Bold = true, Italic = true, Strike = true },
            note.Revision);
        AssertTrue(styled.Bold, "Whole-slip bold should persist.");
        AssertTrue(styled.Italic, "Whole-slip italic should persist.");
        AssertTrue(styled.Strike, "Whole-slip strike should persist.");

        var edited = Update("edit", new UpdateSlipCommand { Text = "edited note" }, styled.Revision);
        AssertTrue(
            edited.Bold && edited.Italic && edited.Strike,
            "Omitting the style flags on a text edit preserves them.");

        var plain = Update(
            "style-off",
            new UpdateSlipCommand { Text = edited.Text, Bold = false, Italic = false, Strike = false },
            edited.Revision);
        AssertTrue(
            !plain.Bold && !plain.Italic && !plain.Strike,
            "Explicit false clears each whole-slip style.");
    }

    [Fact] public void WholeSlipTypographyRoundTripsNormalizesAndClears()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "styled note", "copy");
        var service = new ZetlProjectService(store);

        ZetlSlipSnapshot Update(string id, UpdateSlipCommand command, long revision) =>
            service.Execute(ZetlCommandEnvelope.Create(
                id, ZetlCommandKind.UpdateSlip, command, project.Id, note.Id, revision))
                .Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException($"{id} did not return a slip.");

        var styled = Update(
            "typography-on",
            new UpdateSlipCommand
            {
                Text = note.Text,
                FontFamily = "  Segoe UI  ",
                FontSize = 4,
                TextColor = " #a1b2c3 "
            },
            note.Revision);
        AssertEqual("Segoe UI", styled.FontFamily, "Font families should be trimmed.");
        AssertEqual(8, styled.FontSize, "Positive font sizes should clamp to the supported range.");
        AssertEqual("#A1B2C3", styled.TextColor, "Text colors should normalize to uppercase hex.");
        AssertEqual("Segoe UI", note.FontFamily, "The normalized family should persist on the note.");
        AssertEqual(8, note.FontSize, "The normalized size should persist on the note.");
        AssertEqual("#A1B2C3", note.TextColor, "The normalized color should persist on the note.");

        var edited = Update(
            "typography-preserve",
            new UpdateSlipCommand { Text = "edited note" },
            styled.Revision);
        AssertEqual("Segoe UI", edited.FontFamily, "Omitting a family should preserve it.");
        AssertEqual(8, edited.FontSize, "Omitting a size should preserve it.");
        AssertEqual("#A1B2C3", edited.TextColor, "Omitting a color should preserve it.");

        var capped = Update(
            "typography-cap",
            new UpdateSlipCommand
            {
                Text = edited.Text,
                FontFamily = new string('F', ZetlSlipTypography.MaximumFontFamilyLength + 20),
                FontSize = 500
            },
            edited.Revision);
        AssertEqual(
            ZetlSlipTypography.MaximumFontFamilyLength,
            capped.FontFamily.Length,
            "Font family storage should be bounded.");
        AssertEqual(96, capped.FontSize, "Large font sizes should clamp to the supported range.");

        var invalidColor = Update(
            "typography-invalid-color",
            new UpdateSlipCommand { Text = capped.Text, TextColor = "red" },
            capped.Revision);
        AssertEqual("", invalidColor.TextColor, "Invalid CSS/color text must not persist.");

        var cleared = Update(
            "typography-clear",
            new UpdateSlipCommand
            {
                Text = invalidColor.Text,
                FontFamily = "",
                FontSize = 0,
                TextColor = ""
            },
            invalidColor.Revision);
        AssertEqual("", cleared.FontFamily, "An empty family should restore theme inheritance.");
        AssertEqual(0, cleared.FontSize, "Size zero should restore theme inheritance.");
        AssertEqual("", cleared.TextColor, "An empty color should restore theme inheritance.");
    }

    [Fact] public void DividerNoteAddsWithoutContent()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);

        // A divider is a content-less structural note, so the title-or-note rule is waived.
        var divider = service.Execute(ZetlCommandEnvelope.Create(
            "add-divider",
            ZetlCommandKind.AddSlip,
            new AddSlipCommand { BucketId = bucket.Id, Text = "", Source = "kastn", BlockKind = "divider" },
            project.Id));
        AssertEqual(ZetlResponseStatus.Success, divider.Status, "A content-less divider note should be allowed.");
        var snapshot = divider.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Divider add returned no slip.");
        AssertEqual("divider", snapshot.BlockKind, "The new note should carry the divider kind.");

        // Toggling a content-less divider's visibility re-sends its empty text, which must
        // not trip the title-or-note requirement.
        var hide = service.Execute(ZetlCommandEnvelope.Create(
            "hide-divider",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = snapshot.Text, ExcludedFromViews = true },
            project.Id,
            snapshot.Id,
            snapshot.Revision));
        AssertEqual(ZetlResponseStatus.Success, hide.Status, "Hiding a divider should be allowed.");

        // A non-divider note with no content is still rejected.
        var empty = service.Execute(ZetlCommandEnvelope.Create(
            "add-empty",
            ZetlCommandKind.AddSlip,
            new AddSlipCommand { BucketId = bucket.Id, Text = "", Source = "kastn" },
            project.Id));
        AssertEqual(ZetlResponseStatus.ValidationError, empty.Status, "A content-less plain note is still rejected.");
    }

    [Fact] public void StructuralNoteIsSkippedByPop()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);

        // Activate the bucket in Pop mode, then capture a real note followed by a divider.
        store.SetActiveProject(project.Id);
        store.SetActiveBucket(project, bucket.Id);
        store.SetBucketPopMode(bucket, true);
        var note = store.AddNote(bucket, "value", "copy");
        store.AddNote(bucket, "", "copy", blockKind: "divider");

        // A trailing divider must not block popping the content note above it (Pop only
        // looks at the last note, so a structural one would otherwise shadow it).
        var popped = store.TryPopLastMatchingActiveNote("value", shifted: false, out _, out var poppedNote);
        AssertTrue(popped, "A trailing divider should not block Pop of the note above it.");
        AssertEqual(note.Id, poppedNote?.Id, "Pop should remove the content note, not the divider.");
    }

    [Fact] public void BucketRenderKindRoundTrips()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out _);
        var service = new ZetlProjectService(store);

        var group = service.Execute(ZetlCommandEnvelope.Create(
            "add-group",
            ZetlCommandKind.AddBucket,
            new AddBucketCommand { Name = "Group", RenderKind = "group" },
            project.Id));
        var snapshot = group.Payload?.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Group add returned no bucket.");
        AssertEqual("group", snapshot.RenderKind, "A group bucket carries its render kind.");

        // An unknown render kind normalizes to a normal bucket.
        var weird = service.Execute(ZetlCommandEnvelope.Create(
            "add-weird",
            ZetlCommandKind.AddBucket,
            new AddBucketCommand { Name = "Weird", RenderKind = "carousel" },
            project.Id));
        var weirdSnapshot = weird.Payload?.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Bucket add returned nothing.");
        AssertEqual("", weirdSnapshot.RenderKind, "An unknown render kind normalizes to a normal bucket.");
    }

    [Fact] public void BucketRenderKindIsDurable()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var service = new ZetlProjectService(store);

        var added = service.Execute(ZetlCommandEnvelope.Create(
            "add-group-durable",
            ZetlCommandKind.AddBucket,
            new AddBucketCommand { Name = "Group", RenderKind = "group" },
            project.Id));
        var addedSnapshot = added.Payload?.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Group add returned no bucket.");
        var updated = service.Execute(ZetlCommandEnvelope.Create(
            "update-inbox-render-kind",
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand { Name = inbox.Name, RenderKind = "task" },
            project.Id,
            inbox.Id,
            expectedTargetRevision: inbox.Revision));

        AssertEqual(ZetlResponseStatus.Success, updated.Status, "The render-kind update should succeed.");

        // Success promises a durable write, so a fresh store must see both kinds.
        var reloaded = new ZetlStateStore(temp.StatePath);
        var reloadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
        AssertEqual(
            "group",
            reloadedProject.Buckets.Single(item => item.Id == addedSnapshot.Id).RenderKind,
            "An added bucket's render kind should be on disk.");
        AssertEqual(
            "task",
            reloadedProject.Buckets.Single(item => item.Id == inbox.Id).RenderKind,
            "An updated bucket's render kind should be on disk.");
        AssertEqual(1L, addedSnapshot.Revision, "A newly added bucket should start at revision 1.");
    }

    [Theory]
    [InlineData("Scratch")]
    [InlineData(" deleted ")]
    public void RenameToAReservedBucketNameIsRejected(string reserved)
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var note = store.AddNote(inbox, "keep me live", "copy");
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "rename-reserved",
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand { Name = reserved },
            project.Id,
            inbox.Id,
            expectedTargetRevision: inbox.Revision));
        var renamedDirectly = store.UpdateBucketName(inbox, reserved);

        AssertEqual(ZetlResponseStatus.ValidationError, response.Status, "IPC rename to a reserved name should be rejected.");
        AssertFalse(renamedDirectly, "A direct store rename to a reserved name should be refused.");
        var reloaded = new ZetlStateStore(temp.StatePath);
        var reloadedProject = reloaded.State.Projects.Single(item => item.Id == project.Id);
        var reloadedInbox = reloadedProject.Buckets.Single(item => item.Id == inbox.Id);
        AssertEqual("Inbox", reloadedInbox.Name, "The bucket should keep its name.");
        AssertEqual("Standard", reloadedInbox.Settings.Kind, "The bucket must not become a protected bucket.");
        AssertEqual(note.Id, reloadedInbox.Slips.Single().Id, "The bucket's slips should stay live.");
        AssertEqual(
            1,
            reloadedProject.Buckets.Count(ZetlStateStore.IsScratchBucket),
            "There should still be exactly one Scratch bucket.");
    }

    [Theory]
    [InlineData("scratch")]
    [InlineData("Deleted")]
    public void AddingAReservedBucketNameIsRejected(string reserved)
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out _);
        var service = new ZetlProjectService(store);
        var bucketCount = project.Buckets.Count;

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "add-reserved",
            ZetlCommandKind.AddBucket,
            new AddBucketCommand { Name = reserved },
            project.Id));

        AssertEqual(ZetlResponseStatus.ValidationError, response.Status, "Adding a reserved bucket name should be rejected.");
        AssertEqual(bucketCount, project.Buckets.Count, "No bucket should be created.");
    }

    [Fact] public void ScratchBucketSettingsStillUpdate()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out _);
        var scratch = store.GetScratchBucket(project);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "scratch-settings",
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand
            {
                Name = scratch.Name,
                Settings = new ZETL.Contracts.ZetlBucketSettings { DefaultTsvRowLength = 3 }
            },
            project.Id,
            scratch.Id,
            expectedTargetRevision: scratch.Revision));

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Scratch keeps its own name while its settings change.");
        AssertEqual(3, scratch.Settings.DefaultTsvRowLength, "The Scratch settings change should apply.");
    }

    // A slip copy (Replay review, Pop recovery, temporary Replay) must carry every
    // authored property. Adding a ZetlSlip property fails here until the test sets
    // it, which forces a decision about whether copies carry it.
    [Fact] public void SlipCopiesCarryEveryAuthoredProperty()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var queue);
        store.SetBucketKind(queue, "Replay");
        var source = store.AddNote(queue, "A1\tB1", "copy");
        store.SetNoteImage(project, source, new ZetlClipboardImage([1, 2, 3], 3, 1));
        source.Type = ZetlSlipType.Picture;
        source.Title = "Copied title";
        source.RichHtml = "<b>A1</b>";
        source.ReplayFormats = [new ZetlClipboardFormatData(49161, [1, 2, 3], "Biff12")];
        source.ExcludedFromViews = true;
        source.Align = "center";
        source.BlockKind = "task";
        source.IgnoreBucketRenderKind = true;
        source.Checked = true;
        source.Bold = true;
        source.Italic = true;
        source.Strike = true;
        source.FontFamily = "Georgia";
        source.FontSize = 14;
        source.TextColor = "#123456";
        source.InlineStyles = [new ZetlInlineStyleRange { Start = 0, Length = 2, Kind = "bold" }];
        source.CaptureOrigin = new ZetlCaptureOrigin
        {
            ApplicationName = "Calc",
            ProcessName = "soffice",
            WindowTitle = "Sheet"
        };

        AssertTrue(
            store.TryConsumeReplayNoteToReview(project, queue, source.Id, out _, out _, out var copy)
                && copy is not null,
            "Replay should produce a review copy.");

        // Identity and lifecycle belong to the copy; everything else must match.
        string[] ownedByCopy =
        [
            nameof(ZetlSlip.Id), nameof(ZetlSlip.Revision), nameof(ZetlSlip.Source),
            nameof(ZetlSlip.SessionId), nameof(ZetlSlip.CreatedAtUtc),
            nameof(ZetlSlip.DeletedFromBucketId), nameof(ZetlSlip.DeletedAtUtc)
        ];
        var blank = new ZetlSlip();
        foreach (var property in typeof(ZetlSlip).GetProperties()
            .Where(item => item.CanRead && item.CanWrite && !ownedByCopy.Contains(item.Name)))
        {
            var expected = JsonSerializer.Serialize(property.GetValue(source));
            AssertTrue(
                expected != JsonSerializer.Serialize(property.GetValue(blank)),
                $"Set {property.Name} to a non-default value so the copy check covers it.");
            AssertEqual(expected, JsonSerializer.Serialize(property.GetValue(copy)), $"The copy should carry {property.Name}.");
        }
    }

    [Fact] public void BucketAndSlipCommandsRoundTrip()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var service = new ZetlProjectService(store);

        var addBucket = service.Execute(ZetlCommandEnvelope.Create(
            "bucket-add",
            ZetlCommandKind.AddBucket,
            new AddBucketCommand
            {
                Name = "Drafts",
                Settings = new ZETL.Contracts.ZetlBucketSettings
                {
                    Kind = "Standard",
                    DefaultKind = "Replay",
                    DefaultCompileMode = "TSV",
                    DefaultTsvRowLength = 3,
                    DefaultStartingText = "A\nB\nC",
                    PopMode = true
                }
            },
            project.Id));
        var drafts = addBucket.Payload?.Deserialize<ZetlBucketSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Add bucket did not return a bucket.");

        var addSlip = service.Execute(AddSlipCommand(
            "slip-add-roundtrip",
            project.Id,
            inbox.Id,
            "move me"));
        var slip = addSlip.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Add slip did not return a slip.");
        var addBlankCapture = service.Execute(AddSlipCommand(
            "slip-add-blank-capture",
            project.Id,
            inbox.Id,
            ""));
        var addUntitledKastn = service.Execute(ZetlCommandEnvelope.Create(
            "slip-add-untitled-kastn",
            ZetlCommandKind.AddSlip,
            new AddSlipCommand
            {
                BucketId = inbox.Id,
                Title = "Untitled",
                Text = "",
                Source = "kastn"
            },
            project.Id));
        var untitledKastn = addUntitledKastn.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Untitled Kastn slip did not return a slip.");
        var updateBlankKastn = service.Execute(ZetlCommandEnvelope.Create(
            "slip-update-blank-kastn",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = "" },
            project.Id,
            untitledKastn.Id,
            untitledKastn.Revision));
        var updatedTitleOnly = updateBlankKastn.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Title-only Kastn update did not return a slip.");
        var clearTitleAndText = service.Execute(ZetlCommandEnvelope.Create(
            "slip-clear-title-and-text",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Title = "", Text = "" },
            project.Id,
            updatedTitleOnly.Id,
            updatedTitleOnly.Revision));
        var move = service.Execute(ZetlCommandEnvelope.Create(
            "slip-move",
            ZetlCommandKind.MoveSlip,
            new MoveSlipCommand { DestinationBucketId = drafts.Id },
            project.Id,
            slip.Id,
            slip.Revision));
        var moved = move.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Move slip did not return a slip.");
        var delete = service.Execute(ZetlCommandEnvelope.Create(
            "slip-delete",
            ZetlCommandKind.DeleteSlip,
            new DeleteSlipCommand(),
            project.Id,
            moved.Id,
            moved.Revision));
        var deleted = delete.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Delete slip did not return a slip.");
        var deletedBucket = store.GetDeletedBucket(project);
        var restore = service.Execute(ZetlCommandEnvelope.Create(
            "slip-restore",
            ZetlCommandKind.MoveSlip,
            new MoveSlipCommand { DestinationBucketId = drafts.Id },
            project.Id,
            deleted.Id,
            deleted.Revision));
        var restored = restore.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Restore slip did not return a slip.");
        var addToDeleted = service.Execute(AddSlipCommand(
            "slip-add-deleted",
            project.Id,
            deletedBucket.Id,
            "do not capture here"));
        var deleteDeletedBucket = service.Execute(ZetlCommandEnvelope.Create(
            "bucket-delete-deleted",
            ZetlCommandKind.DeleteBucket,
            new DeleteBucketCommand(),
            project.Id,
            deletedBucket.Id,
            deletedBucket.Revision));
        var snapshotResponse = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "project-snapshot",
            Kind = ZetlCommandKind.GetProject,
            ProjectId = project.Id
        });
        var snapshot = snapshotResponse.Payload?.Deserialize<ZetlProjectSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Get project did not return a snapshot.");

        AssertEqual(ZetlResponseStatus.Success, addBucket.Status, "Bucket add should succeed.");
        AssertEqual("TSV", drafts.Settings.DefaultCompileMode, "Bucket settings should round-trip.");
        AssertEqual(project.ActiveBucketId, snapshot.ActiveBucketId, "Project snapshots should expose the active bucket for clients.");
        AssertEqual(ZetlResponseStatus.ValidationError, addBlankCapture.Status, "Non-Kastn capture commands should still reject blank slips.");
        AssertEqual(ZetlResponseStatus.Success, addUntitledKastn.Status, "Kastn should be able to create a default untitled slip.");
        AssertEqual("Untitled", untitledKastn.Title, "Kastn's default slip title should be explicit.");
        AssertEqual("", untitledKastn.Text, "A new Kastn card should not put its placeholder in note text.");
        AssertEqual(ZetlResponseStatus.Success, updateBlankKastn.Status, "A title-only slip should remain valid.");
        AssertEqual(ZetlResponseStatus.ValidationError, clearTitleAndText.Status, "A non-picture slip cannot clear both title and note.");
        AssertEqual(ZetlResponseStatus.Success, move.Status, "Slip move should succeed.");
        AssertEqual(drafts.Id, moved.BucketId, "Moved slip should identify its destination.");
        AssertEqual(ZetlResponseStatus.Success, delete.Status, "Slip delete should succeed.");
        AssertEqual(deletedBucket.Id, deleted.BucketId, "Deleted slip should move to the protected bucket.");
        AssertEqual(drafts.Id, deleted.DeletedFromBucketId, "Deleted slip should remember its previous bucket.");
        AssertTrue(deleted.DeletedAtUtc is not null, "Deleted slip should record when it was deleted.");
        AssertEqual(ZetlResponseStatus.Success, restore.Status, "Slip restore should succeed.");
        AssertEqual(drafts.Id, restored.BucketId, "Restored slip should return to the requested bucket.");
        AssertTrue(restored.DeletedFromBucketId is null, "Restored slip should clear its original bucket marker.");
        AssertTrue(restored.DeletedAtUtc is null, "Restored slip should clear its deleted timestamp.");
        AssertEqual(ZetlResponseStatus.ValidationError, addToDeleted.Status, "Deleted should reject new capture commands.");
        AssertEqual(ZetlResponseStatus.ValidationError, deleteDeletedBucket.Status, "Deleted bucket should be protected from delete commands.");
        AssertTrue(snapshot.Slips.Any(item => item.Id == moved.Id), "Soft-deleted slips should remain in project snapshots.");
        AssertTrue(snapshot.Buckets.Any(item => item.Id == deletedBucket.Id), "Deleted bucket should remain visible in project snapshots.");
    }

    [Fact] public void ProjectAndBucketCommandsHonorRevisions()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath, "service-session");
        var service = new ZetlProjectService(store);

        var create = service.Execute(ZetlCommandEnvelope.Create(
            "project-create",
            ZetlCommandKind.CreateProject,
            new CreateProjectCommand
            {
                Name = "Created Through Service",
                Buckets =
                [
                    new CreateBucketDefinition { Name = "Inbox" }
                ]
            }));
        var created = create.Payload?.Deserialize<ZetlProjectSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Create project did not return a snapshot.");
        var inbox = created.Buckets.Single(item => item.Name == "Inbox");

        var update = service.Execute(ZetlCommandEnvelope.Create(
            "bucket-update",
            ZetlCommandKind.UpdateBucket,
            new UpdateBucketCommand
            {
                Name = "Research",
                Settings = new ZETL.Contracts.ZetlBucketSettings
                {
                    Kind = "Replay",
                    DefaultKind = "Replay",
                    DefaultCompileMode = "Plain",
                    DefaultTsvRowLength = 7
                }
            },
            created.Id,
            inbox.Id,
            inbox.Revision));
        var updated = update.Payload?.Deserialize<ZetlBucketSnapshot>(
            ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("Update bucket did not return a snapshot.");
        var staleDelete = service.Execute(ZetlCommandEnvelope.Create(
            "bucket-delete-stale",
            ZetlCommandKind.DeleteBucket,
            new DeleteBucketCommand(),
            created.Id,
            updated.Id,
            inbox.Revision));
        var deleteBucket = service.Execute(ZetlCommandEnvelope.Create(
            "bucket-delete-current",
            ZetlCommandKind.DeleteBucket,
            new DeleteBucketCommand(),
            created.Id,
            updated.Id,
            updated.Revision));
        var deleteProject = service.Execute(ZetlCommandEnvelope.Create(
            "project-delete",
            ZetlCommandKind.DeleteProject,
            new DeleteProjectCommand(),
            created.Id,
            expectedTargetRevision: created.MetadataRevision));

        AssertEqual(ZetlResponseStatus.Success, create.Status, "Project create should succeed.");
        AssertEqual("Replay", updated.Settings.Kind, "Bucket update should preserve current kind.");
        AssertEqual("Plain", updated.Settings.DefaultCompileMode, "Bucket update should preserve compile settings.");
        AssertEqual(ZetlResponseStatus.Conflict, staleDelete.Status, "Stale bucket delete should conflict.");
        AssertEqual(ZetlResponseStatus.Success, deleteBucket.Status, "Current bucket delete should succeed.");
        AssertEqual(ZetlResponseStatus.Success, deleteProject.Status, "Project delete should succeed.");
        AssertEqual(0, store.State.Projects.Count, "Deleted project should leave the store.");
    }

    [Fact] public void RevisionsPersistAcrossReload()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "first", "copy");
        store.UpdateNote(note, "second");
        store.UpdateBucketName(bucket, "Renamed Bucket");
        store.UpdateProjectName(project, "Renamed Project");

        var reloaded = new ZetlStateStore(temp.StatePath);
        var loadedProject = reloaded.State.Projects.Single();
        var loadedBucket = loadedProject.Buckets.Single(item => item.Name == "Renamed Bucket");
        var loadedNote = loadedBucket.Slips.Single();

        AssertEqual(2L, loadedProject.MetadataRevision, "Project metadata revision should persist.");
        AssertTrue(loadedProject.ChangeSequence >= 4, "Project change sequence should persist all writes.");
        AssertEqual(2L, loadedBucket.Revision, "Bucket revision should persist.");
        AssertEqual(2L, loadedNote.Revision, "Slip revision should persist.");
    }

    [Fact] public void SuccessfulMutationPublishesOneDetailedEvent()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);
        var events = new List<ZetlProjectChangedEvent>();
        service.ProjectChanged += (_, change) => events.Add(change);

        var response = service.Execute(AddSlipCommand(
            "event-add",
            project.Id,
            bucket.Id,
            "event text"));

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Mutation should succeed.");
        AssertEqual(1, events.Count, "One command should publish one detailed event.");
        AssertEqual(ZetlEntityKind.Slip, events[0].EntityKind, "Event should identify the changed slip.");
        AssertEqual(
            response.ProjectChangeSequence,
            events[0].ProjectChangeSequence,
            "Response and event should report the same durable sequence.");
    }

    [Fact] public void FailedMutationPublishesNoEvent()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "original", "copy");
        var service = new ZetlProjectService(store);
        var events = new List<ZetlProjectChangedEvent>();
        service.ProjectChanged += (_, change) => events.Add(change);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "stale-no-event",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = "bad" },
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision + 1));

        AssertEqual(ZetlResponseStatus.Conflict, response.Status, "Stale mutation should conflict.");
        AssertEqual(0, events.Count, "A rejected mutation must not publish a change.");
    }

    [Fact] public void SubscriberFailureDoesNotChangeAcknowledgement()
    {
        using var temp = new TempStateDirectory();
        var logs = new List<string>();
        var store = new ZetlStateStore(temp.StatePath, "service-session", logs.Add);
        var project = store.CreateProject("Service Project", ["Inbox"], "Inbox");
        var bucket = project.Buckets.Single(item => item.Name == "Inbox");
        var service = new ZetlProjectService(store, log: logs.Add);
        store.ProjectPersisted += (_, _) => throw new InvalidOperationException("store listener failed");
        service.ProjectChanged += (_, _) => throw new InvalidOperationException("service listener failed");

        var response = service.Execute(AddSlipCommand(
            "subscriber-failure",
            project.Id,
            bucket.Id,
            "still durable"));
        var reloaded = new ZetlStateStore(temp.StatePath);

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Subscriber failures must not change a durable success.");
        AssertEqual(
            "still durable",
            reloaded.State.Projects.Single().Buckets.Single(item => item.Name == "Inbox").Slips.Single().Text,
            "Acknowledged mutation should remain durable.");
        AssertTrue(logs.Count >= 2, "Subscriber failures should be logged.");
    }

    [Fact] public void DirectCapturePublishesProjectChange()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);
        var changes = new List<ZetlProjectChangedEvent>();
        service.ProjectChanged += (_, change) => changes.Add(change);

        store.AddNote(bucket, "direct capture", "copy");

        AssertEqual(1, changes.Count, "Direct store capture should publish one project change.");
        AssertEqual(
            project.Id,
            changes[0].ProjectId,
            "Direct capture notification should identify its project.");
        AssertEqual(
            project.ChangeSequence,
            changes[0].ProjectChangeSequence,
            "Direct capture notification should carry the durable sequence.");
    }

    [Fact] public void ListProjectsIncludesCheapPreviewText()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var first = store.AddNote(bucket, "first visible note", "copy");
        first.CreatedAtUtc = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var second = store.AddNote(bucket, "second visible note", "copy");
        second.CreatedAtUtc = new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
        var deletedBucket = store.GetDeletedBucket(project);
        var deleted = store.AddNote(deletedBucket, "deleted should stay out", "copy");
        deleted.CreatedAtUtc = new DateTimeOffset(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);
        var service = new ZetlProjectService(store);

        var response = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "list-preview",
            Kind = ZetlCommandKind.ListProjects
        });
        var summaries = response.Payload?.Deserialize<List<ZetlProjectSummary>>(
                ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("ListProjects returned no summaries.");
        var summary = summaries.Single(item => item.Id == project.Id);

        AssertEqual(ZetlResponseStatus.Success, response.Status, "ListProjects should succeed.");
        AssertEqual(2, summary.VisibleSlipCount, "Visible slip count should skip Deleted.");
        AssertEqual(2, summary.VisibleBucketCount, "Visible bucket count should skip Deleted.");
        AssertEqual(1, summary.DeletedSlipCount, "Deleted slip count should be separate.");
        AssertEqual(
            DateTimeOffset.Parse("2026-06-16T12:00:00Z"),
            summary.LastActivityUtc,
            "Last activity should come from the newest visible slip.");
        AssertTrue(
            summary.PreviewText.Contains("second visible note", StringComparison.Ordinal),
            "Project summaries should include a recent visible note preview.");
        AssertTrue(
            summary.PreviewText.Contains("first visible note", StringComparison.Ordinal),
            "Project summaries should include multiple cheap snippets.");
        AssertTrue(
            !summary.PreviewText.Contains("deleted should stay out", StringComparison.Ordinal),
            "Project summary previews should skip Deleted content.");
    }

    [Fact] public void ListProjectsReportsActiveLanes()
    {
        using var temp = new TempStateDirectory();
        var store = new ZetlStateStore(temp.StatePath);
        var main = store.CreateProject("Main Work", ["Inbox"], "Inbox");
        var alternate = store.CreateProject("Alternate Work", ["Queue"], "Queue", shifted: true);
        var other = store.CreateProject("Other Work", ["Notes"], "Notes");
        store.SetActiveProject(main.Id);
        var service = new ZetlProjectService(store);

        var response = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "list-lanes",
            Kind = ZetlCommandKind.ListProjects
        });
        var summaries = response.Payload?.Deserialize<List<ZetlProjectSummary>>(
                ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("ListProjects returned no summaries.");

        AssertEqual(ZetlResponseStatus.Success, response.Status, "ListProjects should succeed.");
        AssertEqual(
            ZetlStateStore.NormalLane,
            summaries.Single(project => project.Id == main.Id).ActiveLane,
            "The normal active project should be marked Main/Normal.");
        AssertEqual(
            ZetlStateStore.ShiftLane,
            summaries.Single(project => project.Id == alternate.Id).ActiveLane,
            "The Shift active project should be marked Alternate/Shift.");
        AssertEqual(
            "",
            summaries.Single(project => project.Id == other.Id).ActiveLane,
            "Inactive projects should have no active lane.");
    }

    [Fact] public void ReorderSlipMovesWithinBucket()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var drafts = store.AddBucket(project, "Drafts");
        var first = store.AddNote(inbox, "first", "copy");
        var second = store.AddNote(inbox, "second", "copy");
        var third = store.AddNote(inbox, "third", "copy");
        var elsewhere = store.AddNote(drafts, "elsewhere", "copy");
        var service = new ZetlProjectService(store);

        // Move the last slip ahead of the first: first, second, third -> third, first, second.
        var toFront = service.Execute(ReorderSlipCommand(
            "reorder-front", project.Id, third.Id, third.Revision, first.Id));
        AssertEqual(ZetlResponseStatus.Success, toFront.Status, "Reorder to front should succeed.");
        AssertEqual(
            "third,first,second",
            string.Join(",", inbox.Slips.Select(note => note.Text)),
            "Reorder should move the slip immediately before its anchor.");
        AssertEqual(2L, third.Revision, "Reorder should advance the moved slip's revision.");

        // A stale revision must conflict and leave the order untouched.
        var stale = service.Execute(ReorderSlipCommand(
            "reorder-stale", project.Id, third.Id, 1, second.Id));
        AssertEqual(ZetlResponseStatus.Conflict, stale.Status, "A stale reorder should conflict.");
        AssertEqual(
            "third,first,second",
            string.Join(",", inbox.Slips.Select(note => note.Text)),
            "A stale reorder must not change order.");

        // An anchor in another bucket is rejected and changes nothing.
        var crossBucket = service.Execute(ReorderSlipCommand(
            "reorder-cross", project.Id, first.Id, first.Revision, elsewhere.Id));
        AssertEqual(
            ZetlResponseStatus.ValidationError,
            crossBucket.Status,
            "An anchor in another bucket should be rejected.");
        AssertEqual(
            "third,first,second",
            string.Join(",", inbox.Slips.Select(note => note.Text)),
            "A rejected reorder must not change order.");

        // A null anchor moves the slip to the end: third, first, second -> third, second, first.
        var toEnd = service.Execute(ReorderSlipCommand(
            "reorder-end", project.Id, first.Id, first.Revision, beforeSlipId: null));
        AssertEqual(ZetlResponseStatus.Success, toEnd.Status, "Reorder to end should succeed.");
        AssertEqual(
            "third,second,first",
            string.Join(",", inbox.Slips.Select(note => note.Text)),
            "A null anchor should move the slip to the end of its bucket.");
    }

    [Fact] public void PictureContentIsReadOnly()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var imageBytes = new byte[] { 1, 2, 3, 4, 5 };
        var picture = store.AddImageNote(
            project,
            bucket,
            new ZetlClipboardImage(imageBytes, 20, 10),
            "copy",
            caption: "Diagram");
        var text = store.AddNote(bucket, "ordinary", "copy");
        var service = new ZetlProjectService(store);
        var changes = new List<ZetlProjectChangedEvent>();
        service.ProjectChanged += (_, change) => changes.Add(change);
        var command = new ZetlCommandEnvelope
        {
            CommandId = "picture-content",
            Kind = ZetlCommandKind.GetSlipPicture,
            ProjectId = project.Id,
            TargetId = picture.Id
        };

        var first = service.Execute(command);
        var second = service.Execute(command);
        var content = first.Payload?.Deserialize<ZetlPictureContent>(ZetlProtocolJson.Options);
        var textResponse = service.Execute(new ZetlCommandEnvelope
        {
            CommandId = "text-picture-content",
            Kind = ZetlCommandKind.GetSlipPicture,
            ProjectId = project.Id,
            TargetId = text.Id
        });

        AssertEqual(ZetlResponseStatus.Success, first.Status, "Picture content should be readable.");
        AssertTrue(content?.Bytes.SequenceEqual(imageBytes) == true, "Picture content should preserve stored bytes.");
        AssertEqual(20, content?.Width, "Picture content should include dimensions.");
        AssertTrue(!ReferenceEquals(first, second), "Large picture responses should not be retained in retry memory.");
        AssertEqual(0, changes.Count, "Reading picture content must not publish a project mutation.");
        AssertEqual(
            ZetlResponseStatus.ValidationError,
            textResponse.Status,
            "A text slip should not be exposed as picture content.");
    }

    [Fact] public void ProjectStatusRoundTripsAcrossReload()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out _);
        var service = new ZetlProjectService(store);

        AssertEqual(
            ZetlStateStore.ActiveStatus,
            project.Status,
            "A new project should start Active.");

        // Finish the project (revision-checked).
        var finish = service.Execute(SetStatusCommand(
            "status-finish", project.Id, ZetlStateStore.FinishedStatus, project.MetadataRevision));
        var finished = finish.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("SetProjectStatus returned no snapshot.");
        AssertEqual(ZetlResponseStatus.Success, finish.Status, "Finishing should succeed.");
        AssertEqual("Finished", finished.Status, "The snapshot should report the new status.");
        AssertEqual("Finished", project.Status, "The stored project should carry the status.");
        AssertEqual<ZetlProject?>(
            null,
            store.ActiveProject,
            "A sealed project must leave its lane (the non-Active invariant).");

        // A stale revision is rejected and leaves the status untouched.
        var stale = service.Execute(SetStatusCommand(
            "status-stale", project.Id, ZetlStateStore.ArchivedStatus, finished.MetadataRevision - 1));
        AssertEqual(ZetlResponseStatus.Conflict, stale.Status, "A stale status edit should conflict.");
        AssertEqual("Finished", project.Status, "A stale status edit must not change the project.");

        // An unrecognized status is rejected before mutating.
        var invalid = service.Execute(SetStatusCommand(
            "status-invalid", project.Id, "Paused", finished.MetadataRevision));
        AssertEqual(ZetlResponseStatus.ValidationError, invalid.Status, "An unknown status should be rejected.");
        AssertEqual("Finished", project.Status, "A rejected status edit must not change the project.");

        // Reactivating restores the status but not the lane: making a lane active
        // again is an explicit Zetl choice, not a side effect of un-sealing.
        var reactivate = service.Execute(SetStatusCommand(
            "status-reactivate", project.Id, ZetlStateStore.ActiveStatus, finished.MetadataRevision));
        AssertEqual(ZetlResponseStatus.Success, reactivate.Status, "Reactivating should succeed.");
        AssertEqual("Active", project.Status, "Reactivating should restore Active.");
        AssertEqual<ZetlProject?>(
            null,
            store.ActiveProject,
            "Reactivating must not silently re-occupy the lane.");

        // Reload from disk: the status persisted.
        var reloaded = new ZetlStateStore(temp.StatePath);
        AssertEqual(
            "Active",
            reloaded.State.Projects.Single(item => item.Id == project.Id).Status,
            "Lifecycle status should persist across reload.");
    }

    [Fact] public void BucketHeadingRoundTripsAcrossReload()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "set-heading",
            ZetlCommandKind.SetBucketHeading,
            new SetBucketHeadingCommand { Align = "center", Bold = true, Level = 1 },
            project.Id,
            bucket.Id,
            bucket.Revision));
        var snapshot = response.Payload?.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("SetBucketHeading returned no snapshot.");
        AssertEqual(ZetlResponseStatus.Success, response.Status, "Setting a bucket heading should succeed.");
        AssertEqual("center", snapshot.HeadingAlign, "The snapshot should report the heading align.");
        AssertTrue(snapshot.HeadingBold, "The snapshot should report bold.");
        AssertEqual(1, snapshot.HeadingLevel, "The snapshot should report the heading level.");

        // A stale revision conflicts.
        var stale = service.Execute(ZetlCommandEnvelope.Create(
            "set-heading-stale",
            ZetlCommandKind.SetBucketHeading,
            new SetBucketHeadingCommand { Align = "right" },
            project.Id,
            bucket.Id,
            bucket.Revision - 1));
        AssertEqual(ZetlResponseStatus.Conflict, stale.Status, "A stale heading edit should conflict.");

        // Persists across reload.
        var reloaded = new ZetlStateStore(temp.StatePath);
        var loaded = reloaded.State.Projects.Single(item => item.Id == project.Id)
            .Buckets.Single(item => item.Id == bucket.Id);
        AssertEqual("center", loaded.HeadingAlign, "Heading align should persist across reload.");
        AssertEqual(1, loaded.HeadingLevel, "Heading level should persist across reload.");
    }

    [Fact] public void ProjectJournalModeRoundTripsThroughService()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out _);
        var service = new ZetlProjectService(store);

        AssertTrue(!project.JournalMode, "A new project is not journal-mode.");

        var on = service.Execute(ZetlCommandEnvelope.Create(
            "journal-on",
            ZetlCommandKind.SetJournalMode,
            new SetJournalModeCommand { JournalMode = true },
            project.Id,
            expectedTargetRevision: project.MetadataRevision));
        var snapshot = on.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("SetJournalMode returned no snapshot.");
        AssertEqual(ZetlResponseStatus.Success, on.Status, "Enabling journal mode should succeed.");
        AssertTrue(snapshot.JournalMode, "The snapshot reports journal mode on.");
        AssertTrue(project.JournalMode, "The stored project carries journal mode.");

        // A stale revision is rejected and leaves the flag untouched.
        var stale = service.Execute(ZetlCommandEnvelope.Create(
            "journal-stale",
            ZetlCommandKind.SetJournalMode,
            new SetJournalModeCommand { JournalMode = false },
            project.Id,
            expectedTargetRevision: snapshot.MetadataRevision - 1));
        AssertEqual(ZetlResponseStatus.Conflict, stale.Status, "A stale journal-mode edit should conflict.");
        AssertTrue(project.JournalMode, "A stale edit must not change the flag.");

        var off = service.Execute(ZetlCommandEnvelope.Create(
            "journal-off",
            ZetlCommandKind.SetJournalMode,
            new SetJournalModeCommand { JournalMode = false },
            project.Id,
            expectedTargetRevision: snapshot.MetadataRevision));
        AssertEqual(ZetlResponseStatus.Success, off.Status, "Disabling journal mode should succeed.");
        AssertTrue(!project.JournalMode, "The flag is cleared.");
    }

    private static ZetlCommandEnvelope SetStatusCommand(
        string commandId,
        string projectId,
        string status,
        long expectedRevision)
    {
        return ZetlCommandEnvelope.Create(
            commandId,
            ZetlCommandKind.SetProjectStatus,
            new SetProjectStatusCommand { Status = status },
            projectId,
            expectedTargetRevision: expectedRevision);
    }

    [Fact] public void ReorderBucketMovesBeforeSibling()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var middle = store.AddBucket(project, "Middle", setActive: false);
        var last = store.AddBucket(project, "Last", setActive: false);
        var service = new ZetlProjectService(store);

        AssertEqual(
            "Inbox,Middle,Last",
            SiblingOrder(service, project.Id, null, "Inbox", "Middle", "Last"),
            "Top-level buckets start in storage order.");

        var response = service.Execute(ReorderBucketCommand(
            "reorder-front", project.Id, last.Id, last.Revision, beforeBucketId: inbox.Id));

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Reorder should succeed.");
        AssertEqual(
            "Last,Inbox,Middle",
            SiblingOrder(service, project.Id, null, "Inbox", "Middle", "Last"),
            "Last should land immediately before Inbox.");
    }

    [Fact] public void ReorderBucketToEndWithNullAnchor()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var middle = store.AddBucket(project, "Middle", setActive: false);
        var last = store.AddBucket(project, "Last", setActive: false);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ReorderBucketCommand(
            "reorder-end", project.Id, inbox.Id, inbox.Revision, beforeBucketId: null));

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Reorder to end should succeed.");
        AssertEqual(
            "Middle,Last,Inbox",
            SiblingOrder(service, project.Id, null, "Inbox", "Middle", "Last"),
            "A null anchor moves the bucket to the end of its siblings.");
    }

    [Fact] public void ReorderBucketRejectsCrossParentAnchor()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var middle = store.AddBucket(project, "Middle", setActive: false);
        var child = store.AddBucket(project, "Child", inbox.Id, setActive: false);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ReorderBucketCommand(
            "reorder-cross", project.Id, child.Id, child.Revision, beforeBucketId: middle.Id));

        AssertEqual(ZetlResponseStatus.ValidationError, response.Status, "A cross-parent anchor is invalid.");
        AssertEqual("bucket_reorder_anchor_invalid", response.Error?.Code, "The anchor must be a sibling.");
    }

    [Fact] public void ReorderBucketConflictsOnStaleRevision()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var inbox);
        var last = store.AddBucket(project, "Last", setActive: false);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ReorderBucketCommand(
            "reorder-stale", project.Id, last.Id, last.Revision + 5, beforeBucketId: inbox.Id));

        AssertEqual(ZetlResponseStatus.Conflict, response.Status, "A stale revision should conflict.");
        AssertEqual(last.Revision, response.Conflict?.ActualRevision, "Conflict reports the bucket's current revision.");
    }

    private static ZetlCommandEnvelope ReorderBucketCommand(
        string commandId,
        string projectId,
        string bucketId,
        long expectedRevision,
        string? beforeBucketId)
    {
        return ZetlCommandEnvelope.Create(
            commandId,
            ZetlCommandKind.ReorderBucket,
            new ReorderBucketCommand { BeforeBucketId = beforeBucketId },
            projectId,
            bucketId,
            expectedRevision);
    }

    private static string SiblingOrder(
        ZetlProjectService service,
        string projectId,
        string? parentId,
        params string[] names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var response = service.Execute(ZetlCommandEnvelope.Create(
            "get-" + Guid.NewGuid().ToString("N"),
            ZetlCommandKind.GetProject,
            new GetProjectCommand(),
            projectId));
        var snapshot = response.Payload?.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException("GetProject returned no snapshot.");
        return string.Join(
            ",",
            snapshot.Buckets
                .Where(bucket => bucket.ParentBucketId == parentId && wanted.Contains(bucket.Name))
                .Select(bucket => bucket.Name));
    }

    [Fact] public void DualSlipRepresentationTogglesThroughUpdateSlip()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddImageNote(
            project,
            bucket,
            new ZetlClipboardImage([1, 2, 3], 2, 2),
            "copy",
            caption: "A1\tB1",
            preferTextContent: true);
        var service = new ZetlProjectService(store);

        var toPicture = service.Execute(ZetlCommandEnvelope.Create(
            "to-picture",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = note.Text, Type = ZetlSlipType.Picture },
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));
        var pictureSnapshot = toPicture.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options);

        AssertEqual(ZetlResponseStatus.Success, toPicture.Status, "The picture flip should succeed.");
        AssertEqual(ZetlSlipType.Picture, pictureSnapshot?.Type, "The slip should present as a picture.");
        AssertEqual("A1\tB1", pictureSnapshot?.Text, "The flip should keep the text content.");

        var toText = service.Execute(ZetlCommandEnvelope.Create(
            "to-text",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = note.Text, Type = ZetlSlipType.Text },
            project.Id,
            note.Id,
            expectedTargetRevision: pictureSnapshot!.Revision));
        var textSnapshot = toText.Payload?.Deserialize<ZetlSlipSnapshot>(
            ZetlProtocolJson.Options);

        AssertEqual(ZetlResponseStatus.Success, toText.Status, "The text flip should succeed.");
        AssertEqual(ZetlSlipType.Text, textSnapshot?.Type, "The slip should present as text again.");
        AssertTrue(textSnapshot?.Picture is not null, "The flip should keep the attached picture.");
    }

    [Fact] public void PictureFlipRequiresAPicture()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "plain text", "copy");
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "flip-no-picture",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = note.Text, Type = ZetlSlipType.Picture },
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));

        AssertEqual(ZetlResponseStatus.ValidationError, response.Status, "A slip without a picture cannot present as one.");
        AssertEqual(ZetlSlipType.Text, note.Type, "The slip should keep its text representation.");
    }

    [Fact] public void TextFlipWithoutContentIsRejected()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddImageNote(
            project,
            bucket,
            new ZetlClipboardImage([1, 2, 3], 2, 2),
            "copy");
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "flip-captionless",
            ZetlCommandKind.UpdateSlip,
            new UpdateSlipCommand { Text = "", Type = ZetlSlipType.Text },
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));

        AssertEqual(ZetlResponseStatus.ValidationError, response.Status, "A captionless picture cannot present as text.");
        AssertEqual(ZetlSlipType.Picture, note.Type, "The slip should stay a picture.");
    }

    private static byte[] FakePngBytes(byte marker) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, marker];

    [Fact] public void SetSlipPictureAttachesToATextSlip()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "A1\tB1", "copy");
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "attach",
            ZetlCommandKind.SetSlipPicture,
            new SetSlipPictureCommand { Bytes = FakePngBytes(1), Width = 3, Height = 2 },
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));
        var snapshot = response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Attaching a picture should succeed.");
        AssertEqual(ZetlSlipType.Text, snapshot?.Type, "A slip with text stays text-preferred after attach.");
        AssertTrue(snapshot?.Picture is not null, "The snapshot should carry the attached picture.");
        AssertEqual(1, store.GetProjectAssets(project).Count, "Attach should write one project asset.");
    }

    [Fact] public void SetSlipPictureReplacesAnExistingPicture()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "content", "copy");
        var service = new ZetlProjectService(store);

        var first = service.Execute(ZetlCommandEnvelope.Create(
            "attach-first",
            ZetlCommandKind.SetSlipPicture,
            new SetSlipPictureCommand { Bytes = FakePngBytes(1), Width = 3, Height = 2 },
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));
        var firstSnapshot = first.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);
        var second = service.Execute(ZetlCommandEnvelope.Create(
            "attach-second",
            ZetlCommandKind.SetSlipPicture,
            new SetSlipPictureCommand { Bytes = FakePngBytes(2), Width = 5, Height = 4 },
            project.Id,
            note.Id,
            expectedTargetRevision: firstSnapshot!.Revision));
        var secondSnapshot = second.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);

        AssertEqual(ZetlResponseStatus.Success, second.Status, "Replacing a picture should succeed.");
        AssertTrue(
            !string.Equals(firstSnapshot.Picture?.Sha256, secondSnapshot?.Picture?.Sha256, StringComparison.Ordinal),
            "The replacement should carry the new content hash.");
        AssertEqual(5, secondSnapshot?.Picture?.Width, "The replacement should carry the new dimensions.");
    }

    [Fact] public void SetSlipPictureRejectsNonPngBytes()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddNote(bucket, "content", "copy");
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "attach-bad",
            ZetlCommandKind.SetSlipPicture,
            new SetSlipPictureCommand { Bytes = [1, 2, 3], Width = 3, Height = 2 },
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));

        AssertEqual(ZetlResponseStatus.ValidationError, response.Status, "Non-PNG bytes should be rejected.");
        AssertTrue(note.Image is null, "A rejected attach must not touch the slip.");
    }

    [Fact] public void RemoveSlipPictureReturnsADualSlipToText()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddImageNote(
            project,
            bucket,
            new ZetlClipboardImage(FakePngBytes(1), 3, 2),
            "copy",
            caption: "A1\tB1",
            preferTextContent: true);
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "remove",
            ZetlCommandKind.RemoveSlipPicture,
            new RemoveSlipPictureCommand(),
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));
        var snapshot = response.Payload?.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options);

        AssertEqual(ZetlResponseStatus.Success, response.Status, "Removing the picture should succeed.");
        AssertTrue(snapshot?.Picture is null, "The snapshot should no longer carry a picture.");
        AssertEqual(ZetlSlipType.Text, snapshot?.Type, "The slip should stand on its text.");
    }

    [Fact] public void RemoveSlipPictureRejectsACaptionlessPicture()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var note = store.AddImageNote(
            project,
            bucket,
            new ZetlClipboardImage(FakePngBytes(1), 3, 2),
            "copy");
        var service = new ZetlProjectService(store);

        var response = service.Execute(ZetlCommandEnvelope.Create(
            "remove-captionless",
            ZetlCommandKind.RemoveSlipPicture,
            new RemoveSlipPictureCommand(),
            project.Id,
            note.Id,
            expectedTargetRevision: note.Revision));

        AssertEqual(
            ZetlResponseStatus.ValidationError,
            response.Status,
            "A slip with nothing to stand on keeps its picture.");
        AssertTrue(note.Image is not null, "The rejected removal must not touch the slip.");
    }

    private static ZetlStateStore CreateStoreWithProject(
        TempStateDirectory temp,
        out ZetlProject project,
        out ZetlBucket bucket)
    {
        var store = new ZetlStateStore(temp.StatePath, "service-session");
        project = store.CreateProject("Service Project", ["Inbox"], "Inbox");
        bucket = project.Buckets.Single(item => item.Name == "Inbox");
        return store;
    }

    private static ZetlCommandEnvelope AddSlipCommand(
        string commandId,
        string projectId,
        string bucketId,
        string text)
    {
        return ZetlCommandEnvelope.Create(
            commandId,
            ZetlCommandKind.AddSlip,
            new AddSlipCommand
            {
                BucketId = bucketId,
                Text = text,
                Source = "copy"
            },
            projectId);
    }

    private static ZetlCommandEnvelope ReorderSlipCommand(
        string commandId,
        string projectId,
        string slipId,
        long expectedRevision,
        string? beforeSlipId)
    {
        return ZetlCommandEnvelope.Create(
            commandId,
            ZetlCommandKind.ReorderSlip,
            new ReorderSlipCommand { BeforeSlipId = beforeSlipId },
            projectId,
            slipId,
            expectedRevision);
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class TempStateDirectory : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "ZetlProjectServiceTests",
            Guid.NewGuid().ToString("N"));

        public TempStateDirectory()
        {
            Directory.CreateDirectory(directory);
            StatePath = Path.Combine(directory, "state.json");
        }

        public string StatePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
