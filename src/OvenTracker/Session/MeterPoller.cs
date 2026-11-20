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
    readonly Stopwatch clock = new();
    TimeSpan? lastGoodAt;
    TimeSpan lastLinkAttempt;
    long attempts, attemptAtLastGood = -1, attemptAtLastLink;
    Thread? thread;

    /// <summary>
    /// How long the meter can be quiet, with the link still up, before the poller tries reconnecting
    /// anyway (and then again each time this passes). A meter that has switched itself off times out on
    /// every read while the link is fine, so the poller keeps asking at the normal rate and carries on
    /// the moment the meter is woken. In the meter's time, so the simulator's speed-up applies.
    /// </summary>
    public TimeSpan QuietReconnectAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Time passed in the meter's own terms since a moment: real time for a real meter; for the simulator,
    /// the number of reads since then times the interval, which is its clock (the timer can't keep up with
    /// a large speed-up, so real time × speed would overstate it).
    /// </summary>
    TimeSpan MeterTimeSince(TimeSpan realThen, long attemptThen) =>
        speed > 1 ? interval * (attempts - attemptThen) : clock.Elapsed - realThen;

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

    /// <summary>
    /// How long since the last reading, in the meter's time (simulated time for the simulator). Zero
    /// until the first reading, and while readings arrive at the normal rate it stays near the interval.
    /// </summary>
    public TimeSpan SilentFor => lastGoodAt is { } g ? MeterTimeSince(g, attemptAtLastGood) : TimeSpan.Zero;

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
        clock.Start();
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
                attempts++;
                var r = meter.Read();
                ReadCount++;
                failures = 0;
                lastGoodAt = clock.Elapsed;
                attemptAtLastGood = attempts;
                if (r.Status != ReadingStatus.Error) Config = meter.LastConfig;
                if (r is { Status: ReadingStatus.Ok, Celsius: { } c }) LastCelsius = c;
                readings.Writer.TryWrite(r);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                failures++;
                LastError = ex.Message;
                LastErrorAt = DateTimeOffset.UtcNow;
                // Quiet for long enough that the link may be gone after all: try it again, now and then.
                var quietTooLong = ex is TimeoutException
                                   && SilentFor >= QuietReconnectAfter
                                   && MeterTimeSince(lastLinkAttempt, attemptAtLastLink) >= QuietReconnectAfter;
                var linkDown = !IsConnected
                               || ex is IOException or InvalidOperationException or UnauthorizedAccessException
                               || (ex is not TimeoutException && failures >= 3)
                               || quietTooLong;
                if (linkDown)
                {
                    if (IsConnected) Reconnects++;
                    IsConnected = false;
                    State = LinkState.Reconnecting;
                    lastLinkAttempt = clock.Elapsed;
                    attemptAtLastLink = attempts;
                    try { meter.Disconnect(); } catch { }
                    // A meter that doesn't answer (switched off) is retried soon; a failing link backs off further.
                    var backoff = Math.Min(ex is TimeoutException ? 2 : 10, 2 * failures);
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(backoff / speed));
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
