using OvenTracker.Meter;
using OvenTracker.Meter.Ble;
using Tmds.DBus.Protocol;

namespace OvenTracker.Tests;

/// <summary>BlueZ's object tree, as GetManagedObjects returns it, and finding the 121GW's characteristic in it.</summary>
public class BlueZTests
{
    const string Dev = "/org/bluez/hci0/dev_C4_BE_84_12_34_56";

    static BlueZLink.Objects Tree(Guid service, Guid characteristic) => new()
    {
        ["/org/bluez/hci0"] = new() { ["org.bluez.Adapter1"] = new() { ["Address"] = VariantValue.String("00:1A:7D:DA:71:13") } },
        [Dev] = new()
        {
            ["org.bluez.Device1"] = new()
            {
                ["Address"] = VariantValue.String("C4:BE:84:12:34:56"),
                ["Name"] = VariantValue.String("121GW"),
                ["Paired"] = VariantValue.Bool(false),
            },
        },
        ["/org/bluez/hci0/dev_E3_80_76_42_15_D2"] = new()
        {
            ["org.bluez.Device1"] = new()
            {
                ["Address"] = VariantValue.String("E3:80:76:42:15:D2"),
                ["Alias"] = VariantValue.String("M720 Triathlon"),
            },
        },
        // A battery service whose characteristic must not be mistaken for the meter's.
        [Dev + "/service0008"] = new() { ["org.bluez.GattService1"] = new() { ["UUID"] = VariantValue.String("0000180f-0000-1000-8000-00805f9b34fb") } },
        [Dev + "/service0008/char0009"] = new()
        {
            ["org.bluez.GattCharacteristic1"] = new()
            {
                ["UUID"] = VariantValue.String(characteristic.ToString()),
                ["Service"] = VariantValue.ObjectPath(new ObjectPath(Dev + "/service0008")),
            },
        },
        [Dev + "/service000c"] = new() { ["org.bluez.GattService1"] = new() { ["UUID"] = VariantValue.String(service.ToString()) } },
        [Dev + "/service000c/char000d"] = new()
        {
            ["org.bluez.GattCharacteristic1"] = new()
            {
                ["UUID"] = VariantValue.String(characteristic.ToString().ToUpperInvariant()),
                ["Service"] = VariantValue.ObjectPath(new ObjectPath(Dev + "/service000c")),
                ["Flags"] = VariantValue.Array(new[] { "read", "write", "indicate" }),
            },
        },
    };

    [Fact]
    public void ListsTheDevicesBlueZKnows()
    {
        var devices = BlueZLink.Devices(Tree(Gw121Protocol.Service, Gw121Protocol.Characteristic));
        Assert.Equal(2, devices.Count);
        Assert.Contains(new BleDevice("121GW", "C4:BE:84:12:34:56"), devices);
        Assert.Contains(new BleDevice("M720 Triathlon", "E3:80:76:42:15:D2"), devices); // the alias when there's no name
    }

    [Theory]
    [InlineData("C4:BE:84:12:34:56")]
    [InlineData("c4be84123456")]
    public void FindsTheDeviceByAddress(string address) =>
        Assert.Equal(Dev, BlueZLink.DevicePath(Tree(Gw121Protocol.Service, Gw121Protocol.Characteristic), address));

    [Fact]
    public void FindsTheCharacteristicInTheRightService()
    {
        var tree = Tree(Gw121Protocol.Service, Gw121Protocol.Characteristic);
        Assert.Equal(Dev + "/service000c/char000d", BlueZLink.CharacteristicPath(tree, Dev, Gw121Protocol.Service, Gw121Protocol.Characteristic));
        Assert.Null(BlueZLink.CharacteristicPath(tree, "/org/bluez/hci0/dev_E3_80_76_42_15_D2", Gw121Protocol.Service, Gw121Protocol.Characteristic));
        Assert.Null(BlueZLink.DevicePath(tree, "11:22:33:44:55:66"));
    }

    [Fact]
    public void BeforeItsServicesAreReadThereIsNoCharacteristic()
    {
        var tree = Tree(Gw121Protocol.Service, Gw121Protocol.Characteristic);
        foreach (var path in tree.Keys.Where(k => k.Contains("/service")).ToList()) tree.Remove(path);
        Assert.Null(BlueZLink.CharacteristicPath(tree, Dev, Gw121Protocol.Service, Gw121Protocol.Characteristic));
    }

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", "systemctl enable --now bluetooth")]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", "bluetooth group")]
    [InlineData("org.bluez.Error.NotReady", "power on")]
    [InlineData("org.bluez.Error.Failed", "in range")]
    public void ExplainsBlueZErrors(string name, string advice) =>
        Assert.Contains(advice, BlueZLink.Explain(new DBusErrorReplyException(name, "le-connection-abort-by-local")));
}
