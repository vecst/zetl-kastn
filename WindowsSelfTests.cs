using Chordl;

namespace ZETL;

internal static class WindowsSelfTests
{
    public static int Run()
    {
        try
        {
            var assembly = typeof(Program).Assembly;
            using var stream = assembly.GetManifestResourceStream("hotkeys.json")
                ?? throw new InvalidOperationException("Embedded hotkeys.json is missing.");
            using var reader = new StreamReader(stream);
            var config = ChordlConfigLoader.LoadFromJson(reader.ReadToEnd());
            if (config.Actions.Count == 0 || config.HoldDelay <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("Embedded hotkeys.json is not a usable configuration.");
            }

            Console.WriteLine("PASS Windows artifact embeds a parseable default hotkeys config");
            Console.WriteLine("1/1 Windows tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL Windows artifact embeds a parseable default hotkeys config: {ex.Message}");
            return 1;
        }
    }
}
