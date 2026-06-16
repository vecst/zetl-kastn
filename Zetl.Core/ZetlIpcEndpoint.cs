using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ZETL;

internal static class ZetlIpcEndpoint
{
    public static string GetDefaultPipeName()
    {
        var session = OperatingSystem.IsWindows()
            ? Process.GetCurrentProcess().SessionId.ToString()
            : Environment.GetEnvironmentVariable("XDG_SESSION_ID")
                ?? Environment.GetEnvironmentVariable("DISPLAY")
                ?? "default";
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}|{session}";
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16]
            .ToLowerInvariant();
        return $"zetl-{ZETL.Contracts.ZetlProtocol.CurrentVersion}-{hash}";
    }
}
