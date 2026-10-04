using System.Text.Json;
using System.Diagnostics;
using ZETL;
using ZETL.Contracts;

using Xunit;
using Xunit.Abstractions;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ZETL.Tests;

// Opt-in diagnostics for UI/layout work and the separate durable IPC pipeline.
// Timing is reported rather than asserted; ordinary test runs skip both probes.
public class ZetlActionLatencyProbe(ITestOutputHelper output)
{
    [KastnRenderProbeFact]
    public void MeasureUiApplyPass()
    {
        var size = int.TryParse(Environment.GetEnvironmentVariable("ZETL_RENDER_PROBE_SIZE"), out var count) ? count : 1000;
        var buckets = int.TryParse(Environment.GetEnvironmentVariable("ZETL_RENDER_PROBE_BUCKETS"), out var bucketCount) ? bucketCount : 10;
        Assert.InRange(size, 1, 100_000);
        Assert.InRange(buckets, 1, 10_000);
        var directory = Path.Combine(Path.GetTempPath(), "KastnRenderProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = new KASTN.KastnConnectionController(_ => Task.CompletedTask);
        var window = new KASTN.MainWindow(connection,
            new KASTN.KastnDraftStore(Path.Combine(directory, "draft.json")), new ZetlViewStore(Path.Combine(directory, "views")),
            new KASTN.KastnSettings(Path.Combine(directory, "settings.json")), new ZetlTemplateStore(Path.Combine(directory, "templates")),
            new ZetlCreationTypeStore(Path.Combine(directory, "creations")), new KASTN.KastnStateStore(Path.Combine(directory, "state.json")));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(KASTN.MainWindow).GetMethod("ApplySnapshot", flags)!;
        void Time(string label, Action action, int rounds = 1)
        {
            var times = new List<double>();
            var bytes = new List<long>();
            for (var i = 0; i < rounds; i++)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                action();
                window.UpdateLayout();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                times.Add(watch.Elapsed.TotalMilliseconds);
                bytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            }
            output.WriteLine($"{label}: median {times.Order().ElementAt(times.Count / 2):F1} ms; allocated {bytes.Average() / 1024:F0} KiB; samples {string.Join(", ", times.Select(ms => ms.ToString("F1")))}");
        }
        void Apply(ZetlProjectSnapshot project) => apply.Invoke(window, [new KASTN.KastnSessionSnapshot(
            KASTN.KastnConnectionState.Online, "Connected", [new() { Id = project.Id, Name = project.Name,
                MetadataRevision = project.MetadataRevision, ChangeSequence = project.ChangeSequence }], project)]);
        var current = new ZetlProjectSnapshot
        {
            Id = "render-probe", Name = "Render probe", MetadataRevision = 1, ChangeSequence = 1,
            Buckets = Enumerable.Range(0, buckets).Select(i => new ZetlBucketSnapshot { Id = $"b-{i}", Name = $"Bucket {i}", Revision = 1 }).ToArray(),
            Slips = Enumerable.Range(0, size).Select(i => new ZetlSlipSnapshot
            {
                Id = $"s-{i}", Revision = 1, BucketId = $"b-{i % buckets}", Type = ZetlSlipType.Text, Source = "probe",
                CapturedAtUtc = DateTimeOffset.Parse("2026-10-04T00:00:00Z"),
                Text = $"{(i % 2 == 0 ? "even" : "odd")} note {i}\n**Emphasis** and [[s-0|reference]]\nA third line with more text."
            }).ToArray()
        };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            output.WriteLine($"HEADLESS layout probe: {size} slips, {buckets} buckets; no pixel/GPU timing.");
            Time("Initial project", () => Apply(current));
            Assert.Empty(window.slipInspectorFieldsPanel.Children);
            Time("Identical snapshot", () => Apply(current), 5);
            Time("One slip edited", () =>
            {
                current = current with { ChangeSequence = current.ChangeSequence + 1, Slips = current.Slips.Select(slip => slip.Id == "s-0"
                    ? slip with { Revision = slip.Revision + 1, Text = slip.Text + "!" } : slip).ToArray() };
                Apply(current);
            }, 5);
            foreach (var method in new[] { "PopulateProjectCards", "RefreshFilterChoices", "RefreshBuckets", "UpdateEditorFromState", "RefreshSlipView" })
            {
                object?[] args = method switch { "RefreshFilterChoices" => [current], "RefreshBuckets" => [current, null], "RefreshSlipView" => [true], _ => [] };
                Time(method, () => typeof(KASTN.MainWindow).GetMethod(method, flags)!.Invoke(window, args), 3);
            }
            var rowsBefore = window.projectTree.GetVisualDescendants().OfType<TreeViewItem>()
                .Where(row => row.DataContext is KASTN.KastnTreeNode).ToDictionary(row => ((KASTN.KastnTreeNode)row.DataContext!).Id);
            current = current with { ChangeSequence = current.ChangeSequence + 1, Slips = current.Slips.Reverse().ToArray() };
            typeof(KASTN.MainWindow).GetField("currentProject", flags)!.SetValue(window, current);
            typeof(KASTN.MainWindow).GetField("projectIndex", flags)!.SetValue(window, new KASTN.KastnProjectIndex(current));
            Time("Reverse tree only", () => typeof(KASTN.MainWindow).GetMethod("RefreshBuckets", flags)!.Invoke(window, [current, null]));
            Time("Reverse reader only", () => typeof(KASTN.MainWindow).GetMethod("RefreshSlipView", flags)!.Invoke(window, [true]));
            Time("Reverse slip order", () => { current = current with { ChangeSequence = current.ChangeSequence + 1, Slips = current.Slips.Reverse().ToArray() }; Apply(current); }, 3);
            var rowsAfter = window.projectTree.GetVisualDescendants().OfType<TreeViewItem>()
                .Where(row => row.DataContext is KASTN.KastnTreeNode).ToDictionary(row => ((KASTN.KastnTreeNode)row.DataContext!).Id);
            Assert.Equal(size + buckets, rowsAfter.Count);
            output.WriteLine($"Tree containers reused after reorder: {rowsAfter.Count(pair => rowsBefore.GetValueOrDefault(pair.Key) == pair.Value)} / {rowsAfter.Count}");
            Time("Filter half / clear", () => window.searchBox.Text = window.searchBox.Text == "" ? "even" : "", 4);
            window.searchBox.Text = "";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var view = ZetlViewDefaults.CreateAll()[0];
            Time("Export text", () => _ = ZetlViewRenderer.Render(current, current.Slips, view), 3);
            Time("Board first render", () => window.viewModeBoardButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)));
            Time("Board one slip edited", () =>
            {
                current = current with { ChangeSequence = current.ChangeSequence + 1, Slips = current.Slips.Select(slip => slip.Id == "s-0"
                    ? slip with { Revision = slip.Revision + 1, Text = slip.Text + "!" } : slip).ToArray() };
                Apply(current);
            }, 3);
            Time("Details first render", () => window.detailDetailsButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
            output.WriteLine($"Visible Details controls: {window.slipInspectorFieldsPanel.Children.Count}");
            Time("Visible Details one slip edited", () =>
            {
                current = current with { ChangeSequence = current.ChangeSequence + 1, Slips = current.Slips.Select(slip => slip.Id == "s-0"
                    ? slip with { Revision = slip.Revision + 1, Text = slip.Text + "!" } : slip).ToArray() };
                Apply(current);
            }, 3);
        }
        finally
        {
            window.CloseForShutdown();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Skip = "Diagnostic latency probe — run manually when hunting per-action cost.")]
    public async Task MeasureActionPipeline()
    {
            var directory = Path.Combine(
                Path.GetTempPath(), "ZetlLatencyProbe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipeName = $"zetl-latency-{Guid.NewGuid():N}";
            var store = new ZetlStateStore(
                Path.Combine(directory, "state.json"), "latency-session");
            var project = store.CreateProject("Latency Project", ["Inbox"], "Inbox");
            var bucket = project.Buckets.Single(item => item.Name == "Inbox");
            for (var i = 0; i < 300; i++)
            {
                store.AddSlip(
                    bucket,
                    $"note {i}\nsecond line of note {i}\nthird line with some more text {i}",
                    "copy");
            }

            var service = new ZetlProjectService(store);
            using var server = new ZetlIpcServer(service, pipeName);
            server.Start();
            await using var client = new ZetlIpcClient("latency-probe", pipeName);
            await client.ConnectAsync(TimeSpan.FromSeconds(5));

            var open = await client.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "probe-open",
                Kind = ZetlCommandKind.GetProject,
                ProjectId = project.Id
            });
            var snapshot = open.Payload!.Value.Deserialize<ZetlProjectSnapshot>(ZetlProtocolJson.Options)!;
            var slip = snapshot.Slips[^1];

            async Task TimeAsync(string label, Func<Task> action, int rounds = 5)
            {
                var watch = new Stopwatch();
                var samples = new List<double>();
                for (var i = 0; i < rounds; i++)
                {
                    watch.Restart();
                    await action();
                    watch.Stop();
                    samples.Add(watch.Elapsed.TotalMilliseconds);
                }

                output.WriteLine(
                    $"{label}: avg {samples.Average():F1} ms | samples {string.Join(", ", samples.Select(sample => sample.ToString("F1")))}");
            }

            var revision = slip.Revision;
            await TimeAsync("UpdateSlip (durable write)", async () =>
            {
                var response = await client.ExecuteAsync(ZetlCommandEnvelope.Create(
                    Guid.NewGuid().ToString("N"),
                    ZetlCommandKind.UpdateSlip,
                    new UpdateSlipCommand { Text = $"edited {Guid.NewGuid():N}" },
                    project.Id,
                    slip.Id,
                    revision));
                revision = response.Payload!.Value
                    .Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)!.Revision;
            });

            await TimeAsync("ListProjects", () => client.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Kind = ZetlCommandKind.ListProjects
            }));

            await TimeAsync("GetProject (full snapshot)", () => client.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Kind = ZetlCommandKind.GetProject,
                ProjectId = project.Id
            }));

            Directory.Delete(directory, recursive: true);
    }
}

[Xunit.Sdk.XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
public sealed class KastnRenderProbeFactAttribute : FactAttribute
{
    public KastnRenderProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ZETL_RENDER_PROBE") != "1")
            Skip = "Diagnostic layout probe — set ZETL_RENDER_PROBE=1 to run manually.";
    }
}
