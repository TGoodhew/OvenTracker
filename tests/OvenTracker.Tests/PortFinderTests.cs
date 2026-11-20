using OvenTracker.Commands;
using OvenTracker.Meter;

namespace OvenTracker.Tests;

public class PortFinderTests
{
    static PortInfo Port(string name, bool bluetooth = false) => new(name, "", bluetooth, bluetooth, null);

    [Fact]
    public void WindowsPortsSortBluetoothFirstThenByNumber()
    {
        var sorted = PortFinder.Sort([Port("COM10"), Port("COM3"), Port("COM5", bluetooth: true), Port("COM2")]);
        Assert.Equal(["COM5", "COM2", "COM3", "COM10"], sorted.Select(p => p.Port));
    }

    [Fact]
    public void LinuxDevicesSortBluetoothFirstThenByKindAndNumber()
    {
        var sorted = PortFinder.Sort([Port("/dev/ttyUSB10"), Port("/dev/ttyS0"), Port("/dev/ttyUSB2"), Port("/dev/rfcomm1", true), Port("/dev/rfcomm0", true), Port("/dev/ttyACM0")]);
        Assert.Equal(["/dev/rfcomm0", "/dev/rfcomm1", "/dev/ttyACM0", "/dev/ttyS0", "/dev/ttyUSB2", "/dev/ttyUSB10"], sorted.Select(p => p.Port));
    }

    [Theory]
    [InlineData("/dev/rfcomm0", null, true, "Bluetooth serial link (RFCOMM)")]
    [InlineData("/dev/ttyUSB0", "USB-Serial Controller D", false, "USB-Serial Controller D")]
    [InlineData("/dev/ttyUSB1", null, false, "USB serial adapter")]
    [InlineData("/dev/ttyACM0", null, false, "USB serial device")]
    [InlineData("/dev/ttyS0", null, false, "Serial port")]
    public void LinuxDevicesAreDescribedByKind(string path, string? product, bool bluetooth, string description)
    {
        var p = PortFinder.DescribeLinux(path, product);
        Assert.Equal(path, p.Port);
        Assert.Equal(bluetooth, p.IsBluetoothOutgoing);
        Assert.Equal(description, p.Description);
    }

    [Fact]
    public void PortNamesKeepTheirCaseExceptOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("COM5", PortFinder.Normalize(" com5 "));
            Assert.Equal(0, PortFinder.NameComparer.Compare("COM5", "com5"));
        }
        else
        {
            Assert.Equal("/dev/rfcomm0", PortFinder.Normalize("/dev/rfcomm0"));
            Assert.NotEqual(0, PortFinder.NameComparer.Compare("/dev/ttyUSB0", "/dev/ttyusb0"));
        }
    }

    [Fact]
    public void ADeniedPortSaysWhatToDoOnThisSystem()
    {
        var text = U1253BDriver.Explain(new MeterException("Could not open the port", new UnauthorizedAccessException("Access denied")));
        Assert.Contains(OperatingSystem.IsWindows() ? "in use" : "dialout", text);
        Assert.Equal("no reply", U1253BDriver.Explain(new TimeoutException()));
    }

    [Fact]
    public void TheDriverExplainsItselfForThisSystem()
    {
        var d = new U1253BDriver();
        Assert.Contains(OperatingSystem.IsWindows() ? "COM5" : "/dev/rfcomm0", d.ResourceHelp);
        Assert.Contains(OperatingSystem.IsWindows() ? "Windows Bluetooth settings" : "rfcomm bind", d.NotFoundHelp);
    }

    [Fact]
    public void RunsAreSavedUnderAnOvenTrackerFolder()
    {
        var folder = RunCommand.RunsFolder();
        Assert.Equal("OvenTracker", Path.GetFileName(folder));
        Assert.True(Path.IsPathRooted(folder));
    }
}
