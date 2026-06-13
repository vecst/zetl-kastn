namespace ZETL;

// Windows-only smoke tests for the real platform clipboard, run via
// `dotnet run --project Zetl.App -- --self-test`. They exercise the live
// Windows clipboard (write/read round-trips), which portable tests can't do, so
// they are deliberately out of the portable suite. The caller's clipboard is
// captured and restored.
internal static class ZetlWindowsSelfTests
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP Zetl Windows self-tests: not running on Windows.");
            return 0;
        }

        var clipboard = new AvaloniaWindowsClipboard(message => Console.WriteLine($"  clipboard: {message}"));
        var original = clipboard.TryGetText();
        var failures = 0;
        try
        {
            var sample = $"zetl-selftest-{Guid.NewGuid():N}";
            failures += Check("clipboard write reports success", clipboard.SetText(sample));
            failures += Check("clipboard round-trips written text", clipboard.TryGetText() == sample);

            var tokenBefore = clipboard.GetChangeToken();
            failures += Check("clipboard overwrite reports success", clipboard.SetText("second value"));
            failures += Check("clipboard reflects the overwrite", clipboard.TryGetText() == "second value");
            failures += Check("change token advances after a write", clipboard.GetChangeToken() != tokenBefore);

            failures += Check(
                "clipboard round-trips unicode and emoji",
                clipboard.SetText("café — naïve — 日本語 🎉") && clipboard.TryGetText() == "café — naïve — 日本語 🎉");

            // Changing the system foreground-lock timeout and restoring it must
            // leave the setting exactly as it was, so Zetl doesn't alter a
            // system-wide value past its own lifetime.
            uint before = 0;
            Win32Interop.SystemParametersInfo(Win32Interop.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref before, 0);
            ZetlForegroundService.AllowForegroundActivation();
            ZetlForegroundService.RestoreForegroundActivation();
            uint after = 0;
            Win32Interop.SystemParametersInfo(Win32Interop.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref after, 0);
            failures += Check("foreground-lock timeout restored to its original value", after == before);
        }
        finally
        {
            if (!string.IsNullOrEmpty(original))
            {
                clipboard.SetText(original);
            }

            clipboard.Dispose();
        }

        Console.WriteLine(failures == 0
            ? "All Zetl Windows self-tests passed."
            : $"{failures} Zetl Windows self-test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static int Check(string name, bool passed)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
        return passed ? 0 : 1;
    }
}
