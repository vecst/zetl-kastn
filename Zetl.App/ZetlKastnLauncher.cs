using System.Diagnostics;

namespace ZETL;

internal static class ZetlKastnLauncher
{
    public static void Launch(
        string? projectId = null,
        string? explicitPath = null,
        string? pipeName = null)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false
        };
        if (string.IsNullOrWhiteSpace(explicitPath)
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KASTN_EXECUTABLE"))
            && ZetlAppImage.CurrentPath() is { } appImage)
        {
            // From the AppImage, start Kastn through the AppImage file so it gets
            // its own mount and does not depend on this process (see ZetlAppImage).
            startInfo.FileName = appImage;
            startInfo.WorkingDirectory = Path.GetDirectoryName(appImage)!;
            startInfo.ArgumentList.Add(ZetlAppImage.KastnSwitch);
        }
        else
        {
            var path = ResolvePath(explicitPath)
                ?? throw new FileNotFoundException(
                    "Zetl could not find Kastn. Install both applications together "
                    + "or set KASTN_EXECUTABLE.");
            startInfo.WorkingDirectory = Path.GetDirectoryName(path)!;
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = "dotnet";
                startInfo.ArgumentList.Add(path);
            }
            else
            {
                startInfo.FileName = path;
            }
        }

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            startInfo.ArgumentList.Add($"--project={projectId}");
        }

        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            startInfo.ArgumentList.Add($"--ipc-pipe={pipeName}");
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Zetl could not start Kastn.");
    }

    internal static string? ResolvePath(string? explicitPath = null)
    {
        var configured = explicitPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("KASTN_EXECUTABLE");
        }

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? "Kastn.exe" : "Kastn"),
            Path.Combine(baseDirectory, "Kastn.dll"),
            Path.GetFullPath(Path.Combine(
                baseDirectory, "..", "..", "..", "..",
                "Kastn.App", "bin", "Debug", "net10.0", "Kastn.dll")),
            Path.GetFullPath(Path.Combine(
                baseDirectory, "..", "..", "..", "..",
                "Kastn.App", "bin", "Release", "net10.0", "Kastn.dll"))
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
