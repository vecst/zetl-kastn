using System.Diagnostics;
using ZETL;

namespace KASTN;

internal static class KastnZetlLauncher
{
    public static Task LaunchAsync(
        string? explicitPath = null,
        string? pipeName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = AppImageStartInfo(explicitPath)
            ?? CreateStartInfo(ResolvePath(explicitPath)
                ?? throw new FileNotFoundException(
                    "Kastn could not find Zetl. Install both applications together "
                    + "or set ZETL_EXECUTABLE."));
        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            startInfo.ArgumentList.Add($"--ipc-pipe={pipeName}");
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Kastn could not start Zetl.");
        return Task.CompletedTask;
    }

    // From the AppImage, start Zetl through the AppImage file so it gets its
    // own mount and outlives this Kastn (see ZetlAppImage).
    private static ProcessStartInfo? AppImageStartInfo(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ZETL_EXECUTABLE"))
            || ZetlAppImage.CurrentPath() is not { } appImage)
        {
            return null;
        }

        return new ProcessStartInfo(appImage)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(appImage)!
        };
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

        // Zetl ships and builds next to Kastn in one shared output directory
        // (see Directory.Build.props), so look for the host right beside us. The
        // self-contained exe is preferred; the framework-dependent .dll covers a
        // UseAppHost=false build.
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? "Zetl.exe" : "Zetl"),
            Path.Combine(baseDirectory, "Zetl.dll")
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
