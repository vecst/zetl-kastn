namespace ZETL;

internal static class ZetlIpcSmokeServer
{
    public static int Run(string[] args, string dataDirectory)
    {
        var pipeName = Value(args, "--ipc-pipe=")
            ?? $"zetl-smoke-{Guid.NewGuid():N}";
        var readyFile = Value(args, "--ipc-ready-file=");
        var stopFile = Value(args, "--ipc-stop-file=");
        if (string.IsNullOrWhiteSpace(readyFile)
            || string.IsNullOrWhiteSpace(stopFile))
        {
            Console.Error.WriteLine(
                "IPC smoke server requires --ipc-ready-file and --ipc-stop-file.");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(dataDirectory);
            var store = new ZetlStateStore(
                Path.Combine(dataDirectory, "state.json"),
                "ipc-smoke-session",
                message => Console.Error.WriteLine(message));
            if (store.State.Projects.Count == 0)
            {
                store.CreateProject("IPC Smoke Project", ["Inbox"], "Inbox");
            }

            var service = new ZetlProjectService(
                store,
                log: message => Console.Error.WriteLine(message));
            using var server = new ZetlIpcServer(
                service,
                pipeName,
                message => Console.Error.WriteLine(message));
            server.Start();

            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.GetFullPath(readyFile))!);
            File.WriteAllText(readyFile, pipeName);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(stopFile) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }

            return File.Exists(stopFile) ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string? Value(string[] args, string prefix)
    {
        return args.FirstOrDefault(arg =>
                arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..];
    }
}
