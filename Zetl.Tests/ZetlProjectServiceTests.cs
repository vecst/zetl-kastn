using System.Collections.Concurrent;
using System.Text.Json;
using ZETL.Contracts;

namespace ZETL.Tests;

internal static class ZetlProjectServiceTests
{
    public static void RetriedAddDoesNotDuplicate()
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
        AssertEqual(1, bucket.Notes.Count, "Retrying an add must not duplicate the slip.");
    }

    public static void StaleEditReturnsCurrentSlip()
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

    public static void UnrelatedCaptureDoesNotConflictWithRename()
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

    public static void ConcurrentAddsAreSerialized()
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
        AssertEqual(40, bucket.Notes.Count, "Serialized adds should preserve every slip.");
        AssertEqual(
            40,
            bucket.Notes.Select(note => note.Id).Distinct(StringComparer.Ordinal).Count(),
            "Every added slip should have a unique ID.");

        var reloaded = new ZetlStateStore(temp.StatePath);
        AssertEqual(
            40,
            reloaded.State.Projects.Single().Buckets.Single(item => item.Name == "Inbox").Notes.Count,
            "Every acknowledged add should be durable.");
    }

    public static void DirectAndServiceMutationsShareOneWriter()
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

        AssertEqual(40, bucket.Notes.Count, "Direct and service mutations should preserve every slip.");
        var reloaded = new ZetlStateStore(temp.StatePath);
        AssertEqual(
            40,
            reloaded.State.Projects.Single().Buckets.Single(item => item.Name == "Inbox").Notes.Count,
            "The shared writer monitor should make every mixed mutation durable.");
    }

    public static void BucketAndSlipCommandsRoundTrip()
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
                Settings = new ZetlBucketSettings
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
                Text = "Untitled",
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
        AssertEqual("Untitled", untitledKastn.Text, "Kastn's default slip text should be explicit.");
        AssertEqual(ZetlResponseStatus.ValidationError, updateBlankKastn.Status, "Blank updates should still be rejected by the command service.");
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

    public static void ProjectAndBucketCommandsHonorRevisions()
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
                Settings = new ZetlBucketSettings
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

    public static void RevisionsPersistAcrossReload()
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
        var loadedNote = loadedBucket.Notes.Single();

        AssertEqual(2L, loadedProject.MetadataRevision, "Project metadata revision should persist.");
        AssertTrue(loadedProject.ChangeSequence >= 4, "Project change sequence should persist all writes.");
        AssertEqual(2L, loadedBucket.Revision, "Bucket revision should persist.");
        AssertEqual(2L, loadedNote.Revision, "Slip revision should persist.");
    }

    public static void SuccessfulMutationPublishesOneDetailedEvent()
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

    public static void FailedMutationPublishesNoEvent()
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

    public static void SubscriberFailureDoesNotChangeAcknowledgement()
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
            reloaded.State.Projects.Single().Buckets.Single(item => item.Name == "Inbox").Notes.Single().Text,
            "Acknowledged mutation should remain durable.");
        AssertTrue(logs.Count >= 2, "Subscriber failures should be logged.");
    }

    public static void DirectCapturePublishesProjectChange()
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

    public static void ListProjectsIncludesCheapPreviewText()
    {
        using var temp = new TempStateDirectory();
        var store = CreateStoreWithProject(temp, out var project, out var bucket);
        var first = store.AddNote(bucket, "first visible note", "copy");
        first.CreatedAtUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var second = store.AddNote(bucket, "second visible note", "copy");
        second.CreatedAtUtc = new DateTime(2026, 6, 16, 12, 0, 0, DateTimeKind.Utc);
        var deletedBucket = store.GetDeletedBucket(project);
        var deleted = store.AddNote(deletedBucket, "deleted should stay out", "copy");
        deleted.CreatedAtUtc = new DateTime(2026, 6, 17, 12, 0, 0, DateTimeKind.Utc);
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

    public static void ReorderSlipMovesWithinBucket()
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
            string.Join(",", inbox.Notes.Select(note => note.Text)),
            "Reorder should move the slip immediately before its anchor.");
        AssertEqual(2L, third.Revision, "Reorder should advance the moved slip's revision.");

        // A stale revision must conflict and leave the order untouched.
        var stale = service.Execute(ReorderSlipCommand(
            "reorder-stale", project.Id, third.Id, 1, second.Id));
        AssertEqual(ZetlResponseStatus.Conflict, stale.Status, "A stale reorder should conflict.");
        AssertEqual(
            "third,first,second",
            string.Join(",", inbox.Notes.Select(note => note.Text)),
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
            string.Join(",", inbox.Notes.Select(note => note.Text)),
            "A rejected reorder must not change order.");

        // A null anchor moves the slip to the end: third, first, second -> third, second, first.
        var toEnd = service.Execute(ReorderSlipCommand(
            "reorder-end", project.Id, first.Id, first.Revision, beforeSlipId: null));
        AssertEqual(ZetlResponseStatus.Success, toEnd.Status, "Reorder to end should succeed.");
        AssertEqual(
            "third,second,first",
            string.Join(",", inbox.Notes.Select(note => note.Text)),
            "A null anchor should move the slip to the end of its bucket.");
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
