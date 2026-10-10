namespace ZETL;

/// <summary>
/// Explains, in plain words, why Zetl couldn't take the keyboard on Linux and
/// what fixes it. Most distributions open keyboards to the 'input' group, and
/// Zetl's udev rule opens /dev/uinput to it, so the cases are: not in the group
/// yet; in the group on disk but not in this running session (a session that
/// began before joining keeps its old groups, and logging out isn't always
/// enough: a per-user systemd manager kept alive by SSH or lingering survives
/// it); or in the group while /dev/uinput still isn't open to it.
/// </summary>
internal static class LinuxKeyboardSetup
{
    public const string GroupName = "input";

    public static string Explain(bool uinputDenied) => Explain(
        Environment.UserName,
        ReadOrEmpty("/etc/group"),
        ReadOrEmpty("/proc/self/status"),
        uinputDenied);

    /// <param name="groupFile">The contents of /etc/group.</param>
    /// <param name="processStatus">The contents of /proc/self/status (its Groups line).</param>
    internal static string Explain(string userName, string groupFile, string processStatus, bool uinputDenied)
    {
        const string guide = "The Linux setup guide has the steps.";
        if (FindGroup(groupFile) is not { } group)
        {
            return "Zetl needs to read your keyboards and write to /dev/uinput, and this "
                + $"system has no '{GroupName}' group to grant that through. {guide}";
        }

        var onDisk = group.Members.Contains(userName, StringComparer.Ordinal);
        var inSession = ProcessGroups(processStatus).Contains(group.Id);
        if (!onDisk && !inSession)
        {
            return $"Your user isn't in the '{GroupName}' group yet. Join it, add Zetl's udev rule "
                + $"for /dev/uinput, then restart your computer. {guide}";
        }

        if (!inSession)
        {
            return $"You've joined the '{GroupName}' group, but Zetl isn't running with it yet. "
                + "Restart your computer to finish keyboard setup; logging out isn't always enough.";
        }

        return uinputDenied
            ? $"/dev/uinput isn't open to the '{GroupName}' group yet. Install Zetl's udev rule "
                + $"(70-zetl-uinput.rules), then start Zetl again. {guide}"
            : $"Zetl can't open your keyboards even though you're in the '{GroupName}' group. "
                + "The diagnostics log names the keyboard and the error.";
    }

    private static (uint Id, string[] Members)? FindGroup(string groupFile)
    {
        foreach (var line in groupFile.Split('\n'))
        {
            // name:password:gid:member,member
            var fields = line.Trim().Split(':');
            if (fields.Length >= 4 && fields[0] == GroupName && uint.TryParse(fields[2], out var id))
            {
                return (id, fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return null;
    }

    private static HashSet<uint> ProcessGroups(string processStatus)
    {
        var groups = new HashSet<uint>();
        foreach (var line in processStatus.Split('\n'))
        {
            if (!line.StartsWith("Groups:", StringComparison.Ordinal)) continue;
            foreach (var value in line["Groups:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (uint.TryParse(value, out var id)) groups.Add(id);
            }
        }

        return groups;
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
