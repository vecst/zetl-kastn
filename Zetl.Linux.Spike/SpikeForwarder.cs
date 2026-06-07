using Chordl;

namespace ZETL.LinuxSpike;

internal sealed class SpikeForwarder : IDisposable
{
    private const string VirtualDeviceName = "Zetl B1 Safety Forwarder";
    private readonly string devicePath;
    private readonly TimeSpan duration;
    private readonly bool traceEvents;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object releaseGate = new();
    private readonly object outputGate = new();
    private readonly HashSet<ushort> virtualKeysDown = [];
    private int sourceFileDescriptor = -1;
    private int virtualFileDescriptor = -1;
    private bool grabbed;
    private bool disposed;
    private bool leftShiftDown;
    private bool rightShiftDown;
    private long readEvents;
    private long forwardedEvents;
    private long suppressedEvents;
    private ChordlProcessor? processor;

    public SpikeForwarder(
        string devicePath,
        TimeSpan duration,
        bool traceEvents)
    {
        this.devicePath = devicePath;
        this.duration = duration;
        this.traceEvents = traceEvents;
    }

    public int Run()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            sourceFileDescriptor = LinuxInput.OpenReadWrite(devicePath);
            if (!LinuxInput.IsKeyboard(sourceFileDescriptor))
            {
                throw new InvalidOperationException(
                    $"{devicePath} does not expose normal keyboard capabilities.");
            }

            virtualFileDescriptor = LinuxInput.CreateVirtualKeyboard(
                VirtualDeviceName);
            processor = CreateProcessor();
            using var safetyTimer = new Timer(_ =>
            {
                Console.WriteLine("Safety timeout reached; releasing keyboard.");
                cancellation.Cancel();
            }, null, duration, Timeout.InfiniteTimeSpan);

            Console.WriteLine($"Source: {devicePath}");
            Console.WriteLine($"Name: {LinuxInput.GetDeviceName(sourceFileDescriptor)}");
            Console.WriteLine($"Armed for {duration.TotalSeconds:0} seconds.");
            Console.WriteLine("Panic chord: hold both Shift keys and press Escape.");
            Console.WriteLine("SSH recovery: kill this process; closing the fd releases EVIOCGRAB.");

            WaitForAllKeysReleased();
            if (!LinuxInput.Grab(sourceFileDescriptor, enabled: true))
            {
                throw new IOException(
                    $"EVIOCGRAB failed with errno {LinuxInput.LastError}.");
            }

