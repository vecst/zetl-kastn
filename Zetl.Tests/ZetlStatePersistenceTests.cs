using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlStatePersistenceTests
{
    [Fact(DisplayName = "Zetl state round-trips JSON")]
    public static void StateRoundTripsJson()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var bucket = store.ActiveBucket!;
        var note = store.AddSlip(bucket, "round trip", "copy");

        var loaded = new ZetlStateStore(temp.Path);
        AssertEqual(project.Id, loaded.ActiveProject?.Id, "Active project id should round-trip.");
        AssertEqual(bucket.Id, loaded.ActiveBucket?.Id, "Active bucket id should round-trip.");
        AssertEqual(note.Id, loaded.ActiveBucket?.Slips.Single().Id, "Note id should round-trip.");
        AssertEqual("round trip", loaded.ActiveBucket?.Slips.Single().Text, "Note text should round-trip.");

        var projectPath = Directory.GetFiles(
            System.IO.Path.GetDirectoryName(temp.Path)!,
            "project.json",
            SearchOption.AllDirectories).Single();
        var projectJson = System.IO.File.ReadAllText(projectPath);
        AssertTrue(projectJson.Contains("\"notes\":", StringComparison.Ordinal), "Slip storage should retain the historical notes field.");
        AssertFalse(projectJson.Contains("\"slips\":", StringComparison.Ordinal), "Slip storage should not introduce a second serialized collection.");
    }

    [Fact(DisplayName = "Zetl state stores each project in its own folder")]
    public static void StateStoresEachProjectInItsOwnFolder()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Alpha", ["Inbox"], "Inbox");
        store.CreateProject("Beta", ["Inbox"], "Inbox");

        var root = System.IO.Path.GetDirectoryName(temp.Path)!;
        var projectsDirectory = System.IO.Path.Combine(root, "projects");
        AssertTrue(File.Exists(System.IO.Path.Combine(root, "workspace.json")), "Workspace pointer file should be written.");
        AssertFalse(File.Exists(temp.Path), "No monolithic state.json should be written under the new layout.");

        var projectFiles = Directory.GetFiles(projectsDirectory, "project.json", SearchOption.AllDirectories);
        AssertEqual(2, projectFiles.Length, "Each project should get its own project.json.");
        AssertTrue(
            Directory.GetDirectories(projectsDirectory).Any(dir => System.IO.Path.GetFileName(dir).StartsWith("Alpha-", StringComparison.Ordinal)),
            "Project folder should be named from the project name plus a short id.");

        var loaded = new ZetlStateStore(temp.Path);
        AssertEqual(2, loaded.State.Projects.Count, "Projects should reload from their per-project folders.");
        AssertTrue(loaded.State.Projects.Any(project => project.Name == "Beta"), "Reloaded projects should keep their names.");
    }

    [Fact(DisplayName = "Zetl state migrates a legacy single state file")]
    public static void StateMigratesLegacySingleFile()
    {
        using var temp = new TempStateFile();
        var legacyJson =
            """
            { "version": 1, "activeProjectId": "p1", "projects": [ { "id": "p1", "name": "Legacy", "activeBucketId": "b1", "buckets": [ { "id": "b1", "name": "Inbox", "kind": "Standard", "notes": [ { "id": "n1", "text": "carried over", "source": "copy" } ] } ] } ] }
            """;
        File.WriteAllText(temp.Path, legacyJson);

        var store = new ZetlStateStore(temp.Path);
        var root = System.IO.Path.GetDirectoryName(temp.Path)!;

        AssertFalse(File.Exists(temp.Path), "Legacy state.json should be moved aside after migration.");
        AssertTrue(File.Exists(temp.Path + ".bak"), "Legacy state.json should be archived as a .bak backup.");
        AssertTrue(File.Exists(System.IO.Path.Combine(root, "workspace.json")), "Migration should write the workspace pointer file.");
        AssertEqual(
            1,
            Directory.GetFiles(System.IO.Path.Combine(root, "projects"), "project.json", SearchOption.AllDirectories).Length,
            "Migration should split the legacy project into its own file.");
        AssertEqual("Legacy", store.ActiveProject?.Name, "Migrated active project should load.");
        AssertEqual("carried over", store.ActiveBucket?.Slips.Single().Text, "Migrated note should survive the split.");

        var reloaded = new ZetlStateStore(temp.Path);
        AssertEqual("Legacy", reloaded.State.Projects.Single().Name, "Migrated project should reload from the new layout.");
    }

    [Fact(DisplayName = "Zetl json writes do not collide under concurrent writers")]
    public static void JsonFileConcurrentWritesDoNotCollide()
    {
        using var temp = new TempStateFile();
        // Another writer mid-write used to hold this exact temp name,
        // which made WriteAtomic throw a sharing violation.
        using var heldTemp = new FileStream(
            temp.Path + ".tmp",
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        JsonFile.WriteAtomic(temp.Path, new[] { "first" });
        AssertTrue(File.Exists(temp.Path), "Write should land while another writer holds the shared temp name.");

        JsonFile.WriteAtomic(temp.Path, new[] { "second" });
        AssertEqual(
            "second",
            JsonFile.Read<string[]>(temp.Path)?.Single(),
            "Replacing an existing file should also ignore the held temp name.");
    }

    [Fact(DisplayName = "Zetl json parse errors name the damaged file")]
    public static void JsonFileReadNamesDamagedFile()
    {
        using var temp = new TempStateFile();
        File.WriteAllText(temp.Path, "{ this is not json");
        try
        {
            JsonFile.Read<string[]>(temp.Path);
            AssertTrue(false, "Reading a damaged file should throw.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            AssertTrue(
                ex.Message.Contains(temp.Path),
                $"Parse errors should name the damaged file. Got: {ex.Message}");
        }
    }

    [Fact(DisplayName = "Zetl json read-or-quarantine moves corrupt files aside")]
    public static void JsonFileQuarantinesCorruptFile()
    {
        using var temp = new TempStateFile();
        var directory = System.IO.Path.GetDirectoryName(temp.Path)!;
        File.WriteAllText(temp.Path, "{ not json");

        var result = JsonFile.ReadOrQuarantine<string[]>(temp.Path);

        AssertTrue(result is null, "Reading a corrupt file should return default instead of throwing.");
        AssertFalse(File.Exists(temp.Path), "The corrupt file should be moved aside.");
        AssertEqual(
            1,
            Directory.GetFiles(directory, "state.json.corrupt-*").Length,
            "ReadOrQuarantine should leave one quarantined copy.");
    }

    [Fact(DisplayName = "Zetl state skips a corrupt project and keeps the rest")]
    public static void StateSkipsCorruptProjectFile()
    {
        using var temp = new TempStateFile();
        var projectsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "projects");
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("KeepMe", ["Inbox"], "Inbox");
        store.CreateProject("BreakMe", ["Inbox"], "Inbox");

        var corruptFile = Directory
            .GetFiles(projectsDir, "project.json", SearchOption.AllDirectories)
            .Single(path => System.IO.Path
                .GetFileName(System.IO.Path.GetDirectoryName(path)!)
                .StartsWith("BreakMe", StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(corruptFile, "{ not valid json");

        var reloaded = new ZetlStateStore(temp.Path);

        AssertEqual(1, reloaded.State.Projects.Count, "Only the valid project should load; the corrupt one is skipped.");
        AssertEqual("KeepMe", reloaded.State.Projects.Single().Name, "A valid project should survive a sibling's corruption.");
        AssertFalse(File.Exists(corruptFile), "The corrupt project.json should be moved aside.");
        AssertEqual(
            1,
            Directory.GetFiles(System.IO.Path.GetDirectoryName(corruptFile)!, "project.json.corrupt-*").Length,
            "The corrupt project.json should be quarantined in place.");
    }

    [Fact(DisplayName = "Zetl state recovers from a corrupt workspace file")]
    public static void StateRecoversFromCorruptWorkspace()
    {
        using var temp = new TempStateFile();
        var root = System.IO.Path.GetDirectoryName(temp.Path)!;
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("Survivor", ["Inbox"], "Inbox");

        var workspacePath = System.IO.Path.Combine(root, "workspace.json");
        File.WriteAllText(workspacePath, "{ broken");

        var reloaded = new ZetlStateStore(temp.Path);

        AssertEqual("Survivor", reloaded.State.Projects.Single().Name, "Projects should still load when workspace.json is corrupt.");
        AssertFalse(File.Exists(workspacePath), "The corrupt workspace.json should be moved aside.");
        AssertEqual(
            1,
            Directory.GetFiles(root, "workspace.json.corrupt-*").Length,
            "The corrupt workspace.json should be quarantined.");
    }

    [Fact(DisplayName = "Zetl state skips an unreadable project and keeps the rest")]
    public static void StateSkipsUnreadableProjectFile()
    {
        using var temp = new TempStateFile();
        var projectsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "projects");
        var store = new ZetlStateStore(temp.Path);
        store.CreateProject("KeepMe", ["Inbox"], "Inbox");
        store.CreateProject("LockMe", ["Inbox"], "Inbox");

        var lockedFile = Directory
            .GetFiles(projectsDir, "project.json", SearchOption.AllDirectories)
            .Single(path => System.IO.Path
                .GetFileName(System.IO.Path.GetDirectoryName(path)!)
                .StartsWith("LockMe", StringComparison.OrdinalIgnoreCase));

        // Hold the file open with no sharing so the next read fails with an
        // IOException rather than parsing as corrupt.
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var reloaded = new ZetlStateStore(temp.Path);

            AssertEqual(1, reloaded.State.Projects.Count, "An unreadable project should be skipped, not abort the load.");
            AssertEqual("KeepMe", reloaded.State.Projects.Single().Name, "Valid projects should still load past an unreadable sibling.");
        }

        AssertTrue(File.Exists(lockedFile), "An unreadable (not corrupt) file must be left in place, not quarantined.");
    }

    [Fact(DisplayName = "Zetl app settings recover from an unreadable file")]
    public static void AppSettingsRecoverFromUnreadableFile()
    {
        using var temp = new TempStateFile();
        var settingsPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "settings.json");
        File.WriteAllText(settingsPath, "{ \"toastDisplayMs\": 1234 }");

        using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var store = new ZetlAppSettingsStore(settingsPath);
            AssertEqual(950, store.Settings.ToastDisplayMs, "Unreadable settings should fall back to defaults, not abort startup.");
        }

        AssertTrue(File.Exists(settingsPath), "Unreadable settings must be left in place, not quarantined.");
    }

    [Fact(DisplayName = "Zetl state migration tolerates a backup rename failure")]
    public static void StateMigrationToleratesBackupRenameFailure()
    {
        using var temp = new TempStateFile();
        var legacyJson =
            """
            { "version": 1, "activeProjectId": "p1", "projects": [ { "id": "p1", "name": "Legacy", "activeBucketId": "b1", "buckets": [ { "id": "b1", "name": "Inbox", "kind": "Standard", "notes": [] } ] } ] }
            """;
        File.WriteAllText(temp.Path, legacyJson);
        var root = System.IO.Path.GetDirectoryName(temp.Path)!;

        // Hold the legacy file readable but not renamable: migration can read
        // it, but the .bak rename fails with a sharing violation.
        using (new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var store = new ZetlStateStore(temp.Path);

            AssertEqual("Legacy", store.ActiveProject?.Name, "Migration should still produce the project when the backup rename fails.");
            AssertEqual(
                1,
                Directory.GetFiles(System.IO.Path.Combine(root, "projects"), "project.json", SearchOption.AllDirectories).Length,
                "Migration should write the split project file even if the backup rename fails.");
        }
    }
}
