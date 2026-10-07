using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// Linux global shortcuts by grab-and-forward: every physical keyboard is
/// exclusively grabbed (EVIOCGRAB) and each event Chordl does not suppress is
/// re-emitted through one uinput virtual keyboard, which is also where replayed
/// chords come from. See docs/linux-port.md for the model and safety rules.
///
/// Safety properties, each exercised on hardware before this left the spike:
/// a keyboard is grabbed only while no key on it is held, so the desktop never
/// keeps a key whose release Zetl swallowed; every exit path releases the keys
/// the virtual keyboard holds before the grabs end; a failing handler or
/// reader fails open; and holding both Shift keys while pressing Escape
/// releases every keyboard no matter what Chordl's state is.
/// </summary>
internal sealed class LinuxKeyboardBackend : IKeyboardBackend
{
    public const string DefaultVirtualDeviceName = "Zetl virtual keyboard";
    private const string InputDirectory = "/dev/input";
    private static readonly TimeSpan GrabSettle = TimeSpan.FromMilliseconds(100);

    private readonly Action<string> log;
    private readonly bool allowVirtualDevices;
    private readonly string virtualDeviceName;
    private readonly Func<string, bool> acceptDeviceName;
    private readonly Dictionary<string, Source> sources = [];
    private readonly HashSet<string> reportedDenied = [];
    private readonly List<PosixSignalRegistration> signalRegistrations = [];
    private LinuxKeyboardRouter? router;
    private int outputDescriptor = -1;
    private string? outputSysName;
    private (int Read, int Write) wake = (-1, -1);
    private int inotifyDescriptor = -1;
    private Thread? readerThread;
    private int nextSourceId;
    private volatile bool stopping;
    private int disposed;

    /// <param name="allowInjectedInputForTesting">
    /// Also grab virtual (uinput) keyboards. Zetl normally takes only physical
    /// keyboards, the counterpart of Windows ignoring injected input; tests drive
    /// it through synthetic keyboards instead.
    /// </param>
    /// <param name="acceptDeviceName">Limits which keyboards are taken, by name (tests).</param>
    public LinuxKeyboardBackend(
        Action<string> log,
        bool allowInjectedInputForTesting,
        string virtualDeviceName = DefaultVirtualDeviceName,
        Func<string, bool>? acceptDeviceName = null)
    {
        this.log = log;
        allowVirtualDevices = allowInjectedInputForTesting;
        this.virtualDeviceName = virtualDeviceName;
        this.acceptDeviceName = acceptDeviceName ?? (_ => true);
    }

    /// <summary>The kernel name of the virtual keyboard ("input42"), once started.</summary>
    public string? VirtualSysName => outputSysName;

