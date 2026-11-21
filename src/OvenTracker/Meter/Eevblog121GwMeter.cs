using OvenTracker.Meter.Ble;

namespace OvenTracker.Meter;

/// <summary>
/// An EEVblog 121GW reached over Bluetooth LE. The meter streams its display about twice a second without
/// being asked. The app keeps the latest packet, and <see cref="Read"/> waits for a newer one than it last
/// returned, so a slow stream never gives the same reading twice. Found and opened by <see cref="Eevblog121GwDriver"/>.
/// </summary>
public sealed class Eevblog121GwMeter(IBleLink link) : ITemperatureMeter
{
    readonly object gate = new();
    readonly Gw121Assembler assembler = new();
    Gw121Packet? latest;
    DateTimeOffset latestTime;
    long received, returned;
    bool open;

    /// <summary>How long to wait for data before giving up: when connecting, and for each reading.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);

    public string Location => "ble:" + link.Address;

    /// <summary>How many packets have arrived: for tests and the status line.</summary>
    public long PacketsReceived { get { lock (gate) return received; } }

    public MeterIdentity Connect()
    {
        Disconnect();
        try { link.Open(OnData); }
        catch
        {
            try { link.Dispose(); } catch { } // a link that failed part-way through opening is still released
            throw;
        }
        open = true;
        var (p, _) = WaitForPacket(0) ?? throw new TimeoutException("The 121GW sent no data. Check it's on, and Bluetooth is on (BT on its display).");
        return new MeterIdentity("EEVblog", "121GW", p.Serial, "?");
    }

    void OnData(byte[] data)
    {
        lock (gate)
        {
            foreach (var p in assembler.Add(data))
            {
                latest = p;
                latestTime = DateTimeOffset.UtcNow;
                received++;
            }
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>The newest packet after the <paramref name="after"/>th one, or null if none comes in time.</summary>
    /// <summary>
    /// The newest packet after the <paramref name="after"/>th one, with the time it came, or null if none comes in time.
    /// With <paramref name="take"/>, it's marked as returned in the same lock: a packet arriving just after can't be
    /// skipped, or give its time to this one.
    /// </summary>
    (Gw121Packet Packet, DateTimeOffset At)? WaitForPacket(long after, bool take = false)
    {
        var deadline = DateTime.UtcNow + Timeout;
        lock (gate)
        {
            while (received <= after)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return null;
                Monitor.Wait(gate, left);
            }
            if (take) returned = received;
            return (latest!, latestTime);
        }
    }

    public void Disconnect()
    {
        if (open) link.Dispose();
        open = false;
    }

    public MeterReading Read()
    {
        long seen;
        lock (gate) seen = returned;
        var (p, at) = WaitForPacket(seen, take: true) ?? throw new TimeoutException("The 121GW stopped sending (switched off, out of range, or Bluetooth turned off).");
        return Gw121Protocol.ToReading(at, p);
    }

    public MeterConfig? ReadConfig()
    {
        Gw121Packet? p;
        lock (gate) p = latest;
        return p is null ? null : Gw121Protocol.Config(p);
    }

    public MeterConfig? LastConfig => ReadConfig();

    /// <summary>The meter says whether auto power-off is armed (the APO icon); not how long it's set to.</summary>
    public AutoPowerOffSetting ReadAutoPowerOff()
    {
        Gw121Packet? p;
        lock (gate) p = latest;
        return p is null ? AutoPowerOffSetting.Unknown : p.AutoPowerOffArmed ? AutoPowerOffSetting.On(Eevblog121GwDriver.DefaultAutoPowerOff) : AutoPowerOffSetting.Off;
    }

    /// <summary>
    /// Whether the meter shows its low-battery icon. The meter has no battery level to report, so
    /// <see cref="ITemperatureMeter.ReadBatteryPercent"/> stays unknown.
    /// </summary>
    public bool LowBattery
    {
        get { lock (gate) return latest?.LowBattery == true; }
    }

    public void Dispose() => Disconnect();
}
