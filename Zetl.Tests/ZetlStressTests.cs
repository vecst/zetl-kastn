using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;
using ZETL;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlStressTests
{
    [Fact] public void Run()
    {
        Console.WriteLine("Running Zetl Storage & IPC Stress Test...");

        var root = Path.Combine(Path.GetTempPath(), "ZetlStressTest_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            Console.WriteLine("Generating 10,000 slips...");
            var sw = Stopwatch.StartNew();

            var project = new ZetlProject
            {
                Id = "stress-project",
                Name = "Stress Project 10000"
            };

            var bucket = new ZetlBucket
            {
                Id = "stress-bucket",
                Name = "Data"
            };
            project.Buckets.Add(bucket);
            project.ActiveBucketId = bucket.Id;

            for (int i = 0; i < 10000; i++)
            {
                bucket.Slips.Add(new ZetlSlip
                {
                    Id = $"slip-{i:D6}",
                    Text = $"This is stress note {i} with some extra text to pad the size out a bit more.",
                    Source = "test",
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
            }

            sw.Stop();
            Console.WriteLine($"Generated 10,000 slips in {sw.ElapsedMilliseconds} ms.");

            Console.WriteLine("Testing JSON Write/Load on disk...");
            sw.Restart();
            var storage = new ZetlStateStorage(root, legacyStatePath: null);
            storage.WriteProject(project);
            sw.Stop();
            Console.WriteLine($"Wrote massive project to disk in {sw.ElapsedMilliseconds} ms.");

            sw.Restart();
            var loadedState = storage.Load();
            var loadedProject = loadedState.Projects.First();
            sw.Stop();

            Console.WriteLine($"Reloaded massive project from disk in {sw.ElapsedMilliseconds} ms.");

            if (loadedProject is null || loadedProject.Buckets.First().Slips.Count != 10000)
            {
                throw new InvalidOperationException("Failed to reload 10,000 slips from disk.");
            }

            Console.WriteLine("Testing IPC Serialization of massive project...");
            sw.Restart();
            var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(loadedProject);
            var json = JsonSerializer.Serialize(snapshot, ZetlProtocolJson.Options);
            sw.Stop();

            Console.WriteLine($"Serialized massive IPC payload ({json.Length / 1024} KB) in {sw.ElapsedMilliseconds} ms.");

        Console.WriteLine("Stress Test Passed!");
    }
    catch (Exception ex)
    {
        Console.WriteLine("Stress test failed:");
        Console.WriteLine(ex.ToString());
        Assert.Fail("Stress test failed");
    }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
