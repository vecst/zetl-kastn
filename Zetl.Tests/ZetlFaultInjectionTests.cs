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
        Assert.Throws<IOException>(() => store.UpdateNote(slip, "failed edit"));

        var restoredProject = store.State.Projects.Single(item => item.Id == "project-a");
        var restoredSlip = restoredProject.Buckets.Single(item => item.Id == "bucket-a").Slips.Single();
        AssertEqual("durable text", restoredSlip.Text, "A failed write must not remain in live state.");
        AssertEqual(originalRevision, restoredSlip.Revision, "A failed write must restore the slip revision.");
        AssertEqual(originalSequence, restoredProject.ChangeSequence, "A failed write must restore the project sequence.");

        store.UpdateNote(restoredSlip, "confirmed edit");
        AssertEqual(
            "confirmed edit",
            storage.DurableState.Projects.Single(item => item.Id == "project-a")
                .Buckets.Single(item => item.Id == "bucket-a").Slips.Single().Text,
            "A later successful write should persist from the restored baseline.");
    }

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
        public ZetlClipboardImage? TryGetImage() => null;
        public bool SetText(string text) => !failOnSet;
        public bool SetRichText(string plainText, string html) => !failOnSet;
        public bool SetImage(ZetlClipboardImage image) => !failOnSet;
        public uint GetChangeToken() => 0;
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZetlFaultTest_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

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

        public string? GetAssetPath(ZetlProject project, string relativePath) => null;

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

        public void RemoveProject(string projectId)
        {
            if (FailNextProjectRemoval)
            {
                FailNextProjectRemoval = false;
                throw new IOException("Injected project removal failure.");
            }

            DurableState.Projects.RemoveAll(project => project.Id == projectId);
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
}
