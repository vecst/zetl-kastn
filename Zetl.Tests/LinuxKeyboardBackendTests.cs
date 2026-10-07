using Chordl;
using Xunit;

namespace ZETL.Tests;

/// <summary>Runs only on Linux with write access to /dev/uinput.</summary>
public sealed class LinuxUinputFactAttribute : FactAttribute
{
    public LinuxUinputFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs Linux evdev/uinput.";
            return;
        }

        try
        {
            using var _ = File.OpenHandle("/dev/uinput", FileMode.Open, FileAccess.Write);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip = "Needs write access to /dev/uinput.";
        }
    }
}

/// <summary>
/// The Linux keyboard backend against real kernel devices. Each test drives a
/// synthetic uinput keyboard that only its backend may take, and the test
/// itself grabs the backend's virtual keyboard, so no test keystroke reaches
/// the desktop of the machine running the suite. Keys pressed before the
/// backend takes a keyboard do reach the desktop, so those are modifiers only.
/// </summary>
[Collection(RealTimeCollection.Name)]
public sealed class LinuxKeyboardBackendTests : IDisposable
{
    private const ushort A = 30;
    private const ushort C = 46;
    private const ushort LeftCtrl = LinuxKeyMap.KeyLeftCtrl;
    private const ushort LeftShift = LinuxKeyMap.KeyLeftShift;
    private const ushort RightShift = LinuxKeyMap.KeyRightShift;
    private const ushort Esc = LinuxKeyMap.KeyEsc;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string prefix = $"Zetl test {Guid.NewGuid():N}"[..20];
    private readonly List<string> logs = [];
    private readonly List<FakeKeyboard> fakes = [];
    private LinuxKeyboardBackend? backend;
    private OutputReader? output;

    public void Dispose()
    {
        backend?.Dispose();
        output?.Dispose();
        foreach (var fake in fakes) fake.Dispose();
    }

    [LinuxUinputFact]
    public void ForwardsTypingAndRoutesTapsAndHoldsThroughChordl()
    {
        var keyboard = AddFake("main");
        var holds = 0;
        ChordlProcessor? processor = null;
        processor = new ChordlProcessor(
            new Dictionary<ChordlChord, ChordlAction>
            {
                [new ChordlChord(ChordlKeys.VK_C, Ctrl: true, Shift: false)] =
                    new("Ctrl+C", ChordlDispatchMode.TapOnly, ReplayShift: false)
            },
            [ChordlKeys.VK_C],
            TimeSpan.FromMilliseconds(33),
            TimeSpan.FromMilliseconds(250),
            (vk, shift, _, _) => backend!.SendChord(vk, shift, false, false),
            _ => { },
            _ => false,
            _ => Interlocked.Increment(ref holds),
            _ => { },
            () => 0);
        StartBackend((vk, down, up, repeat) => processor.HandleKeyEvent(vk, down, up, repeat), keyboard);

        keyboard.Tap(A);
        Assert.Equal(["A1", "A0"], output!.ReadKeys(2));

        // Tap: Chordl holds C back, then replays Ctrl+C on release.
        keyboard.Press(LeftCtrl, 1);
        keyboard.Press(C, 1);
        keyboard.Press(C, 0);
        keyboard.Press(LeftCtrl, 0);
        Assert.Equal(["LeftCtrl1", "C1", "C0", "LeftCtrl0"], output.ReadKeys(4));

        // Hold: the shortcut fires and C never reaches the desktop.
        keyboard.Press(LeftCtrl, 1);
        keyboard.Press(C, 1);
        Thread.Sleep(450);
        keyboard.Press(C, 0);
        keyboard.Press(LeftCtrl, 0);
        Assert.Equal(["LeftCtrl1", "LeftCtrl0"], output.ReadKeys(2));
        Assert.Equal(1, holds);
        Assert.Empty(output.ReadKeys(1, TimeSpan.FromMilliseconds(300)));
        processor.Dispose();
    }

