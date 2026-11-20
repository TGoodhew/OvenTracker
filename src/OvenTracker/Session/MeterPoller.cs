using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using OvenTracker.Meter;

namespace OvenTracker.Session;

public enum LinkState { Connecting, Connected, Reconnecting, Stopped }

/// <summary>
/// Reads the meter on its own thread, at a steady interval, and survives the link dropping (a
/// Bluetooth link will): it reconnects and carries on, and the gap simply has no readings in it.
/// </summary>
public sealed class MeterPoller : IDisposable
{
    readonly ITemperatureMeter meter;
    readonly TimeSpan interval;
    readonly double speed;
    readonly Channel<MeterReading> readings = Channel.CreateUnbounded<MeterReading>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentQueue<Action<ITemperatureMeter>> commands = new();
    readonly CancellationTokenSource cts = new();
    Thread? thread;

    /// <param name="speed">Real-time speed-up, for the simulator only.</param>
    public MeterPoller(ITemperatureMeter meter, TimeSpan interval, double speed = 1, bool connected = false)
    {
        this.meter = meter;
        this.interval = interval;
        this.speed = Math.Max(1, speed);
        IsConnected = connected;
        State = connected ? LinkState.Connected : LinkState.Connecting;
    }

    public ChannelReader<MeterReading> Readings => readings.Reader;
    public LinkState State { get; private set; }
    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }
    public double? BatteryPercent { get; private set; }
    public MeterConfig? Config { get; private set; }

    /// <summary>The latest good temperature, in °C.</summary>
    public double? LastCelsius { get; private set; }
    public int Reconnects { get; private set; }
    public long ReadCount { get; private set; }

    /// <summary>Battery is checked every this many readings.</summary>
    public int BatteryEvery { get; init; } = 300;

    public void Start()
    {
        thread = new Thread(Loop) { IsBackground = true, Name = "Meter poller" };
        thread.Start();
    }

    public void Enqueue(Action<ITemperatureMeter> command) => commands.Enqueue(command);

    void Loop()
    {
        var token = cts.Token;
        var period = TimeSpan.FromTicks((long)(interval.Ticks / speed));
        var clock = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        var failures = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IsConnected)
                {
                    meter.Connect();
                    IsConnected = true;
                    State = LinkState.Connected;
                    failures = 0;
                }
                while (commands.TryDequeue(out var command))
                {
                    try { command(meter); } catch { /* a beep that fails is not worth losing the link over */ }
                }
                if (ReadCount % BatteryEvery == 0) BatteryPercent = meter.ReadBatteryPercent() ?? BatteryPercent;
                var r = meter.Read();
                ReadCount++;
                failures = 0;
                if (r.Status != ReadingStatus.Error) Config = meter.LastConfig;
                if (r is { Status: ReadingStatus.Ok, Celsius: { } c }) LastCelsius = c;
                readings.Writer.TryWrite(r);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                failures++;
                LastError = ex.Message;
                LastErrorAt = DateTimeOffset.UtcNow;
                if (!IsConnected || failures >= 3 || ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    if (IsConnected) Reconnects++;
                    IsConnected = false;
                    State = LinkState.Reconnecting;
                    try { meter.Disconnect(); } catch { }
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(10, 2 * failures)));
                    next = clock.Elapsed;
                    continue;
                }
            }

            next += period;
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
            else next = clock.Elapsed; // fell behind (slow reply): do not try to catch up in a burst
        }
        State = LinkState.Stopped;
    }

    public void Stop()
    {
        cts.Cancel();
        thread?.Join(TimeSpan.FromSeconds(5));
        readings.Writer.TryComplete();
    }

    public void Dispose()
    {
        Stop();
        cts.Dispose();
    }
}
