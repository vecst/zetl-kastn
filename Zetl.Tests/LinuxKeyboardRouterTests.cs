using Chordl;
using Xunit;

namespace ZETL.Tests;

// Recorded evdev sequences through the device-free half of the Linux backend.
public sealed class LinuxKeyboardRouterTests
{
    private const ushort A = 30;
    private const ushort C = 46;
    private const ushort Esc = LinuxKeyMap.KeyEsc;
    private const ushort LeftCtrl = LinuxKeyMap.KeyLeftCtrl;
    private const ushort LeftShift = LinuxKeyMap.KeyLeftShift;
    private const ushort RightShift = LinuxKeyMap.KeyRightShift;
    private const ushort PlayPause = 164;
    private const ushort MediaUnmapped = 0x2a0;

    private readonly RecordingOutput output = new();
    private readonly List<(int Vk, bool Down, bool Up, bool Repeat)> seen = [];
    private readonly List<string> logs = [];
    private Func<int, bool, bool> suppress = (_, _) => false;

    private LinuxKeyboardRouter CreateRouter()
    {
        var router = new LinuxKeyboardRouter(output, (vk, down, up, repeat) =>
        {
            seen.Add((vk, down, up, repeat));
            return suppress(vk, down);
        }, logs.Add);
        router.AddSource(1);
        return router;
    }

    private static void Press(LinuxKeyboardRouter router, ushort key, int value, int source = 1)
    {
        router.Route(source, LinuxInputEvent.Key(key, value));
        router.Route(source, LinuxInputEvent.Synchronize());
    }

    [Fact]
    public void UnhandledKeysAreForwardedWithTheirFrames()
    {
        var router = CreateRouter();
        Press(router, A, 1);
        Press(router, A, 2);
        Press(router, A, 0);

        Assert.Equal(["A1", "SYN", "A2", "SYN", "A0", "SYN"], output.Describe());
        Assert.Equal([(0x41, true, false, false), (0x41, true, false, true), (0x41, false, true, false)], seen);
    }

    [Fact]
    public void SuppressedKeysAndTheirEmptyFramesStayOffTheDesktop()
    {
        suppress = (vk, _) => vk == ChordlKeys.VK_C;
        var router = CreateRouter();
        Press(router, LeftCtrl, 1);
        Press(router, C, 1);
        Press(router, C, 0);
        Press(router, LeftCtrl, 0);

        Assert.Equal(["LeftCtrl1", "SYN", "LeftCtrl0", "SYN"], output.Describe());
    }

    [Fact]
    public void ASwallowedRepeatOfAPassedKeyStopsTheDesktopsOwnRepeat()
    {
        // Chordl lets the first Ctrl+C through and swallows its repeats.
        suppress = (vk, _) => vk == ChordlKeys.VK_C && seen.Count(e => e.Vk == ChordlKeys.VK_C) > 1;
        var router = CreateRouter();
        Press(router, LeftCtrl, 1);
        Press(router, C, 1);
        Press(router, C, 2);
        Press(router, C, 2);
        Press(router, C, 0);

        Assert.Equal(["LeftCtrl1", "SYN", "C1", "SYN", "C0", "SYN"], output.Describe());
        Assert.Equal([LeftCtrl], router.OutputDown);
    }

    [Fact]
    public void AFailingHandlerFailsOpen()
    {
        var router = new LinuxKeyboardRouter(output, (_, _, _, _) => throw new InvalidOperationException("boom"), logs.Add);
        Press(router, A, 1);

        Assert.Equal(["A1", "SYN"], output.Describe());
        Assert.Contains(logs, line => line.Contains("failed open") && line.Contains("boom"));
    }

    [Fact]
    public void KeysWithoutAVirtualKeyBypassChordl()
    {
        var router = CreateRouter();
        Press(router, MediaUnmapped, 1);
        Press(router, PlayPause, 1);

        Assert.Equal([(0xB3, true, false, false)], seen);
        Assert.Equal([$"{MediaUnmapped}1", "SYN", $"{PlayPause}1", "SYN"], output.Describe());
    }

    [Fact]
    public void ChordAddsCtrlWhenNoneIsHeld()
    {
        var router = CreateRouter();
        Assert.True(router.SendChord(ChordlKeys.VK_C, includeShift: false));

        Assert.Equal(["LeftCtrl1", "SYN", "C1", "SYN", "C0", "SYN", "LeftCtrl0", "SYN"], output.Describe());
        Assert.Empty(router.OutputDown);
    }

    [Fact]
    public void ChordReusesAHeldCtrlAndLeavesItHeld()
    {
        var router = CreateRouter();
        Press(router, LeftCtrl, 1);
        output.Events.Clear();

        Assert.True(router.SendChord(ChordlKeys.VK_C, includeShift: false));

        Assert.Equal(["C1", "SYN", "C0", "SYN"], output.Describe());
        Assert.Equal([LeftCtrl], router.OutputDown);
    }

