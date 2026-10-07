using System.Runtime.InteropServices;

namespace ZETL;

/// <summary>union wl_argument: one marshalled request or event argument.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct WlArgument
{
    [FieldOffset(0)] public int Int;
    [FieldOffset(0)] public uint Uint;
    [FieldOffset(0)] public IntPtr Pointer;

    public static WlArgument Of(uint value) => new() { Uint = value };

    public static WlArgument Of(IntPtr pointer) => new() { Pointer = pointer };

    public static WlArgument Fd(int descriptor) => new() { Int = descriptor };

    public string? String => Marshal.PtrToStringUTF8(Pointer);
}

/// <summary>Receives the events of a Wayland object Zetl created or was given.</summary>
internal unsafe interface IWaylandListener
{
    void OnEvent(IntPtr proxy, uint opcode, WlArgument* args);
}

/// <summary>
/// The parts of libwayland-client Zetl's clipboard uses, plus hand-built
/// interface tables for ext-data-control-v1 (KWin's clipboard-manager protocol;
/// the XML is not installed on most systems, so there is no generated code).
/// Requests go through wl_proxy_marshal_array_flags rather than the variadic
/// marshallers, and every object's events arrive through one dispatcher.
/// Only the clipboard thread calls into this class.
/// </summary>
internal static unsafe class WaylandClient
{
    private const string Library = "libwayland-client.so.0";
    public const uint MarshalDestroy = 1;

    public static readonly IntPtr RegistryInterface;
    public static readonly IntPtr SeatInterface;
    public static readonly IntPtr ManagerInterface;
    public static readonly IntPtr DeviceInterface;
    public static readonly IntPtr SourceInterface;
    public static readonly IntPtr OfferInterface;
    public static readonly bool Available;

