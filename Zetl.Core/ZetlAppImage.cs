namespace ZETL;

/// <summary>
/// Knows when Zetl and Kastn run from the Linux AppImage. An AppImage stays
/// mounted only while the process it started is alive, so one app must never
/// launch the other from inside its own mount: closing a Kastn opened first
/// would unmount the files under the Zetl it started. Inside an AppImage each
/// app launches the other through the AppImage file, giving it its own mount.
/// </summary>
internal static class ZetlAppImage
{
    /// <summary>Command-line switch that makes the AppImage start Kastn instead of Zetl.</summary>
    public const string KastnSwitch = "--kastn";

    /// <summary>
    /// The AppImage file this process runs from, or null. The runtime's APPIMAGE
    /// and APPDIR variables are inherited by children, so they count only when
    /// this executable really lives under that mount.
    /// </summary>
    public static string? CurrentPath() => CurrentPath(
        Environment.GetEnvironmentVariable("APPIMAGE"),
        Environment.GetEnvironmentVariable("APPDIR"),
        AppContext.BaseDirectory);

    internal static string? CurrentPath(string? appImage, string? mountDirectory, string baseDirectory)
    {
        if (string.IsNullOrEmpty(appImage)
            || string.IsNullOrEmpty(mountDirectory)
            || !File.Exists(appImage))
        {
            return null;
        }

        var mount = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountDirectory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(baseDirectory).StartsWith(mount, StringComparison.Ordinal) ? appImage : null;
    }
}
