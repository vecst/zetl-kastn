using System.Runtime.InteropServices;
using System.Text;

namespace ZETL.LinuxSpike;

internal static class LinuxInput
{
    public const ushort EvSyn = 0;
    public const ushort EvKey = 1;
    public const ushort SynReport = 0;
    public const ushort SynDropped = 3;

    public const ushort KeyEsc = 1;
    public const ushort KeyR = 19;
    public const ushort KeyP = 25;
    public const ushort KeyLeftCtrl = 29;
    public const ushort KeyA = 30;
    public const ushort KeyLeftShift = 42;
    public const ushort KeyZ = 44;
    public const ushort KeyX = 45;
    public const ushort KeyC = 46;
    public const ushort KeyV = 47;
    public const ushort KeyB = 48;
    public const ushort KeyRightShift = 54;
    public const ushort KeyRightCtrl = 97;
    public const ushort KeyMax = 0x2ff;

    private const int ReadOnly = 0;
    private const int WriteOnly = 1;
    private const int ReadWrite = 2;
    private const int NonBlock = 0x800;
    private const int CloseOnExec = 0x80000;
    private const short PollInput = 0x0001;

    private const ulong EviocGrab = 0x40044590;
    private const ulong UiSetEvBit = 0x40045564;
    private const ulong UiSetKeyBit = 0x40045565;
    private const ulong UiDevCreate = 0x5501;
    private const ulong UiDevDestroy = 0x5502;

    public const int InputEventSize = 24;

    public static int OpenReadOnly(string path)
    {
        return OpenChecked(path, ReadOnly | NonBlock | CloseOnExec);
    }

    public static int OpenReadWrite(string path)
    {
        return OpenChecked(path, ReadWrite | NonBlock | CloseOnExec);
    }

    public static int OpenUinput()
    {
        return OpenChecked("/dev/uinput", WriteOnly | NonBlock | CloseOnExec);
    }

