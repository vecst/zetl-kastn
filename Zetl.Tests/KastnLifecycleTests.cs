using System.Collections.Concurrent;
using System.Text.Json;
using KASTN;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

[Collection(RealTimeCollection.Name)]
public class KastnLifecycleTests
{
    [Fact] public void ControllerRejectsRefreshCapturedBeforeLaterNavigation()
    {
        RunAsync(async () =>
        {
            using var release = new ManualResetEventSlim();
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? delayedId = null;
            using var fixture = new LifecycleFixture(dropResponseForTesting: command =>
            {
                if (IsProjectRead(command) && command.ProjectId == delayedId
                    && arrived.TrySetResult()) release.Wait(TimeSpan.FromSeconds(8));
                return false;
            });
            var second = fixture.Store.CreateProject("Second", ["Inbox"], "Inbox");
            var third = fixture.Store.CreateProject("Third", ["Inbox"], "Inbox");
            await using var controller = CreateConnectedController(fixture);
            var initial = await WaitForSnapshotAsync(controller, snapshot => snapshot.ConnectionState == KastnConnectionState.Online);
            var published = new ConcurrentQueue<KastnSessionSnapshot>();
            controller.SnapshotChanged += (_, snapshot) => published.Enqueue(snapshot);
            delayedId = second.Id;
            var older = controller.NavigateToProjectAsync(second.Id);
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var newer = controller.NavigateToProjectAsync(third.Id);
                release.Set();
                await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(8));
                await controller.RefreshAsync();
                Assert.Equal(third.Id, controller.Current.Project?.Id);
                Assert.DoesNotContain(published, snapshot => snapshot.Project?.Id == second.Id);
                Assert.All(published, snapshot => Assert.True(snapshot.PublicationVersion > initial.PublicationVersion));
                Assert.Equal(2, controller.Current.NavigationVersion);
            }
            finally { release.Set(); }
        });
    }

    [Fact] public void RefreshPumpCollapsesBurstIntoOneDirtyRerun()
    {
        RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var firstStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var refreshCount = 0;
            await using var pump = new KastnRefreshPump(
                async token =>
                {
                    var count = Interlocked.Increment(ref refreshCount);
                    if (count == 1)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(token);
                    }
                },
                exception => throw new InvalidOperationException(
                    "The refresh pump should not report a failure.", exception),
                cancellation.Token);

            pump.Request();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var request = 0; request < 100; request++)
            {
                pump.Request();
            }

            releaseFirst.TrySetResult();
            await pump.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertEqual(
                2,
                Volatile.Read(ref refreshCount),
                "A burst during one refresh should produce exactly one dirty rerun.");
        });
    }

    [Fact] public void RefreshPumpReportsFailureAndReturnsToIdle()
    {
        RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var reported = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var pump = new KastnRefreshPump(
                _ => Task.FromException(new IOException("refresh disconnected")),
                exception => reported.TrySetResult(exception),
                cancellation.Token);

            pump.Request();
            var failure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pump.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            AssertEqual(
                "refresh disconnected",
                failure.Message,
                "The owned pump should surface background refresh failures.");
        });
    }

    [Fact] public void ActivationHandoffCarriesProjectId()
    {
        RunAsync(async () =>
        {
            var pipeName = $"kastn-control-tests-{Guid.NewGuid():N}";
            await using var server = new KastnControlServer(pipeName);
            var received = new TaskCompletionSource<KastnControlRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ActivationRequested += (_, request) => received.TrySetResult(request);
            server.Start();

            await KastnControlChannel.ActivateAsync(
                "project-to-focus",
                pipeName,
                TimeSpan.FromSeconds(5));
            var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

            AssertEqual(
                "project-to-focus",
                request.ProjectId,
                "Activation should preserve the requested project ID.");
        });
    }

    [Fact] public void ActivationBoundaryReportsNavigationDisconnectWithoutEscaping()
    {
        RunAsync(async () =>
        {
            Exception? reported = null;

            await App.ObserveActivationAsync(
                () => Task.FromException(new IOException("navigation disconnected")),
                exception => reported = exception);

            AssertTrue(
                reported is IOException,
                "The UI activation boundary should observe a navigation disconnect.");
            AssertEqual(
                "navigation disconnected",
                reported!.Message,
                "The activation failure should remain available for visible status reporting.");
        });
    }

    [Fact] public void ShutdownRequestClosesWhenHandlerAgrees()
    {
        RunAsync(async () =>
        {
            var pipeName = $"kastn-control-tests-{Guid.NewGuid():N}";
            await using var server = new KastnControlServer(pipeName);
            var confirmed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ShutdownRequested = () => Task.FromResult(true);
            server.ShutdownConfirmed = () => confirmed.TrySetResult();
            server.Start();

            var decision = await KastnControlChannel.RequestShutdownAsync(
                pipeName,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            AssertEqual(
                KastnShutdownDecision.Close,
                decision,
                "An agreeing Kastn should report a Close decision.");
            await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    [Fact] public void ShutdownRequestCancelsWhenHandlerDeclines()
    {
        RunAsync(async () =>
        {
            var pipeName = $"kastn-control-tests-{Guid.NewGuid():N}";
            await using var server = new KastnControlServer(pipeName);
            var confirmedClose = false;
            server.ShutdownRequested = () => Task.FromResult(false);
            server.ShutdownConfirmed = () => confirmedClose = true;
            server.Start();

            var decision = await KastnControlChannel.RequestShutdownAsync(
                pipeName,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            AssertEqual(
                KastnShutdownDecision.Cancel,
                decision,
                "A declining Kastn should report a Cancel decision.");
            AssertTrue(!confirmedClose, "A cancelled shutdown must not confirm a close.");
        });
    }

    [Fact] public void ShutdownRequestReturnsNoKastnWhenAbsent()
    {
        RunAsync(async () =>
        {
            var decision = await KastnControlChannel.RequestShutdownAsync(
                $"kastn-control-absent-{Guid.NewGuid():N}",
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2));

            AssertEqual(
                KastnShutdownDecision.NoKastn,
                decision,
                "With no Kastn listening the requester should be free to quit.");
        });
    }

    [Fact] public void BeginShutdownStopsZetlRelaunch()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            var launches = 0;
            await using var controller = new KastnConnectionController(
                _ =>
                {
                    Interlocked.Increment(ref launches);
                    fixture.StartServer();
                    return Task.CompletedTask;
                },
                fixture.PipeName,
                TimeSpan.FromMilliseconds(75),
                TimeSpan.FromMilliseconds(25));
            controller.Start(fixture.Project.Id);
            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);

            // Coordinated shutdown: Kastn is closing on purpose, so the dropped
            // connection must not relaunch Zetl the way an unexpected outage would.
            controller.BeginShutdown();
            fixture.StopServer();
            await Task.Delay(300);

            AssertEqual(
                0,
                Volatile.Read(ref launches),
                "A shutting-down Kastn must not relaunch Zetl.");
        });
    }

    [Fact] public void ControllerLaunchesZetlAndLoadsProject()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture(startServer: false);
            var launches = 0;
            await using var controller = new KastnConnectionController(
                _ =>
                {
                    Interlocked.Increment(ref launches);
                    fixture.StartServer();
                    return Task.CompletedTask;
                },
                fixture.PipeName,
                TimeSpan.FromMilliseconds(75),
                TimeSpan.FromMilliseconds(25));
            controller.Start(fixture.Project.Id);

            var online = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online
                    && snapshot.Project?.Id == fixture.Project.Id);

            AssertEqual(1, launches, "Kastn should launch Zetl once when it is absent.");
            AssertEqual(
                fixture.Project.Id,
                online.Project?.Id,
                "Kastn should open the requested project after Zetl starts.");
        });
    }

    [Fact] public void ControllerLaunchesToProjectSelectionWithoutHandoff()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."),
                fixture.PipeName,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(25));
            controller.Start();

            var landing = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online
                    && snapshot.Project is null
                    && snapshot.Projects.Any(project => project.Id == fixture.Project.Id));

            AssertEqual(
                null,
                landing.Project,
                "Kastn should not auto-open the first project without a direct handoff.");

            await controller.NavigateToProjectAsync(fixture.Project.Id);
            var opened = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.Project?.Id == fixture.Project.Id);

            AssertEqual(
                fixture.Project.Id,
                opened.Project?.Id,
                "Selecting a project should open it from the landing state.");
        });
    }

    [Fact] public void NavigationDisconnectStaysAliveAndPublishesAccurateState()
    {
        RunAsync(async () =>
        {
            var getProjectAttempts = 0;
            using var fixture = new LifecycleFixture(
                dropResponseForTesting: command =>
                    IsProjectRead(command)
                    && Interlocked.Increment(ref getProjectAttempts) == 1);
            await using var controller = new KastnConnectionController(
                _ => throw new InvalidOperationException("Zetl was already running."),
                fixture.PipeName,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(25));
            var snapshots = new ConcurrentQueue<KastnSessionSnapshot>();
            controller.SnapshotChanged += (_, snapshot) => snapshots.Enqueue(snapshot);
            controller.Start();
            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online
                    && snapshot.Project is null);

            Exception? reported = null;
            await App.ObserveActivationAsync(
                () => controller.NavigateToProjectAsync(fixture.Project.Id),
                exception => reported = exception);

            var recovered = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online
                    && snapshot.Project?.Id == fixture.Project.Id);
            var outage = snapshots.FirstOrDefault(snapshot =>
                snapshot.ConnectionState == KastnConnectionState.Offline);

            AssertTrue(
                reported is IOException or InvalidOperationException,
                "The activation boundary should observe the navigation disconnect without letting it escape.");
            AssertTrue(outage is not null, "The controller should publish an Offline snapshot for the dropped connection.");
            AssertTrue(
                outage!.Status.Contains("disconnect", StringComparison.OrdinalIgnoreCase)
                || outage.Status.Contains("connection failed", StringComparison.OrdinalIgnoreCase),
                "The Offline snapshot should accurately explain the connection outage.");
            AssertEqual(
                fixture.Project.Id,
                recovered.Project?.Id,
                "Kastn should remain running, reconnect, and finish the requested navigation.");
            AssertTrue(controller.HasLiveConnection, "Recovered navigation should leave Kastn connected.");
            AssertEqual(
                2,
                Volatile.Read(ref getProjectAttempts),
                "The requested project should be retried exactly once after reconnecting.");
        });
    }

    [Fact] public void ControllerRefreshesAfterProjectChange()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            await using var controller = CreateConnectedController(fixture);
            var initial = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);

            fixture.Store.AddSlip(
                fixture.Bucket,
                "captured while Kastn is open",
                "copy");
            var refreshed = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.Project is { } project
                    && project.ChangeSequence > initial.Project!.ChangeSequence
                    && project.Slips.Any(slip => slip.Text == "captured while Kastn is open"));

            AssertTrue(
                refreshed.Project!.Slips.Any(
                    slip => slip.Text == "captured while Kastn is open"),
                "Kastn should refresh its snapshot after a durable Zetl change.");
        });
    }

    [Fact] public void ControllerRebuildsChangedProjectFromDeltas()
    {
        RunAsync(async () =>
        {
            var cursors = new ConcurrentQueue<ZetlProjectCursor?>();
            using var fixture = new LifecycleFixture(dropResponseForTesting: command =>
            {
                if (command.Kind == ZetlCommandKind.GetProjectChanges) cursors.Enqueue(CursorOf(command));
                return false;
            });
            var kept = fixture.AddSlip("kept as it was");
            var edited = fixture.AddSlip("edited later");
            var moved = fixture.AddSlip("moved later");
            var deleted = fixture.AddSlip("deleted later");
            await using var controller = CreateConnectedController(fixture);
            var initial = await WaitForSnapshotAsync(controller, snapshot => snapshot.Project?.Slips.Count == 4);

            var later = fixture.Execute(ZetlCommandEnvelope.Create(
                NewId(), ZetlCommandKind.AddBucket, new AddBucketCommand { Name = "Later" }, fixture.Project.Id))
                .Payload!.Value.Deserialize<ZetlBucketSnapshot>(ZetlProtocolJson.Options)!;
            fixture.Execute(ZetlCommandEnvelope.Create(
                NewId(), ZetlCommandKind.UpdateSlip, new UpdateSlipCommand { Text = "edited" },
                fixture.Project.Id, edited, Slip(initial, edited).Revision));
            fixture.Execute(ZetlCommandEnvelope.Create(
                NewId(), ZetlCommandKind.MoveSlip, new MoveSlipCommand { DestinationBucketId = later.Id },
                fixture.Project.Id, moved, Slip(initial, moved).Revision));
            fixture.Execute(ZetlCommandEnvelope.Create(
                NewId(), ZetlCommandKind.DeleteSlip, new DeleteSlipCommand(),
                fixture.Project.Id, deleted, Slip(initial, deleted).Revision));
            fixture.AddSlip("captured while Kastn is open");
            var full = fixture.ReadFullProjectJson();

            // Coalesced invalidations may take a few reads; the end state must
            // match a direct full read exactly.
            var converged = await WaitForSnapshotAsync(controller, snapshot =>
                snapshot.Project is { } project && JsonSerializer.Serialize(project, ZetlProtocolJson.Options) == full);

            Assert.Same(Slip(initial, kept), Slip(converged, kept));
            Assert.True(cursors.TryDequeue(out var bootstrap) && bootstrap is null, "The first read should bootstrap.");
            Assert.NotEmpty(cursors);
            Assert.All(cursors, cursor => Assert.NotNull(cursor));
        });
    }

    [Fact] public void ControllerResumesFromRetainedCursorAfterReconnect()
    {
        RunAsync(async () =>
        {
            var cursors = new ConcurrentQueue<ZetlProjectCursor?>();
            using var fixture = new LifecycleFixture(dropResponseForTesting: command =>
            {
                if (command.Kind == ZetlCommandKind.GetProjectChanges) cursors.Enqueue(CursorOf(command));
                return false;
            });
            await using var controller = CreateConnectedController(fixture);
            await WaitForSnapshotAsync(controller, snapshot => snapshot.Project is not null);
            var kept = controller.Current.Project!.Buckets[0];

            // The same Zetl process (one change feed) behind a new connection.
            fixture.StopServer();
            await WaitForSnapshotAsync(controller, snapshot => snapshot.ConnectionState == KastnConnectionState.Offline);
            fixture.AddSlip("captured while Kastn was away");
            cursors.Clear();
            fixture.StartServer();
            var resumed = await WaitForSnapshotAsync(controller, snapshot =>
                snapshot.ConnectionState == KastnConnectionState.Online
                && snapshot.Project?.Slips.Any(slip => slip.Text == "captured while Kastn was away") == true);

            Assert.NotEmpty(cursors);
            Assert.All(cursors, cursor => Assert.NotNull(cursor));
            Assert.Same(kept, resumed.Project!.Buckets[0]);
            Assert.Equal(fixture.ReadFullProjectJson(), JsonSerializer.Serialize(resumed.Project, ZetlProtocolJson.Options));
        });
    }

    [Fact] public void ControllerReadsWholeProjectsFromZetlWithoutDeltas()
    {
        RunAsync(async () =>
        {
            var deltaReads = 0;
            var fullReads = 0;
            using var fixture = new LifecycleFixture(
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.GetProjectChanges) Interlocked.Increment(ref deltaReads);
                    if (command.Kind == ZetlCommandKind.GetProject) Interlocked.Increment(ref fullReads);
                    return false;
                },
                serverOptions: new ZetlIpcServerOptions { Capabilities = [] });
            await using var controller = CreateConnectedController(fixture);
            await WaitForSnapshotAsync(controller, snapshot => snapshot.Project is not null);

            fixture.AddSlip("captured from an older Zetl");
            var refreshed = await WaitForSnapshotAsync(controller, snapshot =>
                snapshot.Project?.Slips.Any(slip => slip.Text == "captured from an older Zetl") == true);

            Assert.Equal(0, Volatile.Read(ref deltaReads));
            Assert.True(Volatile.Read(ref fullReads) >= 2, "Each changed refresh should read the whole project.");
            Assert.Equal(fixture.ReadFullProjectJson(), JsonSerializer.Serialize(refreshed.Project, ZetlProtocolJson.Options));
        });
    }

    private static ZetlProjectCursor? CursorOf(ZetlCommandEnvelope command) =>
        command.Payload?.Deserialize<GetProjectChangesCommand>(ZetlProtocolJson.Options)?.Cursor;

    private static ZetlSlipSnapshot Slip(KastnSessionSnapshot snapshot, string slipId) =>
        snapshot.Project!.Slips.Single(slip => slip.Id == slipId);

    private static string NewId() => Guid.NewGuid().ToString("N");

    // Kastn's view after each kind of change must equal a direct full read, and
    // every read after the first must stay a delta (no forced reloads).
    [Fact] public void ControllerFollowsEveryKindOfChangeThroughDeltas()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            var store = fixture.Store;
            var project = fixture.Project;
            var inbox = fixture.Bucket;
            var one = store.AddSlip(inbox, "one", "fixture");
            var two = store.AddSlip(inbox, "two", "fixture");
            await using var controller = CreateConnectedController(fixture);
            await ConvergesAsync(controller, fixture, "the first load");

            async Task Step(string what, Action change)
            {
                change();
                await ConvergesAsync(controller, fixture, what);
            }

            var later = store.AddBucket(project, "Later");
            await Step("a new bucket", () => { });
            await Step("styles", () => store.UpdateSlip(one, "one edited", bold: true, fontSize: 21,
                inlineStyles: [new() { Start = 0, Length = 3, Kind = ZetlInlineStyleKinds.Italic }]));
            await Step("a move between buckets", () => store.MoveSlip(project, one, later));
            await Step("an added slip", () => store.AddSlip(later, "three", "fixture"));
            await Step("a slip reorder", () => store.ReorderSlip(project, one, null));
            await Step("a bucket reorder", () => store.ReorderBucket(project, later, inbox.Id));
            await Step("a bucket heading", () => store.SetBucketHeading(inbox, "center", true, 2));
            await Step("a soft delete", () => AssertEqual(ZetlResponseStatus.Success, fixture.Run(ZetlCommandEnvelope.Create(
                NewId(), ZetlCommandKind.DeleteSlip, new DeleteSlipCommand(), project.Id, two.Id, two.Revision)).Status,
                "The soft delete should succeed."));
            await Step("a restore", () => store.MoveSlip(project, two, inbox));
            await Step("a purge", () => store.DeleteSlip(inbox, two.Id));
            await Step("a nested bucket with a slip", () =>
            {
                var child = store.AddBucket(project, "Child", later.Id);
                store.AddSlip(child, "child note", "fixture");
            });
            await Step("a renamed bucket", () => store.UpdateBucketName(later, "Renamed later"));
            await Step("a renamed project", () => store.UpdateProjectName(project, "Renamed project"));
            await Step("a saved view", () => store.SaveProjectView(project, new ZetlViewDocument { Id = "matrix-view", Name = "Matrix" }));
            await Step("a default view", () => store.SetProjectDefaultView(project, "matrix-view"));
            await Step("a deleted bucket", () => store.DeleteBucket(project, inbox.Id));

            var reads = fixture.ServiceLog.Where(line => line.StartsWith("Project changes read", StringComparison.Ordinal)).ToList();
            Assert.Single(reads, line => line.Contains("reset=", StringComparison.Ordinal));
            Assert.Contains(reads, line => line.Contains("from=", StringComparison.Ordinal));
        });
    }

    // A change feed too small to keep Kastn's cursor forces explicit resets;
    // Kastn must still end up exactly in step.
    [Fact] public void ControllerRecoversFromCapacityResets()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture(changesFeed: new ZetlProjectChangesFeed(versionCapacity: 1, perProjectCapacity: 1, indexUnitCapacity: 4));
            for (var i = 0; i < 6; i++) fixture.Store.AddSlip(fixture.Bucket, $"seed {i}", "fixture");
            await using var controller = CreateConnectedController(fixture);
            await ConvergesAsync(controller, fixture, "the first load");

            fixture.Store.AddSlip(fixture.Bucket, "past capacity", "fixture");
            await ConvergesAsync(controller, fixture, "a change past the feed's capacity");
            Assert.Contains(fixture.ServiceLog, line => line.Contains("reset=capacity_limit", StringComparison.Ordinal));
        });
    }

    // A restarted Zetl has a new change feed: the old cursor's epoch is stale,
    // Zetl answers with an explicit reset, and Kastn reloads.
    [Fact] public void ControllerReloadsAfterZetlRestarts()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            fixture.Store.AddSlip(fixture.Bucket, "before the restart", "fixture");
            await using var controller = CreateConnectedController(fixture);
            await ConvergesAsync(controller, fixture, "the first load");

            fixture.RestartZetl();
            fixture.Store.AddSlip(fixture.Bucket, "after the restart", "fixture");
            await ConvergesAsync(controller, fixture, "the reload after a restart");
            Assert.Contains(fixture.ServiceLog, line => line.Contains("reset=epoch_changed", StringComparison.Ordinal));
        });
    }

    // Leaving a project and coming back must show what changed while away.
    [Fact] public void ControllerShowsChangesMadeWhileAwayFromAProject()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            var other = fixture.Store.CreateProject("Other", ["Inbox"], "Inbox");
            await using var controller = CreateConnectedController(fixture);
            await ConvergesAsync(controller, fixture, "the first project");

            await controller.NavigateToProjectAsync(other.Id);
            await WaitForSnapshotAsync(controller, snapshot => snapshot.Project?.Id == other.Id);
            fixture.Store.AddSlip(fixture.Bucket, "added while away", "fixture");
            await controller.NavigateToProjectAsync(fixture.Project.Id);
            await ConvergesAsync(controller, fixture, "the first project again");
            Assert.Contains(controller.Current.Project!.Slips, slip => slip.Text == "added while away");
        });
    }

    // Deleting the open project lands Kastn on project selection without error.
    [Fact] public void ControllerLetsGoOfADeletedProject()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            var other = fixture.Store.CreateProject("Other", ["Inbox"], "Inbox");
            await using var controller = CreateConnectedController(fixture);
            await ConvergesAsync(controller, fixture, "the first load");

            AssertEqual(ZetlResponseStatus.Success, fixture.Run(ZetlCommandEnvelope.Create(
                NewId(), ZetlCommandKind.DeleteProject, new DeleteProjectCommand(), fixture.Project.Id,
                fixture.Project.Id, fixture.Project.MetadataRevision)).Status,
                "Deleting the project should succeed.");
            var landed = await WaitForSnapshotAsync(controller, snapshot =>
                snapshot.ConnectionState == KastnConnectionState.Online
                && snapshot.Project is null
                && snapshot.Projects.All(project => project.Id != fixture.Project.Id));
            Assert.Contains(landed.Projects, project => project.Id == other.Id);
        });
    }

    private static async Task ConvergesAsync(KastnConnectionController controller, LifecycleFixture fixture, string what)
    {
        var full = fixture.ReadFullProjectJson();
        try
        {
            await WaitForSnapshotAsync(controller, snapshot =>
                snapshot.Project is { } project && JsonSerializer.Serialize(project, ZetlProtocolJson.Options) == full);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Kastn didn't match a full read after {what}.\nKastn: "
                + JsonSerializer.Serialize(controller.Current.Project, ZetlProtocolJson.Options)
                + $"\nZetl:  {full}");
        }
    }

    [Fact] public void ControllerConvergesAfterChangeBurstWithBoundedRefreshes()
    {
        RunAsync(async () =>
        {
            var listRequests = 0;
            using var fixture = new LifecycleFixture(
                dropResponseForTesting: command =>
                {
                    if (command.Kind == ZetlCommandKind.ListProjects)
                    {
                        Interlocked.Increment(ref listRequests);
                    }

                    return false;
                });
            await using var controller = CreateConnectedController(fixture);
            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);
            var baselineRequests = Volatile.Read(ref listRequests);

            for (var index = 0; index < 40; index++)
            {
                fixture.AddSlip($"burst-{index}");
            }

            var refreshed = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.Project?.Slips.Any(
                    slip => slip.Text == "burst-39") == true);
            await Task.Delay(150);
            var burstRequests = Volatile.Read(ref listRequests) - baselineRequests;

            AssertTrue(
                refreshed.Project!.Slips.Any(slip => slip.Text == "burst-39"),
                "The refresh pump should converge on the newest durable snapshot.");
            // Coalescing means far fewer refreshes than invalidations. The writes
            // spread out on a loaded machine, letting a few more refreshes slip in
            // between them (10 was seen), so the bound is half the burst, not a
            // tight count.
            AssertTrue(
                burstRequests < 20,
                $"Forty invalidations should require a bounded refresh count, not one task each (actual {burstRequests}).");
        });
    }

    [Fact] public void ControllerDefersBatchProjectionUntilFinalSynchronization()
    {
        RunAsync(async () =>
        {
            var listRequests = 0;
            using var fixture = new LifecycleFixture(dropResponseForTesting: command =>
            {
                if (command.Kind == ZetlCommandKind.ListProjects)
                {
                    Interlocked.Increment(ref listRequests);
                }
                return false;
            });
            await using var controller = CreateConnectedController(fixture);
            var initial = await WaitForSnapshotAsync(controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);
            var baseline = Volatile.Read(ref listRequests);
            await using (controller.DeferRefresh())
            {
                for (var i = 0; i < 12; i++)
                {
                    var response = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                        Guid.NewGuid().ToString("N"), ZetlCommandKind.AddSlip,
                        new AddSlipCommand { BucketId = fixture.Bucket.Id, Text = $"batch-{i}", Source = "kastn" },
                        fixture.Project.Id));
                    AssertEqual(ZetlResponseStatus.Success, response.Status, "Batch mutation should succeed.");
                }
                AssertEqual(baseline, Volatile.Read(ref listRequests), "Commands and events must defer batch projection requests.");
                AssertEqual(initial.Project!.ChangeSequence, controller.Current.Project!.ChangeSequence,
                    "Intermediate batch snapshots should not replace the starting projection.");
                await controller.SynchronizeAsync();
                AssertEqual(12, controller.Current.Project!.Slips.Count(slip => slip.Text.StartsWith("batch-")),
                    "The final synchronization must include the entire batch.");
            }
            AssertTrue(Volatile.Read(ref listRequests) - baseline <= 2,
                "A batch should need one final query, with at most a late-event follow-up.");
        });
    }

    [Fact] public void ControllerCleanSynchronizationRepublishesWithoutQuerying()
    {
        RunAsync(async () =>
        {
            var listRequests = 0;
            using var fixture = new LifecycleFixture(dropResponseForTesting: command =>
            {
                if (command.Kind == ZetlCommandKind.ListProjects)
                {
                    Interlocked.Increment(ref listRequests);
                }
                return false;
            });
            await using var controller = CreateConnectedController(fixture);
            await WaitForSnapshotAsync(controller, snapshot => snapshot.ConnectionState == KastnConnectionState.Online);
            var baseline = Volatile.Read(ref listRequests);
            var publications = 0;
            controller.SnapshotChanged += (_, _) => publications++;
            await controller.SynchronizeAsync();
            AssertEqual(baseline, Volatile.Read(ref listRequests), "A clean synchronization should not query projects again.");
            AssertEqual(1, publications, "Selection restoration still needs the current snapshot published.");
        });
    }

    [Fact] public void ControllerRetriesUnknownMutationWithSameCommandId()
    {
        RunAsync(async () =>
        {
            const string commandId = "retry-unknown-mutation";
            var dropped = 0;
            using var fixture = new LifecycleFixture(
                dropResponseForTesting: command =>
                    command.CommandId == commandId
                    && Interlocked.Exchange(ref dropped, 1) == 0);
            await using var controller = CreateConnectedController(fixture);
            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);

            var response = await controller.ExecuteAsync(ZetlCommandEnvelope.Create(
                commandId,
                ZetlCommandKind.AddSlip,
                new AddSlipCommand
                {
                    BucketId = fixture.Bucket.Id,
                    Text = "deduplicated retry",
                    Source = "kastn"
                },
                fixture.Project.Id));

            AssertEqual(ZetlResponseStatus.Success, response.Status, "The same-server retry should recover the cached result.");
            AssertEqual(
                1,
                fixture.Bucket.Slips.Count(note => note.Text == "deduplicated retry"),
                "An unknown mutation retry must not execute the command twice.");
        });
    }

    [Fact] public void ControllerReconnectsAfterZetlRestart()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            await using var controller = CreateConnectedController(fixture);
            var first = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);
            var states = new ConcurrentQueue<KastnConnectionState>();
            controller.SnapshotChanged += (_, snapshot) => states.Enqueue(
                snapshot.ConnectionState);

            fixture.StopServer();
            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Offline);
            fixture.AddSlip("during restart");
            fixture.StartServer();

            var reconnected = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online
                    && snapshot.Project is { } project
                    && project.ChangeSequence > first.Project!.ChangeSequence);

            AssertTrue(states.Contains(KastnConnectionState.Offline), "Kastn should report the outage.");
            AssertTrue(
                reconnected.Project!.Slips.Any(slip => slip.Text == "during restart"),
                "Kastn should request a fresh snapshot after reconnecting.");
        });
    }

    [Fact] public void ControllerRelaunchesZetlAfterItExits()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            var launches = 0;
            await using var controller = new KastnConnectionController(
                _ =>
                {
                    Interlocked.Increment(ref launches);
                    fixture.StartServer();
                    return Task.CompletedTask;
                },
                fixture.PipeName,
                TimeSpan.FromMilliseconds(75),
                TimeSpan.FromMilliseconds(25));

            // Record every connection state before the outage. The relaunch is
            // fast, so the Offline blip is fleeting; observing it through a fresh
            // "wait for Offline" would race the immediate reconnect. Recording all
            // transitions and asserting Offline appeared is race-free, and matches
            // ControllerReconnectsAfterZetlRestart.
            var states = new ConcurrentQueue<KastnConnectionState>();
            controller.SnapshotChanged += (_, snapshot) => states.Enqueue(
                snapshot.ConnectionState);
            controller.Start(fixture.Project.Id);

            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);

            fixture.StopServer();
            var relaunched = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online
                    && Volatile.Read(ref launches) == 1
                    && snapshot.Project?.Id == fixture.Project.Id);

            AssertTrue(
                states.Contains(KastnConnectionState.Offline),
                "Kastn should report the outage before relaunching Zetl.");
            AssertEqual(1, launches, "Kastn should launch Zetl again after a later outage.");
            AssertEqual(
                fixture.Project.Id,
                relaunched.Project?.Id,
                "Kastn should reopen the desired project after relaunching Zetl.");
        });
    }

    [Fact] public void ClosingControllerLeavesZetlAvailable()
    {
        RunAsync(async () =>
        {
            using var fixture = new LifecycleFixture();
            var controller = CreateConnectedController(fixture);
            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online);
            await controller.DisposeAsync();

            await using var client = new ZetlIpcClient(
                "after-kastn-close",
                fixture.PipeName,
                subscribeToProjectChanges: false);
            await client.ConnectAsync(TimeSpan.FromSeconds(5));
            var response = await client.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "after-kastn-close",
                Kind = ZetlCommandKind.ListProjects
            });

            AssertEqual(
                ZetlResponseStatus.Success,
                response.Status,
                "Closing Kastn must leave Zetl and its IPC host running.");
        });
    }

    // Kastn reads the open project with GetProjectChanges, or GetProject from
    // a Zetl that does not offer delta reads.
    private static bool IsProjectRead(ZetlCommandEnvelope command) =>
        command.Kind is ZetlCommandKind.GetProject or ZetlCommandKind.GetProjectChanges;

    private static KastnConnectionController CreateConnectedController(
        LifecycleFixture fixture)
    {
        var controller = new KastnConnectionController(
            _ => throw new InvalidOperationException("Zetl was already running."),
            fixture.PipeName,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(25));
        controller.Start(fixture.Project.Id);
        return controller;
    }

    private static async Task<KastnSessionSnapshot> WaitForSnapshotAsync(
        KastnConnectionController controller,
        Func<KastnSessionSnapshot, bool> predicate)
    {
        var completion = new TaskCompletionSource<KastnSessionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<KastnSessionSnapshot>? handler = null;
        handler = (_, snapshot) =>
        {
            if (predicate(snapshot))
            {
                completion.TrySetResult(snapshot);
            }
        };
        controller.SnapshotChanged += handler;
        try
        {
            // Subscribe before checking so a transition cannot fall between the two.
            var current = controller.Current;
            if (predicate(current))
            {
                completion.TrySetResult(current);
            }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            controller.SnapshotChanged -= handler;
        }
    }

    private static void RunAsync(Func<Task> action)
    {
        action().GetAwaiter().GetResult();
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "KastnLifecycleTests",
            Guid.NewGuid().ToString("N"));
        private ZetlProjectService service;
        private ZetlIpcServer? server;

        private readonly Func<ZetlCommandEnvelope, bool>? dropResponseForTesting;
        private readonly ZetlIpcServerOptions? serverOptions;

        public LifecycleFixture(
            bool startServer = true,
            Func<ZetlCommandEnvelope, bool>? dropResponseForTesting = null,
            ZetlIpcServerOptions? serverOptions = null,
            ZetlProjectChangesFeed? changesFeed = null)
        {
            this.dropResponseForTesting = dropResponseForTesting;
            this.serverOptions = serverOptions;
            Directory.CreateDirectory(directory);
            PipeName = $"kastn-lifecycle-{Guid.NewGuid():N}";
            Store = new ZetlStateStore(
                Path.Combine(directory, "state.json"),
                "kastn-lifecycle");
            Project = Store.CreateProject("Lifecycle Project", ["Inbox"], "Inbox");
            Bucket = Project.Buckets.Single(bucket => bucket.Name == "Inbox");
            service = new ZetlProjectService(Store, log: ServiceLog.Enqueue, changesFeed: changesFeed);
            if (startServer)
            {
                StartServer();
            }
        }

        public string PipeName { get; }
        public ZetlStateStore Store { get; }

        // Zetl's diagnostics lines, including each delta read's reset reason.
        public ConcurrentQueue<string> ServiceLog { get; } = new();

        // Zetl quits and starts again on the same data: a new server and a new
        // change feed, so cursors from before carry a stale epoch.
        public void RestartZetl()
        {
            StopServer();
            service = new ZetlProjectService(Store, log: ServiceLog.Enqueue);
            StartServer();
        }

        public ZetlResponseEnvelope Run(ZetlCommandEnvelope command) => service.Execute(command);
        public ZetlProject Project { get; }
        public ZetlBucket Bucket { get; }

        public void StartServer()
        {
            if (server is not null)
            {
                return;
            }

            server = new ZetlIpcServer(
                service,
                PipeName,
                log: null,
                dropResponseForTesting: dropResponseForTesting,
                options: serverOptions);
            server.Start();
        }

        public void StopServer()
        {
            server?.Dispose();
            server = null;
        }

        public string AddSlip(string text)
        {
            var response = service.Execute(ZetlCommandEnvelope.Create(
                Guid.NewGuid().ToString("N"),
                ZetlCommandKind.AddSlip,
                new AddSlipCommand
                {
                    BucketId = Bucket.Id,
                    Text = text,
                    Source = "copy"
                },
                Project.Id));
            AssertEqual(
                ZetlResponseStatus.Success,
                response.Status,
                "Lifecycle fixture mutation should succeed.");
            return response.Payload!.Value.Deserialize<ZetlSlipSnapshot>(ZetlProtocolJson.Options)!.Id;
        }

        public ZetlResponseEnvelope Execute(ZetlCommandEnvelope command)
        {
            var response = service.Execute(command);
            AssertEqual(ZetlResponseStatus.Success, response.Status, $"{command.Kind} should succeed.");
            return response;
        }

        // What a full GetProject returns now, to compare against Kastn's rebuilt
        // snapshot. Serialized so record collections compare by content.
        public string ReadFullProjectJson(string? projectId = null)
        {
            var response = service.Execute(new ZetlCommandEnvelope
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Kind = ZetlCommandKind.GetProject,
                ProjectId = projectId ?? Project.Id
            });
            AssertEqual(ZetlResponseStatus.Success, response.Status, "A full project read should succeed.");
            return response.Payload!.Value.GetRawText();
        }

        public void Dispose()
        {
            StopServer();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
