using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ZETL.Contracts;

using Xunit;
using static ZETL.Tests.XunitAsserts;

namespace ZETL.Tests;

public class ZetlIpcTests
{
    [Fact] public void CancellationAfterCommandWriteReportsOutcomeUnknown()
    {
        RunAsync(async () =>
        {
            var pipeName = $"zetl-unknown-{Guid.NewGuid():N}";
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var commandReceived = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseServer = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var serverTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                _ = await ZetlIpcFraming.ReadAsync(server, CancellationToken.None);
                await ZetlIpcFraming.WriteAsync(
                    server,
                    ZetlIpcMessage.Create(
                        ZetlIpcMessageKind.Welcome,
                        new ZetlIpcWelcome
                        {
                            ServerInstanceId = "outcome-server",
                            ProtocolVersion = ZetlProtocol.CurrentVersion
                        }),
                    CancellationToken.None);
                _ = await ZetlIpcFraming.ReadAsync(server, CancellationToken.None);
                commandReceived.TrySetResult();
                await releaseServer.Task;
            });

            await using var client = new ZetlIpcClient("unknown-client", pipeName);
            await client.ConnectAsync(TimeSpan.FromSeconds(5));
            try
            {
                using var cancellation = new CancellationTokenSource();
                var execute = client.ExecuteAsync(
                    new ZetlCommandEnvelope
                    {
                        CommandId = "unknown-after-write",
                        Kind = ZetlCommandKind.ListProjects
                    },
                    cancellation.Token);
                await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();

                var error = await Assert.ThrowsAsync<ZetlCommandOutcomeUnknownException>(async () => await execute);
                AssertEqual("unknown-after-write", error.CommandId, "The uncertain result should retain its command id.");
            }
            finally
            {
                releaseServer.TrySetResult();
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Fact] public void ClientListsOpensAndMutatesProject()
    {
        RunAsync(async () =>
        {
            using var fixture = new IpcFixture();
            await using var client = await fixture.ConnectClientAsync("integration-client");
            var list = await client.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "ipc-list",
                Kind = ZetlCommandKind.ListProjects
            });
            var summaries = list.Payload?.Deserialize<List<ZetlProjectSummary>>(
                ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("ListProjects returned no summaries.");
            var open = await client.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "ipc-open",
                Kind = ZetlCommandKind.GetProject,
                ProjectId = fixture.Project.Id
            });
            var snapshot = open.Payload?.Deserialize<ZetlProjectSnapshot>(
                ZetlProtocolJson.Options)
                ?? throw new InvalidOperationException("GetProject returned no snapshot.");
            var add = await client.ExecuteAsync(ZetlCommandEnvelope.Create(
                "ipc-add",
                ZetlCommandKind.AddSlip,
                new AddSlipCommand
                {
                    BucketId = fixture.Bucket.Id,
                    Text = "through the pipe",
                    Source = "copy"
                },
                fixture.Project.Id));

            AssertEqual(ZetlResponseStatus.Success, list.Status, "ListProjects should succeed over IPC.");
            AssertEqual(fixture.Project.Id, summaries.Single().Id, "IPC list should return the project.");
            AssertEqual(fixture.Project.Id, snapshot.Id, "IPC open should return the requested project.");
            AssertEqual(ZetlResponseStatus.Success, add.Status, "IPC mutation should succeed.");
            AssertEqual("through the pipe", fixture.Bucket.Notes.Single().Text, "IPC mutation should reach the live store.");
        });
    }

    [Fact] public void ClientRetrievesPictureContent()
    {
        RunAsync(async () =>
        {
            using var fixture = new IpcFixture();
            var bytes = new byte[] { 9, 8, 7, 6 };
            var picture = fixture.Store.AddImageNote(
                fixture.Project,
                fixture.Bucket,
                new ZetlClipboardImage(bytes, 12, 8),
                "copy");
            await using var client = await fixture.ConnectClientAsync("picture-client");

            var response = await client.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "ipc-picture",
                Kind = ZetlCommandKind.GetSlipPicture,
                ProjectId = fixture.Project.Id,
                TargetId = picture.Id
            });
            var content = response.Payload?.Deserialize<ZetlPictureContent>(
                ZetlProtocolJson.Options);

            AssertEqual(ZetlResponseStatus.Success, response.Status, "Picture query should succeed over IPC.");
            AssertTrue(content?.Bytes.SequenceEqual(bytes) == true, "IPC should return the stored picture bytes.");
            AssertEqual(picture.Image?.Sha256, content?.Sha256, "IPC picture content should identify its hash.");
        });
    }

    [Fact] public void TwoClientsReceiveOrderedChanges()
    {
        RunAsync(async () =>
        {
            using var fixture = new IpcFixture();
            await using var first = await fixture.ConnectClientAsync("first-client");
            await using var second = await fixture.ConnectClientAsync("second-client");
            var firstEvents = new ConcurrentQueue<ZetlProjectChangedEvent>();
            var secondEvents = new ConcurrentQueue<ZetlProjectChangedEvent>();
            var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.ProjectChanged += (_, change) =>
            {
                firstEvents.Enqueue(change);
                if (firstEvents.Count == 2)
                {
                    firstReady.TrySetResult();
                }
            };
            second.ProjectChanged += (_, change) =>
            {
                secondEvents.Enqueue(change);
                if (secondEvents.Count == 2)
                {
                    secondReady.TrySetResult();
                }
            };

            await first.ExecuteAsync(AddSlip(
                "ordered-1",
                fixture,
                "first"));
            await first.ExecuteAsync(AddSlip(
                "ordered-2",
                fixture,
                "second"));
            await Task.WhenAll(
                firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                secondReady.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            var firstSequences = firstEvents.Select(change => change.ProjectChangeSequence).ToList();
            var secondSequences = secondEvents.Select(change => change.ProjectChangeSequence).ToList();
            AssertTrue(firstSequences.SequenceEqual(firstSequences.Order()), "First client events should be ordered.");
            AssertTrue(secondSequences.SequenceEqual(secondSequences.Order()), "Second client events should be ordered.");
            AssertTrue(firstSequences.SequenceEqual(secondSequences), "Both clients should observe the same event order.");
        });
    }

    [Fact] public void MalformedMessageDoesNotStopServer()
    {
        RunAsync(async () =>
        {
            using var fixture = new IpcFixture();
            using (var raw = new NamedPipeClientStream(
                ".",
                fixture.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await raw.ConnectAsync(5000);
                await ZetlIpcFraming.WriteAsync(
                    raw,
                    ZetlIpcMessage.Create(
                        ZetlIpcMessageKind.Hello,
                        new ZetlIpcHello
                        {
                            ClientName = "malformed-client",
                            ClientInstanceId = "malformed",
                            SubscribeToProjectChanges = false
                        }),
                    CancellationToken.None);
                var welcome = await ZetlIpcFraming.ReadAsync(raw, CancellationToken.None);
                AssertEqual(ZetlIpcMessageKind.Welcome, welcome?.Kind, "Raw client should complete handshake.");

                var invalidPayload = Encoding.UTF8.GetBytes("{");
                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, invalidPayload.Length);
                await raw.WriteAsync(header);
                await raw.WriteAsync(invalidPayload);
                await raw.FlushAsync();

                var error = await ZetlIpcFraming.ReadAsync(raw, CancellationToken.None);
                var details = error?.Payload?.Deserialize<ZetlProtocolError>(
                    ZetlProtocolJson.Options);
                AssertEqual(ZetlIpcMessageKind.Error, error?.Kind, "Malformed JSON should receive an error.");
                AssertEqual("message_invalid", details?.Code, "Malformed JSON should use a stable error code.");
            }

            await using var healthy = await fixture.ConnectClientAsync("healthy-after-malformed");
            var response = await healthy.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "after-malformed",
                Kind = ZetlCommandKind.ListProjects
            });
            AssertEqual(ZetlResponseStatus.Success, response.Status, "Malformed client must not stop the server.");
        });
    }

    [Fact] public void ProtocolMismatchIsRejectedWithoutStoppingServer()
    {
        RunAsync(async () =>
        {
            using var fixture = new IpcFixture();
            using (var raw = new NamedPipeClientStream(
                ".",
                fixture.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await raw.ConnectAsync(5000);
                await ZetlIpcFraming.WriteAsync(
                    raw,
                    new ZetlIpcMessage
                    {
                        ProtocolVersion = ZetlProtocol.CurrentVersion + 1,
                        Kind = ZetlIpcMessageKind.Hello,
                        Payload = ZetlProtocolJson.ToElement(new ZetlIpcHello
                        {
                            ClientName = "future-client",
                            ClientInstanceId = "future",
                            SubscribeToProjectChanges = false
                        })
                    },
                    CancellationToken.None);
                var error = await ZetlIpcFraming.ReadAsync(raw, CancellationToken.None);
                var details = error?.Payload?.Deserialize<ZetlProtocolError>(
                    ZetlProtocolJson.Options);

                AssertEqual(ZetlIpcMessageKind.Error, error?.Kind, "Unsupported protocol should be rejected.");
                AssertEqual("handshake_required", details?.Code, "Protocol rejection should be explicit.");
            }

            await using var healthy = await fixture.ConnectClientAsync("healthy-after-version");
            var response = await healthy.ExecuteAsync(new ZetlCommandEnvelope
            {
                CommandId = "after-version",
                Kind = ZetlCommandKind.ListProjects
            });
            AssertEqual(ZetlResponseStatus.Success, response.Status, "Version mismatch must not stop the server.");
        });
    }

    [Fact] public void OversizedFrameIsRejected()
    {
        RunAsync(async () =>
        {
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(
                header,
                ZetlIpcFraming.MaxMessageBytes + 1);
            await using var stream = new MemoryStream(header);
            try
            {
                _ = await ZetlIpcFraming.ReadAsync(stream, CancellationToken.None);
                throw new InvalidOperationException("Oversized frame should have been rejected.");
            }
            catch (InvalidDataException)
            {
            }
        });
    }

    [Fact] public void PartialFrameTimesOutWhenProgressStops()
    {
        RunAsync(async () =>
        {
            await using var stream = new StallingReadStream([1, 0]);
            await Assert.ThrowsAsync<TimeoutException>(() =>
                ZetlIpcFraming.ReadAsync(
                    stream,
                    CancellationToken.None,
                    TimeSpan.FromMilliseconds(50)));
        });
    }

    [Fact] public void PartialPayloadTimesOutWhenProgressStops()
    {
        RunAsync(async () =>
        {
            var frameStart = new byte[sizeof(int) + 2];
            BinaryPrimitives.WriteInt32LittleEndian(frameStart, 4);
            frameStart[4] = (byte)'{';
            frameStart[5] = (byte)'"';
            await using var stream = new StallingReadStream(frameStart);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                ZetlIpcFraming.ReadAsync(
                    stream,
                    CancellationToken.None,
                    TimeSpan.FromMilliseconds(50)));
        });
    }

    [Fact] public void IdleFrameWaitUsesCallerCancellationInsteadOfProgressTimeout()
    {
        RunAsync(async () =>
        {
            await using var stream = new StallingReadStream([]);
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(75));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ZetlIpcFraming.ReadAsync(
                    stream,
                    cancellation.Token,
                    TimeSpan.FromMilliseconds(20)));
        });
    }

    [Fact] public void ServerHandshakeHasAnInjectableTotalDeadline()
    {
        RunAsync(async () =>
        {
            await using var stream = new StallingReadStream([]);
            var handshake = new ZetlIpcServerHandshake(
                "deadline-server",
                new ZetlIpcServerOptions
                {
                    HandshakeTimeout = TimeSpan.FromMilliseconds(50),
                    FrameProgressTimeout = TimeSpan.FromMilliseconds(20)
                });

            await Assert.ThrowsAsync<TimeoutException>(() =>
                handshake.AcceptAsync(stream, CancellationToken.None));
        });
    }

    [Fact] public void OutboundResponsesHavePriorityOverCoalescedProjectChanges()
    {
        RunAsync(async () =>
        {
            var outbound = new ZetlIpcOutboundDispatcher(responseCapacity: 1);
            AssertTrue(
                outbound.QueueProjectChange(Change("project-a", sequence: 1)),
                "The first project invalidation should be accepted.");
            AssertTrue(
                outbound.QueueProjectChange(Change("project-a", sequence: 2)),
                "A newer project invalidation should replace the pending one.");
            AssertTrue(
                outbound.TryQueueReply(ZetlIpcMessage.Create(
                    ZetlIpcMessageKind.Response,
                    new { Marker = "response" },
                    "response-1")),
                "Pending events must not consume response capacity.");
            outbound.Complete();

            await using var stream = new MemoryStream();
            await outbound.WriteToAsync(stream, CancellationToken.None);
            stream.Position = 0;
            var first = await ZetlIpcFraming.ReadAsync(stream, CancellationToken.None);
            var second = await ZetlIpcFraming.ReadAsync(stream, CancellationToken.None);
            var end = await ZetlIpcFraming.ReadAsync(stream, CancellationToken.None);
            var change = second?.Payload?.Deserialize<ZetlProjectChangedEvent>(
                ZetlProtocolJson.Options);

            AssertEqual(
                ZetlIpcMessageKind.Response,
                first?.Kind,
                "Responses should be written before pending invalidation events.");
            AssertEqual(
                ZetlIpcMessageKind.ProjectChanged,
                second?.Kind,
                "The coalesced invalidation should follow the response.");
            AssertEqual(
                2L,
                change?.ProjectChangeSequence,
                "Only the newest pending sequence for a project should be delivered.");
            AssertEqual<ZetlIpcMessage?>(null, end, "The dispatcher should emit exactly two messages.");
        });
    }

    [Fact] public void ProjectEventSaturationCannotCrowdOutCompletedMutationResponse()
    {
        RunAsync(async () =>
        {
            using var fixture = new IpcFixture();
            var service = new ZetlProjectService(fixture.Store);
            var outbound = new ZetlIpcOutboundDispatcher(responseCapacity: 1);
            service.ProjectChanged += (_, change) =>
                outbound.QueueProjectChange(change);
            for (var sequence = 1; sequence <= 100; sequence++)
            {
                AssertTrue(
                    outbound.QueueProjectChange(Change(fixture.Project.Id, sequence)),
                    "The event side should coalesce without consuming response capacity.");
            }

            var handler = new ZetlIpcInboundCommandHandler(
                service,
                log: null,
                dropResponseForTesting: null);
            var handled = handler.Handle(ZetlIpcMessage.Create(
                ZetlIpcMessageKind.Command,
                AddSlip("saturated-response", fixture, "committed under saturation"),
                "saturated-response"));

            AssertTrue(
                fixture.Bucket.Notes.Any(note => note.Text == "committed under saturation"),
                "The mutation should be durably applied before its response is queued.");
            AssertTrue(
                handled.Reply is not null && outbound.TryQueueReply(handled.Reply),
                "A completed mutation response must retain its dedicated capacity.");
            outbound.Complete();

            await using var stream = new MemoryStream();
            await outbound.WriteToAsync(stream, CancellationToken.None);
            stream.Position = 0;
            var first = await ZetlIpcFraming.ReadAsync(stream, CancellationToken.None);
            AssertEqual(
                ZetlIpcMessageKind.Response,
                first?.Kind,
                "The completed mutation response should be delivered before saturated events.");
        });
    }

    [Fact] public void DisconnectedClientDoesNotAffectCaptureOrPeers()
    {
        RunAsync(async () =>
        {
            using var fixture = new IpcFixture();
            var disconnected = await fixture.ConnectClientAsync("disconnecting-client");
            await using var remaining = await fixture.ConnectClientAsync("remaining-client");
            await disconnected.DisposeAsync();

            fixture.Store.AddNote(fixture.Bucket, "direct capture", "copy");
            var response = await remaining.ExecuteAsync(AddSlip(
                "after-disconnect",
                fixture,
                "peer mutation"));

            AssertEqual(ZetlResponseStatus.Success, response.Status, "Remaining client should still mutate.");
            AssertEqual(2, fixture.Bucket.Notes.Count, "Direct capture and peer mutation should both survive.");
        });
    }

    [Fact] public void RealZetlProcessHostsIpc()
    {
        RunAsync(async () =>
        {
            // All projects build into one shared output directory (see
            // Directory.Build.props), so the Zetl host assembly sits next to the
            // test assembly.
            var appPath = Path.Combine(AppContext.BaseDirectory, "Zetl.dll");
            if (!File.Exists(appPath))
            {
                throw new FileNotFoundException(
                    "Build Zetl.App before running the process IPC smoke test.",
                    appPath);
            }

            var directory = Path.Combine(
                Path.GetTempPath(),
                "ZetlIpcProcessTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipeName = $"zetl-process-{Guid.NewGuid():N}";
            var readyFile = Path.Combine(directory, "ready");
            var stopFile = Path.Combine(directory, "stop");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add(appPath);
            process.StartInfo.ArgumentList.Add($"--ipc-smoke-server={directory}");
            process.StartInfo.ArgumentList.Add($"--ipc-pipe={pipeName}");
            process.StartInfo.ArgumentList.Add($"--ipc-ready-file={readyFile}");
            process.StartInfo.ArgumentList.Add($"--ipc-stop-file={stopFile}");

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Could not start the Zetl IPC smoke process.");
                }

                await WaitForFileOrExitAsync(process, readyFile, TimeSpan.FromSeconds(10));
                await using (var client = new ZetlIpcClient("process-smoke-client", pipeName))
                {
                    await client.ConnectAsync(TimeSpan.FromSeconds(5));
                    var list = await client.ExecuteAsync(new ZetlCommandEnvelope
                    {
                        CommandId = "process-list",
                        Kind = ZetlCommandKind.ListProjects
                    });
                    var project = list.Payload?.Deserialize<List<ZetlProjectSummary>>(
                        ZetlProtocolJson.Options)
                        ?.Single()
                        ?? throw new InvalidOperationException("Process smoke list returned no project.");
                    var open = await client.ExecuteAsync(new ZetlCommandEnvelope
                    {
                        CommandId = "process-open",
                        Kind = ZetlCommandKind.GetProject,
                        ProjectId = project.Id
                    });
                    var snapshot = open.Payload?.Deserialize<ZetlProjectSnapshot>(
                        ZetlProtocolJson.Options)
                        ?? throw new InvalidOperationException("Process smoke open returned no project.");
                    var add = await client.ExecuteAsync(ZetlCommandEnvelope.Create(
                        "process-add",
                        ZetlCommandKind.AddSlip,
                        new AddSlipCommand
                        {
                            BucketId = snapshot.Buckets.Single(item => item.Name == "Inbox").Id,
                            Text = "written by another process",
                            Source = "copy"
                        },
                        project.Id));

                    AssertEqual(ZetlResponseStatus.Success, add.Status, "Real process mutation should succeed.");
                }

                File.WriteAllText(stopFile, "stop");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var standardError = await process.StandardError.ReadToEndAsync();
                AssertEqual(0, process.ExitCode, $"Smoke process should exit cleanly. {standardError}");

                var reloaded = new ZetlStateStore(Path.Combine(directory, "state.json"));
                AssertEqual(
                    "written by another process",
                    reloaded.State.Projects.Single().Buckets.Single(item => item.Name == "Inbox").Notes.Single().Text,
                    "The real Zetl process should persist the IPC mutation.");
            }
            finally
            {
                if (!process.HasExited)
                {
                    try
                    {
                        File.WriteAllText(stopFile, "stop");
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
    }

    private static ZetlCommandEnvelope AddSlip(
        string commandId,
        IpcFixture fixture,
        string text)
    {
        return ZetlCommandEnvelope.Create(
            commandId,
            ZetlCommandKind.AddSlip,
            new AddSlipCommand
            {
                BucketId = fixture.Bucket.Id,
                Text = text,
                Source = "copy"
            },
            fixture.Project.Id);
    }

    private static ZetlProjectChangedEvent Change(string projectId, long sequence) =>
        new()
        {
            EventId = $"event-{sequence}",
            ProjectId = projectId,
            ProjectChangeSequence = sequence,
            ChangeKind = ZetlChangeKind.Updated,
            EntityKind = ZetlEntityKind.Project,
            EntityId = projectId,
            EntityRevision = sequence
        };

    private static void RunAsync(Func<Task> action)
    {
        action().GetAwaiter().GetResult();
    }

    private static async Task WaitForFileOrExitAsync(
        Process process,
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"Zetl IPC smoke process exited with {process.ExitCode}: {error}");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the Zetl IPC smoke server.");
            }

            await Task.Delay(25);
        }
    }



    private sealed class StallingReadStream(byte[] initialBytes) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (position < initialBytes.Length)
            {
                var count = Math.Min(buffer.Length, initialBytes.Length - position);
                initialBytes.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class IpcFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "ZetlIpcTests",
            Guid.NewGuid().ToString("N"));
        private readonly ZetlIpcServer server;

        public IpcFixture()
        {
            Directory.CreateDirectory(directory);
            PipeName = $"zetl-tests-{Guid.NewGuid():N}";
            Store = new ZetlStateStore(
                Path.Combine(directory, "state.json"),
                "ipc-test-session");
            Project = Store.CreateProject("IPC Project", ["Inbox"], "Inbox");
            Bucket = Project.Buckets.Single(item => item.Name == "Inbox");
            var service = new ZetlProjectService(Store);
            server = new ZetlIpcServer(service, PipeName);
            server.Start();
        }

        public string PipeName { get; }
        public ZetlStateStore Store { get; }
        public ZetlProject Project { get; }
        public ZetlBucket Bucket { get; }

        public async Task<ZetlIpcClient> ConnectClientAsync(string name)
        {
            var client = new ZetlIpcClient(name, PipeName);
            await client.ConnectAsync(TimeSpan.FromSeconds(5));
            return client;
        }

        public void Dispose()
        {
            server.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
