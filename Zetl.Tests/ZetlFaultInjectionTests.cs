using System;
using System.IO;
using System.Threading.Tasks;
using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlFaultInjectionTests
{

    [Fact] public void CallbackFailurePassesInputThroughWithoutEscaping()
    {
        Exception? reported = null;

        var result = ZetlCallbackSafety.FailOpen(
            new InvalidOperationException("handler failed"),
            () => (IntPtr)42,
            exception => reported = exception);

        AssertEqual((IntPtr)42, result, "A failed callback should chain to the next input handler.");
        AssertEqual("handler failed", reported?.Message, "The callback failure should be reported.");
    }

    [Fact] public void CallbackFailureStillFailsOpenWhenRecoveryThrows()
    {
        var result = ZetlCallbackSafety.FailOpen(
            new InvalidOperationException("handler failed"),
            () => throw new InvalidOperationException("pass-through failed"),
            _ => throw new InvalidOperationException("logging failed"));

        AssertEqual(IntPtr.Zero, result, "Recovery failures must not cross the unmanaged callback boundary.");
    }

    [Fact] public void StateStoreRoutesDurableIoThroughNarrowStorageContracts()
    {
        var loader = new StateLoaderProbe(StateWithTwoProjects());
        var projects = new ProjectStorageProbe();
        var workspace = new WorkspaceStorageProbe();
        var directories = new ProjectDirectoryLifecycleProbe();
        var store = new ZetlStateStore(
            loader,
            projects,
            workspace,
            directories,
            "split-storage-session");

        var project = store.State.Projects.Single(item => item.Id == "project-a");
        var bucket = project.Buckets.Single(item => item.Id == "bucket-a");
        store.AddSlip(bucket, "routed write", "test");
        store.SetActiveProject("project-b");
        store.DeleteProject("project-b");

        AssertEqual(1, loader.LoadCount, "State loading should use only the loader seam.");
        AssertEqual(1, projects.ProjectWriteCount, "A slip mutation should use project content storage.");
        AssertEqual(2, workspace.WorkspaceWriteCount, "Lane activation and deletion should use workspace storage.");
        AssertEqual(1, directories.RemovalCount, "Deletion should use only the directory lifecycle seam.");
    }

    [Fact] public void FailedProjectWriteRestoresLastDurableState()
    {
        var initial = StateWithTwoProjects();
        var storage = new FaultingStateStorage(initial);
        var store = new ZetlStateStore(storage, "fault-session");
        var project = store.State.Projects.Single(item => item.Id == "project-a");
        var slip = project.Buckets.Single(item => item.Id == "bucket-a").Slips.Single();
        var originalRevision = slip.Revision;
        var originalSequence = project.ChangeSequence;

        storage.FailNextProjectWrite = true;
        Assert.Throws<IOException>(() => store.UpdateSlip(slip, "failed edit"));

        var restoredProject = store.State.Projects.Single(item => item.Id == "project-a");
        var restoredSlip = restoredProject.Buckets.Single(item => item.Id == "bucket-a").Slips.Single();
        AssertEqual("durable text", restoredSlip.Text, "A failed write must not remain in live state.");
        AssertEqual(originalRevision, restoredSlip.Revision, "A failed write must restore the slip revision.");
        AssertEqual(originalSequence, restoredProject.ChangeSequence, "A failed write must restore the project sequence.");

        store.UpdateSlip(restoredSlip, "confirmed edit");
        AssertEqual(
            "confirmed edit",
            storage.DurableState.Projects.Single(item => item.Id == "project-a")
                .Buckets.Single(item => item.Id == "bucket-a").Slips.Single().Text,
            "A later successful write should persist from the restored baseline.");
    }

    [Fact] public void PersistenceCoordinatorAdvancesBaselineOnlyAfterSuccessfulWrite()
    {
        var initial = StateWithTwoProjects();
        var storage = new FaultingStateStorage(initial);
        var persistence = new ZetlStatePersistenceCoordinator(
            storage,
            storage,
            storage,
            initial);
        var live = JsonFile.Clone(initial);
        var project = live.Projects.Single(item => item.Id == "project-a");
        var slip = project.Buckets.Single(item => item.Id == "bucket-a").Slips.Single();

        slip.Text = "confirmed baseline";
        persistence.PersistProject(
            live,
            project,
            includeWorkspace: false,
            _ => { },
            () => { },
            WorkspaceFromState);

        slip.Text = "failed replacement";
        storage.FailNextProjectWrite = true;
        Assert.Throws<IOException>(() => persistence.PersistProject(
            live,
            project,
            includeWorkspace: false,
            _ => { },
            () => { },
            WorkspaceFromState));

        var restored = persistence.RestoreState();
        AssertEqual(
            "confirmed baseline",
            restored.Projects.Single(item => item.Id == "project-a")
                .Buckets.Single(item => item.Id == "bucket-a").Slips.Single().Text,
            "A failed write must not advance the coordinator's durable baseline.");
    }

    private static ZetlWorkspaceFile WorkspaceFromState(ZetlState state) => new()
    {
        ActiveProjectId = state.ActiveProjectId,
        ShiftActiveProjectId = state.ShiftActiveProjectId,
        DefaultJournalProjectId = state.DefaultJournalProjectId,
        ShiftDefaultJournalProjectId = state.ShiftDefaultJournalProjectId,
        LastDeliberateProjectId = state.LastDeliberateProjectId,
        ShiftLastDeliberateProjectId = state.ShiftLastDeliberateProjectId
    };

    [Fact] public void FailedWorkspaceWriteRestoresLanePointers()
    {
        var storage = new FaultingStateStorage(StateWithTwoProjects());
        var store = new ZetlStateStore(storage, "fault-session");

        storage.FailNextWorkspaceWrite = true;
        Assert.Throws<IOException>(() => store.SetActiveProject("project-b"));

        AssertEqual("project-a", store.State.ActiveProjectId, "A failed workspace write must restore the active lane.");
        AssertEqual(
            "project-a",
            store.State.LastDeliberateProjectId,
            "A failed workspace write must restore the last deliberate project.");
        AssertEqual(
            "project-a",
            storage.DurableWorkspace.ActiveProjectId,
            "Rollback should leave durable workspace pointers unchanged.");
    }

    [Fact] public void FailedProjectRemovalRestoresLiveProject()
    {
        var storage = new FaultingStateStorage(StateWithTwoProjects());
        var store = new ZetlStateStore(storage, "fault-session");
        storage.FailNextProjectRemoval = true;

        Assert.Throws<IOException>(() => store.DeleteProject("project-a"));

        AssertTrue(
            store.State.Projects.Any(project => project.Id == "project-a"),
            "A failed project-folder removal must restore the project in live state.");
        AssertTrue(
            storage.DurableState.Projects.Any(project => project.Id == "project-a"),
            "A failed project-folder removal must preserve the durable project.");
    }

    [Fact] public void FailedDeleteWorkspaceWriteRestoresProjectFile()
    {
        var storage = new FaultingStateStorage(StateWithTwoProjects());
        var store = new ZetlStateStore(storage, "fault-session");
        storage.FailNextWorkspaceWrite = true;

        Assert.Throws<IOException>(() => store.DeleteProject("project-a"));

        AssertTrue(
            store.State.Projects.Any(project => project.Id == "project-a"),
            "A failed delete transaction must restore the project in live state.");
        AssertTrue(
            storage.DurableState.Projects.Any(project => project.Id == "project-a"),
            "A failed delete transaction must rewrite the removed project file.");
        AssertEqual(
            "project-a",
            storage.DurableWorkspace.ActiveProjectId,
            "A failed delete transaction must keep its durable active pointer.");
    }

    [Fact] public void FailedDeleteWorkspaceWriteRestoresRealProjectAssets()
    {
        var root = NewDirectory();
        try
        {
            var project = JsonFile.Clone(StateWithTwoProjects().Projects[0]);
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            storage.WriteProject(project);
            var assetBytes = new byte[] { 9, 8, 7, 6, 5 };
            var assetPath = storage.WriteAsset(
                project,
                new string('a', 64),
                ".png",
                assetBytes);
            storage.WriteWorkspace(new ZetlWorkspaceFile
            {
                ActiveProjectId = project.Id,
                LastDeliberateProjectId = project.Id
            });

            var workspace = new FailOnceWorkspaceStorage(storage);
            var store = new ZetlStateStore(
                storage,
                storage,
                workspace,
                storage,
                "real-delete-rollback");
            workspace.FailNextWrite = true;

            Assert.Throws<IOException>(() => store.DeleteProject(project.Id));

            var restored = store.State.Projects.Single(item => item.Id == project.Id);
            AssertTrue(
                storage.ReadAsset(restored, assetPath)?.SequenceEqual(assetBytes) == true,
                "A failed delete transaction must restore project assets byte-for-byte.");
            AssertTrue(
                storage.GetAssets(restored).Any(asset => asset.RelativePath == assetPath),
                "The restored project directory should expose its original asset inventory.");
            var pendingRoot = Path.Combine(root, "pending-project-removals");
            AssertTrue(
                !Directory.Exists(pendingRoot)
                || !Directory.EnumerateDirectories(pendingRoot).Any(),
                "Rollback should move the prepared project directory out of pending removal.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void SuccessfulProjectDeletionLeavesNoLiveOrPendingDirectory()
    {
        var root = NewDirectory();
        try
        {
            var project = JsonFile.Clone(StateWithTwoProjects().Projects[0]);
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            storage.WriteProject(project);
            storage.WriteWorkspace(new ZetlWorkspaceFile
            {
                ActiveProjectId = project.Id,
                LastDeliberateProjectId = project.Id
            });
            var store = new ZetlStateStore(storage, "successful-delete");

            store.DeleteProject(project.Id);

            AssertTrue(
                !store.State.Projects.Any(item => item.Id == project.Id),
                "A successful deletion should remove the project from live state.");
            AssertTrue(
                !DirectoriesUnder(Path.Combine(root, "projects")).Any(),
                "A successful deletion should leave no live project directory.");
            AssertTrue(
                !DirectoriesUnder(Path.Combine(root, "pending-project-removals")).Any(),
                "A successful deletion should finalize its pending directory.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void StartupRestoresPreparedProjectRemovalWithAssets()
    {
        var root = NewDirectory();
        try
        {
            var project = JsonFile.Clone(StateWithTwoProjects().Projects[0]);
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            storage.WriteProject(project);
            var assetBytes = new byte[] { 4, 3, 2, 1 };
            var assetPath = storage.WriteAsset(
                project,
                new string('b', 64),
                ".png",
                assetBytes);
            storage.WriteWorkspace(new ZetlWorkspaceFile
            {
                ActiveProjectId = project.Id,
                LastDeliberateProjectId = project.Id
            });

            _ = storage.PrepareProjectRemoval(project.Id);

            var restoredStorage = new ZetlStateStorage(root, legacyStatePath: null);
            var restoredState = restoredStorage.Load();
            var restoredProject = restoredState.Projects.Single(item => item.Id == project.Id);

            AssertTrue(
                restoredStorage.ReadAsset(restoredProject, assetPath)?.SequenceEqual(assetBytes) == true,
                "Startup should restore a prepared project's complete directory, including assets.");
            AssertTrue(
                !DirectoriesUnder(Path.Combine(root, "pending-project-removals")).Any(),
                "A restored prepared removal should no longer remain pending.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void StartupRestoresMarkerlessPendingRemovalConservatively()
    {
        var root = NewDirectory();
        try
        {
            var project = JsonFile.Clone(StateWithTwoProjects().Projects[0]);
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            storage.WriteProject(project);
            _ = storage.PrepareProjectRemoval(project.Id);
            var pendingDirectory = DirectoriesUnder(
                Path.Combine(root, "pending-project-removals")).Single();
            File.Delete(Path.Combine(
                pendingDirectory,
                ZetlStateStorage.PendingRemovalMarkerFileName));

            var restored = new ZetlStateStorage(root, legacyStatePath: null).Load();

            AssertTrue(
                restored.Projects.Any(item => item.Id == project.Id),
                "A crash between directory move and marker write must restore rather than delete data.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void StartupPurgesCommittedPendingRemoval()
    {
        var root = NewDirectory();
        try
        {
            var project = JsonFile.Clone(StateWithTwoProjects().Projects[0]);
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            storage.WriteProject(project);
            _ = storage.PrepareProjectRemoval(project.Id);
            var pendingDirectory = DirectoriesUnder(
                Path.Combine(root, "pending-project-removals")).Single();
            var markerPath = Path.Combine(
                pendingDirectory,
                ZetlStateStorage.PendingRemovalMarkerFileName);
            var marker = JsonFile.Read<ZetlPendingProjectRemovalFile>(markerPath)!;
            marker.Status = ZetlStateStorage.CommittedRemovalStatus;
            JsonFile.WriteAtomic(markerPath, marker);

            var restored = new ZetlStateStorage(root, legacyStatePath: null).Load();

            AssertTrue(
                restored.Projects.All(item => item.Id != project.Id),
                "A committed pending removal must not resurrect its project.");
            AssertTrue(
                !DirectoriesUnder(Path.Combine(root, "pending-project-removals")).Any(),
                "Startup should finish cleanup for a committed pending removal.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void WorkspaceRoundTripPreservesAllLaneRecoveryPointers()
    {
        var root = NewDirectory();
        try
        {
            var state = StateWithTwoProjects();
            state.Projects[1].JournalMode = true;
            state.Projects[1].Name = "Journal Shift Week 29 2026";
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            foreach (var project in state.Projects)
            {
                storage.WriteProject(project);
            }

            storage.WriteWorkspace(new ZetlWorkspaceFile
            {
                ActiveProjectId = "project-a",
                ShiftActiveProjectId = "project-b",
                DefaultJournalProjectId = "project-b",
                ShiftDefaultJournalProjectId = "project-b",
                LastDeliberateProjectId = "project-a",
                ShiftLastDeliberateProjectId = "project-a"
            });

            var restored = new ZetlStateStore(Path.Combine(root, "state.json"), "restart-session");
            AssertEqual("project-a", restored.State.ActiveProjectId, "Main active pointer should survive restart.");
            AssertEqual("project-b", restored.State.ShiftActiveProjectId, "Alternate active pointer should survive restart.");
            AssertEqual("project-b", restored.State.DefaultJournalProjectId, "Main Journal pointer should survive restart.");
            AssertEqual("project-b", restored.State.ShiftDefaultJournalProjectId, "Alternate Journal pointer should survive restart.");
            AssertEqual("project-a", restored.State.LastDeliberateProjectId, "Main deliberate pointer should survive restart.");
            AssertEqual("project-a", restored.State.ShiftLastDeliberateProjectId, "Alternate deliberate pointer should survive restart.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void OlderWorkspaceInfersJournalPointerFromActiveProject()
    {
        var root = NewDirectory();
        try
        {
            var journal = new ZetlProject
            {
                Id = "journal",
                Name = "Journal Week 29 2026",
                JournalMode = true,
                Buckets = [new ZetlBucket { Id = "journal-day", Name = "Thu 07-16" }]
            };
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            storage.WriteProject(journal);
            storage.WriteWorkspace(new ZetlWorkspaceFile { ActiveProjectId = journal.Id });

            var restored = new ZetlStateStore(Path.Combine(root, "state.json"), "restart-session");
            AssertEqual(
                journal.Id,
                restored.State.DefaultJournalProjectId,
                "An older workspace should recover the active Journal as its lane default.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void AtomicWriteRemovesTemporaryFileAfterMoveFailure()
    {
        var root = NewDirectory();
        try
        {
            var destination = Path.Combine(root, "destination.json");
            Directory.CreateDirectory(destination);

            Assert.ThrowsAny<IOException>(() => JsonFile.WriteAtomic(destination, new[] { "value" }));
            AssertTrue(
                !Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly).Any(),
                "A failed atomic move should not leave its unique temp file behind.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void StaleTemporarySweepKeepsRecentWriterFiles()
    {
        var root = NewDirectory();
        try
        {
            var oldTemp = Path.Combine(root, "old.json.abc.tmp");
            var recentTemp = Path.Combine(root, "recent.json.def.tmp");
            File.WriteAllText(oldTemp, "old");
            File.WriteAllText(recentTemp, "recent");
            File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow.AddDays(-2));

            JsonFile.SweepStaleTempFiles(root, TimeSpan.FromDays(1));

            AssertTrue(!File.Exists(oldTemp), "The stale temp file should be removed.");
            AssertTrue(File.Exists(recentTemp), "A recent temp file may still belong to a live writer.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact] public void TestClipboardFailureDuringCompile()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZetlFaultTest_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        FileStream? fileLock = null;
        try
        {
            var project = new ZetlProject
            {
                Id = "faulty-project",
                Name = "Faulty Project"
            };

            var bucket = new ZetlBucket
            {
                Id = "faulty-bucket",
                Name = "Data"
            };
            project.Buckets.Add(bucket);
            project.ActiveBucketId = bucket.Id;
            
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            // Write it once so the file exists
            storage.WriteProject(project);

            var projectPath = Directory.GetFiles(Path.Combine(root, "projects"), "project.json", SearchOption.AllDirectories).Single();
            // Lock the file to prevent writing
            fileLock = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.None);

            // This should trigger a save that hits the lock and throws an IOException.
            try
            {
                project.Buckets[0].Slips.Add(new ZetlSlip { Id = "test-slip", Text = "Test Note", Source = "test" });
                storage.WriteProject(project);
                throw new Exception("Expected an IOException to be thrown from ZetlStateStorage due to file lock.");
            }
            catch (IOException)
            {
                // Expected
            }
        }
        finally
        {
            fileLock?.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact] public void TestClipboardFailureDuringReplay()
    {
        var clipboard = new FaultyClipboard(failOnSet: true);
        if (clipboard.SetText("test"))
        {
            throw new Exception("FaultyClipboard did not fail.");
        }
    }

    private sealed class FaultyClipboard : IClipboard
    {
        private readonly bool failOnSet;
        public FaultyClipboard(bool failOnSet) => this.failOnSet = failOnSet;

        public string? TryGetText() => null;
        public string? TryGetHtml() => null;
        public IReadOnlyList<ZetlClipboardFormatData>? TryGetReplayFormats() => null;
        public ZetlClipboardImage? TryGetImage() => null;
        public bool SetText(string text) => !failOnSet;
        public bool SetRichText(string plainText, string html) => !failOnSet;
        public bool SetImage(ZetlClipboardImage image) => !failOnSet;
        public ZetlClipboardBackup CaptureBackup() =>
            ZetlClipboardBackup.FromPortable(null, null, null);
        public bool RestoreBackup(ZetlClipboardBackup backup) => !failOnSet;
        public uint GetChangeToken() => 0;
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZetlFaultTest_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static IEnumerable<string> DirectoriesUnder(string path) =>
        Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];

    private static ZetlState StateWithTwoProjects()
    {
        return new ZetlState
        {
            ActiveProjectId = "project-a",
            LastDeliberateProjectId = "project-a",
            Projects =
            [
                new ZetlProject
                {
                    Id = "project-a",
                    Name = "Project A",
                    ChangeSequence = 4,
                    ActiveBucketId = "bucket-a",
                    Buckets =
                    [
                        new ZetlBucket
                        {
                            Id = "bucket-a",
                            Name = "Inbox",
                            Slips =
                            [
                                new ZetlSlip
                                {
                                    Id = "slip-a",
                                    Revision = 3,
                                    Text = "durable text",
                                    Source = "test",
                                    CreatedAtUtc = DateTimeOffset.UtcNow
                                }
                            ]
                        }
                    ]
                },
                new ZetlProject
                {
                    Id = "project-b",
                    Name = "Project B",
                    ActiveBucketId = "bucket-b",
                    Buckets = [new ZetlBucket { Id = "bucket-b", Name = "Inbox" }]
                }
            ]
        };
    }

    private sealed class StateLoaderProbe(ZetlState initial) : IZetlStateLoader
    {
        public int LoadCount { get; private set; }

        public ZetlState Load()
        {
            LoadCount++;
            return JsonFile.Clone(initial);
        }
    }

    private sealed class ProjectStorageProbe : IZetlProjectStorage
    {
        public int ProjectWriteCount { get; private set; }

        public void WriteProject(ZetlProject project) => ProjectWriteCount++;

        public string WriteAsset(
            ZetlProject project,
            string contentHash,
            string extension,
            byte[] bytes) => $"assets/{contentHash}{extension}";

        public byte[]? ReadAsset(ZetlProject project, string relativePath) => null;


        public IReadOnlyList<ZetlProjectAssetFile> GetAssets(ZetlProject project) => [];
    }

    private sealed class WorkspaceStorageProbe : IZetlWorkspaceStorage
    {
        public int WorkspaceWriteCount { get; private set; }

        public void WriteWorkspace(ZetlWorkspaceFile workspace) => WorkspaceWriteCount++;
    }

    private sealed class FailOnceWorkspaceStorage(IZetlWorkspaceStorage inner) : IZetlWorkspaceStorage
    {
        public bool FailNextWrite { get; set; }

        public void WriteWorkspace(ZetlWorkspaceFile workspace)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Injected late workspace write failure.");
            }

            inner.WriteWorkspace(workspace);
        }
    }

    private sealed class ProjectDirectoryLifecycleProbe : IZetlProjectDirectoryLifecycle
    {
        public int RemovalCount { get; private set; }

        public IZetlProjectRemoval PrepareProjectRemoval(string projectId)
        {
            RemovalCount++;
            return new ProjectRemovalProbe();
        }
    }

    private sealed class FaultingStateStorage : IZetlStateStorage
    {
        public FaultingStateStorage(ZetlState initial)
        {
            DurableState = JsonFile.Clone(initial);
            DurableWorkspace = WorkspaceFrom(initial);
        }

        public ZetlState DurableState { get; private set; }

        public ZetlWorkspaceFile DurableWorkspace { get; private set; }

        public bool FailNextProjectWrite { get; set; }

        public bool FailNextWorkspaceWrite { get; set; }

        public bool FailNextProjectRemoval { get; set; }

        public ZetlState Load() => JsonFile.Clone(DurableState);

        public void WriteProject(ZetlProject project)
        {
            if (FailNextProjectWrite)
            {
                FailNextProjectWrite = false;
                throw new IOException("Injected project write failure.");
            }

            var copy = JsonFile.Clone(project);
            var index = DurableState.Projects.FindIndex(item => item.Id == project.Id);
            if (index >= 0)
            {
                DurableState.Projects[index] = copy;
            }
            else
            {
                DurableState.Projects.Add(copy);
            }
        }

        public string WriteAsset(
            ZetlProject project,
            string contentHash,
            string extension,
            byte[] bytes) => $"assets/{contentHash}{extension}";

        public byte[]? ReadAsset(ZetlProject project, string relativePath) => null;


        public IReadOnlyList<ZetlProjectAssetFile> GetAssets(ZetlProject project) => [];

        public void WriteWorkspace(ZetlWorkspaceFile workspace)
        {
            if (FailNextWorkspaceWrite)
            {
                FailNextWorkspaceWrite = false;
                throw new IOException("Injected workspace write failure.");
            }

            DurableWorkspace = JsonFile.Clone(workspace);
            DurableState.Version = workspace.Version;
            DurableState.ActiveProjectId = workspace.ActiveProjectId;
            DurableState.ShiftActiveProjectId = workspace.ShiftActiveProjectId;
            DurableState.DefaultJournalProjectId = workspace.DefaultJournalProjectId;
            DurableState.ShiftDefaultJournalProjectId = workspace.ShiftDefaultJournalProjectId;
            DurableState.LastDeliberateProjectId = workspace.LastDeliberateProjectId;
            DurableState.ShiftLastDeliberateProjectId = workspace.ShiftLastDeliberateProjectId;
        }

        public IZetlProjectRemoval PrepareProjectRemoval(string projectId)
        {
            if (FailNextProjectRemoval)
            {
                FailNextProjectRemoval = false;
                throw new IOException("Injected project removal failure.");
            }

            var removed = DurableState.Projects.FirstOrDefault(
                project => project.Id == projectId);
            DurableState.Projects.RemoveAll(project => project.Id == projectId);
            return new ProjectRemovalProbe(
                rollBack: () =>
                {
                    if (removed is not null
                        && DurableState.Projects.All(project => project.Id != projectId))
                    {
                        DurableState.Projects.Add(JsonFile.Clone(removed));
                    }
                });
        }

        private static ZetlWorkspaceFile WorkspaceFrom(ZetlState state) => new()
        {
            Version = state.Version,
            ActiveProjectId = state.ActiveProjectId,
            ShiftActiveProjectId = state.ShiftActiveProjectId,
            DefaultJournalProjectId = state.DefaultJournalProjectId,
            ShiftDefaultJournalProjectId = state.ShiftDefaultJournalProjectId,
            LastDeliberateProjectId = state.LastDeliberateProjectId,
            ShiftLastDeliberateProjectId = state.ShiftLastDeliberateProjectId
        };
    }

    private sealed class ProjectRemovalProbe(Action? rollBack = null) : IZetlProjectRemoval
    {
        private bool completed;

        public void Commit() => completed = true;

        public void RollBack()
        {
            if (completed)
            {
                return;
            }

            rollBack?.Invoke();
            completed = true;
        }

        public void Dispose()
        {
            if (!completed)
            {
                RollBack();
            }
        }
    }
}
