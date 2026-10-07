using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZETL;

/// <summary>
/// What <see cref="LinuxClipboard"/> needs from the desktop's clipboard: the
/// MIME types and data of the current selection, and a way to own it. Wayland
/// data-control in production; a fake in tests.
/// </summary>
internal interface ILinuxSelection
{
    /// <summary>Advances every time the selection changes, Zetl's own writes included.</summary>
    uint Generation { get; }

    /// <summary>The current selection's MIME types (empty when nothing is copied), or null when unavailable.</summary>
    IReadOnlyList<string>? GetMimeTypes(out uint generation);

    /// <summary>
    /// Reads the given types from one selection generation. A type whose
    /// transfer failed or timed out is absent. Null when unavailable.
    /// </summary>
    IReadOnlyDictionary<string, byte[]>? Read(IReadOnlyCollection<string> mimeTypes, out uint generation);

    /// <summary>
    /// Makes Zetl the selection owner, handing data over only when an app asks
    /// (<paramref name="sent"/> reports each request). <paramref name="replaced"/>
    /// runs when something else takes the selection. Returns the generation of
    /// Zetl's selection, or null if it could not be set.
    /// </summary>
    uint? Offer(IReadOnlyDictionary<string, byte[]> data, Action<string>? sent = null, Action? replaced = null);

    /// <summary>Empties the selection; returns the new generation or null.</summary>
    uint? Clear();
}

