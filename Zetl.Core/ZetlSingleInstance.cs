namespace ZETL;

/// <summary>
/// Names the mutex that keeps one Zetl (or Kastn) per user. On Windows a Local
/// name is per logon session. On Unix .NET scopes an unprefixed name to the
/// process's Unix session, and every launch from the app menu, autostart, or
/// Kastn's relaunch can start a new one, so two Zetls could fight over the
/// keyboard. There the name is global and carries the user instead.
/// </summary>
internal static class ZetlSingleInstance
{
    public static string MutexName(string app) => MutexName(app, OperatingSystem.IsWindows(), Environment.UserName);

    internal static string MutexName(string app, bool windows, string user) => windows
        ? $@"Local\{app}SingleInstance"
        : $@"Global\{app}SingleInstance-{user}";
}
