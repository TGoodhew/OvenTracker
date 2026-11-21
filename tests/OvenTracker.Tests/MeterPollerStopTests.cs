using OvenTracker.Meter;
using OvenTracker.Session;

namespace OvenTracker.Tests;

/// <summary>Stopping the poller while the meter is in the middle of something (#43).</summary>
public class MeterPollerStopTests
{
    /// <summary>A meter whose read blocks (as a Bluetooth write does, for its whole timeout), then fails.</summary>
    sealed class StuckMeter : ITemperatureMeter
    {
        public readonly ManualResetEventSlim Reading = new();
        public readonly ManualResetEventSlim Release = new();
        public string Location => "stuck";
        public MeterIdentity Connect() => new("Test", "Stuck", "1", "1");
        public void Disconnect() { }

        public MeterReading Read()
        {
            Reading.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            throw new TimeoutException("The write timed out.");
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task AMeterThatFailsAfterStopEndsThePollerQuietly()
    {
        var meter = new StuckMeter();
        var poller = new MeterPoller(meter, TimeSpan.FromSeconds(1), connected: true);
        poller.Start();
        Assert.True(meter.Reading.Wait(TimeSpan.FromSeconds(5)), "the poller never read");

        // Stop while the read is stuck, then let the read fail: the failure must not escape the poller's thread
        // (that ended the process), and Stop must return.
        var stop = Task.Run(poller.Stop);
        await Task.Delay(100);
        meter.Release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10)); // throws TimeoutException if Stop doesn't return
        Assert.Equal(LinkState.Stopped, poller.State);
        poller.Dispose();
    }

    sealed class BrokenMeter : ITemperatureMeter
    {
        public string Location => "broken";
        public MeterIdentity Connect() => new("Test", "Broken", "1", "1");
        public void Disconnect() => throw new InvalidOperationException("can't even disconnect");
        public MeterReading Read() => throw new IOException("the link is gone");
        public void Dispose() { }
    }

    [Fact]
    public void AMeterThatFailsEveryWayStillLetsThePollerStop()
    {
        var poller = new MeterPoller(new BrokenMeter(), TimeSpan.FromSeconds(1), speed: 100, connected: true);
        poller.Start();
        Thread.Sleep(200);
        poller.Stop();
        Assert.Equal(LinkState.Stopped, poller.State);
        Assert.NotNull(poller.LastError);
    }
}
