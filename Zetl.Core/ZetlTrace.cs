using System.Diagnostics;

namespace ZETL;

// Crash diagnostics: when trace.flag exists in the data folder, each traced
// step is appended to trace.log immediately, unbuffered, so the last line
// survives a process that dies without unwinding (native heap corruption). Off
// by default; a trace write costs a file append, too slow for normal use.
internal static class ZetlTrace
{
    private static readonly object Gate = new();
    // Once trace.log passes this size it starts over, so leaving tracing on for
    // days can't fill the disk.
    private const long MaxBytes = 20L * 1024 * 1024;
    private static string? path;

    public static bool Enabled => path is not null;

    public static void EnableIfFlagged(string dataDirectory)
    {
        if (File.Exists(Path.Combine(dataDirectory, "trace.flag")))
        {
            path = Path.Combine(dataDirectory, "trace.log");
            Write($"trace started, pid {Environment.ProcessId}");
        }
    }

    public static void Write(string step)
    {
        if (path is not { } target)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} t{Environment.CurrentManagedThreadId} {step}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                if (new FileInfo(target) is { Exists: true, Length: > MaxBytes })
                {
                    File.Delete(target);
                }

                File.AppendAllText(target, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tracing must never take the app down with it.
            }
        }
    }
}
