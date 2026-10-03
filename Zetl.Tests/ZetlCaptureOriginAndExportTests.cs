using Chordl;
using static Chordl.ChordlKeys;
using static ZETL.Tests.ZetlTestSupport;
using Xunit;
using System;
using System.Collections.Generic;

namespace ZETL.Tests;

public class ZetlCaptureOriginAndExportTests
{
    [Fact(DisplayName = "Zetl capture origin respects privacy detail")]
    public static void CaptureOriginRespectsPrivacyDetail()
    {
        var full = ZetlCaptureOrigin.Create(
            "Browser",
            "browser",
            "Private document title",
            ZetlCaptureOriginDetail.ApplicationAndWindowTitle);
        AssertEqual("Browser", full?.ApplicationName, "Full origin should retain the application name.");
        AssertEqual("browser", full?.ProcessName, "Full origin should retain the process name.");
        AssertEqual("Private document title", full?.WindowTitle, "Full origin should retain the window title.");

        var applicationOnly = ZetlCaptureOrigin.Create(
            "Browser",
            "browser",
            "Private document title",
            ZetlCaptureOriginDetail.ApplicationOnly);
        AssertEqual("Browser", applicationOnly?.ApplicationName, "Application-only origin should retain the application.");
        AssertEqual<string?>(null, applicationOnly?.WindowTitle, "Application-only origin should omit the window title.");

        AssertEqual<ZetlCaptureOrigin?>(
            null,
            ZetlCaptureOrigin.Create(
                "Browser",
                "browser",
                "Private document title",
                ZetlCaptureOriginDetail.Off),
            "Disabled origin capture should produce no metadata envelope.");
    }

    [Fact(DisplayName = "Zetl capture origin round-trips with notes")]
    public static void CaptureOriginRoundTripsWithNotes()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        store.AddSlip(
            project.Buckets[0],
            "captured text",
            "copy",
            ZetlCaptureOrigin.Create(
                "Browser",
                "browser",
                "Research — Browser",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle));