    [LinuxUinputFact]
    public void WaitsForHeldKeysBeforeTakingAKeyboard()
    {
        var keyboard = AddFake("held");
        keyboard.Press(LeftCtrl, 1);
        StartBackend((_, _, _, _) => false, keyboard, waitForTake: false);

        WaitForLog("waiting for keys to be released");
        Thread.Sleep(300);
        Assert.DoesNotContain(Logs(), line => line.Contains("took"));

        keyboard.Press(LeftCtrl, 0);
        WaitForLog($"took {keyboard.Name}");
        keyboard.Tap(A);
        Assert.Equal(["A1", "A0"], output!.ReadKeys(2));
    }

    [LinuxUinputFact]
    public void TakesKeyboardsPluggedInLaterAndReleasesKeysOfRemovedOnes()
    {
        var first = AddFake("first");
        StartBackend((_, _, _, _) => false, first);

        var second = AddFake("second");
        WaitForLog($"took {second.Name}");
        second.Press(LeftShift, 1);
        Assert.Equal(["LeftShift1"], output!.ReadKeys(1));

        second.Dispose();
        Assert.Equal(["LeftShift0"], output.ReadKeys(1));
        WaitForLog("is gone");

        first.Tap(A);
        Assert.Equal(["A1", "A0"], output.ReadKeys(2));
    }

    [LinuxUinputFact]
    public void AFailingHandlerLetsKeysThrough()
    {
        var keyboard = AddFake("faulty");
        StartBackend((_, _, _, _) => throw new InvalidOperationException("handler fault"), keyboard);

        keyboard.Tap(A);
        Assert.Equal(["A1", "A0"], output!.ReadKeys(2));
        Assert.Contains(Logs(), line => line.Contains("failed open"));
    }

    [LinuxUinputFact]
    public void DisposeReleasesHeldKeysAndTheGrab()
    {
        var keyboard = AddFake("dispose");
        StartBackend((_, _, _, _) => false, keyboard);
        keyboard.Press(LeftCtrl, 1);
        Assert.Equal(["LeftCtrl1"], output!.ReadKeys(1));

        // Read while disposing, as the desktop does: once the virtual keyboard is
        // destroyed, evdev refuses reads even of events still queued.
        var disposing = Task.Run(backend!.Dispose);
        Assert.Equal(["LeftCtrl0"], output.ReadKeys(1));
        disposing.Wait();
        Assert.True(keyboard.IsFree(), "The keyboard should be ungrabbed after dispose.");
        keyboard.Press(LeftCtrl, 0);
    }

    [LinuxUinputFact]
    public void PanicChordReleasesEverything()
    {
        var keyboard = AddFake("panic");
        StartBackend((_, _, _, _) => false, keyboard);
        keyboard.Press(LeftShift, 1);
        keyboard.Press(RightShift, 1);
        Assert.Equal(["LeftShift1", "RightShift1"], output!.ReadKeys(2));

        keyboard.Press(Esc, 1);

        Assert.Equal(["LeftShift0", "RightShift0"], output.ReadKeys(2));
        WaitForLog("every keyboard released");
        Assert.True(keyboard.IsFree(), "The panic chord should release the grab.");
        Assert.False(backend!.SendChord(ChordlKeys.VK_C, false, false, false).Result);
        // These releases now reach the desktop: Escape and Shift releases only.
        keyboard.Press(Esc, 0);
        keyboard.Press(RightShift, 0);
        keyboard.Press(LeftShift, 0);
    }

    private FakeKeyboard AddFake(string role)
    {
        var fake = new FakeKeyboard($"{prefix} {role}");
        fakes.Add(fake);
        return fake;
    }

    private void StartBackend(Func<int, bool, bool, bool, bool> handler, FakeKeyboard keyboard, bool waitForTake = true)
    {
        backend = new LinuxKeyboardBackend(
            line => { lock (logs) logs.Add(line); },
            allowInjectedInputForTesting: true,
            virtualDeviceName: $"{prefix} output",
            acceptDeviceName: name => name.StartsWith(prefix, StringComparison.Ordinal) && !name.EndsWith(" output"));
        Assert.True(backend.Start(handler), string.Join("\n", Logs()));
        output = new OutputReader(backend.VirtualSysName!);
        if (waitForTake) WaitForLog($"took {keyboard.Name}");
    }

    private List<string> Logs()
    {
        lock (logs) return logs.ToList();
    }

