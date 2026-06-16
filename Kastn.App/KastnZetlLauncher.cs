using System.Diagnostics;

namespace KASTN;

internal static class KastnZetlLauncher
{
    public static Task LaunchAsync(
        string? explicitPath = null,
        string? pipeName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(explicitPath)
            ?? throw new FileNotFoundException(
                "Kastn could not find Zetl. Install both applications together "
                + "or set ZETL_EXECUTABLE.");
        var startInfo = CreateStartInfo(path);
        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            startInfo.ArgumentList.Add($"--ipc-pipe={pipeName}");
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Kastn could not start Zetl.");
        return Task.CompletedTask;
    }

    internal static string? ResolvePath(string? explicitPath = null)
    {
        var configured = explicitPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("ZETL_EXECUTABLE");
        }

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? "Zetl.exe" : "Zetl"),
            Path.Combine(baseDirectory, "Zetl.dll"),
            Path.GetFullPath(Path.Combine(
                baseDirectory, "..", "..", "..", "..",
                "Zetl.App", "bin", "Debug", "net10.0", "Zetl.dll")),
            Path.GetFullPath(Path.Combine(
                baseDirectory, "..", "..", "..", "..",
                "Zetl.App", "bin", "Release", "net10.0", "Zetl.dll"))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static ProcessStartInfo CreateStartInfo(string path)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = "dotnet";
            info.ArgumentList.Add(path);
        }
        else
        {
            info.FileName = path;
        }

        return info;
    }
}
