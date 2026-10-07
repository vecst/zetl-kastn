using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>
/// The file-descriptor plumbing shared by the keyboard and clipboard backends:
/// poll, pipes, non-blocking reads, and errno text.
/// </summary>
internal static class LinuxPosix
{
    public const int Eintr = 4;
    public const int Eagain = 11;
    public const int Enodev = 19;
    public const int Eacces = 13;
    public const int Ebusy = 16;

    private const int NonBlock = 0x800;
    private const int CloseOnExec = 0x80000;
    private const int GetFlags = 3;
    private const int SetFlags = 4;
    private const short PollIn = 0x0001;
    private const short PollErr = 0x0008;
    private const short PollHup = 0x0010;
    private const short PollNval = 0x0020;

    public static int LastError => Marshal.GetLastPInvokeError();

    public static string ErrorText(int errno) => errno switch
    {
        Eacces => "permission denied",
        Ebusy => "already grabbed by another program",
        Enodev => "device removed",
        2 => "no such file",
        _ => $"errno {errno}"
    };

    public static void Close(int descriptor)
    {
        if (descriptor >= 0) close(descriptor);
    }

    /// <summary>
    /// Waits for input on any descriptor. Returns, per descriptor, whether it is
    /// readable or has failed (HUP/ERR/NVAL); all false on timeout.
    /// </summary>
    public static bool[] Poll(IReadOnlyList<int> descriptors, int timeoutMilliseconds)
    {
        var entries = new PollDescriptor[descriptors.Count];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = new PollDescriptor { FileDescriptor = descriptors[i], Events = PollIn };
        }

        var ready = new bool[entries.Length];
        if (poll(entries, (nuint)entries.Length, timeoutMilliseconds) <= 0) return ready;
        for (var i = 0; i < entries.Length; i++)
        {
            ready[i] = (entries[i].ReturnedEvents & (PollIn | PollErr | PollHup | PollNval)) != 0;
        }

        return ready;
    }

    /// <summary>A non-blocking pipe used to wake a poll loop; [read, write].</summary>
    public static (int Read, int Write) CreateWakePipe()
    {
        var ends = new int[2];
        if (pipe2(ends, NonBlock | CloseOnExec) < 0)
        {
            throw new IOException($"pipe2 failed: {ErrorText(LastError)}.");
        }

        return (ends[0], ends[1]);
    }

    /// <summary>
    /// A pipe for receiving data from another process. The write end stays
    /// blocking because it is handed to the other process, and duplicated
    /// descriptors share that flag; only the read end is made non-blocking.
    /// </summary>
    public static (int Read, int Write)? CreateTransferPipe()
    {
        var ends = new int[2];
        if (pipe2(ends, CloseOnExec) < 0) return null;
        var flags = fcntl(ends[0], GetFlags, 0);
        if (flags < 0 || fcntl(ends[0], SetFlags, flags | NonBlock) < 0)
        {
            close(ends[0]);
            close(ends[1]);
            return null;
        }

        return (ends[0], ends[1]);
    }

    public static void Signal(int writeEnd)
    {
        if (writeEnd >= 0) write(writeEnd, [1], 1);
    }

    /// <summary>Discards everything readable from a non-blocking descriptor.</summary>
    public static void Drain(int descriptor)
    {
        var buffer = new byte[4096];
        while (read(descriptor, buffer, (nuint)buffer.Length) > 0)
        {
        }
    }

    /// <summary>
    /// Appends what a non-blocking descriptor has ready. Returns true at end of
    /// file, false when more may come; throws on a read error.
    /// </summary>
    public static bool ReadAvailable(int descriptor, MemoryStream into, byte[] scratch)
    {
        while (true)
        {
            var count = read(descriptor, scratch, (nuint)scratch.Length);
            if (count > 0)
            {
                into.Write(scratch, 0, (int)count);
                continue;
            }

            if (count == 0) return true;
            var error = LastError;
            if (error == Eintr) continue;
            if (error == Eagain) return false;
            throw new IOException($"read failed: {ErrorText(error)}.");
        }
    }

    public static nint Read(int descriptor, byte[] buffer, nuint count) => read(descriptor, buffer, count);

    public static nint Write(int descriptor, byte[] buffer, nuint count) => write(descriptor, buffer, count);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int descriptor, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int descriptor, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollDescriptor[] descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe2(int[] descriptors, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int descriptor, int command, int argument);
}
