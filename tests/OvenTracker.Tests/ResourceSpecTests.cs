using System.IO.Ports;
using OvenTracker.Commands;
using OvenTracker.Meter;

namespace OvenTracker.Tests;

public class ResourceSpecTests
{
    [Theory]
    [InlineData("COM5", "COM5")]
    [InlineData("/dev/rfcomm0", "/dev/rfcomm0")]
    [InlineData("/dev/ttyUSB0", "/dev/ttyUSB0")]
    [InlineData(@"\\.\COM10", @"\\.\COM10")]
    public void ASerialPortOnItsOwnUsesTheDriversSettings(string text, string port)
    {
        var r = ResourceSpec.Parse(text)!;
        Assert.Equal(Transport.Serial, r.Transport);
        Assert.Equal(port, r.Address);
        Assert.Null(r.Settings);
        Assert.False(r.Visa);
        Assert.Equal(SerialSettings.U1250, r.SettingsOr(SerialSettings.U1250));
    }

    [Fact]
    public void SerialSettingsCanBeGivenWithThePort()
    {
        // The 34401A's factory settings: 9600 baud, even parity, 7 data bits, 2 stop bits, DTR/DSR.
        var r = ResourceSpec.Parse("COM3:9600,E,7,2,dtr")!;
        Assert.Equal("COM3", r.Address);
        Assert.Equal(new SerialSettings(9600, Parity.Even, 7, StopBits.Two), r.Settings);
        Assert.True(r.DtrDsr);

        var linux = ResourceSpec.Parse("/dev/ttyUSB0:19200,n,8,1,rts")!;
        Assert.Equal("/dev/ttyUSB0", linux.Address);
        Assert.Equal(new SerialSettings(19200, Parity.None, 8, StopBits.One, Handshake.RequestToSend), linux.Settings);
        Assert.False(linux.DtrDsr);

        Assert.Equal(StopBits.OnePointFive, ResourceSpec.Parse("COM1:4800,O,7,1.5")!.Settings!.StopBits);
    }

    [Theory]
    [InlineData("GPIB0::22::INSTR", "GPIB0::22::INSTR")]
    [InlineData("GPIB::22", "GPIB0::22::INSTR")]
    [InlineData("gpib:22", "GPIB0::22::INSTR")]
    [InlineData("GPIB1::5::INSTR", "GPIB1::5::INSTR")]
    [InlineData("GPIB0::22::96::INSTR", "GPIB0::22::96::INSTR")] // a secondary address
    public void GpibAddressesBecomeVisaResources(string text, string visa)
    {
        var r = ResourceSpec.Parse(text)!;
        Assert.Equal(Transport.Gpib, r.Transport);
        Assert.Equal(visa, r.Address);
        Assert.True(r.Visa);
    }

    [Theory]
    [InlineData("ASRL3::INSTR", Transport.Serial)]
    [InlineData("USB0::0x2A8D::0x0101::MY12345678::INSTR", Transport.Usb)]
    [InlineData("TCPIP0::192.168.1.20::inst0::INSTR", Transport.Lan)]
    public void OtherVisaResourcesKeepTheirTransport(string text, Transport transport)
    {
        var r = ResourceSpec.Parse(text)!;
        Assert.Equal(transport, r.Transport);
        Assert.True(r.Visa);
        Assert.Equal(text, r.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("COM3:fast")]          // a colon but no settings
    [InlineData("COM3:9600,X,8,1")]    // no such parity
    [InlineData("COM3:9600,N,9,1")]    // 9 data bits
    [InlineData("GPIB0::31::INSTR")]   // GPIB addresses go to 30
    public void MistakesAreRejected(string text) => Assert.Null(ResourceSpec.Parse(text));

    [Fact]
    public void SettingsReadBackAsTheyAreWritten()
    {
        Assert.Equal("9600,E,7,2", new SerialSettings(9600, Parity.Even, 7, StopBits.Two).ToString());
        Assert.Equal("9600,N,8,1", SerialSettings.U1250.ToString());
        var round = ResourceSpec.Parse($"COM3:{new SerialSettings(2400, Parity.Odd, 7, StopBits.One, Handshake.XOnXOff)}")!;
        Assert.Equal(new SerialSettings(2400, Parity.Odd, 7, StopBits.One, Handshake.XOnXOff), round.Settings);
    }

    [Fact]
    public void TheU1253BOnlyConnectsThroughASerialPort()
    {
        Assert.Null(U1253BDriver.Check("COM5"));
        Assert.Null(U1253BDriver.Check("/dev/rfcomm0:9600,N,8,1"));
        Assert.Contains("not GPIB", U1253BDriver.Check("GPIB0::22::INSTR"));
        Assert.Contains("isn't a serial port", U1253BDriver.Check("COM5:oops"));
        // Probing a GPIB address says so, without opening anything.
        var r = new U1253BDriver().Probe("GPIB0::22::INSTR");
        Assert.Null(r.Identity);
        Assert.Contains("serial port", r.Error);
    }

    [Fact]
    public void AMeterOnUnusualSettingsRemembersThem()
    {
        Assert.Equal("COM5", new U1253BMeter("COM5").Location);
        Assert.Equal("COM5:19200,N,8,1", new U1253BMeter("COM5", new SerialSettings(19200)).Location);
    }

    [Fact]
    public void RunRejectsAResourceItCantRead()
    {
        var result = new RunSettings { Resource = "COM5:bad" }.Validate();
        Assert.False(result.Successful);
        Assert.Contains("GPIB0::22::INSTR", result.Message);
        Assert.True(new RunSettings { Resource = "COM5:9600,N,8,1" }.Validate().Successful);
    }
}