            grabbed = true;
            Console.WriteLine("GRAB ACTIVE");
            ForwardLoop();
            Console.WriteLine(
                $"Summary: read={readEvents}, forwarded={forwardedEvents}, suppressed={suppressedEvents}");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        processor?.Dispose();
        processor = null;
        try
        {
            ReleaseVirtualKeys();
            Thread.Sleep(75);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"WARNING: virtual key release failed: {ex.Message}");
        }
        finally
        {
            ReleaseGrab();
            Thread.Sleep(25);
            LinuxInput.DestroyVirtualKeyboard(virtualFileDescriptor);
            virtualFileDescriptor = -1;
            LinuxInput.Close(sourceFileDescriptor);
            sourceFileDescriptor = -1;
            cancellation.Dispose();
        }
    }

    private void ForwardLoop()
    {
        var buffer = new byte[LinuxInput.InputEventSize];
        while (!cancellation.IsCancellationRequested)
        {
            if (!LinuxInput.WaitReadable(sourceFileDescriptor, 100))
            {
                continue;
            }

            var readCount = LinuxInput.ReadEvent(
                sourceFileDescriptor,
                buffer);
            if (readCount < 0)
            {
                var error = LinuxInput.LastError;
                if (error is 11 or 4)
                {
                    continue;
                }

                throw new IOException(
                    $"evdev read failed with errno {error}.");
            }

            if (readCount != LinuxInput.InputEventSize)
            {
                continue;
            }

            var inputEvent = InputEvent.FromBytes(buffer);
            readEvents++;
            if (traceEvents && inputEvent.Type == LinuxInput.EvKey)
            {
                Console.WriteLine(
                    $"EVENT code={inputEvent.Code} value={inputEvent.Value}");
            }

            if (inputEvent.Type == LinuxInput.EvSyn
                && inputEvent.Code == LinuxInput.SynDropped)
            {
                Console.WriteLine("SYN_DROPPED received; stopping the safety spike.");
                cancellation.Cancel();
                continue;
            }

            if (IsPanic(inputEvent))
            {
                Console.WriteLine("Panic chord received; releasing keyboard.");
                cancellation.Cancel();
                continue;
            }

            var suppress = inputEvent.Type == LinuxInput.EvKey
                && TryMapKey(inputEvent.Code, out var virtualKey)
                && processor?.HandleKeyEvent(
                    virtualKey,
                    isKeyDown: inputEvent.Value is 1 or 2,
                    isKeyUp: inputEvent.Value == 0) == true;
            if (!suppress)
            {
                WriteVirtualEvent(inputEvent);
                forwardedEvents++;
            }
            else
            {
                suppressedEvents++;
            }
        }
    }

    private ChordlProcessor CreateProcessor()
    {
        var actions = new Dictionary<ChordlChord, ChordlAction>
        {
            [new ChordlChord(
                ChordlKeys.VK_C,
                Ctrl: true,
                Shift: false)] = new(
                    "Ctrl+C",
                    ChordlDispatchMode.TapOnly,
                    ReplayShift: false),
            [new ChordlChord(
                ChordlKeys.VK_C,
                Ctrl: true,
                Shift: true)] = new(
                    "Ctrl+Shift+C",
                    ChordlDispatchMode.TapOnly,
                    ReplayShift: true)
        };
        return new ChordlProcessor(
            actions,
            [ChordlKeys.VK_C],
            TimeSpan.FromMilliseconds(33),
            TimeSpan.FromMilliseconds(353),
            SendChord,
            context => Console.WriteLine(
                $"PASSTHROUGH {context.Name}"),
            context =>
            {
                Console.WriteLine($"TAP {context.Name}");
                return false;
            },
            context => Console.WriteLine($"HOLD {context.Name}"),
            message => Console.WriteLine($"CHORDL {message}"),
            () => 0);
    }

    private void SendChord(
        int virtualKey,
        bool includeShift,
        bool restoreCtrl,
        bool restoreShift)
    {
        if (!TryMapVirtualKey(virtualKey, out var key))
        {
            return;
        }

        if (!restoreCtrl)
        {
            EmitKey(LinuxInput.KeyLeftCtrl, 1);
        }

        if (includeShift && !restoreShift)
        {
            EmitKey(LinuxInput.KeyLeftShift, 1);
        }

        EmitKey(key, 1);
        EmitKey(key, 0);

        if (includeShift && !restoreShift)
        {
            EmitKey(LinuxInput.KeyLeftShift, 0);
        }

        if (!restoreCtrl)
        {
            EmitKey(LinuxInput.KeyLeftCtrl, 0);
        }

        WriteVirtualEvent(InputEvent.Synchronize());
    }

    private void EmitKey(ushort key, int value)
    {
        WriteVirtualEvent(InputEvent.Key(key, value));
    }

    private bool IsPanic(InputEvent inputEvent)
    {
        if (inputEvent.Type != LinuxInput.EvKey)
        {
            return false;
        }

        if (inputEvent.Code == LinuxInput.KeyLeftShift)
        {
            leftShiftDown = inputEvent.Value != 0;
        }
        else if (inputEvent.Code == LinuxInput.KeyRightShift)
        {
            rightShiftDown = inputEvent.Value != 0;
        }

        return inputEvent.Code == LinuxInput.KeyEsc
            && inputEvent.Value == 1
            && leftShiftDown
            && rightShiftDown;
    }

    private void ReleaseGrab()
    {
        lock (releaseGate)
        {
            if (!grabbed)
            {
                return;
            }

            LinuxInput.Grab(sourceFileDescriptor, enabled: false);
            grabbed = false;
        }
    }

    private void ReleaseVirtualKeys()
    {
        if (virtualFileDescriptor < 0)
        {
            return;
        }

        lock (outputGate)
        {
            var keysToRelease = virtualKeysDown
                .Concat(new[]
                {
                    LinuxInput.KeyLeftCtrl,
                    LinuxInput.KeyRightCtrl,
                    LinuxInput.KeyLeftShift,
                    LinuxInput.KeyRightShift
                })
                .Distinct()
                .ToArray();
            foreach (var key in keysToRelease)
            {
                LinuxInput.WriteEvent(
                    virtualFileDescriptor,
                    InputEvent.Key(key, 0));
            }

            LinuxInput.WriteEvent(
                virtualFileDescriptor,
                InputEvent.Synchronize());
            virtualKeysDown.Clear();
        }
    }

    private void OnCancelKeyPress(
        object? sender,
        ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        cancellation.Cancel();
    }

    private void WaitForAllKeysReleased()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var announced = false;
        while (true)
        {
            var pressedKeys = LinuxInput.GetPressedKeys(sourceFileDescriptor);
            if (pressedKeys.Count == 0)
            {
                Thread.Sleep(100);
                if (LinuxInput.GetPressedKeys(sourceFileDescriptor).Count == 0)
                {
                    return;
                }
            }
            else if (!announced)
            {
                Console.WriteLine(
                    $"Waiting for all physical keys to be released before grab (pressed: {string.Join(", ", pressedKeys)})...");
                announced = true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "Keyboard still has pressed keys after 10 seconds; refusing to grab.");
            }

            Thread.Sleep(50);
        }
    }

    private void WriteVirtualEvent(InputEvent inputEvent)
    {
        lock (outputGate)
        {
            LinuxInput.WriteEvent(
                virtualFileDescriptor,
                inputEvent);
            if (inputEvent.Type != LinuxInput.EvKey)
            {
                return;
            }

            if (inputEvent.Value == 0)
            {
                virtualKeysDown.Remove(inputEvent.Code);
            }
            else
            {
                virtualKeysDown.Add(inputEvent.Code);
            }
        }
    }

    private static bool TryMapKey(ushort linuxKey, out int virtualKey)
    {
        virtualKey = linuxKey switch
        {
            LinuxInput.KeyLeftCtrl => ChordlKeys.VK_LCONTROL,
            LinuxInput.KeyRightCtrl => ChordlKeys.VK_RCONTROL,
            LinuxInput.KeyLeftShift => ChordlKeys.VK_LSHIFT,
            LinuxInput.KeyRightShift => ChordlKeys.VK_RSHIFT,
            LinuxInput.KeyB => ChordlKeys.VK_B,
            LinuxInput.KeyC => ChordlKeys.VK_C,
            LinuxInput.KeyP => ChordlKeys.VK_P,
            LinuxInput.KeyR => ChordlKeys.VK_R,
            LinuxInput.KeyV => ChordlKeys.VK_V,
            LinuxInput.KeyX => ChordlKeys.VK_X,
            LinuxInput.KeyZ => ChordlKeys.VK_Z,
            _ => 0
        };
        return virtualKey != 0;
    }

    private static bool TryMapVirtualKey(
        int virtualKey,
        out ushort linuxKey)
    {
        linuxKey = virtualKey switch
        {
            ChordlKeys.VK_B => LinuxInput.KeyB,
            ChordlKeys.VK_C => LinuxInput.KeyC,
            ChordlKeys.VK_P => LinuxInput.KeyP,
            ChordlKeys.VK_R => LinuxInput.KeyR,
            ChordlKeys.VK_V => LinuxInput.KeyV,
            ChordlKeys.VK_X => LinuxInput.KeyX,
            ChordlKeys.VK_Z => LinuxInput.KeyZ,
            _ => (ushort)0
        };
        return linuxKey != 0;
    }
}
