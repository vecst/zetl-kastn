using System.Runtime.InteropServices;
using System.Text;

namespace ZETL;

/// <summary>One evdev event without its timestamp; uinput stamps what Zetl writes.</summary>
internal readonly record struct LinuxInputEvent(ushort Type, ushort Code, int Value)
{
    public static LinuxInputEvent Key(ushort code, int value) => new(LinuxEvdev.EvKey, code, value);

    public static LinuxInputEvent Synchronize() => new(LinuxEvdev.EvSyn, LinuxEvdev.SynReport, 0);

    public bool IsKey => Type == LinuxEvdev.EvKey;

    public bool IsReport => Type == LinuxEvdev.EvSyn && Code == LinuxEvdev.SynReport;

    public bool IsDropped => Type == LinuxEvdev.EvSyn && Code == LinuxEvdev.SynDropped;
}

/// <summary>
/// evdev and uinput system calls for the Linux keyboard backend: reading and
/// exclusively grabbing <c>/dev/input/event*</c> keyboards, creating the virtual
/// keyboard Zetl forwards through, and the poll/inotify plumbing around them.
/// Failures return a negative errno instead of throwing so the reader loop can
/// decide what each one means. Shared descriptor plumbing is in <see cref="LinuxPosix"/>.
/// </summary>
internal static class LinuxEvdev
{
    public const ushort EvSyn = 0x00;
    public const ushort EvKey = 0x01;
    public const ushort EvRel = 0x02;
    public const ushort EvAbs = 0x03;
    public const ushort SynReport = 0;
    public const ushort SynDropped = 3;
    public const ushort KeyMax = 0x2ff;

    // struct input_event on 64-bit Linux: timeval (16), type (2), code (2), value (4).
    public const int InputEventSize = 24;

    private const int ReadWrite = 2;
    private const int WriteOnly = 1;
    private const int NonBlock = 0x800;
    private const int CloseOnExec = 0x80000;

    private const ulong EviocGrab = 0x40044590;
    private const ulong UiSetEvBit = 0x40045564;
    private const ulong UiSetKeyBit = 0x40045565;
    private const ulong UiDevCreate = 0x5501;
    private const ulong UiDevDestroy = 0x5502;
    private const int UinputUserDevSize = 1116;
    private const int UinputMaxNameSize = 80;

    public const uint InCreate = 0x100;
    public const uint InAttrib = 0x004;

    /// <summary>Opens an event device for reading and grabbing; a negative result is -errno.</summary>
    public static int OpenDevice(string path)
    {
        var descriptor = open(path, ReadWrite | NonBlock | CloseOnExec);
        return descriptor >= 0 ? descriptor : -LinuxPosix.LastError;
    }