    private void WaitForLog(string fragment)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Logs().Any(line => line.Contains(fragment, StringComparison.Ordinal))) return;
            Thread.Sleep(20);
        }

        Assert.Fail($"No log line containing '{fragment}'. Log:\n{string.Join("\n", Logs())}");
    }

    private static string Describe(LinuxInputEvent e) => e.Code switch
    {
        A => "A",
        C => "C",
        LeftCtrl => "LeftCtrl",
        LeftShift => "LeftShift",
        RightShift => "RightShift",
        Esc => "Esc",
        _ => e.Code.ToString()
    } + e.Value;

    /// <summary>A uinput keyboard standing in for a physical one.</summary>
    private sealed class FakeKeyboard : IDisposable
    {
        private int descriptor;

        public FakeKeyboard(string name)
        {
            Name = name;
            descriptor = LinuxEvdev.CreateVirtualKeyboard(name);
            Assert.True(descriptor >= 0, $"uinput: {LinuxPosix.ErrorText(-descriptor)}");
            var sysName = LinuxEvdev.GetVirtualSysName(descriptor)!;
            Node = WaitForEventNode(sysName);
        }

        public string Name { get; }

        public string Node { get; }

        public void Press(ushort key, int value)
        {
            LinuxEvdev.Write(descriptor, LinuxInputEvent.Key(key, value));
            LinuxEvdev.Write(descriptor, LinuxInputEvent.Synchronize());
            Thread.Sleep(15);
        }

        public void Tap(ushort key)
        {
            Press(key, 1);
            Press(key, 0);
        }

        /// <summary>True when nobody holds an exclusive grab on this keyboard.</summary>
        public bool IsFree()
        {
            var probe = LinuxEvdev.OpenDevice(Node);
            Assert.True(probe >= 0);
            try
            {
                if (LinuxEvdev.Grab(probe, enabled: true) != 0) return false;
                LinuxEvdev.Grab(probe, enabled: false);
                return true;
            }
            finally
            {
                LinuxPosix.Close(probe);
            }
        }

        public void Dispose()
        {
            LinuxEvdev.DestroyVirtualKeyboard(descriptor);
            descriptor = -1;
        }
    }

    /// <summary>Grabs the backend's virtual keyboard and reads what it emits.</summary>
    private sealed class OutputReader : IDisposable
    {
        private readonly int descriptor;
        private readonly Queue<string> pending = [];
        private readonly byte[] scratch = new byte[LinuxEvdev.InputEventSize * 64];

        public OutputReader(string sysName)
        {
            descriptor = LinuxEvdev.OpenDevice(WaitForEventNode(sysName));
            Assert.True(descriptor >= 0, "Could not open the backend's virtual keyboard.");
            Assert.Equal(0, LinuxEvdev.Grab(descriptor, enabled: true));
        }

        /// <summary>The next <paramref name="count"/> key events, or fewer at the timeout.</summary>
        public List<string> ReadKeys(int count, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? Timeout);
            var events = new List<LinuxInputEvent>();
            while (pending.Count < count && DateTime.UtcNow < deadline)
            {
                if (!LinuxPosix.Poll([descriptor], 50)[0]) continue;
                if (LinuxEvdev.ReadEvents(descriptor, scratch, events) < 0) break;
                foreach (var e in events.Where(e => e.IsKey)) pending.Enqueue(Describe(e));
            }

            var keys = new List<string>();
            while (keys.Count < count && pending.Count > 0) keys.Add(pending.Dequeue());
            return keys;
        }

        public void Dispose() => LinuxPosix.Close(descriptor);
    }

    /// <summary>The device node of a kernel input device, once this user can open it.</summary>
    private static string WaitForEventNode(string sysName)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var directory = $"/sys/class/input/{sysName}";
            var node = Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory, "event*").Select(Path.GetFileName).FirstOrDefault()
                : null;
            // udev grants the input group access a moment after the node appears.
            if (node is not null && LinuxEvdev.OpenDevice($"/dev/input/{node}") is >= 0 and var probe)
            {
                LinuxPosix.Close(probe);
                return $"/dev/input/{node}";
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException($"No event node appeared for {sysName}.");
    }
}
