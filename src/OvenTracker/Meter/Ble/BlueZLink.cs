using Tmds.DBus.Protocol;

namespace OvenTracker.Meter.Ble;

/// <summary>
/// A GATT characteristic on a Bluetooth LE device through BlueZ, Linux's Bluetooth stack, over the system D-Bus
/// (Tmds.DBus.Protocol, pure managed code). It finds the device among those BlueZ knows, scanning briefly if it
/// doesn't know it yet, connects (<c>org.bluez.Device1.Connect</c>), waits for its services, finds the
/// characteristic, and subscribes with <c>GattCharacteristic1.StartNotify</c>. BlueZ then sends each value as a
/// <c>PropertiesChanged</c> signal. It handles notify or indicate itself.
/// </summary>
public sealed class BlueZLink(string address, Guid service, Guid characteristic) : IBleLink
{
    const string Bluez = "org.bluez";
    const string DeviceInterface = "org.bluez.Device1";
    const string AdapterInterface = "org.bluez.Adapter1";
    const string ServiceInterface = "org.bluez.GattService1";
    const string CharacteristicInterface = "org.bluez.GattCharacteristic1";
    const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary>How long to scan for a device BlueZ doesn't know yet.</summary>
    public static TimeSpan ScanTime { get; set; } = TimeSpan.FromSeconds(8);

    DBusConnection? connection;
    IDisposable? watch;
    string? devicePath, characteristicPath;

    public string Address => address;

    /// <summary>The objects BlueZ exports: path → interface → property → value.</summary>
    public sealed class Objects : Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>;

    /// <summary>The devices BlueZ knows (paired, or seen in a scan), by name. Empty if BlueZ can't be reached.</summary>
    public static IReadOnlyList<BleDevice> KnownDevices()
    {
        try
        {
            using var c = Connect();
            return Devices(ManagedObjects(c));
        }
        catch (Exception ex) when (ex is MeterException or DBusErrorReplyException) { return []; }
    }

    /// <summary>Scans for a while, then lists the devices BlueZ knows. Empty if BlueZ can't be reached.</summary>
    public static IReadOnlyList<BleDevice> ScanDevices()
    {
        try
        {
            using var c = Connect();
            Scan(c, ManagedObjects(c));
            return Devices(ManagedObjects(c));
        }
        catch (Exception ex) when (ex is MeterException or DBusErrorReplyException) { return []; }
    }

    public static IReadOnlyList<BleDevice> Devices(Objects objects) =>
        objects.Values
            .Select(i => i.TryGetValue(DeviceInterface, out var p) ? p : null)
            .OfType<Dictionary<string, VariantValue>>()
            .Select(p => (Address: Str(p, "Address"), Name: Str(p, "Name") ?? Str(p, "Alias")))
            .Where(d => d.Address is not null && BleLinks.NormalizeAddress(d.Address) is not null)
            .Select(d => new BleDevice(d.Name ?? "", BleLinks.NormalizeAddress(d.Address!)!))
            .DistinctBy(d => d.Address)
            .ToList();

    /// <summary>The device object for an address, e.g. /org/bluez/hci0/dev_C4_BE_84_12_34_56. Null if BlueZ doesn't know it.</summary>
    public static string? DevicePath(Objects objects, string address) =>
        objects.FirstOrDefault(o => o.Value.TryGetValue(DeviceInterface, out var p)
            && BleLinks.NormalizeAddress(Str(p, "Address") ?? "") == BleLinks.NormalizeAddress(address)).Key;

    /// <summary>The characteristic's object under a device, in the given service. Null if it isn't there (yet).</summary>
    public static string? CharacteristicPath(Objects objects, string devicePath, Guid service, Guid characteristic)
    {
        foreach (var (path, interfaces) in objects)
        {
            if (!path.StartsWith(devicePath + "/", StringComparison.Ordinal)) continue;
            if (!interfaces.TryGetValue(CharacteristicInterface, out var p)) continue;
            if (!Guid.TryParse(Str(p, "UUID"), out var uuid) || uuid != characteristic) continue;
            var servicePath = p.TryGetValue("Service", out var s) ? s.GetObjectPathAsString() : null;
            if (servicePath is null || !objects.TryGetValue(servicePath, out var si) || !si.TryGetValue(ServiceInterface, out var sp)) continue;
            if (Guid.TryParse(Str(sp, "UUID"), out var su) && su == service) return path;
        }
        return null;
    }

    static string? Str(Dictionary<string, VariantValue> props, string name) =>
        props.TryGetValue(name, out var v) && v.Type == VariantValueType.String ? v.GetString() : null;

