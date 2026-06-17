using System.Collections.Concurrent;
using KASTN;
using ZETL.Contracts;

namespace ZETL.Tests;

internal static class KastnLifecycleTests
{
    public static void ActivationHandoffCarriesProjectId()
    {
        RunAsync(async () =>
        {
            var pipeName = $"kastn-activation-tests-{Guid.NewGuid():N}";
            await using var server = new KastnActivationServer(pipeName);
            var received = new TaskCompletionSource<KastnActivationRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            server.ActivationRequested += (_, request) => received.TrySetResult(request);
            server.Start();

            await KastnActivationClient.SendAsync(
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

    public static void ControllerLaunchesZetlAndLoadsProject()
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

    public static void ControllerLaunchesToProjectSelectionWithoutHandoff()
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

    public static void ControllerRefreshesAfterProjectChange()
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

    public static void ControllerReconnectsAfterZetlRestart()
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

    public static void ControllerRelaunchesZetlAfterItExits()
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

            fixture.StopServer();
            await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Offline);
            var relaunched = await WaitForSnapshotAsync(
                controller,
                snapshot => snapshot.ConnectionState == KastnConnectionState.Online
                    && Volatile.Read(ref launches) == 1);

            AssertEqual(1, launches, "Kastn should launch Zetl again after a later outage.");
            AssertEqual(
                fixture.Project.Id,
                relaunched.Project?.Id,
                "Kastn should reopen the desired project after relaunching Zetl.");
        });
    }

    public static void ClosingControllerLeavesZetlAvailable()
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

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "KastnLifecycleTests",
            Guid.NewGuid().ToString("N"));
        private readonly ZetlProjectService service;
        private ZetlIpcServer? server;

        public LifecycleFixture(bool startServer = true)
        {
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

            server = new ZetlIpcServer(service, PipeName);
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
