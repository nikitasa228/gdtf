using Candela.Dmx.Input;

namespace Candela.Dmx.Tests;

/// <summary>The hub's receive path for recorders: observers see every merged change and timecode with timestamps.</summary>
public sealed class DmxInputObserverTests
{
    private sealed class Log : IDmxInputObserver
    {
        public List<(int Universe, byte First, bool Live, DateTime At)> Universes { get; } = new();
        public List<TimecodeSample> Timecodes { get; } = new();

        public void OnUniverse(int universe, ReadOnlySpan<byte> merged, bool live, DateTime timestampUtc) =>
            Universes.Add((universe, merged[0], live, timestampUtc));

        public void OnTimecode(TimecodeSample sample) => Timecodes.Add(sample);
    }

    [Fact]
    public void Observers_get_merged_universes_terminations_timeouts_and_timecodes_with_their_times()
    {
        var start = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var now = start;
        var hub = new DmxInputHub(() => now) { SourceTimeout = TimeSpan.FromSeconds(2) };
        var a = new DmxSourceId("artnet", "10.0.0.1:6454");
        var b = new DmxSourceId("sacn", "10.0.0.2:5568");
        hub.Submit(a, 1, new byte[] { 40 });
        var log = new Log();
        hub.AddObserver(log); // the live universe is reported first
        Assert.Equal((1, (byte)40, true, start), Assert.Single(log.Universes));

        now = start.AddMilliseconds(10);
        hub.Submit(b, 1, new byte[] { 90 }); // HTP merge: the observer sees the merged value
        hub.Submit(a, 2, new byte[] { 7 });
        Assert.Equal((1, (byte)90, true, now), log.Universes[1]);
        Assert.Equal((2, (byte)7, true, now), log.Universes[2]);

        now = start.AddMilliseconds(20);
        hub.Terminate(b, 1);
        Assert.Equal((1, (byte)40, true, now), log.Universes[3]);

        hub.SubmitTimecode(new Timecode(1, 0, 0, 0, TimecodeType.Ebu25), a);
        var sample = Assert.Single(log.Timecodes);
        Assert.Equal(a, sample.Source);
        Assert.Equal(now, sample.ReceivedUtc);
        Assert.Equal(sample, hub.LastTimecodeSample);
        Assert.Equal(a, hub.LastTimecodeSource);
        hub.SubmitTimecode(new Timecode(1, 0, 0, 1, TimecodeType.Ebu25));
        Assert.Equal(DmxInputHub.UnnamedSource, hub.LastTimecodeSource);

        // a timeout is reported when the hub notices it, stamped with the moment the source timed out
        now = start.AddSeconds(5);
        Assert.Empty(hub.ActiveUniverses());
        var lost = log.Universes.Where(u => !u.Live).ToList();
        Assert.Equal(2, lost.Count);
        Assert.Equal(start.AddSeconds(2), lost.Single(u => u.Universe == 1).At);
        Assert.Equal(start.AddMilliseconds(10).AddSeconds(2), lost.Single(u => u.Universe == 2).At);
        Assert.All(lost, u => Assert.Equal(0, u.First));

        hub.RemoveObserver(log);
        int count = log.Universes.Count;
        hub.Submit(a, 1, new byte[] { 1 });
        Assert.Equal(count, log.Universes.Count);
        hub.RemoveObserver(log); // idempotent
    }
}
