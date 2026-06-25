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
                Bucket("child", "Child", "root"),
                Bucket("root", "Root"),
                Bucket("other", "Other"),
                Bucket("deleted", "Deleted", kind: "Deleted")
            ],
            slips:
            [
                Slip("one", "root", "first match", "copy", "a", now.AddDays(-1)),
                Slip("two", "child", "second match", "manual", "b", now.AddHours(-1)),
                Slip("three", "other", "third match", "copy", "a", now)
            ]);

        var hierarchy = KastnWorkbench.BuildBucketHierarchy(project, includeAll: true);
        var pickerChoices = KastnWorkbench.BuildBucketPickerChoices(project, includeAll: true);
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
        AssertTrue(
            hierarchy.Any(item => item.Id == "deleted"),
            "Kastn should intentionally show the protected Deleted bucket.");
        var childIndex = hierarchy.ToList().FindIndex(item => item.Id == "child");
        AssertEqual(
            "root",
            hierarchy[childIndex - 1].Id,
            "Child buckets should appear directly under their parent even if snapshots arrive out of order.");
        AssertEqual(
            "Root > Child",
            pickerChoices.Single(item => item.Id == "child").Label,
            "Bucket pickers should use full paths so nested placement is obvious.");
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

    public static void ProjectTreeNestsBucketsSlipsAndCounts()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var project = Project(
            buckets:
            [
                Bucket("root", "Root"),
                Bucket("child", "Child", "root")
            ],
            slips:
            [
                Slip("s1", "root", "kept note", "copy", "a", now),
                Slip("s2", "root", "hidden note", "copy", "a", now) with { ExcludedFromViews = true },
                Slip("s3", "child", "", "copy", "a", now) with { Type = ZetlSlipType.Picture }
            ]);

        var tree = KastnWorkbench.BuildProjectTree(project, project.Slips);

        AssertEqual(1, tree.Count, "Only the top-level Root bucket is a root node.");
        var root = tree[0];
        AssertEqual(KastnTreeNodeKind.Bucket, root.Kind, "The top node is a bucket.");
        AssertTrue(root.IsBucket, "Bucket nodes should expose their presentation kind.");
        AssertEqual(2, root.IncludedCount, "Root counts included slips in its complete subtree.");
        AssertEqual(1, root.HiddenCount, "Root counts one excluded slip.");
        AssertEqual("2 · 1 hidden", root.CountLabel, "Bucket badges should summarize hidden slips.");
        AssertTrue(root.IsVisibilityMixed, "A partly hidden bucket should expose mixed visibility.");
        AssertTrue(root.CanToggleVisibility, "A non-empty bucket should expose its visibility action.");

        AssertEqual(KastnTreeNodeKind.Bucket, root.Children[0].Kind, "Sub-buckets precede slips.");
        AssertEqual("child", root.Children[0].Id, "The Child bucket nests under Root.");

        var rootSlips = root.Children.Where(node => node.Kind == KastnTreeNodeKind.Slip).ToList();
        AssertEqual(2, rootSlips.Count, "Root's two slips are leaves.");
        AssertEqual("kept note", rootSlips[0].Label, "A slip label falls back to its text.");
        AssertTrue(
            rootSlips.Single(node => node.Id == "s2").IsExcluded,
            "Excluded slips stay in the tree and are flagged.");
        AssertTrue(
            rootSlips.Single(node => node.Id == "s2").ShowClosedEye,
            "Excluded slips should display the closed eye action.");

        var pictureLeaf = root.Children[0].Children.Single(node => node.Kind == KastnTreeNodeKind.Slip);
        AssertTrue(pictureLeaf.IsPicture, "Picture slips are flagged.");
        AssertTrue(!pictureLeaf.IsText, "Picture slips should not use the text icon.");
        AssertTrue(pictureLeaf.ShowOpenEye, "Included slips should display the open eye.");
        AssertEqual("Picture", pictureLeaf.Label, "A textless picture labels as Picture.");
    }

    public static void ProjectTreeSeparatesDeletedBucket()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var project = Project(
            buckets:
            [
                Bucket("root", "Root"),
                Bucket("deleted", "Deleted", kind: "Deleted")
            ],
            slips:
            [
                Slip("a", "root", "active", "copy", "s", now),
                Slip("d", "deleted", "trashed", "copy", "s", now)
            ]);

        // The normal tree excludes Deleted; the deleted-only tree shows just it.
        var normal = KastnWorkbench.BuildProjectTree(project, project.Slips);
        AssertEqual(1, normal.Count, "Only Root is a root node in the normal tree.");
        AssertTrue(normal.All(node => node.Id != "deleted"), "The normal tree omits the Deleted bucket.");

        var deleted = KastnWorkbench.BuildProjectTree(project, project.Slips, deletedOnly: true);
        AssertEqual(1, deleted.Count, "The deleted-only tree shows just the Deleted bucket.");
        AssertEqual("deleted", deleted[0].Id, "The deleted-only tree root is the Deleted bucket.");
        AssertTrue(
            deleted[0].Children.Any(child => child.Id == "d"),
            "Deleted slips appear under the Deleted bucket.");
    }

    public static void BatchFormatRewritesMarkersAndStrike()
    {
        // Strikethrough toggles. (List-item kind is now a per-note property, not body
        // markup, so there is no marker-rewriting helper to test here.)
        AssertEqual("~~done~~", KastnBatchFormat.ToggleStrike("done"), "Wrap in strike.");
        AssertEqual("done", KastnBatchFormat.ToggleStrike("~~done~~"), "Unwrap strike.");
    }

    public static void SelectionComputesSlipsTitleAndNone()
    {
        var s1 = SlipTreeNode("s1");
        var s2 = SlipTreeNode("s2");
        var s3 = SlipTreeNode("s3");
        var bucket = BucketTreeNode("b1", deleted: false, s1, s2, s3);
        var deletedBucket = BucketTreeNode("deleted", deleted: true, SlipTreeNode("d1"));

        // A single slip is a one-item Slips selection (single-slip edit).
        AssertTrue(
            KastnSelection.Compute([s1], s1, expandedBucketId: null) is KastnSelection.Slips { SlipIds.Count: 1 },
            "A single slip computes to a one-item Slips selection.");

        // Several slips form a batch, preserving order.
        var multi = KastnSelection.Compute([s1, s2, s3], s3, expandedBucketId: null) as KastnSelection.Slips;
        AssertEqual(3, multi?.SlipIds.Count ?? 0, "Several slips form a batch Slips selection.");
        AssertEqual("s1", multi?.SlipIds[0], "Batch selection preserves order.");

        // An un-expanded bucket is a title selection; expanded, it is its slips.
        AssertTrue(
            KastnSelection.Compute([bucket], bucket, expandedBucketId: null) is KastnSelection.BucketTitle { BucketId: "b1" },
            "An un-expanded bucket computes to a title selection.");
        AssertEqual(
            3,
            (KastnSelection.Compute([bucket], bucket, expandedBucketId: "b1") as KastnSelection.Slips)?.SlipIds.Count ?? 0,
            "An expanded bucket selects its slips.");

        // A deleted bucket is never a title; nothing selected is None.
        AssertTrue(
            KastnSelection.Compute([deletedBucket], deletedBucket, expandedBucketId: null) is KastnSelection.None,
            "A deleted bucket is not a title selection.");
        AssertTrue(
            KastnSelection.Compute([], null, expandedBucketId: null) is KastnSelection.None,
            "No nodes computes to the None selection.");
    }

    private static KastnTreeNode SlipTreeNode(string id) => new()
    {
        Kind = KastnTreeNodeKind.Slip,
        Id = id,
        Label = id,
        Slip = Slip(id, "b", "", "copy", "s", new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero)),
    };

    private static KastnTreeNode BucketTreeNode(string id, bool deleted, params KastnTreeNode[] children) => new()
    {
        Kind = KastnTreeNodeKind.Bucket,
        Id = id,
        Label = id,
        IsDeletedBucket = deleted,
        Children = children,
    };

    public static void SlipLabelTruncatesLongText()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var project = Project(
            buckets: [Bucket("b", "Bucket")],
            slips: [Slip("s", "b", "this is a very long slip note that should be truncated", "copy", "a", now)]);

        var label = KastnWorkbench.BuildProjectTree(project, project.Slips)[0].Children.Single().Label;

        AssertTrue(label.EndsWith("…", StringComparison.Ordinal), "A long slip label should end with an ellipsis.");
        AssertTrue(label.Length <= 24, "A truncated slip label should respect the max length.");
    }

    public static void ViewerFormatsVisibleSlipsAsReadableOutline()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var project = Project(
            buckets:
            [
                Bucket("child", "Child", "root"),
                Bucket("root", "Root"),
                Bucket("other", "Other")
            ],
            slips:
            [
                Slip("one", "root", "root note", "copy", "a", now),
                Slip("two", "child", $"child note{Environment.NewLine}continued", "copy", "a", now),
                Slip("three", "other", "hidden", "copy", "a", now)
            ]);

        var text = KastnWorkbench.BuildViewerText(
            project,
            project.Slips.Where(slip => slip.BucketId != "other").ToList());
        var expected = string.Join(Environment.NewLine,
        [
            "Project",
            "",
            "Root",
            "\troot note",
            "",
            "\tChild",
            "\t\tchild note",
            "\t\tcontinued"
        ]);

        AssertEqual(expected, text, "Viewer text should outline visible slips by nested bucket.");
    }

    public static void InspectorSurfacesCaptureAndPictureMetadata()
    {
        var captured = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var project = Project(
            buckets:
            [
                Bucket("root", "Research"),
                Bucket("child", "Images", "root"),
                Bucket("deleted", "Deleted", kind: "Deleted")
            ],
            slips: []);
        var slip = Slip(
            "picture-one",
            "deleted",
            "whiteboard sketch",
            "clipboard",
            "session-one",
            captured,
            revision: 4) with
        {
            Type = ZetlSlipType.Picture,
            Picture = new ZetlPictureSnapshot
            {
                SourceUrl = "https://example.com/sketch.png",
                Width = 1600,
                Height = 900,
                ByteLength = 1_572_864,
                Sha256 = new string('a', 64)
            },
            CaptureOrigin = new ZetlCaptureOriginSnapshot
            {
                ApplicationName = "Microsoft Edge",
                ProcessName = "msedge",
                WindowTitle = "Project reference"
            },
            DeletedFromBucketId = "child",
            DeletedAtUtc = captured.AddHours(1)
        };

        var sections = KastnSlipInspector.Build(project, slip);
        var fields = sections
            .SelectMany(section => section.Fields)
            .ToDictionary(field => field.Label, field => field.Value);

        AssertSequence(
            ["Overview", "Capture location", "Picture", "Lifecycle", "Technical"],
            sections.Select(section => section.Heading),
            "Inspector metadata should stay grouped and scannable.");
        AssertEqual("Research > Images", fields["Deleted from"], "Deleted slips should retain their original bucket path.");
        AssertEqual("1,600 × 900 px", fields["Dimensions"], "Picture dimensions should be human readable.");
        AssertEqual("1.5 MiB", fields["Size"], "Picture byte size should be human readable.");
        AssertEqual("Microsoft Edge", fields["Application"], "Capture application should be visible.");
        AssertEqual("https://example.com/sketch.png", fields["Original URL"], "Downloaded-image provenance should be visible.");
        AssertEqual("4", fields["Revision"], "Technical details should expose the current revision.");
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
            var deletedSlip = Payload<ZetlSlipSnapshot>(deleteSlip);
            var deleteBucket = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-delete-bucket",
                ZetlCommandKind.DeleteBucket,
                new DeleteBucketCommand(),
                fixture.Project.Id,
                sources.Id,
                sources.Revision));
            var createProject = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-create-project",
                ZetlCommandKind.CreateProject,
                new CreateProjectCommand
                {
                    Name = "Delete Me",
                    Buckets = [new CreateBucketDefinition { Name = "Inbox" }]
                }));
            var disposableProject = Payload<ZetlProjectSnapshot>(createProject);
            var deleteProject = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                "k4-delete-project",
                ZetlCommandKind.DeleteProject,
                new DeleteProjectCommand(),
                disposableProject.Id,
                disposableProject.Id,
                disposableProject.MetadataRevision));
            var listProjects = await controller.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "k4-list-after-project-delete",
                Kind = ZetlCommandKind.ListProjects
            });
            var summaries = Payload<List<ZetlProjectSummary>>(listProjects);

            AssertEqual("Sources", sources.Name, "Bucket rename should pass through IPC.");
            AssertEqual(fixture.Inbox.Id, sources.ParentBucketId, "Bucket move should pass through IPC.");
            AssertEqual("edited", edited.Text, "Slip edit should pass through IPC.");
            AssertEqual(sources.Id, moved.BucketId, "Slip move should pass through IPC.");
            AssertEqual(ZetlResponseStatus.Success, deleteSlip.Status, "Slip delete should succeed.");
            AssertEqual(sources.Id, deletedSlip.DeletedFromBucketId, "Slip delete should remember its source bucket.");
            AssertEqual(ZetlResponseStatus.Success, deleteBucket.Status, "Bucket delete should succeed.");
            AssertEqual(ZetlResponseStatus.Success, deleteProject.Status, "Project delete should succeed.");
            AssertFalse(
                summaries.Any(project => project.Id == disposableProject.Id),
                "Deleted project should disappear from Kastn's project list.");
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
        string? parentId = null,
        string kind = "Standard")
    {
        return new ZetlBucketSnapshot
        {
            Id = id,
            Revision = 1,
            Name = name,
            ParentBucketId = parentId,
            Settings = new ZetlBucketSettings { Kind = kind, DefaultKind = kind }
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

    private static void AssertFalse(bool condition, string message)
    {
        AssertTrue(!condition, message);
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
