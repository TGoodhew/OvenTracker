using System.IO.Ports;
using System.Text;

namespace OvenTracker.Meter;

/// <summary>A line-at-a-time link to a SCPI instrument: GPIB through VISA, or a serial port. Tests use a fake.</summary>
public interface IScpiPort : IDisposable
{
    /// <summary>Where it is, e.g. "GPIB0::22::INSTR" or "COM3:9600,E,7,2".</summary>
    string Location { get; }

    /// <summary>Serial (RS-232) rather than GPIB: the 34401A then needs SYST:REM, and has no device clear.</summary>
    bool IsSerial { get; }

    void Open();
    void WriteLine(string command);

    /// <summary>One reply, without its line ending. Throws <see cref="TimeoutException"/> if none comes.</summary>
    string ReadLine();

    /// <summary>Empties anything waiting, and on GPIB sends the device clear.</summary>
    void Clear();

    /// <summary>Gives the front panel back (GPIB go-to-local, or SYST:LOC on serial), as far as it can.</summary>
    void Release();
}

/// <summary>An instrument through VISA (GPIB, USB-TMC, LAN, ASRL).</summary>
public sealed class VisaScpiPort(string resource, TimeSpan timeout) : IScpiPort
{
    Visa.VisaSession? session;

    public string Location => resource;
    public bool IsSerial => resource.StartsWith("ASRL", StringComparison.OrdinalIgnoreCase);

    public void Open()
    {
        Dispose();
        session = Visa.Open(resource, timeout);
    }

    Visa.VisaSession S => session ?? throw new MeterException("Not connected.");

    public void WriteLine(string command) => S.Write(command + "\n");
    public string ReadLine() => S.Read().TrimEnd('\r', '\n');

    public void Clear()
    {
        if (!IsSerial) S.Clear();
    }

    public void Release()
    {
        if (session is null) return;
        if (IsSerial) { try { WriteLine("SYST:LOC"); } catch { } }
        else session.GoToLocal();
    }

    public void Dispose()
    {
        session?.Dispose();
        session = null;
    }
}

/// <summary>An instrument on a serial port, with DTR held on (the 34401A won't send without it).</summary>
public sealed class SerialScpiPort(string port, SerialSettings settings, TimeSpan timeout) : IScpiPort
{
    SerialPort? sp;

    public string Location => $"{port}:{settings}";
    public bool IsSerial => true;

    public void Open()
    {
        Dispose();
        var p = ResourceSpec.OpenSerial(port, settings, dtrDsr: true);
        p.NewLine = "\n";
        p.Encoding = Encoding.ASCII;
        p.ReadTimeout = (int)timeout.TotalMilliseconds;
        p.WriteTimeout = (int)timeout.TotalMilliseconds;
        try
        {
            p.Open();
        }
        catch (Exception ex)
        {
            p.Dispose();
            throw new MeterException($"Could not open {port}: {ex.Message}", ex);
        }
        sp = p;
    }

    SerialPort P => sp ?? throw new MeterException("Not connected.");

    public void WriteLine(string command) => P.Write(command + "\n");
    public string ReadLine() => P.ReadLine().TrimEnd('\r', '\n');

    public void Clear()
    {
        P.DiscardInBuffer();
        P.DiscardOutBuffer();
    }

    public void Release()
    {
        if (sp is null) return;
        try { WriteLine("SYST:LOC"); } catch { }
    }

    public void Dispose()
    {
        if (sp is null) return;
        try { sp.Close(); } catch { }
        sp.Dispose();
        sp = null;
    }
}
