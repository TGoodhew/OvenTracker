using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace OvenTracker.Meter.Ble;

/// <summary>
/// A GATT characteristic on a paired Bluetooth LE device, through the Win32 Bluetooth GATT API
/// (BluetoothApis.dll). The app's target stays plain .NET, which the WinRT Bluetooth API wouldn't allow.
/// <para>
/// The device must be paired in Windows Bluetooth settings first: pairing makes Windows add a device
/// interface for each of its GATT services. This class opens the interface of the wanted service, finds
/// the characteristic, writes its Client Characteristic Configuration descriptor (indications if the
/// characteristic has them, otherwise notifications), and registers for value changes.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGattLink(string address, Guid service, Guid characteristic, Guid? write = null) : IBleLink
{
    SafeFileHandle? handle;
    Native.GattCharacteristic? writeCharacteristic;
    IntPtr eventHandle;
    Native.GattEventCallback? callback; // kept alive while Windows holds it
    Action<byte[]>? received;

    public string Address => address;

    /// <summary>Paired Bluetooth LE devices, from Windows' device list (their "BTHLE\DEV_&lt;address&gt;" entries).</summary>
    public static IReadOnlyList<BleDevice> PairedDevices()
    {
        var list = new List<BleDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'BTHLE\\DEV[_]%'");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                var id = o["PNPDeviceID"] as string ?? "";
                var name = o["Name"] as string ?? "";
                var at = id.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || id.Length < at + 16) continue;
                if (BleLinks.NormalizeAddress(id.Substring(at + 4, 12)) is { } addr) list.Add(new BleDevice(name, addr));
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { }
        return list.DistinctBy(d => d.Address).ToList();
    }

    public void Open(Action<byte[]> onData)
    {
        Dispose();
        received = onData;
        var path = ServiceInterfacePath(service, address)
            ?? throw new MeterException($"Windows has no paired Bluetooth LE device {address} with the meter's service. Pair it in Windows Bluetooth settings (Add device, Bluetooth) first.");
        handle = Native.CreateFile(path, Native.GenericRead | Native.GenericWrite, Native.FileShareRead | Native.FileShareWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new MeterException($"Couldn't open the Bluetooth LE device {address} (error {Marshal.GetLastWin32Error()}).");

        var svc = Services(handle).FirstOrDefault(s => s.ServiceUuid.Guid == service);
        if (svc.ServiceUuid.Guid != service) throw new MeterException($"The device {address} doesn't offer the meter's GATT service.");
        var chars = Characteristics(handle, svc);
        var ch = chars.FirstOrDefault(c => c.CharacteristicUuid.Guid == characteristic);
        if (ch.CharacteristicUuid.Guid != characteristic) throw new MeterException($"The device {address} doesn't have the meter's GATT characteristic.");
        if (write is { } w)
        {
            var wc = chars.FirstOrDefault(c => c.CharacteristicUuid.Guid == w);
            if (wc.CharacteristicUuid.Guid != w) throw new MeterException($"The device {address} doesn't have the meter's command characteristic.");
            writeCharacteristic = wc;
        }

        // Subscribe: write the CCCD.
        var cccd = Descriptors(handle, ch).FirstOrDefault(d => d.DescriptorType == Native.ClientCharacteristicConfiguration);
        if (cccd.DescriptorType != Native.ClientCharacteristicConfiguration) throw new MeterException("The meter's characteristic has no notification descriptor.");
        var value = new byte[Native.DescriptorValueSize];
        BitConverter.GetBytes(Native.ClientCharacteristicConfiguration).CopyTo(value, 0);
        value[Native.SubscribeToNotificationOffset] = (byte)(ch.IsIndicatable == 0 ? 1 : 0);
        value[Native.SubscribeToIndicationOffset] = (byte)(ch.IsIndicatable != 0 ? 1 : 0);
        Check(Native.BluetoothGATTSetDescriptorValue(handle, ref cccd, value, 0), "subscribe to the meter");

        // Register for the values.
        var reg = new Native.ValueChangedEventRegistration { NumCharacteristics = 1, Characteristic = ch };
        callback = OnEvent;
        Check(Native.BluetoothGATTRegisterEvent(handle, Native.CharacteristicValueChangedEvent, ref reg, callback, IntPtr.Zero, out eventHandle, 0),
            "register for the meter's data");
    }

    /// <summary>Writes to the command characteristic: without a response when it allows that (as the meters' own apps do).</summary>
    public void Write(byte[] data)
    {
        if (handle is null || writeCharacteristic is not { } wc) throw new MeterException("The Bluetooth LE link isn't open for writing.");
        // BTH_LE_GATT_CHARACTERISTIC_VALUE: ULONG DataSize, then the bytes.
        var value = new byte[Math.Max(8, 4 + data.Length)];
        BitConverter.GetBytes(data.Length).CopyTo(value, 0);
        data.CopyTo(value, 4);
        var flags = wc.IsWritableWithoutResponse != 0 ? Native.WriteWithoutResponse : 0u;
        Check(Native.BluetoothGATTSetCharacteristicValue(handle, ref wc, value, 0, flags), "send the meter a command");
    }

    void OnEvent(int eventType, IntPtr parameter, IntPtr context)
    {
        if (eventType != Native.CharacteristicValueChangedEvent || parameter == IntPtr.Zero) return;
        // BLUETOOTH_GATT_VALUE_CHANGED_EVENT: USHORT handle; size_t size; PBTH_LE_GATT_CHARACTERISTIC_VALUE value.
        var valuePtr = Marshal.ReadIntPtr(parameter, 2 * IntPtr.Size);
        if (valuePtr == IntPtr.Zero) return;
        var size = Marshal.ReadInt32(valuePtr);
        if (size <= 0 || size > 512) return;
        var data = new byte[size];
        Marshal.Copy(valuePtr + 4, data, 0, size);
        try { received?.Invoke(data); } catch { /* never throw back into Windows */ }
    }

    static Native.GattService[] Services(SafeFileHandle h)
    {
        Native.BluetoothGATTGetServices(h, 0, null, out var needed, 0);
        if (needed == 0) return [];
        var items = new Native.GattService[needed];
        Check(Native.BluetoothGATTGetServices(h, needed, items, out _, 0), "list the meter's services");
        return items;
    }

    static Native.GattCharacteristic[] Characteristics(SafeFileHandle h, Native.GattService s)
    {
        Native.BluetoothGATTGetCharacteristics(h, ref s, 0, null, out var needed, 0);
        if (needed == 0) return [];
        var items = new Native.GattCharacteristic[needed];
        Check(Native.BluetoothGATTGetCharacteristics(h, ref s, needed, items, out _, 0), "list the meter's characteristics");
        return items;
    }

    static Native.GattDescriptor[] Descriptors(SafeFileHandle h, Native.GattCharacteristic c)
    {
        Native.BluetoothGATTGetDescriptors(h, ref c, 0, null, out var needed, 0);
        if (needed == 0) return [];
        var items = new Native.GattDescriptor[needed];
        Check(Native.BluetoothGATTGetDescriptors(h, ref c, needed, items, out _, 0), "list the meter's descriptors");
        return items;
    }

    static void Check(int hr, string what)
    {
        if (hr != 0) throw new MeterException($"Couldn't {what} over Bluetooth LE (0x{hr:X8}).");
    }

    /// <summary>The device interface Windows made for this service on this device: its path holds the address.</summary>
    static string? ServiceInterfacePath(Guid serviceGuid, string address)
    {
        var want = address.Replace(":", "").ToLowerInvariant();
        var set = Native.SetupDiGetClassDevs(ref serviceGuid, IntPtr.Zero, IntPtr.Zero, Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new Native.DeviceInterfaceData { cbSize = Marshal.SizeOf<Native.DeviceInterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref serviceGuid, i, ref data)) return null;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size == 0) continue;
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6); // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buffer + 4);
                    if (path is not null && path.Contains(want, StringComparison.OrdinalIgnoreCase)) return path;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    public void Dispose()
    {
        if (eventHandle != IntPtr.Zero)
        {
            try { Native.BluetoothGATTUnregisterEvent(eventHandle, 0); } catch { }
            eventHandle = IntPtr.Zero;
        }
        handle?.Dispose();
        handle = null;
        writeCharacteristic = null;
    }

    static class Native
    {
        public const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
        public const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
        public const int CharacteristicValueChangedEvent = 0;
        public const int ClientCharacteristicConfiguration = 2;
        public const uint WriteWithoutResponse = 0x20; // BLUETOOTH_GATT_FLAG_WRITE_WITHOUT_RESPONSE

        // BTH_LE_GATT_DESCRIPTOR_VALUE: type (4), BTH_LE_UUID (20), a 48-byte union, DataSize (4), Data (1), padded to 80.
        public const int DescriptorValueSize = 80, SubscribeToNotificationOffset = 24, SubscribeToIndicationOffset = 25;

        [StructLayout(LayoutKind.Explicit, Size = 20)]
        public struct BthLeUuid
        {
            [FieldOffset(0)] public byte IsShortUuid;
            [FieldOffset(4)] public ushort ShortUuid;
            [FieldOffset(4)] public Guid LongUuid;

            /// <summary>The UUID, with a 16-bit one expanded on the Bluetooth base UUID.</summary>
            public readonly Guid Guid => IsShortUuid != 0 ? new Guid(ShortUuid, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB) : LongUuid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GattService
        {
            public BthLeUuid ServiceUuid;
            public ushort AttributeHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GattCharacteristic
        {
            public ushort ServiceHandle;
            public BthLeUuid CharacteristicUuid;
            public ushort AttributeHandle;
            public ushort CharacteristicValueHandle;
            public byte IsBroadcastable, IsReadable, IsWritable, IsWritableWithoutResponse, IsSignedWritable, IsNotifiable, IsIndicatable, HasExtendedProperties;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GattDescriptor
        {
            public ushort ServiceHandle;
            public ushort CharacteristicHandle;
            public int DescriptorType;
            public BthLeUuid DescriptorUuid;
            public ushort AttributeHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ValueChangedEventRegistration
        {
            public ushort NumCharacteristics;
            public GattCharacteristic Characteristic;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void GattEventCallback(int eventType, IntPtr eventOutParameter, IntPtr context);

        [DllImport("BluetoothApis.dll", ExactSpelling = true)]
        public static extern int BluetoothGATTGetServices(SafeFileHandle device, ushort count, [Out] GattService[]? services, out ushort needed, uint flags);

        [DllImport("BluetoothApis.dll", ExactSpelling = true)]
        public static extern int BluetoothGATTGetCharacteristics(SafeFileHandle device, ref GattService service, ushort count, [Out] GattCharacteristic[]? characteristics, out ushort needed, uint flags);

        [DllImport("BluetoothApis.dll", ExactSpelling = true)]
        public static extern int BluetoothGATTGetDescriptors(SafeFileHandle device, ref GattCharacteristic characteristic, ushort count, [Out] GattDescriptor[]? descriptors, out ushort needed, uint flags);

        [DllImport("BluetoothApis.dll", ExactSpelling = true)]
        public static extern int BluetoothGATTSetDescriptorValue(SafeFileHandle device, ref GattDescriptor descriptor, byte[] value, uint flags);

        [DllImport("BluetoothApis.dll", ExactSpelling = true)]
        public static extern int BluetoothGATTSetCharacteristicValue(SafeFileHandle device, ref GattCharacteristic characteristic, byte[] value, ulong reliableWriteContext, uint flags);

        [DllImport("BluetoothApis.dll", ExactSpelling = true)]
        public static extern int BluetoothGATTRegisterEvent(SafeFileHandle service, int eventType, ref ValueChangedEventRegistration registration, GattEventCallback callback, IntPtr context, out IntPtr eventHandle, uint flags);

        [DllImport("BluetoothApis.dll", ExactSpelling = true)]
        public static extern int BluetoothGATTUnregisterEvent(IntPtr eventHandle, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid classGuid, uint index, ref DeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr deviceInfo);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