    public void Open(Action<byte[]> received)
    {
        Dispose();
        try
        {
            connection = Connect();
            var objects = ManagedObjects(connection);
            devicePath = DevicePath(objects, address);
            if (devicePath is null)
            {
                Scan(connection, objects);
                objects = ManagedObjects(connection);
                devicePath = DevicePath(objects, address)
                    ?? throw new MeterException($"BlueZ can't see {address}. Check the meter is on with Bluetooth on (BT on its display), and in range.");
            }

            Call(connection, devicePath, DeviceInterface, "Connect");
            // Wait for BlueZ to read the device's services, then find the characteristic.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (true)
            {
                objects = ManagedObjects(connection);
                characteristicPath = CharacteristicPath(objects, devicePath, service, characteristic);
                if (characteristicPath is not null) break;
                if (DateTime.UtcNow > deadline) throw new MeterException($"{address} connected, but doesn't offer the meter's GATT characteristic.");
                Thread.Sleep(250);
            }

            var rule = new MatchRule
            {
                Type = MessageType.Signal,
                Sender = Bluez,
                Path = characteristicPath,
                Interface = PropertiesInterface,
                Member = "PropertiesChanged",
            };
            watch = connection.AddMatchAsync(rule, (Message m, object? _) => ValueFrom(m),
                (Notification<byte[]?> n) =>
                {
                    if (n.HasValue && n.Value is { Length: > 0 } value) received(value);
                }, emitOnCapturedContext: false).AsTask().GetAwaiter().GetResult();
            Call(connection, characteristicPath, CharacteristicInterface, "StartNotify");
        }
        catch (DBusErrorReplyException ex)
        {
            Dispose();
            throw new MeterException(Explain(ex), ex);
        }
    }

    /// <summary>The new value from a PropertiesChanged signal: (s interface, a{sv} changed, as invalidated).</summary>
    static byte[]? ValueFrom(Message m)
    {
        var r = m.GetBodyReader();
        var iface = r.ReadString();
        if (iface != CharacteristicInterface) return null;
        var changed = r.ReadDictionaryOfStringToVariantValue();
        return changed.TryGetValue("Value", out var v) && v.Type == VariantValueType.Array ? v.GetArray<byte>() : null;
    }

    static DBusConnection Connect()
    {
        if (DBusAddress.System is not { } bus) throw new MeterException(NoBusHelp);
        var c = new DBusConnection(bus);
        try
        {
            c.ConnectAsync().AsTask().GetAwaiter().GetResult();
            return c;
        }
        catch (Exception ex)
        {
            c.Dispose();
            throw new MeterException($"{NoBusHelp} ({ex.Message})", ex);
        }
    }

    const string NoBusHelp = "Can't reach the system D-Bus, which BlueZ uses. Is this a full Linux system with Bluetooth (not WSL)?";

    static Objects ManagedObjects(DBusConnection c)
    {
        MessageBuffer msg;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bluez, "/", "org.freedesktop.DBus.ObjectManager", "GetManagedObjects");
            msg = w.CreateMessage();
        }
        return c.CallMethodAsync(msg, (Message m, object? _) =>
        {
            var objects = new Objects();
            var r = m.GetBodyReader();
            var all = r.ReadDictionaryStart();
            while (r.HasNext(all))
            {
                var path = r.ReadObjectPathAsString();
                var interfaces = new Dictionary<string, Dictionary<string, VariantValue>>();
                var each = r.ReadDictionaryStart();
                while (r.HasNext(each))
                {
                    var name = r.ReadString();
                    interfaces[name] = r.ReadDictionaryOfStringToVariantValue();
                }
                objects[path] = interfaces;
            }
            return objects;
        }).GetAwaiter().GetResult();
    }

    static void Call(DBusConnection c, string path, string iface, string member)
    {
        MessageBuffer msg;
        using (var w = c.GetMessageWriter())
        {
            w.WriteMethodCallHeader(Bluez, path, iface, member);
            msg = w.CreateMessage();
        }
        c.CallMethodAsync(msg).GetAwaiter().GetResult();
    }

    /// <summary>Scans with the first adapter for a while, so BlueZ sees a device it didn't know.</summary>
    static void Scan(DBusConnection c, Objects objects)
    {
        var adapter = objects.FirstOrDefault(o => o.Value.ContainsKey(AdapterInterface)).Key
            ?? throw new MeterException("BlueZ has no Bluetooth adapter. Check one is plugged in and powered (bluetoothctl power on).");
        try { Call(c, adapter, AdapterInterface, "StartDiscovery"); }
        catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.bluez.Error.InProgress") { }
        Thread.Sleep(ScanTime);
        try { Call(c, adapter, AdapterInterface, "StopDiscovery"); } catch (DBusErrorReplyException) { }
    }

    /// <summary>A BlueZ or D-Bus error, in terms of what to do about it.</summary>
    public static string Explain(DBusErrorReplyException ex) => ex.ErrorName switch
    {
        "org.freedesktop.DBus.Error.ServiceUnknown" => "BlueZ isn't running. Install bluez and start it (sudo systemctl enable --now bluetooth).",
        "org.freedesktop.DBus.Error.AccessDenied" => "BlueZ refused access. Add yourself to the bluetooth group (sudo usermod -aG bluetooth $USER), then log in again.",
        "org.bluez.Error.NotReady" => "The Bluetooth adapter is off. Turn it on (bluetoothctl power on).",
        "org.bluez.Error.Failed" or "org.bluez.Error.NotAvailable" => $"BlueZ couldn't connect to the meter: {ex.ErrorMessage}. Check it's on, with Bluetooth on, and in range.",
        _ => $"BlueZ: {ex.ErrorMessage} ({ex.ErrorName})",
    };

    public void Dispose()
    {
        if (connection is { } c)
        {
            if (characteristicPath is not null) { try { Call(c, characteristicPath, CharacteristicInterface, "StopNotify"); } catch { } }
            if (devicePath is not null) { try { Call(c, devicePath, DeviceInterface, "Disconnect"); } catch { } }
            watch?.Dispose();
            c.Dispose();
        }
        watch = null;
        connection = null;
        devicePath = characteristicPath = null;
    }
}