    public bool Start(Func<int, bool, bool, bool, bool> handleKeyEvent)
    {
        if (!OperatingSystem.IsLinux() || router is not null) return false;

        outputDescriptor = LinuxEvdev.CreateVirtualKeyboard(virtualDeviceName);
        if (outputDescriptor < 0)
        {
            log($"Linux keyboard: can't create a virtual keyboard through /dev/uinput "
                + $"({LinuxEvdev.ErrorText(-outputDescriptor)}). Zetl needs write access to "
                + "/dev/uinput to forward keys; global shortcuts are off.");
            outputDescriptor = -1;
            return false;
        }

        outputSysName = LinuxEvdev.GetVirtualSysName(outputDescriptor);
        router = new LinuxKeyboardRouter(new DescriptorOutput(outputDescriptor), handleKeyEvent, log);
        router.PanicRequested += OnPanic;
        // Compile the chord path now rather than inside the first tap's key event.
        ZetlChordInjection.BuildCtrlChord(Chordl.ChordlKeys.VK_V, false, false, false, false, false);
        wake = LinuxEvdev.CreateWakePipe();
        inotifyDescriptor = LinuxEvdev.WatchDirectory(InputDirectory, LinuxEvdev.InCreate | LinuxEvdev.InAttrib);
        if (inotifyDescriptor < 0)
        {
            log("Linux keyboard: can't watch /dev/input; keyboards plugged in later need a restart.");
        }

        Scan();
        if (sources.Count == 0 && reportedDenied.Count > 0)
        {
            log("Linux keyboard: no keyboard could be opened; global shortcuts are off.");
            ReleaseEverything();
            return false;
        }

        if (sources.Count == 0)
        {
            log("Linux keyboard: no keyboard found yet; waiting for one to be connected.");
        }

        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT })
        {
            // Release before the default handling ends the process; never cancel it.
            signalRegistrations.Add(PosixSignalRegistration.Create(signal, _ => Dispose()));
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Zetl Linux keyboard"
        };
        readerThread.Start();
        return true;
    }

    public Task<bool> SendChord(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift) =>
        Task.FromResult(router?.SendChord(vkCode, includeShift) ?? false);

    public Task<bool> SendPaste() => SendChord(Chordl.ChordlKeys.VK_V, false, false, false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        stopping = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        if (wake.Write >= 0) LinuxEvdev.Signal(wake.Write);
        if (readerThread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }

        ReleaseEverything();
        foreach (var registration in signalRegistrations) registration.Dispose();
        signalRegistrations.Clear();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    private void ReadLoop()
    {
        var scratch = new byte[LinuxEvdev.InputEventSize * 64];
        var events = new List<LinuxInputEvent>(64);
        try
        {
            while (!stopping)
            {
                var grabbed = sources.Values.Where(source => source.Grabbed).ToList();
                var descriptors = new List<int> { wake.Read };
                if (inotifyDescriptor >= 0) descriptors.Add(inotifyDescriptor);
                descriptors.AddRange(grabbed.Select(source => source.Descriptor));
                var waiting = sources.Values.Any(source => !source.Grabbed);
                var ready = LinuxEvdev.Poll(descriptors, waiting ? 50 : -1);
                if (stopping) break;

                if (ready[0]) LinuxEvdev.Drain(wake.Read);
                if (inotifyDescriptor >= 0 && ready[1])
                {
                    LinuxEvdev.Drain(inotifyDescriptor);
                    Scan();
                }

                var offset = inotifyDescriptor >= 0 ? 2 : 1;
                for (var i = 0; i < grabbed.Count && !stopping; i++)
                {
                    if (ready[offset + i]) ReadSource(grabbed[i], scratch, events);
                }

                if (!stopping) GrabWaitingSources();
            }
        }
        catch (Exception ex)
        {
            // Fail open: hand every keyboard back to the desktop untouched.
            log($"Linux keyboard: reader failed ({ex.GetType().Name}: {ex.Message}); "
                + "keyboards released and global shortcuts are off.");
            stopping = true;
            ReleaseEverything();
        }
    }

    private void ReadSource(Source source, byte[] scratch, List<LinuxInputEvent> events)
    {
        var count = LinuxEvdev.ReadEvents(source.Descriptor, scratch, events);
        foreach (var inputEvent in events)
        {
            if (stopping) return;
            if (source.Dropping)
            {
                if (!inputEvent.IsReport) continue;
                source.Dropping = false;
                router!.Resync(source.Id, LinuxEvdev.GetPressedKeys(source.Descriptor) ?? []);
                continue;
            }

            if (inputEvent.IsDropped)
            {
                log($"Linux keyboard: events dropped on {source.Name}; resynchronizing.");
                source.Dropping = true;
                continue;
            }

            router!.Route(source.Id, inputEvent);
        }

        if (count < 0)
        {
            RemoveSource(source, LinuxEvdev.ErrorText(-count));
        }
    }

    /// <summary>
    /// Grabs each waiting keyboard once it has held no key for a short settle
    /// time. A keyboard with a key down is left alone: if Zetl grabbed it, the
    /// desktop would keep that key pressed forever, its release going to Zetl.
    /// </summary>
    private void GrabWaitingSources()
    {
        foreach (var source in sources.Values.Where(source => !source.Grabbed).ToList())
        {
            var pressed = LinuxEvdev.GetPressedKeys(source.Descriptor);
            if (pressed is null)
            {
                RemoveSource(source, "unreadable");
                continue;
            }

            if (pressed.Count > 0)
            {
                source.ClearSince = null;
                if (!source.AnnouncedWait)
                {
                    log($"Linux keyboard: waiting for keys to be released on {source.Name} before taking it.");
                    source.AnnouncedWait = true;
                }

                continue;
            }

            var now = Stopwatch.GetTimestamp();
            source.ClearSince ??= now;
            if (Stopwatch.GetElapsedTime(source.ClearSince.Value, now) < GrabSettle) continue;

            var error = LinuxEvdev.Grab(source.Descriptor, enabled: true);
            if (error != 0)
            {
                log($"Linux keyboard: can't take {source.Name} ({source.Node}): {LinuxEvdev.ErrorText(error)}. "
                    + "Its shortcuts are unavailable.");
                RemoveSource(source, reason: null);
                continue;
            }

            // A key pressed in the instant before the grab reached the desktop as a
            // press whose release would now come to Zetl. Let go and wait again.
            if (LinuxEvdev.GetPressedKeys(source.Descriptor) is not { Count: 0 })
            {
                LinuxEvdev.Grab(source.Descriptor, enabled: false);
                source.ClearSince = null;
                continue;
            }

            // Whatever is queued was delivered to the desktop before the grab.
            LinuxEvdev.ReadEvents(source.Descriptor, new byte[LinuxEvdev.InputEventSize * 64], []);
            source.Grabbed = true;
            router!.AddSource(source.Id);
            log($"Linux keyboard: took {source.Name} ({source.Node}).");
        }
    }

    private void Scan()
    {
        IEnumerable<string> nodes;
        try
        {
            nodes = Directory.EnumerateFiles(InputDirectory, "event*").Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (IOException ex)
        {
            log($"Linux keyboard: can't list {InputDirectory}: {ex.Message}");
            return;
        }

        foreach (var node in nodes.Order(StringComparer.Ordinal))
        {
            if (stopping || sources.ContainsKey(node) || !IsCandidate(node, out var name)) continue;

            var path = Path.Combine(InputDirectory, node);
            var descriptor = LinuxEvdev.OpenDevice(path);
            if (descriptor < 0)
            {
                // udev may still be applying permissions; a later attribute change
                // rescans. Report each refusal once.
                if (reportedDenied.Add(node))
                {
                    log($"Linux keyboard: can't open {path} ({name}): {LinuxEvdev.ErrorText(-descriptor)}. "
                        + "Zetl needs read/write access to keyboard devices (usually the 'input' group).");
                }

                continue;
            }

            if (!LinuxEvdev.IsKeyboard(descriptor))
            {
                LinuxEvdev.Close(descriptor);
                continue;
            }

            reportedDenied.Remove(node);
            sources[node] = new Source(nextSourceId++, node, name, descriptor);
        }
    }

    /// <summary>
    /// Decides from sysfs, without opening the device, whether a node is a
    /// keyboard Zetl should take: never its own virtual keyboard, and virtual
    /// devices only when testing.
    /// </summary>
    private bool IsCandidate(string node, out string name)
    {
        var device = $"/sys/class/input/{node}/device";
        name = ReadSysfs($"{device}/name") ?? node;
        if (outputSysName is not null && Directory.Exists($"/sys/class/input/{outputSysName}/{node}")) return false;
        if (name == virtualDeviceName) return false;
        if (!acceptDeviceName(name)) return false;
        if (!allowVirtualDevices && IsVirtual(device)) return false;

        var keys = ParseBitmap(ReadSysfs($"{device}/capabilities/key"));
        var types = ParseBitmap(ReadSysfs($"{device}/capabilities/ev"));
        return HasBit(types, LinuxEvdev.EvKey)
            && !HasBit(types, LinuxEvdev.EvRel)
            && !HasBit(types, LinuxEvdev.EvAbs)
            && HasBit(keys, LinuxKeyMap.KeyA)
            && HasBit(keys, LinuxKeyMap.KeyC)
            && HasBit(keys, LinuxKeyMap.KeyLeftCtrl);
    }

    private static bool IsVirtual(string device)
    {
        try
        {
            var target = new DirectoryInfo(device).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName.Contains("/devices/virtual/", StringComparison.Ordinal) == true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? ReadSysfs(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a sysfs capability bitmap: hexadecimal words of 64 bits, most
    /// significant first, so the last word holds bits 0-63.
    /// </summary>
    internal static ulong[] ParseBitmap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bits = new ulong[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            bits[words.Length - 1 - i] = Convert.ToUInt64(words[i], 16);
        }

        return bits;
    }

    private static bool HasBit(ulong[] bits, int bit) =>
        bit / 64 < bits.Length && (bits[bit / 64] & (1UL << (bit % 64))) != 0;

    private void RemoveSource(Source source, string? reason)
    {
        if (source.Grabbed)
        {
            router?.RemoveSource(source.Id);
            LinuxEvdev.Grab(source.Descriptor, enabled: false);
        }

        LinuxEvdev.Close(source.Descriptor);
        sources.Remove(source.Node);
        if (reason is not null) log($"Linux keyboard: {source.Name} ({source.Node}) is gone ({reason}).");
    }

    private void OnPanic()
    {
        stopping = true;
        foreach (var source in sources.Values.ToList())
        {
            if (source.Grabbed) LinuxEvdev.Grab(source.Descriptor, enabled: false);
            LinuxEvdev.Close(source.Descriptor);
        }

        sources.Clear();
        log("Linux keyboard: every keyboard released; restart Zetl to turn global shortcuts back on.");
    }

    /// <summary>
    /// Every exit path ends here: release the keys the virtual keyboard holds,
    /// let the desktop process those releases, then end the grabs and destroy
    /// the virtual keyboard. Idempotent.
    /// </summary>
    private void ReleaseEverything()
    {
        lock (sources)
        {
            if (router is not null && outputDescriptor >= 0)
            {
                router.ReleaseAll();
                router.Close();
                Thread.Sleep(50);
            }

            foreach (var source in sources.Values)
            {
                if (source.Grabbed) LinuxEvdev.Grab(source.Descriptor, enabled: false);
                LinuxEvdev.Close(source.Descriptor);
            }

            sources.Clear();
            LinuxEvdev.DestroyVirtualKeyboard(outputDescriptor);
            outputDescriptor = -1;
            LinuxEvdev.Close(inotifyDescriptor);
            inotifyDescriptor = -1;
            LinuxEvdev.Close(wake.Read);
            LinuxEvdev.Close(wake.Write);
            wake = (-1, -1);
        }
    }

    private sealed class Source(int id, string node, string name, int descriptor)
    {
        public int Id { get; } = id;
        public string Node { get; } = node;
        public string Name { get; } = name;
        public int Descriptor { get; } = descriptor;
        public bool Grabbed { get; set; }
        public bool Dropping { get; set; }
        public bool AnnouncedWait { get; set; }
        public long? ClearSince { get; set; }
    }

    private sealed class DescriptorOutput(int descriptor) : ILinuxKeyboardOutput
    {
        public void Write(LinuxInputEvent inputEvent) => LinuxEvdev.Write(descriptor, inputEvent);
    }
}
