using Candela.Dmx.Input;
using Candela.Protocols.ArtNet;

namespace Candela.Protocols.Tests;

public class TestPatternTests
{
    [Fact]
    public void Looks_stay_in_range_and_move_over_time()
    {
        var settings = new TestPatternSettings();
        var pans = new HashSet<float>();
        for (double t = 0; t < 20; t += 0.37)
        {
            for (int i = 0; i < 8; i++)
            {
                var look = ArtNetTestPattern.LookAt(t, i, 8, settings);
                foreach (var v in new[] { look.Pan, look.Tilt, look.Dimmer, look.Red, look.Green, look.Blue, look.White, look.Zoom, look.Focus })
                    Assert.InRange(v, 0f, 1f);
                pans.Add(look.Pan);
            }
        }
        Assert.True(pans.Count > 100);
        Assert.Equal(ArtNetTestPattern.LookAt(3.3, 2, 8, settings), ArtNetTestPattern.LookAt(3.3, 2, 8, settings));
        Assert.NotEqual(ArtNetTestPattern.LookAt(3.3, 1, 8, settings).Pan, ArtNetTestPattern.LookAt(3.3, 2, 8, settings).Pan);

        var still = ArtNetTestPattern.LookAt(5, 0, 1, new TestPatternSettings { PanTilt = false, ColourChase = false, DimmerWave = false, Zoom = false });
        Assert.Equal(new TestPatternLook(0.5f, 0.5f, 1, 1, 1, 1, 1, 0.5f, 0.5f), still);
    }

    [Fact]
    public void Raw_encoder_writes_16_bit_and_fixed_channels()
    {
        var fixture = TestPatternFixture.MovingHead(1, 101);
        Assert.Equal(12, fixture.Footprint);
        var encoder = new RawChannelEncoder(fixture);
        var slots = new byte[512];
        encoder.Encode(new TestPatternLook(0.5f, 1f, 0.25f, 1, 0, 0.5f, 0, 0, 1), slots);
        Assert.Equal(0x80, slots[100]); // pan coarse: round(0.5 × 65535) = 32768 = 0x8000
        Assert.Equal(0x00, slots[101]);
        Assert.Equal(0xFF, slots[102]); // tilt 1.0
        Assert.Equal(0xFF, slots[103]);
        Assert.Equal(255, slots[104]); // shutter open
        Assert.Equal(64, slots[105]); // dimmer 0.25
        Assert.Equal(new byte[] { 255, 0, 128, 0, 0, 255 }, slots[106..112]);

        var nearEnd = new RawChannelEncoder(TestPatternFixture.MovingHead(1, 510));
        nearEnd.Encode(new TestPatternLook(1, 1, 1, 1, 1, 1, 1, 1, 1), slots); // channels past 512 are skipped
        Assert.Equal(255, slots[511]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RawChannelEncoder(TestPatternFixture.RgbwPar(0, 1)));
    }

    [Fact]
    public void Demo_rig_spreads_fixtures_over_universes_and_renders()
    {
        var pattern = ArtNetTestPattern.DemoRig(movingHeads: 43, pars: 1, firstUniverse: 5);
        Assert.Equal(44, pattern.FixtureCount);
        Assert.Equal(new[] { 5, 6 }, pattern.Universes);
        pattern.Render(1.25);
        Assert.Contains(pattern.GetUniverse(5).ToArray(), b => b != 0);
        Assert.Contains(pattern.GetUniverse(6).ToArray(), b => b != 0);
        Assert.True(pattern.GetUniverse(7).IsEmpty);
        var first = pattern.GetUniverse(5).ToArray();
        pattern.Render(4.0);
        Assert.NotEqual(first, pattern.GetUniverse(5).ToArray());
    }

    [Theory]
    [InlineData(0.0, TimecodeType.Ebu25, 0, 0, 0, 0)]
    [InlineData(3661.5, TimecodeType.Ebu25, 1, 1, 1, 12)]
    [InlineData(1.0 / 24 * 23, TimecodeType.Film24, 0, 0, 0, 23)]
    [InlineData(86400.0 + 2, TimecodeType.Smpte30, 0, 0, 2, 0)]
    public void Timecode_from_seconds(double seconds, TimecodeType type, int h, int m, int s, int f) =>
        Assert.Equal(new Timecode(h, m, s, f, type), ArtNetTestConsole.TimecodeAt(seconds, type));

    [Fact]
    public void Drop_frame_timecode_inverts_the_hub_conversion()
    {
        for (int frame = 0; frame < 108000; frame += 997)
        {
            double seconds = (frame + 0.5) * 1001.0 / 30000.0;
            var tc = ArtNetTestConsole.TimecodeAt(seconds, TimecodeType.Df2997);
            Assert.False(tc.Seconds == 0 && tc.Frames < 2 && tc.Minutes % 10 != 0, $"dropped label {tc} used");
            Assert.Equal(frame * 1001.0 / 30000.0, tc.TotalSeconds, 6);
        }
    }
}
