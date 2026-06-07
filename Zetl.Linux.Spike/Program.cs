namespace ZETL.LinuxSpike;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine(
                "Zetl.Linux.Spike only runs on Linux.");
            return 2;
        }

        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "diagnose" => Diagnose(args),
                "observe" => Observe(args),
                "uinput-test" => UinputTest(),
                "forward" => Forward(args),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int Diagnose(string[] args)
    {
        Console.WriteLine("Zetl B1 Linux input diagnostic");
        Console.WriteLine($"User: {Environment.UserName}");
        Console.WriteLine($"OS: {Environment.OSVersion}");
        Console.WriteLine();

        var paths = ReadValue(args, "--device=") is { } selected
            ? [selected]
            : DiscoverDevices();
        foreach (var path in paths)
        {
            var fileDescriptor = -1;
            try
            {
                fileDescriptor = LinuxInput.OpenReadOnly(path);
                Console.WriteLine(
                    $"{path}\n  name: {LinuxInput.GetDeviceName(fileDescriptor)}\n  keyboard: {LinuxInput.IsKeyboard(fileDescriptor)}\n  readable: true");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"{path}\n  readable: false\n  error: {ex.Message}");
            }
            finally
            {
                LinuxInput.Close(fileDescriptor);
            }
        }

        var uinputFileDescriptor = -1;
        try
        {
            uinputFileDescriptor = LinuxInput.OpenUinput();
            Console.WriteLine("\n/dev/uinput\n  writable: true");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"\n/dev/uinput\n  writable: false\n  error: {ex.Message}");
            return 1;
        }
        finally
        {
            LinuxInput.Close(uinputFileDescriptor);
        }

        return 0;
    }

    private static int Observe(string[] args)
    {
        var device = ReadStableDevice(args);
        var seconds = ReadSeconds(args, defaultSeconds: 10, maximum: 60);
        var fileDescriptor = -1;
        try
        {
            fileDescriptor = LinuxInput.OpenReadOnly(device);
            Console.WriteLine(
                $"Observing {LinuxInput.GetDeviceName(fileDescriptor)} for {seconds} seconds without EVIOCGRAB.");
            Console.WriteLine("Key events remain available to the desktop.");
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var buffer = new byte[LinuxInput.InputEventSize];
            while (DateTime.UtcNow < deadline)
            {
                if (!LinuxInput.WaitReadable(fileDescriptor, 100))
                {
                    continue;
                }

                var readCount = LinuxInput.ReadEvent(fileDescriptor, buffer);
                if (readCount != LinuxInput.InputEventSize)
                {
                    continue;
                }

                var inputEvent = InputEvent.FromBytes(buffer);
                if (inputEvent.Type == LinuxInput.EvKey)
                {
                    Console.WriteLine(
                        $"KEY code={inputEvent.Code} value={inputEvent.Value}");
                }
            }

            return 0;
        }
        finally
        {
            LinuxInput.Close(fileDescriptor);
        }
    }

    private static int UinputTest()
    {
        var fileDescriptor = -1;
        try
        {
            fileDescriptor = LinuxInput.CreateVirtualKeyboard(
                "Zetl B1 Uinput Test");
            Console.WriteLine(
                "Created the virtual keyboard without grabbing a physical device.");
            Thread.Sleep(500);
            return 0;
        }
        finally
        {
            LinuxInput.DestroyVirtualKeyboard(fileDescriptor);
            Console.WriteLine("Destroyed the virtual keyboard.");
        }
    }

    private static int Forward(string[] args)
    {
        if (!args.Contains("--arm", StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                "Refusing to grab a keyboard without --arm.");
            return 2;
        }

        var device = ReadStableDevice(args);
        var seconds = ReadSeconds(args, defaultSeconds: 15, maximum: 60);

        using var forwarder = new SpikeForwarder(
            device,
            TimeSpan.FromSeconds(seconds),
            args.Contains("--trace-events", StringComparer.Ordinal));
        return forwarder.Run();
    }

    private static string ReadStableDevice(string[] args)
    {
        var device = ReadValue(args, "--device=");
        if (string.IsNullOrWhiteSpace(device)
            || !device.StartsWith(
                "/dev/input/by-id/",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A stable /dev/input/by-id/... --device path is required.");
        }

        return device;
    }

    private static int ReadSeconds(
        string[] args,
        int defaultSeconds,
        int maximum)
    {
        var secondsText = ReadValue(args, "--seconds=");
        var seconds = int.TryParse(secondsText, out var parsed)
            ? parsed
            : defaultSeconds;
        if (seconds is < 1 || seconds > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                $"--seconds must be between 1 and {maximum}.");
        }

        return seconds;
    }

    private static IReadOnlyList<string> DiscoverDevices()
    {
        const string byId = "/dev/input/by-id";
        if (Directory.Exists(byId))
        {
            var stablePaths = Directory
                .EnumerateFileSystemEntries(
                    byId,
                    "*event-kbd")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            if (stablePaths.Count > 0)
            {
                return stablePaths;
            }
        }

        return Directory
            .EnumerateFiles("/dev/input", "event*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static string? ReadValue(
        IEnumerable<string> args,
        string prefix)
    {
        return args.FirstOrDefault(arg =>
                arg.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
    }

    private static int Usage()
    {
        Console.WriteLine(
            """
            Zetl B1 Linux input safety spike

            Read-only diagnostics:
              Zetl.Linux.Spike diagnose
              Zetl.Linux.Spike diagnose --device=/dev/input/by-id/...-event-kbd

            Non-grabbing hardware checks:
              Zetl.Linux.Spike observe --seconds=10 --device=/dev/input/by-id/...-event-kbd
              Zetl.Linux.Spike uinput-test

            Explicit, time-bounded grab and forward:
              Zetl.Linux.Spike forward --arm --seconds=15 --device=/dev/input/by-id/...-event-kbd
              Add --trace-events to print grabbed EV_KEY events.

            Panic chord while grabbed:
              Hold both Shift keys and press Escape.
            """);
        return 2;
    }
}
