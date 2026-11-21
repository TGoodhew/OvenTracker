using OvenTracker.Meter.Ble;

namespace OvenTracker.Meter;

/// <summary>
/// A UNI-T UT60BT reached over Bluetooth LE. Unlike the 121GW it must be asked: the app sends it commands on one
/// characteristic, and its frames come back as notifications on another.
/// <para>
/// The sources differ on how to ask. libreble, from a real UT60BT, asks for its name first (it ignored requests
/// for data until then) and then asks for data once, after which frames came a few times a second. dmmpy and
/// unit_ut61eplus send a different command for each frame. So on connecting the app asks for the name, then asks
/// for data whenever no frame has come for <see cref="PollEvery"/>, switching between the two commands while a
/// request goes unanswered. Either kind of meter then works, and a stream that stalls is asked again.
/// </para>
/// <para>
/// Like the 121GW, the newest frame is kept, and <see cref="Read"/> waits for one newer than it last returned.
/// Found and opened by <see cref="Ut60btDriver"/>.
/// </para>
/// </summary>
public sealed class Ut60btMeter(IBleLink link) : ITemperatureMeter
{
    readonly object gate = new();
    readonly Ut60btAssembler assembler = new();
    readonly byte[][] requests = [Ut60btProtocol.GetData, Ut60btProtocol.Trigger];
    Ut60btFrame? latest;
    DateTimeOffset latestTime;
    long received, returned;
    string? name;
    bool open;

    // The poll: which request is next, when the last went, and how many frames had come by then.
    int request;
    DateTime lastPoll = DateTime.MinValue;
    long receivedAtPoll = -1;

    /// <summary>How long to wait for data before giving up: when connecting, and for each reading.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How long without a frame before asking again: the meter sends about three a second when streaming.</summary>
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>How long to wait for the meter's name on connecting, each time it is asked (three times at most).</summary>
    public TimeSpan NameWait { get; init; } = TimeSpan.FromMilliseconds(600);

    public string Location => "ble:" + link.Address;

    /// <summary>How many data frames have arrived: for tests.</summary>
    public long FramesReceived { get { lock (gate) return received; } }

    /// <summary>The name the meter gave when asked, e.g. "UT60BT"; null if it didn't answer.</summary>
    public string? DeviceName { get { lock (gate) return name; } }

    public MeterIdentity Connect()
    {
        Disconnect();
        lock (gate)
        {
            request = 0;
            lastPoll = DateTime.MinValue;
            receivedAtPoll = -1;
        }
        try { link.Open(OnData); }
        catch
        {
            try { link.Dispose(); } catch { } // a link that failed part-way through opening is still released
            throw;
        }
        open = true;

        for (var i = 0; i < 3 && DeviceName is null; i++)
        {
            Send(Ut60btProtocol.GetName);
            lock (gate)
            {
                var until = DateTime.UtcNow + NameWait;
                while (name is null && DateTime.UtcNow < until) Monitor.Wait(gate, until - DateTime.UtcNow);
            }
        }

        _ = NextFrame(0, take: false) ?? throw new TimeoutException("The UT60BT sent no data. Check it's on, and Bluetooth is on (hold SELECT until BT shows on its display).");
        return new MeterIdentity("UNI-T", "UT60BT", "?", "?");
    }

    void OnData(byte[] data)
    {
        lock (gate)
        {
            foreach (var f in assembler.Add(data))
            {
                if (Ut60btProtocol.IsData(f))
                {
                    latest = new Ut60btFrame(f);
                    latestTime = DateTimeOffset.UtcNow;
                    received++;
                }
                else if (Ut60btProtocol.NameIn(f) is { } n) name = n;
            }
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>Sends a command, ignoring a failure: an unanswered request is retried, and a link that's gone shows as no data.</summary>
    void Send(byte[] command)
    {
        try { link.Write(command); }
        catch (Exception ex) when (ex is MeterException or NotSupportedException or IOException or InvalidOperationException) { }
    }

    /// <summary>Asks for data, if no frame has come for a while and the last request has had its chance.</summary>
    void PollIfQuiet()
    {
        byte[] command;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var quiet = received == 0 || DateTimeOffset.UtcNow - latestTime >= PollEvery;
            if (!quiet || now - lastPoll < PollEvery) return;
            if (receivedAtPoll == received) request = (request + 1) % requests.Length; // the last went unanswered
            receivedAtPoll = received;
            lastPoll = now;
            command = requests[request];
        }
        Send(command);
    }

    /// <summary>
    /// The newest frame after the <paramref name="after"/>th one, with the time it came, asking for data while none
    /// comes; null if none comes within <see cref="Timeout"/>. With <paramref name="take"/>, it's marked as returned in
    /// the same lock, so a frame arriving just after can't be skipped.
    /// </summary>
    (Ut60btFrame Frame, DateTimeOffset At)? NextFrame(long after, bool take)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            PollIfQuiet();
            lock (gate)
            {
                var left = deadline - DateTime.UtcNow;
                if (received <= after && left > TimeSpan.Zero)
                    Monitor.Wait(gate, left < PollEvery ? left : PollEvery);
                if (received > after)
                {
                    if (take) returned = received;
                    return (latest!, latestTime);
                }
                if (DateTime.UtcNow >= deadline) return null;
            }
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
        var (f, at) = NextFrame(seen, take: true) ?? throw new TimeoutException("The UT60BT stopped sending (switched off, out of range, or Bluetooth turned off).");
        return Ut60btProtocol.ToReading(at, f);
    }

    public MeterConfig? ReadConfig()
    {
        Ut60btFrame? f;
        lock (gate) f = latest;
        return f is null ? null : Ut60btProtocol.Config(f);
    }

    public MeterConfig? LastConfig => ReadConfig();

    /// <summary>
    /// Whether the meter shows its low-battery sign (from 4.2 V on its three AAA cells). It has no battery level to
    /// report, so <see cref="ITemperatureMeter.ReadBatteryPercent"/> stays unknown.
    /// </summary>
    public bool? ReadLowBattery()
    {
        lock (gate) return latest?.LowBattery;
    }

    public void Dispose() => Disconnect();
}
