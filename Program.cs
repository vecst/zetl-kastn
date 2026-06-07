using Chordl;

namespace ZETL;

internal static partial class Program
{
    private static ChordlProcessor? chordlProcessor;
    private static ZetlApplicationContext? appContext;
    private static IKeyboardBackend? keyboardBackend;
    private static IClipboard? clipboard;
    private static Mutex? singleInstanceMutex;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            return WindowsSelfTests.Run();
        }

        var parityDirectory = ReadValueArgument(args, "--parity-smoke=");
        if (parityDirectory is not null)
        {
            ZetlParityScenario.Run(parityDirectory);
            return 0;
        }

        // Only one Zetl may own the global keyboard hook per session: a second
        // instance would install a competing hook and both would race on every
        // chord and write the same state files. Local\ scopes the guard to this
        // login session, so separate RDP sessions can each run their own.
        singleInstanceMutex = new Mutex(initiallyOwned: false, @"Local\ZetlSingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Zetl is already running in this session.",
                "Zetl",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            var chordlConfig = LoadChordlConfig(out var configSource);
            keyboardBackend = new WindowsKeyboardBackend(LogEvent);
            clipboard = new WindowsClipboard();
            var dataDirectory = ReadValueArgument(args, "--data-dir=");
            appContext = new ZetlApplicationContext(
                chordlConfig.HoldDelay,
                keyboardBackend,
                clipboard,
                dataDirectory);
            LogEvent(configSource);
            chordlProcessor = new ChordlProcessor(
                chordlConfig.Actions,
                chordlConfig.ConfiguredKeyCodes,
                chordlConfig.RepeatSuppressionDelay,
                chordlConfig.HoldDelay,
                DispatchOriginalAction,
                appContext.OnPhysicalShortcutPassedThrough,
                appContext.OnTapDispatched,
                appContext.OnHoldDetected,
                LogEvent,
                clipboard.GetChangeToken);

            if (!keyboardBackend.Start(chordlProcessor.HandleKeyEvent))
            {
                MessageBox.Show(
                    "Failed to install the global keyboard hook.",
                    "Zetl",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            LogEvent($"Loaded {chordlConfig.Actions.Count} Chordl definitions.");
            Application.Run(appContext);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Zetl startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            chordlProcessor?.Dispose();
            keyboardBackend?.Dispose();
            appContext?.Dispose();
            singleInstanceMutex?.Dispose();
        }
    }

    // Prefer an external hotkeys.json (editable, beside the exe or in the source
    // tree). When none exists — most commonly a single-file publish run without
    // the loose config next to it — fall back to the embedded default so the
    // app always launches.
    private static ChordlConfiguration LoadChordlConfig(out string configSource)
    {
        if (ChordlConfigLoader.TryFindConfigFile(out var configPath))
        {
            configSource = $"Loaded Chordl config from {configPath}.";
            return ChordlConfigLoader.LoadFromFile(configPath);
        }

        configSource = "hotkeys.json not found; using embedded default Chordl config.";
        return ChordlConfigLoader.LoadFromJson(ReadEmbeddedDefaultConfig());
    }

    private static string ReadEmbeddedDefaultConfig()
    {
        var assembly = typeof(Program).Assembly;
        using var stream = assembly.GetManifestResourceStream("hotkeys.json")
            ?? throw new InvalidOperationException("Embedded default hotkeys.json is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void LogEvent(string message)
    {
        appContext?.Log(message);
    }

    private static void DispatchOriginalAction(int vkCode, bool includeShift, bool restoreCtrl, bool restoreShift)
    {
        var sent = keyboardBackend?.SendChord(vkCode, includeShift, restoreCtrl, restoreShift) ?? false;
        LogEvent(sent
            ? $"Sent synthetic {ChordlKeys.FormatComboName(vkCode, includeShift)}."
            : $"Failed to send synthetic {ChordlKeys.FormatComboName(vkCode, includeShift)}.");
    }

    private static string? ReadValueArgument(string[] args, string prefix)
    {
        return args.FirstOrDefault(arg =>
                arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
    }
}
