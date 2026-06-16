namespace ZETL.Tests;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Any(arg => arg.Equals("--storage-baseline", StringComparison.OrdinalIgnoreCase)))
        {
            return StorageBaselineScenario.Run();
        }

        return PortableSelfTests.Run();
    }
}