    static WaylandClient()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad(Library, out var handle)) return;
        if (!NativeLibrary.TryGetExport(handle, "wl_registry_interface", out RegistryInterface)
            || !NativeLibrary.TryGetExport(handle, "wl_seat_interface", out SeatInterface))
        {
            return;
        }

        // struct wl_interface is 40 bytes on 64-bit; allocate all four before
        // filling them, since their messages refer to one another.
        ManagerInterface = Allocate(40);
        DeviceInterface = Allocate(40);
        SourceInterface = Allocate(40);
        OfferInterface = Allocate(40);

        // Message order and signatures follow ext-data-control-v1.xml exactly;
        // opcodes are positions in these lists.
        FillInterface(ManagerInterface, "ext_data_control_manager_v1",
            [("create_data_source", "n", [SourceInterface]),
             ("get_data_device", "no", [DeviceInterface, SeatInterface]),
             ("destroy", "", [])],
            []);
        FillInterface(DeviceInterface, "ext_data_control_device_v1",
            [("set_selection", "?o", [SourceInterface]),
             ("destroy", "", []),
             ("set_primary_selection", "?o", [SourceInterface])],
            [("data_offer", "n", [OfferInterface]),
             ("selection", "?o", [OfferInterface]),
             ("finished", "", []),
             ("primary_selection", "?o", [OfferInterface])]);
        FillInterface(SourceInterface, "ext_data_control_source_v1",
            [("offer", "s", [IntPtr.Zero]),
             ("destroy", "", [])],
            [("send", "sh", [IntPtr.Zero, IntPtr.Zero]),
             ("cancelled", "", [])]);
        FillInterface(OfferInterface, "ext_data_control_offer_v1",
            [("receive", "sh", [IntPtr.Zero, IntPtr.Zero]),
             ("destroy", "", [])],
            [("offer", "s", [IntPtr.Zero])]);
        Available = true;
    }

    public const uint ManagerCreateDataSource = 0;
    public const uint ManagerGetDataDevice = 1;
    public const uint ManagerDestroy = 2;
    public const uint DeviceSetSelection = 0;
    public const uint DeviceDestroy = 1;
    public const uint DeviceEventDataOffer = 0;
    public const uint DeviceEventSelection = 1;
    public const uint DeviceEventFinished = 2;
    public const uint DeviceEventPrimarySelection = 3;
    public const uint SourceOffer = 0;
    public const uint SourceDestroy = 1;
    public const uint SourceEventSend = 0;
    public const uint SourceEventCancelled = 1;
    public const uint OfferReceive = 0;
    public const uint OfferDestroy = 1;
    public const uint OfferEventOffer = 0;
    public const uint RegistryBind = 0;
    public const uint RegistryEventGlobal = 0;

    public static string InterfaceName(IntPtr wlInterface) =>
        Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(wlInterface)) ?? "";

    /// <summary>Sends a request; returns the new object for constructor requests.</summary>
    public static IntPtr Send(IntPtr proxy, uint opcode, IntPtr wlInterface, uint flags, params WlArgument[] args)
    {
        fixed (WlArgument* arguments = args)
        {
            return wl_proxy_marshal_array_flags(
                proxy, opcode, wlInterface, wl_proxy_get_version(proxy), flags, arguments);
        }
    }

    /// <summary>Sends a request with string arguments, keeping them alive for the call.</summary>
    public static IntPtr SendWithString(IntPtr proxy, uint opcode, string text, params WlArgument[] rest)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return Send(proxy, opcode, IntPtr.Zero, 0, [WlArgument.Of(utf8), .. rest]);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    public static IntPtr BindGlobal(IntPtr registry, uint name, IntPtr wlInterface, uint version)
    {
        var interfaceName = Marshal.ReadIntPtr(wlInterface);
        fixed (WlArgument* arguments = new[]
               {
                   WlArgument.Of(name),
                   WlArgument.Of(interfaceName),
                   WlArgument.Of(version),
                   WlArgument.Of(IntPtr.Zero)
               })
        {
            return wl_proxy_marshal_array_flags(registry, RegistryBind, wlInterface, version, 0, arguments);
        }
    }

    public static IntPtr GetRegistry(IntPtr display)
    {
        fixed (WlArgument* arguments = new[] { WlArgument.Of(IntPtr.Zero) })
        {
            // wl_display.get_registry is opcode 1.
            return wl_proxy_marshal_array_flags(
                display, 1, RegistryInterface, wl_proxy_get_version(display), 0, arguments);
        }
    }

    /// <summary>
    /// Routes an object's events to the <see cref="IWaylandListener"/> held by
    /// <paramref name="listener"/>, which must stay allocated while the object lives.
    /// </summary>
    public static void Listen(IntPtr proxy, GCHandle listener) =>
        wl_proxy_add_dispatcher(proxy, &Dispatch, GCHandle.ToIntPtr(listener), IntPtr.Zero);

    [UnmanagedCallersOnly]
    private static int Dispatch(IntPtr listener, IntPtr proxy, uint opcode, IntPtr message, WlArgument* args)
    {
        try
        {
            ((IWaylandListener)GCHandle.FromIntPtr(listener).Target!).OnEvent(proxy, opcode, args);
        }
        catch
        {
            // An exception must never unwind into libwayland. Listeners log
            // their own failures; this is the last line of defense.
        }

        return 0;
    }

    private static IntPtr Allocate(int bytes)
    {
        var memory = Marshal.AllocHGlobal(bytes);
        new Span<byte>((void*)memory, bytes).Clear();
        return memory;
    }

    private static void FillInterface(
        IntPtr target,
        string name,
        (string Name, string Signature, IntPtr[] Types)[] requests,
        (string Name, string Signature, IntPtr[] Types)[] events)
    {
        var interfaceStruct = (byte*)target;
        *(IntPtr*)interfaceStruct = Utf8(name);
        *(int*)(interfaceStruct + 8) = 1;
        *(int*)(interfaceStruct + 12) = requests.Length;
        *(IntPtr*)(interfaceStruct + 16) = Messages(requests);
        *(int*)(interfaceStruct + 24) = events.Length;
        *(IntPtr*)(interfaceStruct + 32) = Messages(events);
    }

    // struct wl_message { const char *name; const char *signature; const struct wl_interface **types; }
    private static IntPtr Messages((string Name, string Signature, IntPtr[] Types)[] messages)
    {
        if (messages.Length == 0) return IntPtr.Zero;
        var table = (IntPtr*)Allocate(messages.Length * 24);
        for (var i = 0; i < messages.Length; i++)
        {
            var (name, signature, types) = messages[i];
            var typeTable = (IntPtr*)Allocate(Math.Max(1, types.Length) * IntPtr.Size);
            for (var t = 0; t < types.Length; t++) typeTable[t] = types[t];
            table[i * 3] = Utf8(name);
            table[i * 3 + 1] = Utf8(signature);
            table[i * 3 + 2] = (IntPtr)typeTable;
        }

        return (IntPtr)table;
    }

    private static IntPtr Utf8(string text) => Marshal.StringToCoTaskMemUTF8(text);

    [DllImport(Library)]
    public static extern IntPtr wl_display_connect(IntPtr name);

    [DllImport(Library)]
    public static extern void wl_display_disconnect(IntPtr display);

    [DllImport(Library)]
    public static extern int wl_display_get_fd(IntPtr display);

    [DllImport(Library)]
    public static extern int wl_display_roundtrip(IntPtr display);

    [DllImport(Library)]
    public static extern int wl_display_dispatch_pending(IntPtr display);

    [DllImport(Library)]
    public static extern int wl_display_flush(IntPtr display);

    [DllImport(Library)]
    public static extern int wl_display_prepare_read(IntPtr display);

    [DllImport(Library)]
    public static extern int wl_display_read_events(IntPtr display);

    [DllImport(Library)]
    public static extern void wl_display_cancel_read(IntPtr display);

    [DllImport(Library)]
    public static extern int wl_display_get_error(IntPtr display);

    [DllImport(Library)]
    public static extern void wl_proxy_destroy(IntPtr proxy);

    [DllImport(Library)]
    private static extern uint wl_proxy_get_version(IntPtr proxy);

    [DllImport(Library)]
    private static extern IntPtr wl_proxy_marshal_array_flags(
        IntPtr proxy, uint opcode, IntPtr wlInterface, uint version, uint flags, WlArgument* args);

    [DllImport(Library)]
    private static extern int wl_proxy_add_dispatcher(
        IntPtr proxy,
        delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, WlArgument*, int> dispatcher,
        IntPtr dispatcherData,
        IntPtr data);
}