/// <summary>
/// The compositor's clipboard through ext-data-control-v1, the protocol KWin
/// gives clipboard managers. Unlike an ordinary Wayland client, a data-control
/// client sees every selection change without having focus, which is what a
/// tray app capturing copies needs.
///
/// One dedicated thread owns the Wayland connection and runs its event loop;
/// requests from other threads are queued to it. Transfers never block that
/// thread: incoming data is read on the caller's thread, and Zetl's own data
/// is written from the thread pool, so Zetl can read a selection it owns.
/// </summary>
internal sealed unsafe class WaylandDataControl : ILinuxSelection, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SelectionConfirmTimeout = TimeSpan.FromSeconds(1);

    private readonly Action<string> log;
    private readonly ConcurrentQueue<Action> requests = new();
    private readonly Dictionary<IntPtr, List<string>> offers = [];
    private readonly Dictionary<IntPtr, OwnedSource> sources = [];
    private readonly object generationGate = new();
    private readonly GCHandle deviceHandle;
    private readonly GCHandle offerHandle;
    private readonly GCHandle sourceHandle;
    private readonly GCHandle ignoreHandle;
    private readonly GCHandle registryHandle;
    private readonly List<(uint Name, string Interface)> globals = [];
    private IntPtr display;
    private IntPtr manager;
    private IntPtr device;
    private IntPtr currentOffer;
    private string[] currentTypes = [];
    private uint generation;
    private (int Read, int Write) wake = (-1, -1);
    private Thread? thread;
    private volatile bool stopping;
    private volatile bool failed;

    private WaylandDataControl(Action<string> log)
    {
        this.log = log;
        deviceHandle = GCHandle.Alloc(new Listener(OnDeviceEvent));
        offerHandle = GCHandle.Alloc(new Listener(OnOfferEvent));
        sourceHandle = GCHandle.Alloc(new Listener(OnSourceEvent));
        ignoreHandle = GCHandle.Alloc(new Listener((_, _, _) => { }));
        registryHandle = GCHandle.Alloc(new Listener((_, opcode, args) =>
        {
            if (opcode == WaylandClient.RegistryEventGlobal && args[1].String is { } name)
            {
                globals.Add((args[0].Uint, name));
            }
        }));
    }

    public uint Generation => Volatile.Read(ref generation);

    /// <summary>
    /// Connects to the session's Wayland compositor. Null (with the reason
    /// logged) when there is no Wayland session or it lacks ext-data-control.
    /// </summary>
    public static WaylandDataControl? TryConnect(Action<string> log)
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            log("Linux clipboard: not a Wayland session; clipboard capture is off.");
            return null;
        }

        if (!WaylandClient.Available)
        {
            log("Linux clipboard: libwayland-client is not available; clipboard capture is off.");
            return null;
        }

        var control = new WaylandDataControl(log);
        if (control.Connect()) return control;
        control.Dispose();
        return null;
    }

    private bool Connect()
    {
        display = WaylandClient.wl_display_connect(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            log("Linux clipboard: can't connect to the Wayland compositor; clipboard capture is off.");
            return false;
        }

        var registry = WaylandClient.GetRegistry(display);
        WaylandClient.Listen(registry, registryHandle);
        WaylandClient.wl_display_roundtrip(display);
        var managerGlobal = globals.FirstOrDefault(g => g.Interface == "ext_data_control_manager_v1");
        var seatGlobal = globals.FirstOrDefault(g => g.Interface == "wl_seat");
        if (managerGlobal.Interface is null || seatGlobal.Interface is null)
        {
            log("Linux clipboard: the compositor does not offer ext-data-control-v1 "
                + "(KDE Plasma 6 and wlroots compositors do); clipboard capture is off.");
            return false;
        }

        manager = WaylandClient.BindGlobal(registry, managerGlobal.Name, WaylandClient.ManagerInterface, 1);
        var seat = WaylandClient.BindGlobal(registry, seatGlobal.Name, WaylandClient.SeatInterface, 1);
        WaylandClient.Listen(seat, ignoreHandle);
        device = WaylandClient.Send(
            manager,
            WaylandClient.ManagerGetDataDevice,
            WaylandClient.DeviceInterface,
            0,
            WlArgument.Of(IntPtr.Zero),
            WlArgument.Of(seat));
        WaylandClient.Listen(device, deviceHandle);
        // The device announces the current selection straight away.
        if (WaylandClient.wl_display_roundtrip(display) < 0)
        {
            log("Linux clipboard: the Wayland connection failed during setup.");
            return false;
        }

        wake = LinuxPosix.CreateWakePipe();
        thread = new Thread(EventLoop) { IsBackground = true, Name = "Zetl Wayland clipboard" };
        thread.Start();
        log("Linux clipboard: connected through ext-data-control-v1.");
        return true;
    }

    public IReadOnlyList<string>? GetMimeTypes(out uint selectionGeneration)
    {
        var result = Invoke(() => (Generation, (IReadOnlyList<string>)currentTypes));
        selectionGeneration = result?.Item1 ?? Generation;
        return result?.Item2;
    }

    public IReadOnlyDictionary<string, byte[]>? Read(IReadOnlyCollection<string> mimeTypes, out uint selectionGeneration)
    {
        if (thread == Thread.CurrentThread)
        {
            throw new InvalidOperationException("Clipboard reads must not run on the Wayland thread.");
        }

        // Ask for every type in one go, from one offer: one selection generation.
        var started = Invoke(() =>
        {
            var pipes = new List<(string Type, int Read)>();
            if (currentOffer != IntPtr.Zero)
            {
                foreach (var type in mimeTypes.Distinct().Where(currentTypes.Contains))
                {
                    if (LinuxPosix.CreateTransferPipe() is not { } pipe) continue;
                    // libwayland duplicates the descriptor it sends; close ours.
                    WaylandClient.SendWithString(currentOffer, WaylandClient.OfferReceive, type, WlArgument.Fd(pipe.Write));
                    LinuxPosix.Close(pipe.Write);
                    pipes.Add((type, pipe.Read));
                }
            }

            WaylandClient.wl_display_flush(display);
            return (Generation, pipes);
        });
        if (started is not { } transfer)
        {
            selectionGeneration = Generation;
            return null;
        }

        selectionGeneration = transfer.Item1;
        return ReadTransfers(transfer.pipes);
    }

    public uint? Offer(IReadOnlyDictionary<string, byte[]> data, Action<string>? sent = null, Action? replaced = null)
    {
        var before = Invoke(() =>
        {
            var source = WaylandClient.Send(
                manager, WaylandClient.ManagerCreateDataSource, WaylandClient.SourceInterface, 0, WlArgument.Of(IntPtr.Zero));
            if (source == IntPtr.Zero) return (Created: false, Generation: 0u);
            WaylandClient.Listen(source, sourceHandle);
            sources[source] = new OwnedSource(data, sent, replaced);
            foreach (var type in data.Keys) WaylandClient.SendWithString(source, WaylandClient.SourceOffer, type);
            var current = Generation;
            WaylandClient.Send(device, WaylandClient.DeviceSetSelection, IntPtr.Zero, 0, WlArgument.Of(source));
            WaylandClient.wl_display_flush(display);
            return (Created: true, Generation: current);
        });
        return before is { Created: true } offered ? WaitForNewGeneration(offered.Generation) : null;
    }

    public uint? Clear()
    {
        var before = Invoke(() =>
        {
            var current = Generation;
            WaylandClient.Send(device, WaylandClient.DeviceSetSelection, IntPtr.Zero, 0, WlArgument.Of(IntPtr.Zero));
            WaylandClient.wl_display_flush(display);
            return current;
        });
        return before is { } generationBefore ? WaitForNewGeneration(generationBefore) : null;
    }

    public void Dispose()
    {
        stopping = true;
        LinuxPosix.Signal(wake.Write);
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(1));
        if (display != IntPtr.Zero)
        {
            // Disconnecting releases every object, ending Zetl's ownership.
            WaylandClient.wl_display_disconnect(display);
            display = IntPtr.Zero;
        }

        LinuxPosix.Close(wake.Read);
        LinuxPosix.Close(wake.Write);
        wake = (-1, -1);
        foreach (var handle in new[] { deviceHandle, offerHandle, sourceHandle, ignoreHandle, registryHandle })
        {
            if (handle.IsAllocated) handle.Free();
        }
    }

    private void EventLoop()
    {
        var displayFd = WaylandClient.wl_display_get_fd(display);
        try
        {
            while (!stopping)
            {
                while (requests.TryDequeue(out var request)) request();
                while (WaylandClient.wl_display_prepare_read(display) != 0)
                {
                    if (WaylandClient.wl_display_dispatch_pending(display) < 0) throw ConnectionError();
                }

                WaylandClient.wl_display_flush(display);
                var ready = LinuxPosix.Poll([displayFd, wake.Read], -1);
                if (ready[0])
                {
                    if (WaylandClient.wl_display_read_events(display) < 0) throw ConnectionError();
                }
                else
                {
                    WaylandClient.wl_display_cancel_read(display);
                }

                if (ready[1]) LinuxPosix.Drain(wake.Read);
                if (WaylandClient.wl_display_dispatch_pending(display) < 0) throw ConnectionError();
            }
        }
        catch (Exception ex)
        {
            log($"Linux clipboard: Wayland connection lost ({ex.Message}); clipboard capture is off.");
            failed = true;
            lock (generationGate)
            {
                generation++;
                Monitor.PulseAll(generationGate);
            }
        }
    }

    private IOException ConnectionError() =>
        new($"protocol error {WaylandClient.wl_display_get_error(display)}");

    /// <summary>Runs work on the Wayland thread and waits for its result; null if unavailable.</summary>
    private T? Invoke<T>(Func<T> work) where T : struct
    {
        if (failed || stopping) return null;
        if (thread == Thread.CurrentThread) return work();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        LinuxPosix.Signal(wake.Write);
        try
        {
            return completion.Task.Wait(RequestTimeout) ? completion.Task.Result : null;
        }
        catch (AggregateException ex)
        {
            log($"Linux clipboard: request failed ({ex.InnerException?.Message}).");
            return null;
        }
    }

    private uint? WaitForNewGeneration(uint before)
    {
        var deadline = DateTime.UtcNow + SelectionConfirmTimeout;
        lock (generationGate)
        {
            while (Generation == before && !failed)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return null;
                Monitor.Wait(generationGate, remaining);
            }
        }

        return failed ? null : Generation;
    }

    /// <summary>Reads every transfer to end of file, together, under one deadline.</summary>
    private static Dictionary<string, byte[]> ReadTransfers(List<(string Type, int Read)> pipes)
    {
        var results = new Dictionary<string, byte[]>();
        var pending = pipes.Select(pipe => (pipe.Type, pipe.Read, Data: new MemoryStream())).ToList();
        var scratch = new byte[64 * 1024];
        var deadline = DateTime.UtcNow + TransferTimeout;
        try
        {
            while (pending.Count > 0)
            {
                var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;
                var ready = LinuxPosix.Poll(pending.Select(p => p.Read).ToList(), remaining);
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (!ready[i]) continue;
                    var (type, descriptor, data) = pending[i];
                    bool finished;
                    try
                    {
                        finished = LinuxPosix.ReadAvailable(descriptor, data, scratch);
                    }
                    catch (IOException)
                    {
                        LinuxPosix.Close(descriptor);
                        pending.RemoveAt(i);
                        continue;
                    }

                    if (!finished) continue;
                    results[type] = data.ToArray();
                    LinuxPosix.Close(descriptor);
                    pending.RemoveAt(i);
                }
            }
        }
        finally
        {
            foreach (var (_, descriptor, _) in pending) LinuxPosix.Close(descriptor);
        }

        return results;
    }

    private void OnDeviceEvent(IntPtr proxy, uint opcode, WlArgument* args)
    {
        switch (opcode)
        {
            case WaylandClient.DeviceEventDataOffer:
                var offer = args[0].Pointer;
                offers[offer] = [];
                WaylandClient.Listen(offer, offerHandle);
                break;
            case WaylandClient.DeviceEventSelection:
                var selected = args[0].Pointer;
                var previous = currentOffer;
                currentOffer = selected;
                currentTypes = selected != IntPtr.Zero && offers.TryGetValue(selected, out var types)
                    ? types.ToArray()
                    : [];
                if (previous != IntPtr.Zero && previous != selected) DestroyOffer(previous);
                lock (generationGate)
                {
                    generation++;
                    Monitor.PulseAll(generationGate);
                }

                break;
            case WaylandClient.DeviceEventPrimarySelection:
                // Zetl uses only the clipboard selection, not middle-click paste.
                var primary = args[0].Pointer;
                if (primary != IntPtr.Zero && primary != currentOffer) DestroyOffer(primary);
                break;
            case WaylandClient.DeviceEventFinished:
                log("Linux clipboard: the compositor ended clipboard access; clipboard capture is off.");
                failed = true;
                break;
        }
    }

    private void OnOfferEvent(IntPtr proxy, uint opcode, WlArgument* args)
    {
        if (opcode == WaylandClient.OfferEventOffer
            && offers.TryGetValue(proxy, out var types)
            && args[0].String is { } type)
        {
            types.Add(type);
        }
    }

    private void OnSourceEvent(IntPtr proxy, uint opcode, WlArgument* args)
    {
        if (!sources.TryGetValue(proxy, out var source)) return;
        if (opcode == WaylandClient.SourceEventSend)
        {
            var type = args[0].String ?? "";
            var descriptor = args[1].Int;
            if (!source.Data.TryGetValue(type, out var bytes))
            {
                LinuxPosix.Close(descriptor);
                return;
            }

            source.Sent?.Invoke(type);
            // Write off this thread: the reader may be slow, or be Zetl itself.
            _ = Task.Run(() =>
            {
                try
                {
                    using var stream = new FileStream(new SafeFileHandle(descriptor, ownsHandle: true), FileAccess.Write, 1);
                    stream.Write(bytes);
                }
                catch (IOException)
                {
                    // The reader went away before taking everything.
                }
            });
        }
        else if (opcode == WaylandClient.SourceEventCancelled)
        {
            sources.Remove(proxy);
            WaylandClient.Send(proxy, WaylandClient.SourceDestroy, IntPtr.Zero, WaylandClient.MarshalDestroy);
            source.Replaced?.Invoke();
        }
    }

    private void DestroyOffer(IntPtr offer)
    {
        offers.Remove(offer);
        WaylandClient.Send(offer, WaylandClient.OfferDestroy, IntPtr.Zero, WaylandClient.MarshalDestroy);
    }

    private sealed record OwnedSource(IReadOnlyDictionary<string, byte[]> Data, Action<string>? Sent, Action? Replaced);

    private sealed class Listener(Listener.Handler handler) : IWaylandListener
    {
        public delegate void Handler(IntPtr proxy, uint opcode, WlArgument* args);

        public void OnEvent(IntPtr proxy, uint opcode, WlArgument* args) => handler(proxy, opcode, args);
    }
}
