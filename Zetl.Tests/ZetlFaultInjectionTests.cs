using System;
using System.IO;
using System.Threading.Tasks;
using ZETL;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlFaultInjectionTests
{

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
}
