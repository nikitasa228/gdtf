using Candela.Dmx.Input;

namespace Candela.Dmx.Tests;

public class DmxInputHubTests
{
    private static readonly DmxSourceId ConsoleA = new("artnet", "10.0.0.1");
    private static readonly DmxSourceId ConsoleB = new("artnet", "10.0.0.2");

    [Fact]
    public void Unknown_universe_reads_zero_and_false()
    {
        var hub = new DmxInputHub();
        var buf = new byte[512];
        buf[0] = 9;
        Assert.False(hub.TryRead(1, buf, out _));
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Htp_merges_two_sources_of_equal_priority()
    {
        var hub = new DmxInputHub();
        hub.Submit(ConsoleA, 1, new byte[] { 10, 200, 0 });
        hub.Submit(ConsoleB, 1, new byte[] { 50, 100, 7 });
        var buf = new byte[512];
        Assert.True(hub.TryRead(1, buf, out var v1));
        Assert.Equal(new byte[] { 50, 200, 7 }, buf[..3]);
        Assert.True(hub.TryRead(1, buf, out var v2));
        Assert.Equal(v1, v2);
    }

    [Fact]
    public void Higher_priority_source_wins_and_lower_is_ignored()
    {
        var hub = new DmxInputHub();
        hub.Submit(new DmxSourceId("sacn", "a"), 3, new byte[] { 255 }, priority: 100);
        hub.Submit(new DmxSourceId("sacn", "b"), 3, new byte[] { 20 }, priority: 150);
        var buf = new byte[512];
        hub.TryRead(3, buf, out _);
        Assert.Equal(20, buf[0]);
    }

    [Fact]
    public void Expired_and_terminated_sources_drop_out()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hub = new DmxInputHub(() => now) { SourceTimeout = TimeSpan.FromSeconds(2.5) };
        hub.Submit(ConsoleA, 1, new byte[] { 100 });
        now += TimeSpan.FromSeconds(2);
        hub.Submit(ConsoleB, 1, new byte[] { 30 });
        now += TimeSpan.FromSeconds(1);
        var buf = new byte[512];
        Assert.True(hub.TryRead(1, buf, out _));
        Assert.Equal(30, buf[0]);
        hub.Terminate(ConsoleB, 1);
        Assert.False(hub.TryRead(1, buf, out _));
        Assert.Empty(hub.ActiveUniverses());
    }

    [Fact]
    public void Ltp_takes_latest_source()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hub = new DmxInputHub(() => now) { MergeMode = DmxMergeMode.Ltp };
        hub.Submit(ConsoleA, 1, new byte[] { 200 });
        now += TimeSpan.FromMilliseconds(20);
        hub.Submit(ConsoleB, 1, new byte[] { 10 });
        var buf = new byte[512];
        hub.TryRead(1, buf, out _);
        Assert.Equal(10, buf[0]);
    }

    [Fact]
    public void Timecode_seconds_including_drop_frame()
    {
        Assert.Equal(3723.5, new Timecode(1, 2, 3, 12, TimecodeType.Film24).TotalSeconds, 6);
        Assert.Equal(1800 * 1001 / 30000.0, new Timecode(0, 1, 0, 2, TimecodeType.Df2997).TotalSeconds, 6);
        Assert.Equal("00:01:00;02", new Timecode(0, 1, 0, 2, TimecodeType.Df2997).ToString());
    }

    [Fact]
    public void Concurrent_submits_are_safe()
    {
        var hub = new DmxInputHub();
        var data = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        Parallel.For(0, 2000, i => hub.Submit(new DmxSourceId("artnet", $"10.0.0.{i % 4}"), 1 + (i / 4) % 8, data));
        var buf = new byte[512];
        for (int u = 1; u <= 8; u++)
        {
            Assert.True(hub.TryRead(u, buf, out _));
            Assert.Equal(data, buf);
        }
        Assert.Equal(32, hub.Sources().Count);
    }
}
