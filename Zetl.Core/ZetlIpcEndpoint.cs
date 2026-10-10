using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ZETL;

internal static class ZetlIpcEndpoint
{
    public static string GetDefaultPipeName() =>
        $"zetl-{ZETL.Contracts.ZetlProtocol.CurrentVersion}-{SessionHash()}";

    /// <summary>
    /// A short stable hash of the user and login session. Every local pipe Zetl
    /// and Kastn share is named from it, so both compute the same names without
    /// passing them at launch, and other users and sessions never collide.
    /// </summary>
    public static string SessionHash()
    {
        var session = OperatingSystem.IsWindows()
            ? Process.GetCurrentProcess().SessionId.ToString()
            : Environment.GetEnvironmentVariable("XDG_SESSION_ID")
                ?? Environment.GetEnvironmentVariable("DISPLAY")
                ?? "default";
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{session}";
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16]
            .ToLowerInvariant();
    }
}