    public static string GetDeviceName(int fileDescriptor)
    {
        var buffer = new byte[256];
        var request = ReadIoctl((byte)'E', 0x06, buffer.Length);
        if (ioctl_buffer(fileDescriptor, request, buffer) < 0)
        {
            return "(name unavailable)";
        }

        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    public static bool IsKeyboard(int fileDescriptor)
    {
        var eventBits = ReadCapabilityBits(fileDescriptor, 0, 8);
        if (!HasBit(eventBits, EvKey))
        {
            return false;
        }

        var keyBits = ReadCapabilityBits(
            fileDescriptor,
            EvKey,
            (KeyMax / 8) + 1);
        return HasBit(keyBits, KeyA)
            && HasBit(keyBits, KeyC)
            && HasBit(keyBits, KeyLeftCtrl);
    }

    public static bool Grab(int fileDescriptor, bool enabled)
    {
        return ioctl_int(fileDescriptor, EviocGrab, enabled ? 1 : 0) == 0;
    }

    public static IReadOnlyList<ushort> GetPressedKeys(int fileDescriptor)
    {
        var keyBits = new byte[(KeyMax / 8) + 1];
        var request = ReadIoctl((byte)'E', 0x18, keyBits.Length);
        if (ioctl_buffer(fileDescriptor, request, keyBits) < 0)
        {
            throw new IOException(
                $"EVIOCGKEY failed with errno {LastError}.");
        }

        var pressedKeys = new List<ushort>();
        for (ushort key = 0; key <= KeyMax; key++)
        {
            if (HasBit(keyBits, key))
            {
                pressedKeys.Add(key);
            }
        }

        return pressedKeys;
    }

    public static bool WaitReadable(int fileDescriptor, int timeoutMilliseconds)
    {
        var descriptors = new[]
        {
            new PollDescriptor
            {
                FileDescriptor = fileDescriptor,
                Events = PollInput
            }
        };
        var result = poll(descriptors, 1, timeoutMilliseconds);
        return result > 0 && (descriptors[0].ReturnedEvents & PollInput) != 0;
    }

    public static int ReadEvent(int fileDescriptor, byte[] buffer)
    {
        var readCount = read(
            fileDescriptor,
            buffer,
            (nuint)InputEventSize);
        return checked((int)readCount);
    }

    public static void WriteEvent(int fileDescriptor, InputEvent inputEvent)
    {
        var buffer = inputEvent.ToBytes();
        WriteAll(fileDescriptor, buffer);
    }

    public static int CreateVirtualKeyboard(string name)
    {
        var fileDescriptor = OpenUinput();
        try
        {
            IoctlChecked(fileDescriptor, UiSetEvBit, EvSyn);
            IoctlChecked(fileDescriptor, UiSetEvBit, EvKey);
            for (var key = 0; key <= KeyMax; key++)
            {
                IoctlChecked(fileDescriptor, UiSetKeyBit, key);
            }

            var userDevice = new byte[1116];
            var nameBytes = Encoding.UTF8.GetBytes(name);
            Array.Copy(
                nameBytes,
                userDevice,
                Math.Min(nameBytes.Length, 79));
            WriteUInt16(userDevice, 80, 0x03);
            WriteUInt16(userDevice, 82, 0x1);
            WriteUInt16(userDevice, 84, 0x1);
            WriteUInt16(userDevice, 86, 1);
            WriteAll(fileDescriptor, userDevice);
            IoctlChecked(fileDescriptor, UiDevCreate, 0);
            Thread.Sleep(150);
            return fileDescriptor;
        }
        catch
        {
            close(fileDescriptor);
            throw;
        }
    }

    public static void DestroyVirtualKeyboard(int fileDescriptor)
    {
        if (fileDescriptor < 0)
        {
            return;
        }

        ioctl_int(fileDescriptor, UiDevDestroy, 0);
        close(fileDescriptor);
    }

    public static void Close(int fileDescriptor)
    {
        if (fileDescriptor >= 0)
        {
            close(fileDescriptor);
        }
    }

    public static int LastError => Marshal.GetLastPInvokeError();

    private static byte[] ReadCapabilityBits(
        int fileDescriptor,
        int eventType,
        int byteCount)
    {
        var buffer = new byte[byteCount];
        var request = ReadIoctl(
            (byte)'E',
            (byte)(0x20 + eventType),
            byteCount);
        return ioctl_buffer(fileDescriptor, request, buffer) < 0
            ? []
            : buffer;
    }

    private static bool HasBit(byte[] bits, int bit)
    {
        var index = bit / 8;
        return index < bits.Length
            && (bits[index] & (1 << (bit % 8))) != 0;
    }

    private static ulong ReadIoctl(byte type, byte number, int size)
    {
        const int readDirection = 2;
        return ((ulong)readDirection << 30)
            | ((ulong)size << 16)
            | ((ulong)type << 8)
            | number;
    }

    private static void IoctlChecked(
        int fileDescriptor,
        ulong request,
        int value)
    {
        if (ioctl_int(fileDescriptor, request, value) < 0)
        {
            throw new IOException(
                $"ioctl 0x{request:X} failed with errno {LastError}.");
        }
    }

    private static int OpenChecked(string path, int flags)
    {
        var fileDescriptor = open(path, flags);
        if (fileDescriptor < 0)
        {
            throw new IOException(
                $"Could not open {path}; errno {LastError}.");
        }

        return fileDescriptor;
    }

    private static void WriteAll(int fileDescriptor, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var remaining = buffer.Length - offset;
            var segment = offset == 0
                ? buffer
                : buffer[offset..];
            var written = write(
                fileDescriptor,
                segment,
                (nuint)remaining);
            if (written <= 0)
            {
                throw new IOException(
                    $"Device write failed with errno {LastError}.");
            }

            offset += checked((int)written);
        }
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fileDescriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(
        int fileDescriptor,
        byte[] buffer,
        nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(
        int fileDescriptor,
        byte[] buffer,
        nuint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_int(
        int fileDescriptor,
        ulong request,
        int value);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl_buffer(
        int fileDescriptor,
        ulong request,
        byte[] buffer);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(
        [In, Out] PollDescriptor[] descriptors,
        nuint count,
        int timeoutMilliseconds);
}

internal readonly record struct InputEvent(
    long Seconds,
    long Microseconds,
    ushort Type,
    ushort Code,
    int Value)
{
    public static InputEvent FromBytes(byte[] buffer)
    {
        return new InputEvent(
            BitConverter.ToInt64(buffer, 0),
            BitConverter.ToInt64(buffer, 8),
            BitConverter.ToUInt16(buffer, 16),
            BitConverter.ToUInt16(buffer, 18),
            BitConverter.ToInt32(buffer, 20));
    }

    public static InputEvent Key(ushort code, int value)
    {
        return new InputEvent(0, 0, LinuxInput.EvKey, code, value);
    }

    public static InputEvent Synchronize()
    {
        return new InputEvent(
            0,
            0,
            LinuxInput.EvSyn,
            LinuxInput.SynReport,
            0);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[LinuxInput.InputEventSize];
        BitConverter.GetBytes(Seconds).CopyTo(buffer, 0);
        BitConverter.GetBytes(Microseconds).CopyTo(buffer, 8);
        BitConverter.GetBytes(Type).CopyTo(buffer, 16);
        BitConverter.GetBytes(Code).CopyTo(buffer, 18);
        BitConverter.GetBytes(Value).CopyTo(buffer, 20);
        return buffer;
    }
}
