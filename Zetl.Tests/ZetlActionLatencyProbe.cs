using System.Text.Json;
using System.Diagnostics;
using ZETL;
using ZETL.Contracts;

using Xunit;
using Xunit.Abstractions;

namespace ZETL.Tests;

// Temporary diagnostic probe: times the pieces of one Kastn "action" (a mutation
// command, ListProjects, GetProject) over the real IPC pair, at a realistic
// project size, to locate the per-action latency the UI feels.
public class ZetlActionLatencyProbe(ITestOutputHelper output)
{
    [Avalonia.Headless.XUnit.AvaloniaFact(Skip = "Diagnostic latency probe — run manually when hunting per-action cost.")]
    public void MeasureUiApplyPass()
    {
        var connection = new KASTN.KastnConnectionController(_ => Task.CompletedTask);
        var window = new KASTN.MainWindow(connection);
        window.Show();

        ZetlProjectSnapshot Project(long sequence, long editedRevision) => new()
        {
            Id = "proj-1",
            Name = "Latency Project",
            MetadataRevision = 1,
            ChangeSequence = sequence,
            Buckets = new[]
            {
                new ZetlBucketSnapshot { Id = "b-1", Revision = 1, Name = "Inbox" }
            },
            Slips = Enumerable.Range(0, 300).Select(i => new ZetlSlipSnapshot
            {
                Id = $"s{i}",
                Revision = i == 0 ? editedRevision : 1,
                Type = ZetlSlipType.Text,
                BucketId = "b-1",
                Text = $"note {i}\nsecond line of note {i}\nthird line with some more text {i}",
                Source = "copy",
                CapturedAtUtc = DateTimeOffset.UtcNow
            }).ToArray()
        };

        var applyMethod = typeof(KASTN.MainWindow).GetMethod(
            "ApplySnapshot",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        void Apply(long sequence, long editedRevision, string label)
        {
            var project = Project(sequence, editedRevision);
            var snapshot = new KASTN.KastnSessionSnapshot(
                KASTN.KastnConnectionState.Online,
                "Connected",
                new[] { new ZetlProjectSummary { Id = "proj-1", Name = "Latency Project", MetadataRevision = 1, ChangeSequence = sequence } },
                project);
            var watch = Stopwatch.StartNew();
            applyMethod.Invoke(window, [snapshot]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            watch.Stop();
            output.WriteLine($"{label}: {watch.Elapsed.TotalMilliseconds:F1} ms");
        }

        Apply(1, 1, "ApplySnapshot #1 (project open)");
        Apply(2, 2, "ApplySnapshot #2 (one slip edited)");
        Apply(2, 2, "ApplySnapshot #3 (identical snapshot)");
        Apply(3, 3, "ApplySnapshot #4 (one slip edited)");
        Apply(3, 3, "ApplySnapshot #5 (identical snapshot)");

        // Break the pass down: time each piece ApplySnapshot runs, in isolation.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void TimePiece(string method, params object?[] args)
        {
            var target = typeof(KASTN.MainWindow).GetMethod(method, flags)!;
            var watch = Stopwatch.StartNew();
            target.Invoke(window, args);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            watch.Stop();
            output.WriteLine($"  {method}: {watch.Elapsed.TotalMilliseconds:F1} ms");
        }

        var current = Project(3, 3);
        TimePiece("PopulateProjectCards");
        TimePiece("RefreshFilterChoices", current);
        TimePiece("RefreshBuckets", current, null);
        TimePiece("UpdateEditorFromState");
        TimePiece("RefreshSlipView", true);

        // The changed-signature path's two candidates, separately.
        var view = ZetlViewDefaults.CreateAll()[0];
        var watch = Stopwatch.StartNew();
        _ = ZetlViewRenderer.Render(current, current.Slips, view, preferSlipKindOverBucketKind: false);
        watch.Stop();
        output.WriteLine($"  ZetlViewRenderer.Render (copy text): {watch.Elapsed.TotalMilliseconds:F1} ms");

        var key = KASTN.KastnViewRenderKey.Create(current, current.Slips, view, new ZetlAppSettings());
        var capture = typeof(KASTN.MainWindow).GetMethod("CaptureReaderInputs", flags)!;
        var inputs = (KASTN.KastnReaderRenderInputs)capture.Invoke(window, [current.Slips, key])!;
        var reader = (KASTN.KastnReaderPresenter)typeof(KASTN.MainWindow).GetField("readerPresenter", flags)!.GetValue(window)!;
        watch.Restart();
        reader.Render(inputs, selectedSlipId: null, force: true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        watch.Stop();
        output.WriteLine($"  Reader render (warm block cache): {watch.Elapsed.TotalMilliseconds:F1} ms");

        window.Close();
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