    [Fact]
    public void PlainChordLiftsAHeldShiftAndRestoresIt()
    {
        var router = CreateRouter();
        Press(router, LeftShift, 1);
        output.Events.Clear();

        Assert.True(router.SendChord(ChordlKeys.VK_C, includeShift: false));

        Assert.Equal(
            ["LeftCtrl1", "SYN", "LeftShift0", "SYN", "C1", "SYN", "C0", "SYN", "LeftShift1", "SYN", "LeftCtrl0", "SYN"],
            output.Describe());
        Assert.Equal([LeftShift], router.OutputDown);
    }

    [Fact]
    public void ShiftedChordAddsShiftOnlyForTheChord()
    {
        var router = CreateRouter();
        Assert.True(router.SendChord(ChordlKeys.VK_C, includeShift: true));

        Assert.Equal(
            ["LeftCtrl1", "SYN", "LeftShift1", "SYN", "C1", "SYN", "C0", "SYN", "LeftShift0", "SYN", "LeftCtrl0", "SYN"],
            output.Describe());
    }

    [Fact]
    public void ARemovedKeyboardReleasesItsKeysThroughChordl()
    {
        var router = CreateRouter();
        Press(router, LeftCtrl, 1);
        Press(router, A, 1);
        output.Events.Clear();
        seen.Clear();

        router.RemoveSource(1);

        Assert.Equal(["LeftCtrl0", "A0", "SYN"], output.Describe());
        Assert.Equal([(0xA2, false, true, false), (0x41, false, true, false)], seen);
        Assert.Empty(router.OutputDown);
    }

    [Fact]
    public void ResyncRestoresLostReleasesAndPresses()
    {
        var router = CreateRouter();
        Press(router, LeftCtrl, 1);
        output.Events.Clear();

        router.Resync(1, new HashSet<ushort> { A });

        Assert.Equal(["LeftCtrl0", "A1", "SYN"], output.Describe());
        Assert.Equal([A], router.OutputDown);
    }

    [Fact]
    public void PanicReleasesEveryKeyAndStopsRouting()
    {
        var router = CreateRouter();
        var panics = 0;
        router.PanicRequested += () => panics++;
        Press(router, LeftShift, 1);
        Press(router, RightShift, 1);
        output.Events.Clear();

        router.Route(1, LinuxInputEvent.Key(Esc, 1));
        Press(router, A, 1);

        Assert.Equal(1, panics);
        Assert.True(router.Released);
        Assert.Equal(["LeftShift0", "SYN", "RightShift0", "SYN"], output.Describe());
        Assert.Empty(router.OutputDown);
        Assert.False(router.SendChord(ChordlKeys.VK_C, includeShift: false));
    }

    [Fact]
    public void NothingIsWrittenAfterClose()
    {
        var router = CreateRouter();
        Press(router, A, 1);
        router.ReleaseAll();
        router.Close();
        output.Events.Clear();

        Press(router, A, 1);
        Assert.False(router.SendChord(ChordlKeys.VK_C, includeShift: false));
        Assert.Empty(output.Events);
    }

    [Fact]
    public void KeyMapRoundTripsTheKeysChordsUse()
    {
        foreach (var vk in new[] { ChordlKeys.VK_A, ChordlKeys.VK_C, ChordlKeys.VK_V, ChordlKeys.VK_X, ChordlKeys.VK_Z,
                     ChordlKeys.VK_LCONTROL, ChordlKeys.VK_RCONTROL, ChordlKeys.VK_LSHIFT, ChordlKeys.VK_RSHIFT })
        {
            Assert.True(LinuxKeyMap.TryGetLinuxKey(vk, out var key));
            Assert.True(LinuxKeyMap.TryGetVirtualKey(key, out var back));
            Assert.Equal(vk, back);
        }

        Assert.True(LinuxKeyMap.TryGetLinuxKey(0x0D, out var enter));
        Assert.Equal(28, enter); // main Enter, not keypad Enter
    }

    [Fact]
    public void SysfsBitmapsAreReadLeastSignificantWordLast()
    {
        var bits = LinuxKeyboardBackend.ParseBitmap("1000000000007 ff9f207ac14057ff febeffdfffefffff fffffffffffffffe");
        Assert.Equal(4, bits.Length);
        Assert.Equal(0xfffffffffffffffeUL, bits[0]);
        Assert.Equal(0x1000000000007UL, bits[3]);
        Assert.Empty(LinuxKeyboardBackend.ParseBitmap(""));
    }

    private sealed class RecordingOutput : ILinuxKeyboardOutput
    {
        public List<LinuxInputEvent> Events { get; } = [];

        public void Write(LinuxInputEvent inputEvent) => Events.Add(inputEvent);

        public List<string> Describe() => Events.Select(e => e.IsReport ? "SYN" : $"{Name(e.Code)}{e.Value}").ToList();

        private static string Name(ushort code) => code switch
        {
            A => "A",
            C => "C",
            LeftCtrl => "LeftCtrl",
            LeftShift => "LeftShift",
            RightShift => "RightShift",
            _ => code.ToString()
        };
    }
}
