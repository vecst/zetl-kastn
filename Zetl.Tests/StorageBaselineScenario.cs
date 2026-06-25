using System.Diagnostics;

namespace ZETL.Tests;

internal static class StorageBaselineScenario
{
    private static readonly int[] NoteCounts = [1_000, 5_000, 20_000];
    private const int MeasurementRuns = 5;

    public static int Run()
    {
        try
        {
            WarmUp();
            Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            Console.WriteLine($"Process architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            Console.WriteLine();
            Console.WriteLine("| Slips | JSON MiB | Atomic rewrite ms | Load ms | Loaded memory MiB |");
            Console.WriteLine("| ---: | ---: | ---: | ---: | ---: |");

            foreach (var noteCount in NoteCounts)
            {
                var result = Measure(noteCount);
                Console.WriteLine(
                    $"| {result.NoteCount:N0} | {result.FileSizeMib:F2} | {result.RewriteMilliseconds:F2} | {result.LoadMilliseconds:F2} | {result.LoadedMemoryMib:F2} |");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Storage baseline failed: {ex}");
            return 1;
        }
    }

    private static StorageBaselineResult Measure(int noteCount)
    {
        using var temp = new BaselineTempDirectory();
        var storage = new ZetlStateStorage(temp.Path, legacyStatePath: null);
        var project = CreateProject(noteCount);
        storage.WriteProject(project);

        var projectPath = Directory
            .GetFiles(Path.Combine(temp.Path, "projects"), "project.json", SearchOption.AllDirectories)
            .Single();
        var fileSizeMib = new FileInfo(projectPath).Length / 1024d / 1024d;

        var rewriteTimes = new List<double>(MeasurementRuns);
        for (var run = 0; run < MeasurementRuns; run++)
        {
            project.Buckets[0].Notes[0].Text = $"revised-{run:D2}";
            var stopwatch = Stopwatch.StartNew();
            storage.WriteProject(project);
            stopwatch.Stop();
            rewriteTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var loadTimes = new List<double>(MeasurementRuns);
        var loadMemory = new List<double>(MeasurementRuns);
        for (var run = 0; run < MeasurementRuns; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

            var stopwatch = Stopwatch.StartNew();
            var loaded = new ZetlStateStorage(temp.Path, legacyStatePath: null).Load();
            stopwatch.Stop();
            var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);

            if (loaded.Projects.Single().Buckets.Sum(bucket => bucket.Notes.Count) != noteCount)
            {
                throw new InvalidOperationException("Loaded slip count did not match the baseline project.");
            }

            loadTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
            loadMemory.Add(Math.Max(0, memoryAfter - memoryBefore) / 1024d / 1024d);
            GC.KeepAlive(loaded);
        }

        return new StorageBaselineResult(
            noteCount,
            fileSizeMib,
            Median(rewriteTimes),
            Median(loadTimes),
            Median(loadMemory));
    }

    private static ZetlProject CreateProject(int noteCount)
    {
        const int bucketCount = 10;
        var notesPerBucket = noteCount / bucketCount;
        var remainder = noteCount % bucketCount;
        var createdAt = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var project = new ZetlProject
        {
            Id = "baseline-project",
            Name = $"Baseline {noteCount:N0}"
        };

        for (var bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
        {
            var bucket = new ZetlBucket
            {
                Id = $"bucket-{bucketIndex:D2}",
                Name = $"Bucket {bucketIndex + 1}",
                DefaultStartingText = "Header One\nHeader Two"
            };
            var count = notesPerBucket + (bucketIndex < remainder ? 1 : 0);
            for (var noteIndex = 0; noteIndex < count; noteIndex++)
            {
                var globalIndex = bucketIndex * notesPerBucket + Math.Min(bucketIndex, remainder) + noteIndex;
                bucket.Slips.Add(new ZetlSlip
                {
                    Id = $"note-{globalIndex:D6}",
                    Text = $"Baseline note {globalIndex:D6}: representative captured text for JSON storage measurement.",
                    Source = globalIndex % 4 == 0 ? "cut" : "copy",
                    SessionId = "baseline-session",
                    CreatedAtUtc = createdAt.AddSeconds(globalIndex)
                });
            }

            project.Buckets.Add(bucket);
        }

        project.ActiveBucketId = project.Buckets[0].Id;
        project.QuickNoteBucketId = project.Buckets[1].Id;
        return project;
    }

    private static void WarmUp()
    {
        using var temp = new BaselineTempDirectory();
        var storage = new ZetlStateStorage(temp.Path, legacyStatePath: null);
        storage.WriteProject(CreateProject(10));
        _ = new ZetlStateStorage(temp.Path, legacyStatePath: null).Load();
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private sealed record StorageBaselineResult(
        int NoteCount,
        double FileSizeMib,
        double RewriteMilliseconds,
        double LoadMilliseconds,
        double LoadedMemoryMib);

    private sealed class BaselineTempDirectory : IDisposable
    {
        public BaselineTempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"zetl-storage-baseline-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