    public static string GetName(int descriptor)
    {
        var buffer = new byte[256];
        if (ioctl_buffer(descriptor, ReadIoctl((byte)'E', 0x06, buffer.Length), buffer) < 0)
        {
            return "(unnamed device)";
        }

        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    /// <summary>
    /// True for an ordinary keyboard node: letter keys and Ctrl, and no pointer
    /// axes. Combined keyboard/mouse nodes are left alone, since grabbing one
    /// would also take the pointer.
    /// </summary>
    public static bool IsKeyboard(int descriptor)
    {
        var types = ReadBits(descriptor, 0, 4);
        if (!HasBit(types, EvKey) || HasBit(types, EvRel) || HasBit(types, EvAbs))
        {
            return false;
        }

        var keys = ReadBits(descriptor, EvKey, (KeyMax / 8) + 1);
        return HasBit(keys, LinuxKeyMap.KeyA)
            && HasBit(keys, LinuxKeyMap.KeyC)
            && HasBit(keys, LinuxKeyMap.KeyLeftCtrl);
    }

    /// <summary>Keys the kernel reports held on the device, or null if it cannot say.</summary>
    public static HashSet<ushort>? GetPressedKeys(int descriptor)
    {
        var bits = new byte[(KeyMax / 8) + 1];
        if (ioctl_buffer(descriptor, ReadIoctl((byte)'E', 0x18, bits.Length), bits) < 0)
        {
            return null;
        }

        var pressed = new HashSet<ushort>();
        for (ushort key = 0; key <= KeyMax; key++)
        {
            if (HasBit(bits, key)) pressed.Add(key);
        }

        return pressed;
    }

    /// <summary>Takes or releases the exclusive grab; returns 0 or an errno.</summary>
    public static int Grab(int descriptor, bool enabled) =>
        ioctl_int(descriptor, EviocGrab, enabled ? 1 : 0) == 0 ? 0 : LinuxPosix.LastError;

    /// <summary>
    /// Reads every queued event into <paramref name="events"/>. Returns the count,
    /// 0 when nothing is queued, or -errno on failure.
    /// </summary>
    public static int ReadEvents(int descriptor, byte[] scratch, List<LinuxInputEvent> events)
    {
        events.Clear();
        while (true)
        {
            var count = read(descriptor, scratch, (nuint)scratch.Length);
            if (count < 0)
            {
                var error = LinuxPosix.LastError;
                if (error == LinuxPosix.Eintr) continue;
                return error == LinuxPosix.Eagain ? events.Count : -error;
            }

            if (count == 0) return events.Count;
            for (var offset = 0; offset + InputEventSize <= count; offset += InputEventSize)
            {
                events.Add(new LinuxInputEvent(
                    BitConverter.ToUInt16(scratch, offset + 16),
                    BitConverter.ToUInt16(scratch, offset + 18),
                    BitConverter.ToInt32(scratch, offset + 20)));
            }

            if (count < scratch.Length) return events.Count;
        }
    }

    public static void Write(int descriptor, LinuxInputEvent inputEvent)
    {
        Span<byte> buffer = stackalloc byte[InputEventSize];
        buffer.Clear();
        BitConverter.TryWriteBytes(buffer[16..], inputEvent.Type);
        BitConverter.TryWriteBytes(buffer[18..], inputEvent.Code);
        BitConverter.TryWriteBytes(buffer[20..], inputEvent.Value);
        WriteAll(descriptor, buffer);
    }

    /// <summary>
    /// Creates a uinput keyboard able to emit every key code. Returns its
    /// descriptor, or -errno when /dev/uinput cannot be opened or configured.
    /// </summary>
    public static int CreateVirtualKeyboard(string name)
    {
        var descriptor = open("/dev/uinput", WriteOnly | NonBlock | CloseOnExec);
        if (descriptor < 0) return -LinuxPosix.LastError;
        try
        {
            if (ioctl_int(descriptor, UiSetEvBit, EvSyn) < 0
                || ioctl_int(descriptor, UiSetEvBit, EvKey) < 0)
            {
                return Fail();
            }

            for (var key = 1; key <= KeyMax; key++)
            {
                if (ioctl_int(descriptor, UiSetKeyBit, key) < 0) return Fail();
            }

            // struct uinput_user_dev: name[80], then struct input_id.
            var device = new byte[UinputUserDevSize];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            nameBytes.AsSpan(0, Math.Min(nameBytes.Length, UinputMaxNameSize - 1)).CopyTo(device);
            BitConverter.TryWriteBytes(device.AsSpan(80), (ushort)0x03); // BUS_USB
            BitConverter.TryWriteBytes(device.AsSpan(82), (ushort)0x1);
            BitConverter.TryWriteBytes(device.AsSpan(84), (ushort)0x1);
            BitConverter.TryWriteBytes(device.AsSpan(86), (ushort)1);
            WriteAll(descriptor, device);
            return ioctl_int(descriptor, UiDevCreate, 0) < 0 ? Fail() : descriptor;
        }
        catch (IOException)
        {
            return Fail();
        }

        int Fail()
        {
            var error = LinuxPosix.LastError;
            close(descriptor);
            return -(error == 0 ? LinuxPosix.Enodev : error);
        }
    }

    /// <summary>The kernel name ("input42") of a created uinput device, or null.</summary>
    public static string? GetVirtualSysName(int descriptor)
    {
        var buffer = new byte[64];
        if (ioctl_buffer(descriptor, ReadIoctl((byte)'U', 44, buffer.Length), buffer) < 0)
        {
            return null;
        }

        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }

    public static void DestroyVirtualKeyboard(int descriptor)
    {
        if (descriptor < 0) return;
        ioctl_int(descriptor, UiDevDestroy, 0);
        close(descriptor);
    }

    /// <summary>An inotify descriptor watching a directory, or -1 when unavailable.</summary>
    public static int WatchDirectory(string path, uint mask)
    {
        var descriptor = inotify_init1(NonBlock | CloseOnExec);
        if (descriptor < 0) return -1;
        if (inotify_add_watch(descriptor, path, mask) < 0)
        {
            close(descriptor);
            return -1;
        }

        return descriptor;
    }

    private static byte[] ReadBits(int descriptor, int eventType, int byteCount)
    {
        var buffer = new byte[byteCount];
        return ioctl_buffer(descriptor, ReadIoctl((byte)'E', (byte)(0x20 + eventType), byteCount), buffer) < 0
            ? []
            : buffer;
    }

    private static bool HasBit(byte[] bits, int bit) =>
        bit / 8 < bits.Length && (bits[bit / 8] & (1 << (bit % 8))) != 0;

    private static ulong ReadIoctl(byte type, byte number, int size) =>
        (2UL << 30) | ((ulong)size << 16) | ((ulong)type << 8) | number;

    private static void WriteAll(int descriptor, ReadOnlySpan<byte> buffer)
    {
        var bytes = buffer.ToArray();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var written = write(descriptor, bytes[offset..], (nuint)(bytes.Length - offset));
            if (written < 0 && LinuxPosix.LastError == LinuxPosix.Eintr) continue;
            if (written <= 0)
            {
                throw new IOException($"uinput write failed: {LinuxPosix.ErrorText(LinuxPosix.LastError)}.");
            }

            offset += checked((int)written);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int descriptor, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int descriptor, byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_int(int descriptor, ulong request, int value);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_buffer(int descriptor, ulong request, byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_init1(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_add_watch(int descriptor, string path, uint mask);
}
