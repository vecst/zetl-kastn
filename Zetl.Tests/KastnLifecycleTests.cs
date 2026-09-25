using System.Collections.Concurrent;
using KASTN;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class KastnLifecycleTests
{
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
                    command.Kind == ZetlCommandKind.GetProject
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

            fixture.Store.AddNote(
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
            AssertTrue(
                burstRequests < 10,
                $"Forty invalidations should require a bounded refresh count, not one task each (actual {burstRequests}).");
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
        if (predicate(controller.Current))
        {
            return controller.Current;
        }

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
        private readonly ZetlProjectService service;
        private ZetlIpcServer? server;

        private readonly Func<ZetlCommandEnvelope, bool>? dropResponseForTesting;

        public LifecycleFixture(
            bool startServer = true,
            Func<ZetlCommandEnvelope, bool>? dropResponseForTesting = null)
        {
            this.dropResponseForTesting = dropResponseForTesting;
            Directory.CreateDirectory(directory);
            PipeName = $"kastn-lifecycle-{Guid.NewGuid():N}";
            Store = new ZetlStateStore(
                Path.Combine(directory, "state.json"),
                "kastn-lifecycle");
            Project = Store.CreateProject("Lifecycle Project", ["Inbox"], "Inbox");
            Bucket = Project.Buckets.Single(bucket => bucket.Name == "Inbox");
            service = new ZetlProjectService(Store);
            if (startServer)
            {
                StartServer();
            }
        }

        public string PipeName { get; }
        public ZetlStateStore Store { get; }
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
                dropResponseForTesting: dropResponseForTesting);
            server.Start();
        }

        public void StopServer()
        {
            server?.Dispose();
            server = null;
        }

        public void AddSlip(string text)
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
