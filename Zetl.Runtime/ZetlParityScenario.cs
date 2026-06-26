namespace ZETL;

internal static class ZetlParityScenario
{
    public const string SnapshotFileName = "parity-snapshot.json";

    public static void Run(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A parity data directory is required.", nameof(dataDirectory));
        }

        if (Directory.Exists(dataDirectory)
            && Directory.EnumerateFileSystemEntries(dataDirectory).Any())
        {
            throw new InvalidOperationException(
                "The parity data directory must be empty.");
        }

        Directory.CreateDirectory(dataDirectory);
        var settingsStore = new ZetlAppSettingsStore(
            Path.Combine(dataDirectory, "settings.json"));
        settingsStore.Settings.DefaultProjectBuckets = ["Inbox", "Queue", "Scratch"];
        settingsStore.Settings.DefaultCompileMode = "TSV";
        settingsStore.Settings.DefaultTsvRowLength = 3;
        settingsStore.Settings.AutoCaptureOnCopy = false;
        settingsStore.Settings.QuickNoteToClipboard = true;
        settingsStore.MarkFirstRunSeen();

        var store = new ZetlStateStore(
            Path.Combine(dataDirectory, "state.json"),
            sessionId: "parity-session");
        ZetlRuntimeSettings.ApplyTo(store, settingsStore.Settings);

        var project = store.CreateProject(
            "Parity Project",
            settingsStore.Settings.DefaultProjectBuckets,
            activeBucketName: "Inbox");
        var inbox = project.Buckets.Single(bucket => bucket.Name == "Inbox");
        var queue = project.Buckets.Single(bucket => bucket.Name == "Queue");
        var child = store.AddBucket(project, "Child", inbox.Id, setActive: false);
        store.AddNote(inbox, "alpha", "copy");
        store.AddNote(inbox, "beta", "cut");
        store.AddNote(child, "nested", "manual");
        store.AddNote(queue, "first replay item", "copy");
        store.AddNote(queue, "second replay item", "copy");
        store.SetBucketKind(queue, "Replay");
        store.SetQuickNoteBucket(project, inbox.Id);
        store.SetActiveBucket(project, inbox.Id);

        var shifted = store.CreateProject(
            "Shift Parity",
            ["Shift Inbox", "Scratch"],
            activeBucketName: "Shift Inbox",
            shifted: true);
        store.AddNote(
            shifted.Buckets.Single(bucket => bucket.Name == "Shift Inbox"),
            "shift lane note",
            "copy");
        store.AppendLogNotes(["Parity scenario completed."], 14, 2000);

        var snapshot = new ParitySnapshot(
            settingsStore.Settings.HasSeenFirstRun,
            settingsStore.Settings.AutoCaptureOnCopy,
            settingsStore.Settings.QuickNoteToClipboard,
            settingsStore.Settings.DefaultCompileMode,
            settingsStore.Settings.DefaultTsvRowLength,
            NormalizeProject(store.GetActiveProject()),
            NormalizeProject(store.GetActiveProject(shifted: true)),
            store.State.Projects
                .Where(item => item.Name == ZetlStateStore.LogProjectName)
                .Select(NormalizeProject)
                .Single());
        JsonFile.WriteAtomic(
            Path.Combine(dataDirectory, SnapshotFileName),
            snapshot);
    }

    private static ParityProject NormalizeProject(ZetlProject? project)
    {
        if (project is null)
        {
            throw new InvalidOperationException("Parity scenario did not create its expected project.");
        }

        var activeBucket = project.Buckets.FirstOrDefault(
            bucket => bucket.Id == project.ActiveBucketId)?.Name;
        var quickNoteBucket = project.Buckets.FirstOrDefault(
            bucket => bucket.Id == project.QuickNoteBucketId)?.Name;
        var bucketNames = project.Buckets.ToDictionary(
            bucket => bucket.Id,
            bucket => bucket.Name,
            StringComparer.Ordinal);
        return new ParityProject(
            project.Name,
            activeBucket,
            quickNoteBucket,
            project.Buckets
                .OrderBy(bucket => bucket.Name, StringComparer.Ordinal)
                .Select(bucket => new ParityBucket(
                    bucket.Name,
                    bucket.ParentBucketId is not null
                        && bucketNames.TryGetValue(bucket.ParentBucketId, out var parentName)
                            ? parentName
                            : null,
                    bucket.Settings.Kind,
                    bucket.Settings.PopMode,
                    bucket.Settings.DefaultCompileMode,
                    bucket.Settings.DefaultTsvRowLength,
                    bucket.Slips
                        .Select(note => new ParityNote(note.Text, note.Source))
                        .ToList()))
                .ToList());
    }

    private sealed record ParitySnapshot(
        bool HasSeenFirstRun,
        bool AutoCaptureOnCopy,
        bool QuickNoteToClipboard,
        string DefaultCompileMode,
        int DefaultTsvRowLength,
        ParityProject ActiveProject,
        ParityProject ShiftActiveProject,
        ParityProject ActivityLog);

    private sealed record ParityProject(
        string Name,
        string? ActiveBucket,
        string? QuickNoteBucket,
        IReadOnlyList<ParityBucket> Buckets);

    private sealed record ParityBucket(
        string Name,
        string? ParentBucket,
        string Kind,
        bool PopMode,
        string DefaultCompileMode,
        int DefaultTsvRowLength,
        IReadOnlyList<ParityNote> Notes);

    private sealed record ParityNote(string Text, string Source);
}
