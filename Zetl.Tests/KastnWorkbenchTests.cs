using KASTN;
using System.Text.Json;
using ZETL.Contracts;

namespace ZETL.Tests;

internal static class KastnWorkbenchTests
{
    public static void FiltersPreserveSnapshotOrderAndHierarchy()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var project = Project(
            buckets:
            [
                Bucket("root", "Root"),
                Bucket("child", "Child", "root"),
                Bucket("other", "Other")
            ],
            slips:
            [
                Slip("one", "root", "first match", "copy", "a", now.AddDays(-1)),
                Slip("two", "child", "second match", "manual", "b", now.AddHours(-1)),
                Slip("three", "other", "third match", "copy", "a", now)
            ]);

        var hierarchy = KastnWorkbench.BuildBucketHierarchy(project, includeAll: true);
        var filtered = KastnWorkbench.FilterSlips(
            project,
            "root",
            source: null,
            sessionId: null,
            KastnDateFilter.Last7Days,
            "match",
            now);
        var sourceFiltered = KastnWorkbench.FilterSlips(
            project,
            bucketId: null,
            source: "copy",
            sessionId: "a",
            KastnDateFilter.Today,
            search: null,
            now);

        AssertEqual("All buckets", hierarchy[0].Label, "Hierarchy should start with all buckets.");
        AssertTrue(
            hierarchy.Single(item => item.Id == "child").Label.StartsWith("   "),
            "Child buckets should be indented.");
        AssertSequence(
            ["one", "two"],
            filtered.Select(slip => slip.Id),
            "A bucket filter should include descendants without changing order.");
        AssertSequence(
            ["three"],
            sourceFiltered.Select(slip => slip.Id),
            "Source, session, and date filters should compose.");
    }

    public static void DirtyEditorSurvivesUnrelatedChanges()
    {
        var editor = new KastnEditorState();
        var original = Slip(
            "one",
            "bucket",
            "original",
            "copy",
            "session",
            DateTimeOffset.UtcNow,
            revision: 2);
        editor.Select(original);
        editor.SetDraft("local draft");

        editor.Reconcile(original);

        AssertEqual("local draft", editor.DraftText, "Unrelated refreshes must keep local text.");
        AssertTrue(editor.IsDirty, "The editor should remain dirty.");
        AssertEqual(null, editor.ConflictCurrent, "An unchanged slip should not conflict.");
    }

    public static void SameSlipChangesRequireExplicitResolution()
    {
        var editor = new KastnEditorState();
        var original = Slip(
            "one",
            "bucket",
            "original",
            "copy",
            "session",
            DateTimeOffset.UtcNow,
            revision: 2);
        var remote = original with { Text = "remote edit", Revision = 3 };
        editor.Select(original);
        editor.SetDraft("local edit");

        editor.Reconcile(remote);

        AssertEqual("local edit", editor.DraftText, "Conflict detection must preserve local text.");
        AssertEqual("remote edit", editor.ConflictCurrent?.Text, "Conflict should expose Zetl text.");

        editor.PrepareOverwrite();
        AssertEqual(3L, editor.Revision, "Keep Mine should retry against the current revision.");
        AssertEqual("local edit", editor.DraftText, "Keep Mine should retain local text.");

        editor.Reconcile(remote with { Text = "newer remote", Revision = 4 });
        editor.UseCurrent();
        AssertEqual("newer remote", editor.DraftText, "Use Zetl should adopt the current version.");
        AssertTrue(!editor.IsDirty, "Using Zetl should leave a clean editor.");

        var localSave = new KastnEditorState();
        localSave.Select(original);
        localSave.SetDraft("saved locally");
        localSave.Reconcile(
            original with { Text = "saved locally", Revision = 3 },
            pendingSaveText: "saved locally");
        AssertEqual(null, localSave.ConflictCurrent, "A local save event must not create a conflict.");
        AssertTrue(!localSave.IsDirty, "An acknowledged local save event should clean the editor.");
    }

    public static void CommandsOrganizeThroughZetl()
    {
        RunAsync(async () =>
        {
            using var fixture = new WorkbenchFixture();
            await using var controller = fixture.CreateController();
            controller.Start(fixture.Project.Id);
            await WaitForOnlineAsync(controller);

            var addBucket = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-add-bucket",
                ZetlCommandKind.AddBucket,
                new AddBucketCommand { Name = "Research" },
                fixture.Project.Id));
            var research = Payload<ZetlBucketSnapshot>(addBucket);
            var updatedBucket = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-update-bucket",
                ZetlCommandKind.UpdateBucket,
                new UpdateBucketCommand
                {
                    Name = "Sources",
                    ParentBucketId = fixture.Inbox.Id,
                    Settings = research.Settings
                },
                fixture.Project.Id,
                research.Id,
                research.Revision));
            var sources = Payload<ZetlBucketSnapshot>(updatedBucket);

            var addSlip = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-add-slip",
                ZetlCommandKind.AddSlip,
                new AddSlipCommand
                {
                    BucketId = fixture.Inbox.Id,
                    Text = "draft",
                    Source = "manual"
                },
                fixture.Project.Id));
            var slip = Payload<ZetlSlipSnapshot>(addSlip);
            var edit = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-edit-slip",
                ZetlCommandKind.UpdateSlip,
                new UpdateSlipCommand { Text = "edited" },
                fixture.Project.Id,
                slip.Id,
                slip.Revision));
            var edited = Payload<ZetlSlipSnapshot>(edit);
            var move = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-move-slip",
                ZetlCommandKind.MoveSlip,
                new MoveSlipCommand { DestinationBucketId = sources.Id },
                fixture.Project.Id,
                edited.Id,
                edited.Revision));
            var moved = Payload<ZetlSlipSnapshot>(move);
            var deleteSlip = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-delete-slip",
                ZetlCommandKind.DeleteSlip,
                new DeleteSlipCommand(),
                fixture.Project.Id,
                moved.Id,
                moved.Revision));
            var deleteBucket = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-delete-bucket",
                ZetlCommandKind.DeleteBucket,
                new DeleteBucketCommand(),
                fixture.Project.Id,
                sources.Id,
                sources.Revision));

            AssertEqual("Sources", sources.Name, "Bucket rename should pass through IPC.");
            AssertEqual(fixture.Inbox.Id, sources.ParentBucketId, "Bucket move should pass through IPC.");
            AssertEqual("edited", edited.Text, "Slip edit should pass through IPC.");
            AssertEqual(sources.Id, moved.BucketId, "Slip move should pass through IPC.");
            AssertEqual(ZetlResponseStatus.Success, deleteSlip.Status, "Slip delete should succeed.");
            AssertEqual(ZetlResponseStatus.Success, deleteBucket.Status, "Bucket delete should succeed.");
        });
    }

    private static ZetlProjectSnapshot Project(
        IReadOnlyList<ZetlBucketSnapshot> buckets,
        IReadOnlyList<ZetlSlipSnapshot> slips)
    {
        return new ZetlProjectSnapshot
        {
            Id = "project",
            Name = "Project",
            MetadataRevision = 1,
            ChangeSequence = 1,
            Buckets = buckets,
            Slips = slips
        };
    }

    private static ZetlBucketSnapshot Bucket(
        string id,
        string name,
        string? parentId = null)
    {
        return new ZetlBucketSnapshot
        {
            Id = id,
            Revision = 1,
            Name = name,
            ParentBucketId = parentId
        };
    }

    private static ZetlSlipSnapshot Slip(
        string id,
        string bucketId,
        string text,
        string source,
        string session,
        DateTimeOffset captured,
        long revision = 1)
    {
        return new ZetlSlipSnapshot
        {
            Id = id,
            Revision = revision,
            Type = ZetlSlipType.Text,
            BucketId = bucketId,
            Text = text,
            Source = source,
            SessionId = session,
            CapturedAtUtc = captured
        };
    }

    private static T Payload<T>(ZetlResponseEnvelope response)
        where T : class
    {
        AssertEqual(
            ZetlResponseStatus.Success,
            response.Status,
            $"Expected {typeof(T).Name} response to succeed.");
        return response.Payload?.Deserialize<T>(ZetlProtocolJson.Options)
            ?? throw new InvalidOperationException($"Response contained no {typeof(T).Name}.");
    }

    private static async Task WaitForOnlineAsync(KastnConnectionController controller)
    {
        if (controller.Current.ConnectionState == KastnConnectionState.Online)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<KastnSessionSnapshot>? handler = null;
        handler = (_, snapshot) =>
        {
            if (snapshot.ConnectionState == KastnConnectionState.Online)
            {
                completion.TrySetResult();
            }
        };
        controller.SnapshotChanged += handler;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            controller.SnapshotChanged -= handler;
        }
    }

    private static void RunAsync(Func<Task> action)
    {
        action().GetAwaiter().GetResult();
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
            throw new InvalidOperationException(
                $"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    private static void AssertSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class WorkbenchFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "KastnWorkbenchTests",
            Guid.NewGuid().ToString("N"));
        private readonly ZetlIpcServer server;

        public WorkbenchFixture()
        {
            Directory.CreateDirectory(directory);
            PipeName = $"kastn-workbench-{Guid.NewGuid():N}";
            Store = new ZetlStateStore(
                Path.Combine(directory, "state.json"),
                "kastn-workbench");
            Project = Store.CreateProject("Workbench", ["Inbox"], "Inbox");
            Inbox = Project.Buckets.Single(bucket => bucket.Name == "Inbox");
            server = new ZetlIpcServer(
                new ZetlProjectService(Store),
                PipeName);
            server.Start();
        }

        public string PipeName { get; }
        public ZetlStateStore Store { get; }
        public ZetlProject Project { get; }
        public ZetlBucket Inbox { get; }

        public KastnConnectionController CreateController()
        {
            return new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl is already running."),
                PipeName,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(25));
        }

        public void Dispose()
        {
            server.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
