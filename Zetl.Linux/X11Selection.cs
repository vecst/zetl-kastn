using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// The clipboard through X11's CLIPBOARD selection, for desktops without a
/// Wayland clipboard-manager protocol: X11 sessions, and GNOME, whose Mutter
/// offers neither data-control protocol but mirrors every copy into Xwayland,
/// where any X11 client can read it without focus.
///
/// XFixes reports every change of owner, which advances <see cref="Generation"/>.
/// Reads ask the owner to convert the selection into a property on Zetl's own
/// unmapped window, including the INCR protocol for large transfers. Owning the
/// selection means answering other clients' SelectionRequests, which is also how
/// a staged paste learns that an app read it.
///
/// One dedicated thread owns the X connection and runs its event loop; other
/// threads queue requests to it and wait on the results. Xlib's default error
/// handler exits the process, so errors from this connection (a requestor that
/// went away mid-transfer) are swallowed and logged instead.
/// </summary>
internal sealed unsafe class X11Selection : ILinuxSelection, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SelectionConfirmTimeout = TimeSpan.FromSeconds(1);
    private const int MaxReadBytes = 64 * 1024 * 1024;
    // Data larger than this goes out incrementally (INCR), well under the
    // smallest request size an X server allows a single property change.
    private const int IncrementalThreshold = 128 * 1024;
    private const int IncrementalChunk = 64 * 1024;

    private const int PropertyNotify = 28;
    private const int SelectionClear = 29;
    private const int SelectionRequest = 30;
    private const int SelectionNotify = 31;
    private const int PropertyNewValue = 0;
    private const int PropertyDelete = 1;
    private const int PropModeReplace = 0;
    private const long PropertyChangeMask = 1L << 22;
    private const ulong FixesSelectionMask = 1 | 2 | 4; // owner change, window destroy, client close
    private static readonly IntPtr XaAtom = 4;
    private static readonly IntPtr XaInteger = 19;

    private static readonly ConcurrentDictionary<IntPtr, X11Selection> Connections = new();
    private static IntPtr previousErrorHandler;
    private static int errorHandlerInstalled;

    private readonly Action<string> log;
    private readonly ConcurrentQueue<Action> requests = new();
    private readonly Dictionary<IntPtr, Conversion> conversions = [];
    private readonly Dictionary<(IntPtr Requestor, IntPtr Property), OutgoingTransfer> outgoing = [];
    private readonly Stack<IntPtr> freeProperties = new();
    private readonly object generationGate = new();
    private IntPtr display;
    private IntPtr window;
    private IntPtr clipboard;
    private IntPtr targetsAtom;
    private IntPtr timestampAtom;
    private IntPtr multipleAtom;
    private IntPtr incrAtom;
    private int fixesEventBase;
    private uint generation;
    private bool lastOwnerWasZetl;
    private (uint Generation, IReadOnlyList<string> Types)? typesCache;
    private OwnedSelection? owned;
    private (int Read, int Write) wake = (-1, -1);
    private Thread? thread;
    private volatile bool stopping;
    private volatile bool failed;

    private X11Selection(Action<string> log)
    {
        this.log = log;
    }

    public uint Generation => Volatile.Read(ref generation);

    /// <summary>
    /// Connects to the X server named by DISPLAY (Xwayland on a Wayland desktop).
    /// Null, with the reason logged, when there is no display or no XFixes.
    /// </summary>
    public static X11Selection? TryConnect(Action<string> log)
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            log("Linux clipboard: no X display either; clipboard capture is off.");
            return null;
        }

        var selection = new X11Selection(log);
        try
        {
            if (selection.Connect()) return selection;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log($"Linux clipboard: the X11 libraries are missing ({ex.Message}); clipboard capture is off.");
        }

        selection.Dispose();
        return null;
    }

    private bool Connect()
    {
        display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            log("Linux clipboard: can't open the X display; clipboard capture is off.");
            return false;
        }

        if (XFixesQueryExtension(display, out fixesEventBase, out _) == 0)
        {
            log("Linux clipboard: the X server lacks XFixes; clipboard capture is off.");
            return false;
        }

        InstallErrorHandler();
        Connections[display] = this;
        window = XCreateSimpleWindow(display, XDefaultRootWindow(display), 0, 0, 1, 1, 0, 0, 0);
        XSelectInput(display, window, (IntPtr)PropertyChangeMask);
        clipboard = Atom("CLIPBOARD");
        targetsAtom = Atom("TARGETS");
        timestampAtom = Atom("TIMESTAMP");
        multipleAtom = Atom("MULTIPLE");
        incrAtom = Atom("INCR");
        for (var i = 15; i >= 0; i--) freeProperties.Push(Atom($"ZETL_CLIPBOARD_{i}"));
        XFixesSelectSelectionInput(display, window, clipboard, FixesSelectionMask);
        XFlush(display);

        wake = LinuxPosix.CreateWakePipe();
        thread = new Thread(EventLoop) { IsBackground = true, Name = "Zetl X11 clipboard" };
        thread.Start();
        log("Linux clipboard: connected through the X11 CLIPBOARD selection.");
        return true;
    }

    public IReadOnlyList<string>? GetMimeTypes(out uint selectionGeneration)
    {
        selectionGeneration = Generation;
        lock (generationGate)
        {
            if (typesCache is { } cached && cached.Generation == selectionGeneration) return cached.Types;
        }

        var started = Wait(Post<(uint Generation, Task<Converted?>? Pending)?>(() =>
        {
            var at = Generation;
            return XGetSelectionOwner(display, clipboard) == IntPtr.Zero
                ? (at, null)
                : (at, StartConversion(targetsAtom));
        }), RequestTimeout);
        if (started is not { } request)
        {
            return null;
        }

        selectionGeneration = request.Generation;
        IReadOnlyList<string> types = [];
        if (request.Pending is { } pending)
        {
            if (Wait(pending, TransferTimeout) is not { } converted) return null;
            types = Wait(Post(() => AtomNames(converted.Data)), RequestTimeout) ?? [];
        }

        lock (generationGate)
        {
            if (Generation == request.Generation) typesCache = (request.Generation, types);
        }

        return types;
    }

    public IReadOnlyDictionary<string, byte[]>? Read(IReadOnlyCollection<string> mimeTypes, out uint selectionGeneration)
    {
        if (thread == Thread.CurrentThread)
        {
            throw new InvalidOperationException("Clipboard reads must not run on the X11 thread.");
        }

        if (GetMimeTypes(out selectionGeneration) is not { } offered) return null;
        var wanted = mimeTypes.Distinct().Where(offered.Contains).ToList();
        var started = Wait(Post(() => wanted.Select(type => (type, StartConversion(Atom(type)))).ToList()), RequestTimeout);
        if (started is null) return null;

        var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var deadline = DateTime.UtcNow + TransferTimeout;
        foreach (var (type, pending) in started)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero && Wait(pending, remaining) is { } converted) data[type] = converted.Data;
        }

        return data;
    }

    public uint? Offer(IReadOnlyDictionary<string, byte[]> data, Action<string>? sent = null, Action? replaced = null)
    {
        var before = Wait(Post(() =>
        {
            var current = Generation;
            var byTarget = data.ToDictionary(pair => Atom(pair.Key), pair => (pair.Key, pair.Value));
            owned = new OwnedSelection(byTarget, sent, replaced);
            XSetSelectionOwner(display, clipboard, window, IntPtr.Zero);
            XFlush(display);
            if (XGetSelectionOwner(display, clipboard) != window)
            {
                owned = null;
                return (uint?)null;
            }

            return current;
        }), RequestTimeout);
        return before is { } generationBefore ? WaitForNewGeneration(generationBefore) : null;
    }

    public uint? Clear()
    {
        var before = Wait(Post(() =>
        {
            var current = Generation;
            owned = null;
            XSetSelectionOwner(display, clipboard, IntPtr.Zero, IntPtr.Zero);
            XFlush(display);
            return (uint?)current;
        }), RequestTimeout);
        return before is { } generationBefore ? WaitForNewGeneration(generationBefore) : null;
    }

    public void Dispose()
    {
        stopping = true;
        if (wake.Write >= 0) LinuxPosix.Signal(wake.Write);
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(1));
        if (display != IntPtr.Zero)
        {
            // Closing the connection destroys the window and gives up ownership.
            Connections.TryRemove(display, out _);
            XCloseDisplay(display);
            display = IntPtr.Zero;
        }

        LinuxPosix.Close(wake.Read);
        LinuxPosix.Close(wake.Write);
        wake = (-1, -1);
    }

    private uint? WaitForNewGeneration(uint before)
    {
        var deadline = DateTime.UtcNow + SelectionConfirmTimeout;
        lock (generationGate)
        {
            while (Generation == before)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(generationGate, remaining)) return null;
            }

            return Generation;
        }
    }

    private Task<T> Post<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (failed || stopping)
        {
            completion.TrySetCanceled();
            return completion.Task;
        }

        requests.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        LinuxPosix.Signal(wake.Write);
        return completion.Task;
    }

    private static T? Wait<T>(Task<T> task, TimeSpan timeout)
    {
        try
        {
            return task.Wait(timeout) ? task.Result : default;
        }
        catch (AggregateException)
        {
            return default;
        }
    }

    // ---- Event loop (X thread only) ----------------------------------------

    private void EventLoop()
    {
        var connection = XConnectionNumber(display);
        var xevent = stackalloc byte[192];
        try
        {
            while (!stopping)
            {
                while (requests.TryDequeue(out var request)) request();
                XFlush(display);
                while (XPending(display) > 0)
                {
                    XNextEvent(display, xevent);
                    Handle(xevent);
                }

                ExpireConversions();
                var ready = LinuxPosix.Poll([connection, wake.Read], 100);
                if (ready[1]) LinuxPosix.Drain(wake.Read);
            }
        }
        catch (Exception ex)
        {
            failed = true;
            log($"Linux clipboard: the X11 clipboard thread failed: {ex.Message}");
        }

        foreach (var conversion in conversions.Values) conversion.Done.TrySetResult(null);
        conversions.Clear();
        lock (generationGate) Monitor.PulseAll(generationGate);
    }

    private void Handle(byte* xevent)
    {
        var type = *(int*)xevent;
        if (type == fixesEventBase)
        {
            // XFixesSelectionNotifyEvent: owner at 48, selection at 56.
            if (*(IntPtr*)(xevent + 56) != clipboard) return;
            lock (generationGate)
            {
                lastOwnerWasZetl = *(IntPtr*)(xevent + 48) == window;
                generation++;
                typesCache = null;
                Monitor.PulseAll(generationGate);
            }

            return;
        }

        switch (type)
        {
            case SelectionNotify:
                OnSelectionNotify(*(IntPtr*)(xevent + 48), *(IntPtr*)(xevent + 56));
                break;
            case PropertyNotify:
                OnPropertyNotify(*(IntPtr*)(xevent + 32), *(IntPtr*)(xevent + 40), *(int*)(xevent + 56));
                break;
            case SelectionRequest:
                OnSelectionRequest(
                    requestor: *(IntPtr*)(xevent + 40),
                    selection: *(IntPtr*)(xevent + 48),
                    target: *(IntPtr*)(xevent + 56),
                    property: *(IntPtr*)(xevent + 64),
                    time: *(IntPtr*)(xevent + 72));
                break;
            case SelectionClear:
                if (*(IntPtr*)(xevent + 40) == clipboard && owned is { } lost)
                {
                    owned = null;
                    lost.Replaced?.Invoke();
                }

                break;
        }
    }

    // ---- Reading: conversions into a property on Zetl's window ---------------

    private Task<Converted?> StartConversion(IntPtr target)
    {
        if (freeProperties.Count == 0)
        {
            return Task.FromResult<Converted?>(null);
        }

        var property = freeProperties.Pop();
        var conversion = new Conversion(target, property, DateTime.UtcNow + TransferTimeout);
        conversions[property] = conversion;
        XDeleteProperty(display, window, property);
        XConvertSelection(display, clipboard, target, property, window, IntPtr.Zero);
        return conversion.Done.Task;
    }

    private void OnSelectionNotify(IntPtr target, IntPtr property)
    {
        // A refusal names no property: it ends the waiting conversion of that target.
        if (property == IntPtr.Zero)
        {
            if (conversions.Values.FirstOrDefault(item => item.Target == target && item.Incremental is null) is { } refused)
            {
                Finish(refused, null);
            }

            return;
        }

        if (!conversions.TryGetValue(property, out var conversion) || conversion.Incremental is not null) return;
        if (ReadProperty(window, property, delete: true) is not { } value)
        {
            Finish(conversion, null);
            return;
        }

        if (value.Type == incrAtom)
        {
            // The owner sends the data in chunks, each after Zetl deletes the
            // property (which reading with delete just did).
            conversion.Incremental = new MemoryStream();
            conversion.Deadline = DateTime.UtcNow + TransferTimeout;
            return;
        }

        Finish(conversion, new Converted(value.Data));
    }

    private void OnPropertyNotify(IntPtr propertyWindow, IntPtr property, int state)
    {
        if (propertyWindow == window)
        {
            if (state != PropertyNewValue
                || !conversions.TryGetValue(property, out var conversion)
                || conversion.Incremental is not { } received)
            {
                return;
            }

            if (ReadProperty(window, property, delete: true) is not { } chunk)
            {
                Finish(conversion, null);
            }
            else if (chunk.Data.Length == 0)
            {
                Finish(conversion, new Converted(received.ToArray()));
            }
            else if (received.Length + chunk.Data.Length > MaxReadBytes)
            {
                Finish(conversion, null);
            }
            else
            {
                received.Write(chunk.Data);
                conversion.Deadline = DateTime.UtcNow + TransferTimeout;
            }

            return;
        }

        // A requestor took the last chunk of an incremental send: send the next.
        if (state == PropertyDelete && outgoing.TryGetValue((propertyWindow, property), out var transfer))
        {
            var chunkLength = Math.Min(IncrementalChunk, transfer.Data.Length - transfer.Sent);
            fixed (byte* bytes = transfer.Data)
            {
                XChangeProperty(display, propertyWindow, property, transfer.Type, 8, PropModeReplace,
                    bytes + transfer.Sent, chunkLength);
            }

            transfer.Sent += chunkLength;
            if (chunkLength == 0)
            {
                outgoing.Remove((propertyWindow, property));
                XSelectInput(display, propertyWindow, IntPtr.Zero);
            }
        }
    }

    private void Finish(Conversion conversion, Converted? result)
    {
        conversions.Remove(conversion.Property);
        XDeleteProperty(display, window, conversion.Property);
        freeProperties.Push(conversion.Property);
        conversion.Done.TrySetResult(result);
    }

    private void ExpireConversions()
    {
        var now = DateTime.UtcNow;
        foreach (var conversion in conversions.Values.Where(item => item.Deadline < now).ToList())
        {
            Finish(conversion, null);
        }
    }

    private (IntPtr Type, byte[] Data)? ReadProperty(IntPtr target, IntPtr property, bool delete)
    {
        if (XGetWindowProperty(display, target, property, IntPtr.Zero, MaxReadBytes / 4, delete ? 1 : 0,
                IntPtr.Zero, out var actualType, out var format, out var items, out _, out var data) != 0)
        {
            return null;
        }

        try
        {
            if (actualType == IntPtr.Zero) return null;
            // Format 32 items are C longs: 8 bytes each on 64-bit Linux.
            var itemSize = format switch { 32 => sizeof(IntPtr), 16 => 2, _ => 1 };
            var length = checked((int)items * itemSize);
            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(data, bytes, 0, length);
            return (actualType, bytes);
        }
        finally
        {
            if (data != IntPtr.Zero) XFree(data);
        }
    }

    private IReadOnlyList<string> AtomNames(byte[] targets)
    {
        var names = new List<string>();
        for (var offset = 0; offset + sizeof(IntPtr) <= targets.Length; offset += sizeof(IntPtr))
        {
            var atom = BitConverter.ToInt64(targets, offset);
            if (atom == 0) continue;
            var name = XGetAtomName(display, (IntPtr)atom);
            if (name == IntPtr.Zero) continue;
            names.Add(Marshal.PtrToStringUTF8(name) ?? "");
            XFree(name);
        }

        return names;
    }

    // ---- Owning: answering other clients ------------------------------------

    private void OnSelectionRequest(IntPtr requestor, IntPtr selection, IntPtr target, IntPtr property, IntPtr time)
    {
        // Obsolete clients name no property; ICCCM says to use the target.
        if (property == IntPtr.Zero) property = target;
        var answered = selection == clipboard && owned is { } source && Answer(source, requestor, target, property);
        SendSelectionNotify(requestor, selection, target, answered ? property : IntPtr.Zero, time);
    }

    private bool Answer(OwnedSelection source, IntPtr requestor, IntPtr target, IntPtr property)
    {
        if (target == targetsAtom)
        {
            var atoms = new List<long> { (long)targetsAtom, (long)timestampAtom };
            atoms.AddRange(source.ByTarget.Keys.Select(atom => (long)atom));
            var array = atoms.ToArray();
            fixed (long* values = array)
            {
                XChangeProperty(display, requestor, property, XaAtom, 32, PropModeReplace, (byte*)values, array.Length);
            }

            return true;
        }

        if (target == timestampAtom)
        {
            long now = 0;
            XChangeProperty(display, requestor, property, XaInteger, 32, PropModeReplace, (byte*)&now, 1);
            return true;
        }

        if (target == multipleAtom || !source.ByTarget.TryGetValue(target, out var entry))
        {
            return false;
        }

        source.Sent?.Invoke(entry.Type);
        if (entry.Data.Length > IncrementalThreshold)
        {
            // Announce the size, then send chunks as the requestor deletes each.
            XSelectInput(display, requestor, (IntPtr)PropertyChangeMask);
            long size = entry.Data.Length;
            XChangeProperty(display, requestor, property, incrAtom, 32, PropModeReplace, (byte*)&size, 1);
            outgoing[(requestor, property)] = new OutgoingTransfer(target, entry.Data);
            return true;
        }

        fixed (byte* bytes = entry.Data)
        {
            XChangeProperty(display, requestor, property, target, 8, PropModeReplace, bytes, entry.Data.Length);
        }

        return true;
    }

    private void SendSelectionNotify(IntPtr requestor, IntPtr selection, IntPtr target, IntPtr property, IntPtr time)
    {
        var xevent = stackalloc byte[192];
        new Span<byte>(xevent, 192).Clear();
        *(int*)xevent = SelectionNotify;
        *(int*)(xevent + 16) = 1; // send_event
        *(IntPtr*)(xevent + 24) = display;
        *(IntPtr*)(xevent + 32) = requestor;
        *(IntPtr*)(xevent + 40) = selection;
        *(IntPtr*)(xevent + 48) = target;
        *(IntPtr*)(xevent + 56) = property;
        *(IntPtr*)(xevent + 64) = time;
        XSendEvent(display, requestor, 0, IntPtr.Zero, xevent);
    }

    private IntPtr Atom(string name) => XInternAtom(display, name, 0);

    // ---- Errors -------------------------------------------------------------

    private static void InstallErrorHandler()
    {
        if (Interlocked.Exchange(ref errorHandlerInstalled, 1) == 1) return;
        previousErrorHandler = XSetErrorHandler(&OnXError);
    }

    // Errors on Zetl's clipboard connection (a requestor window that vanished,
    // say) are expected and harmless; anything else goes to the handler that was
    // there before (Avalonia's in Zetl).
    [UnmanagedCallersOnly]
    private static int OnXError(IntPtr errorDisplay, IntPtr errorEvent)
    {
        if (Connections.TryGetValue(errorDisplay, out var selection))
        {
            // XErrorEvent: error_code (a byte) at 32.
            selection.log($"Linux clipboard: ignored X error {Marshal.ReadByte(errorEvent, 32)}.");
            return 0;
        }

        return previousErrorHandler != IntPtr.Zero
            ? ((delegate* unmanaged<IntPtr, IntPtr, int>)previousErrorHandler)(errorDisplay, errorEvent)
            : 0;
    }

    private sealed record Converted(byte[] Data);

    private sealed class Conversion(IntPtr target, IntPtr property, DateTime deadline)
    {
        public IntPtr Target { get; } = target;
        public IntPtr Property { get; } = property;
        public DateTime Deadline { get; set; } = deadline;
        public MemoryStream? Incremental { get; set; }
        public TaskCompletionSource<Converted?> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record OwnedSelection(
        IReadOnlyDictionary<IntPtr, (string Type, byte[] Data)> ByTarget,
        Action<string>? Sent,
        Action? Replaced);

    private sealed class OutgoingTransfer(IntPtr type, byte[] data)
    {
        public IntPtr Type { get; } = type;
        public byte[] Data { get; } = data;
        public int Sent { get; set; }
    }

    // ---- libX11 / libXfixes ---------------------------------------------------

    private const string X11 = "libX11.so.6";
    private const string XFixes = "libXfixes.so.3";

    [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] private static extern int XConnectionNumber(IntPtr display);
    [DllImport(X11)] private static extern IntPtr XCreateSimpleWindow(
        IntPtr display, IntPtr parent, int x, int y, uint width, uint height, uint borderWidth, IntPtr border, IntPtr background);
    [DllImport(X11)] private static extern int XSelectInput(IntPtr display, IntPtr window, IntPtr eventMask);
    [DllImport(X11)] private static extern IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);
    [DllImport(X11)] private static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);
    [DllImport(X11)] private static extern int XFree(IntPtr data);
    [DllImport(X11)] private static extern int XFlush(IntPtr display);
    [DllImport(X11)] private static extern int XPending(IntPtr display);
    [DllImport(X11)] private static extern int XNextEvent(IntPtr display, byte* xevent);
    [DllImport(X11)] private static extern int XConvertSelection(
        IntPtr display, IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, IntPtr time);
    [DllImport(X11)] private static extern int XSetSelectionOwner(IntPtr display, IntPtr selection, IntPtr owner, IntPtr time);
    [DllImport(X11)] private static extern IntPtr XGetSelectionOwner(IntPtr display, IntPtr selection);
    [DllImport(X11)] private static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr longOffset, IntPtr longLength, int delete,
        IntPtr requestType, out IntPtr actualType, out int actualFormat, out IntPtr items, out IntPtr bytesAfter,
        out IntPtr data);
    [DllImport(X11)] private static extern int XChangeProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, byte* data, int elements);
    [DllImport(X11)] private static extern int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);
    [DllImport(X11)] private static extern int XSendEvent(IntPtr display, IntPtr window, int propagate, IntPtr eventMask, byte* xevent);
    [DllImport(X11)] private static extern IntPtr XSetErrorHandler(delegate* unmanaged<IntPtr, IntPtr, int> handler);
    [DllImport(XFixes)] private static extern int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(XFixes)] private static extern void XFixesSelectSelectionInput(
        IntPtr display, IntPtr window, IntPtr selection, ulong eventMask);
}
