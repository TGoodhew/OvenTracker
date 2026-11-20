using OvenTracker.Meter;

namespace OvenTracker.Tests;

public class ProtocolTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void ReadsTheConfigurationAU1253BSendsInTemperatureMode()
    {
        // Captured from a U1253B (fw V2.19) through a U1177A.
        var c = U1253BProtocol.ParseConfig("\"TEMP:K CEL\"");
        Assert.True(c.IsTemperature);
        Assert.Equal(TempUnit.Celsius, c.Unit);
        Assert.Equal("TEMP:K CEL", c.Description);
        Assert.Equal("type K thermocouple", c.Sensor?.Name);

        Assert.Equal(TempUnit.Fahrenheit, U1253BProtocol.ParseConfig("\"TEMP:K FAR\"").Unit);
        Assert.True(U1253BProtocol.ParseConfig("\"T1:K CEL\"").IsTemperature);
        Assert.Equal("type J thermocouple", U1253BProtocol.ParseConfig("\"TEMP:J CEL\"").Sensor?.Name);
        Assert.Null(U1253BProtocol.ParseConfig("\"VOLT:DC +5.000000E+00,+1.000000E-04\"").Sensor);
    }

    [Theory]
    [InlineData("\"VOLT:DC +5.000000E+00,+1.000000E-04\"")]
    [InlineData("\"RES +5.000000E+02,+1.000000E-02\"")]
    [InlineData("\"FREQ\"")]
    public void OtherFunctionsAreNotTemperature(string reply) =>
        Assert.False(U1253BProtocol.ParseConfig(reply).IsTemperature);

    [Fact]
    public void ConvertsReadingsToCelsius()
    {
        var cel = U1253BProtocol.ParseConfig("\"TEMP:K CEL\"");
        var far = U1253BProtocol.ParseConfig("\"TEMP:K FAR\"");

        var r = U1253BProtocol.ToReading(Now, "+1.65446000E+02", cel);
        Assert.Equal(ReadingStatus.Ok, r.Status);
        Assert.Equal(165.446, r.Celsius!.Value, 6);

        Assert.Equal(176.6667, U1253BProtocol.ToReading(Now, "+3.50000000E+02", far).Celsius!.Value, 3);
    }

    [Fact]
    public void AnOpenThermocoupleIsOverloadNotAReading()
    {
        var cel = U1253BProtocol.ParseConfig("\"TEMP:K CEL\"");
        Assert.Equal(ReadingStatus.Overload, U1253BProtocol.ToReading(Now, "+9.90000000E+37", cel).Status);
        Assert.Equal(ReadingStatus.Overload, U1253BProtocol.ToReading(Now, "-9.90000000E+37", cel).Status);
        Assert.Null(U1253BProtocol.ToReading(Now, "+9.90000000E+37", cel).Celsius);
    }

    [Fact]
    public void AReadingInTheWrongModeIsNotUsed()
    {
        var volts = U1253BProtocol.ParseConfig("\"VOLT:DC +5.000000E+00,+1.000000E-04\"");
        Assert.Equal(ReadingStatus.NotTemperature, U1253BProtocol.ToReading(Now, "+1.20000000E+00", volts).Status);
        Assert.Equal(ReadingStatus.Error, U1253BProtocol.ToReading(Now, "garbage", volts).Status);
    }

    [Fact]
    public void ParsesTheIdentity()
    {
        var id = U1253BProtocol.ParseIdentity("Agilent Technologies,U1253B,MY50490064,V2.19");
        Assert.Equal("U1253B", id.Model);
        Assert.Equal("MY50490064", id.Serial);
        Assert.Equal("V2.19", id.Firmware);
    }

    [Theory]
    [InlineData("*0", true)]
    [InlineData("*7", true)]
    [InlineData("*L", true)]
    [InlineData("*E", false)]
    [InlineData("+1.0E+00", false)]
    [InlineData("\"TEMP:K CEL\"", false)]
    public void TellsEventsFromReplies(string line, bool isEvent) => Assert.Equal(isEvent, U1253BProtocol.IsEvent(line));
}
