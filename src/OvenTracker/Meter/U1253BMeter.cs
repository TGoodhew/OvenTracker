using System.IO.Ports;
using System.Text;

namespace OvenTracker.Meter;

/// <summary>A U1253B reached through a serial port: the U1177A's Bluetooth SPP port, or a U1173 cable. Found and opened by <see cref="U1253BDriver"/>.</summary>
public sealed class U1253BMeter(string portName) : ITemperatureMeter
{
    SerialPort? port;
    MeterConfig? config;
    int readsSinceConfig;

    public string PortName => portName;
    public MeterConfig? LastConfig => config;
    public string Location => portName;

    /// <summary>The configuration is re-read every this many readings, so turning the dial is noticed quickly.</summary>
    public int ConfigEvery { get; init; } = 5;

    public int OpenAttempts { get; init; } = 4;

    public MeterIdentity Connect()
    {
        Disconnect();
        var p = new SerialPort(portName, U1253BProtocol.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            NewLine = "\n",
            Encoding = Encoding.ASCII,
            ReadTimeout = 3000,
            WriteTimeout = 3000,
        };
        // Opening the outgoing Bluetooth port is what makes Windows connect to the adapter: a second or two.
        // Straight after the port was closed, the old link may still be coming down ("Element not found"),
        // so a failed open is tried again after a pause.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                p.Open();
                break;
            }
            catch (Exception ex) when (attempt < OpenAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1.5 * attempt));
            }
            catch (Exception ex)
            {
                p.Dispose();
                throw new MeterException($"Could not open {portName}: {ex.Message}", ex);
            }
        }
        port = p;
        var idn = QueryWithRetry(U1253BProtocol.Identify, 3);
        ReadConfig();
        return U1253BProtocol.ParseIdentity(idn);
    }

    public void Disconnect()
    {
        if (port is null) return;
        try { port.Close(); } catch { /* the link may already be gone */ }
        port.Dispose();
        port = null;
        config = null;
    }

    public MeterConfig ReadConfig()
    {
        config = U1253BProtocol.ParseConfig(Query(U1253BProtocol.Configuration));
        readsSinceConfig = 0;
        return config;
    }

    public MeterReading Read()
    {
        if (config is null || ++readsSinceConfig >= ConfigEvery) ReadConfig();
        var reply = Query(U1253BProtocol.Fetch);
        return U1253BProtocol.ToReading(DateTimeOffset.UtcNow, reply, config);
    }

    public double? ReadBatteryPercent() =>
        U1253BProtocol.TryParseNumber(Query(U1253BProtocol.Battery), out var v) ? v : null;

    public void Beep() => Send(U1253BProtocol.BeepCommand);

    string QueryWithRetry(string command, int attempts)
    {
        for (var i = 1; ; i++)
        {
            try { return Query(command); }
            catch (TimeoutException) when (i < attempts) { }
        }
    }

    void Send(string command)
    {
        var p = port ?? throw new MeterException("Not connected.");
        p.Write(command + "\n");
    }

    string Query(string command)
    {
        var p = port ?? throw new MeterException("Not connected.");
        if (p.BytesToRead > 0) p.DiscardInBuffer();
        p.Write(command + "\n");
        var deadline = DateTime.UtcNow.AddMilliseconds(p.ReadTimeout);
        while (true)
        {
            var line = p.ReadLine().Trim('\r', '\n', ' ');
            if (line.Length == 0) continue;
            if (U1253BProtocol.IsCommandError(line)) throw new MeterException($"The meter did not accept {command}.");
            if (U1253BProtocol.IsEvent(line))
            {
                config = null; // the dial or a button changed something
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                continue;
            }
            return line;
        }
    }

    public void Dispose() => Disconnect();
}