        var reloaded = new ZetlStateStore(temp.Path);
        var note = reloaded.State.Projects
            .Single(project => project.Name == "Demo")
            .Buckets.Single(bucket => bucket.Name == "Inbox")
            .Slips.Single();
        AssertEqual("Browser", note.CaptureOrigin?.ApplicationName, "Application name should persist with the note.");
        AssertEqual("browser", note.CaptureOrigin?.ProcessName, "Process name should persist with the note.");
        AssertEqual("Research — Browser", note.CaptureOrigin?.WindowTitle, "Window title should persist with the note.");
        AssertTrue(note.HasCaptureOrigin, "A persisted origin should remain displayable.");
    }

    [Fact(DisplayName = "Zetl clean export strips capture origin")]
    public static void CleanExportStripsCaptureOrigin()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Demo", ["Inbox"], "Inbox");
        var note = store.AddSlip(
            project.Buckets[0],
            "captured text",
            "copy",
            captureOrigin: ZetlCaptureOrigin.Create(
                "Editor",
                "editor",
                "Sensitive customer name",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle),
            richHtml: "<p data-private=\"producer-metadata\"><strong>captured text</strong></p>",
            replayFormats:
            [
                new ZetlClipboardFormatData(
                    50002,
                    [1, 2, 3],
                    "Star Embed Source (XML)")
            ]);

        var clean = ZetlProjectExportSnapshot.Create(project, includeCaptureOrigins: false);
        var archive = ZetlProjectExportSnapshot.Create(project, includeCaptureOrigins: true);

        AssertEqual<ZetlCaptureOrigin?>(
            null,
            clean.Buckets.SelectMany(bucket => bucket.Slips).Single().CaptureOrigin,
            "A clean export snapshot should remove the complete capture-origin envelope.");
        AssertEqual<string?>(
            null,
            clean.Buckets.SelectMany(bucket => bucket.Slips).Single().RichHtml,
            "A clean export snapshot should remove hidden source HTML.");
        AssertEqual(
            "Sensitive customer name",
            archive.Buckets.SelectMany(bucket => bucket.Slips).Single().CaptureOrigin?.WindowTitle,
            "An archive export snapshot should preserve capture origin.");
        AssertTrue(
            archive.Buckets.SelectMany(bucket => bucket.Slips).Single().RichHtml
                ?.Contains("producer-metadata", StringComparison.Ordinal) == true,
            "An archive export snapshot should preserve Replay's source HTML.");
        AssertEqual<List<ZetlClipboardFormatData>?>(
            null,
            clean.Buckets.SelectMany(bucket => bucket.Slips).Single().ReplayFormats,
            "A clean export snapshot should remove native Replay formats.");
        AssertTrue(
            archive.Buckets.SelectMany(bucket => bucket.Slips).Single().ReplayFormats
                ?.Any(item => item.RegisteredName == "Star Embed Source (XML)") == true,
            "An archive export snapshot should preserve native Replay formats.");
        AssertTrue(note.CaptureOrigin is not null, "Sanitizing an export snapshot must not modify the live project.");
    }

    [Fact(DisplayName = "Zetl project packages separate clean and archive provenance")]
    public static void ProjectPackagesSeparateProvenance()
    {
        using var temp = new TempStateFile();
        var root = System.IO.Path.GetDirectoryName(temp.Path)!;
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Share Me", ["Inbox"], "Inbox");
        var liveNote = store.AddSlip(
            project.Buckets[0],
            "captured text",
            "copy",
            ZetlCaptureOrigin.Create(
                "Browser",
                "browser",
                "Private account title",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle));
        var cleanPath = System.IO.Path.Combine(root, "clean.zetl.zip");
        var archivePath = System.IO.Path.Combine(root, "archive.zetl.zip");
        var exportedAt = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        ZetlProjectExportPackage.Write(
            cleanPath,
            project,
            includeCaptureOrigins: false,
            exportedAtUtc: exportedAt);
        ZetlProjectExportPackage.Write(
            archivePath,
            project,
            includeCaptureOrigins: true,
            exportedAtUtc: exportedAt);

        (ZetlProjectExportManifest Manifest, ZetlProject Project) ReadPackage(string path)
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            AssertEqual(2, archive.Entries.Count, "A text-only package should contain only its manifest and project snapshot.");
            var manifestEntry = archive.GetEntry(ZetlProjectExportPackage.ManifestEntryName)
                ?? throw new InvalidOperationException("Missing export manifest.");
            var projectEntry = archive.GetEntry(ZetlProjectExportPackage.ProjectEntryName)
                ?? throw new InvalidOperationException("Missing project snapshot.");
            using var manifestStream = manifestEntry.Open();
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ZetlProjectExportManifest>(
                manifestStream,
                JsonFile.Options)
                ?? throw new InvalidOperationException("Invalid export manifest.");
            using var projectStream = projectEntry.Open();
            var packagedProject = System.Text.Json.JsonSerializer.Deserialize<ZetlProject>(
                projectStream,
                JsonFile.Options)
                ?? throw new InvalidOperationException("Invalid project snapshot.");
            return (manifest, packagedProject);
        }

        var clean = ReadPackage(cleanPath);
        var archiveCopy = ReadPackage(archivePath);
        AssertEqual("zetl-project", clean.Manifest.Format, "The package manifest should identify the format.");
        AssertEqual(exportedAt, clean.Manifest.ExportedAtUtc, "The manifest should record export time.");
        AssertFalse(clean.Manifest.CaptureOriginsIncluded, "Clean package manifest should declare stripped provenance.");
        AssertEqual<ZetlCaptureOrigin?>(
            null,
            clean.Project.Buckets.SelectMany(bucket => bucket.Slips).Single().CaptureOrigin,
            "Clean package should not contain capture provenance.");
        AssertTrue(archiveCopy.Manifest.CaptureOriginsIncluded, "Archive package manifest should declare retained provenance.");
        AssertEqual(
            "Private account title",
            archiveCopy.Project.Buckets.SelectMany(bucket => bucket.Slips).Single().CaptureOrigin?.WindowTitle,
            "Archive package should retain capture provenance.");
        AssertTrue(liveNote.CaptureOrigin is not null, "Writing either package must leave live state untouched.");
        AssertEqual(
            0,
            Directory.GetFiles(root, "*.tmp").Length,
            "Successful package writes should not leave temporary files behind.");
    }

    [Fact(DisplayName = "Zetl image slips store deduplicated project assets")]
    public static void ImageSlipsStoreDeduplicatedAssets()
    {
        using var temp = new TempStateFile();
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Images", ["Inbox"], "Inbox");
        var bucket = project.Buckets.Single(item => item.Name == "Inbox");
        var image = new ZetlClipboardImage([1, 2, 3, 4, 5], 20, 10);

        var first = store.AddImageSlip(
            project,
            bucket,
            image,
            "copy",
            ZetlCaptureOrigin.Create(
                "Snipping Tool",
                "SnippingTool",
                "Screenshot",
                ZetlCaptureOriginDetail.ApplicationAndWindowTitle),
            "Diagram");
        var second = store.AddImageSlip(project, bucket, image, "copy");

        AssertTrue(first.IsImage, "An image slip should identify its typed content.");
        AssertEqual("Diagram", first.Text, "An image caption should remain separate from its asset bytes.");
        AssertEqual(first.Image?.RelativePath, second.Image?.RelativePath, "Equal image content should deduplicate by hash.");
        AssertEqual(1, store.GetProjectAssets(project).Count, "Deduplicated image content should create one asset file.");
        AssertEqual(5, store.ReadImageAsset(project, first)?.Length, "Stored image bytes should be readable through the project store.");
        var snapshot = ZetlProjectSnapshotMapper.ToSnapshot(bucket, first);
        AssertEqual(
            ZETL.Contracts.ZetlSlipType.Picture,
            snapshot.Type,
            "Kastn snapshots should retain the image slip type.");
        AssertEqual("Diagram", snapshot.Text, "The image caption should be available to Kastn.");
        AssertEqual(20, snapshot.Picture?.Width, "Kastn snapshots should expose image dimensions.");
        AssertEqual(
            "Screenshot",
            snapshot.CaptureOrigin?.WindowTitle,
            "Kastn snapshots should expose private capture provenance locally.");

        store.DeleteSlip(bucket, first.Id);
        AssertEqual(1, store.GetProjectAssets(project).Count, "Deleting a slip should retain its asset for undo safety.");

        var reloaded = new ZetlStateStore(temp.Path);
        var loadedProject = reloaded.State.Projects.Single(item => item.Name == "Images");
        var loadedImage = loadedProject.Buckets
            .Single(item => item.Name == "Inbox")
            .Slips.First();
        AssertTrue(loadedImage.IsImage, "Image slip type should survive persistence.");
        AssertEqual(20, loadedImage.Image?.Width, "Image dimensions should survive persistence.");
    }

    [Fact(DisplayName = "Zetl image project packages include referenced assets")]
    public static void ImageProjectPackagesIncludeAssets()
    {
        using var temp = new TempStateFile();
        var root = System.IO.Path.GetDirectoryName(temp.Path)!;
        var store = new ZetlStateStore(temp.Path);
        var project = store.CreateProject("Images", ["Inbox"], "Inbox");
        var bytes = new byte[] { 9, 8, 7, 6 };
        var note = store.AddImageSlip(
            project,
            project.Buckets.Single(item => item.Name == "Inbox"),
            new ZetlClipboardImage(bytes, 2, 2),
            "copy",
            sourceUrl: "https://private.example/image.png?token=secret");
        var exportPath = System.IO.Path.Combine(root, "images.zetl.zip");

        ZetlProjectExportPackage.Write(
            exportPath,
            project,
            includeCaptureOrigins: false,
            assets: store.GetProjectAssets(project));

        using var archive = System.IO.Compression.ZipFile.OpenRead(exportPath);
        AssertEqual(3, archive.Entries.Count, "An image package should include manifest, project, and asset entries.");
        var assetEntry = archive.GetEntry(note.Image!.RelativePath)
            ?? throw new InvalidOperationException("Missing packaged image asset.");
        using var assetStream = assetEntry.Open();
        using var copied = new MemoryStream();
        assetStream.CopyTo(copied);
        AssertEqual(bytes.Length, copied.ToArray().Length, "The package should preserve normalized image bytes.");

        var manifestEntry = archive.GetEntry(ZetlProjectExportPackage.ManifestEntryName)!;
        using var manifestStream = manifestEntry.Open();
        var manifest = System.Text.Json.JsonSerializer.Deserialize<ZetlProjectExportManifest>(
            manifestStream,
            JsonFile.Options)!;
        AssertEqual(1, manifest.AssetCount, "The package manifest should report included assets.");
        AssertFalse(manifest.SourceUrlsIncluded, "A clean package should declare stripped image source URLs.");

        var projectEntry = archive.GetEntry(ZetlProjectExportPackage.ProjectEntryName)!;
        using var projectStream = projectEntry.Open();
        var packagedProject = System.Text.Json.JsonSerializer.Deserialize<ZetlProject>(
            projectStream,
            JsonFile.Options)!;
        AssertEqual<string?>(
            null,
            packagedProject.Buckets.SelectMany(bucket => bucket.Slips).Single().Image?.SourceUrl,
            "A clean package should strip private image source URLs.");
        AssertTrue(note.Image?.SourceUrl is not null, "Clean export must not modify the live image source URL.");
    }
}
