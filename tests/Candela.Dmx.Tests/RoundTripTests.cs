using Xunit.Abstractions;

namespace Candela.Dmx.Tests;

/// <summary>DmxEncoder and FixtureEvaluator are inverse: encoding a value of any channel function and evaluating gives it back.</summary>
public class RoundTripTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Martin_Professional@MAC_Viper_Profile", "Basic")]
    [InlineData("Martin_Professional@MAC_Viper_Profile", "Extended")]
    [InlineData("Robe_Lighting@Robin_Esprite@", "Mode 1 - Standard 16 bit")]
    [InlineData("Prolights@ArcPod27Q", "17ch")]
    [InlineData("Robe_Lighting@Robin_Tetra1", "Mode 6 Pattern full RGBW")]
    [InlineData("Elation@Paladin_Panel", null)]
    [InlineData("ADJ@Entour_Haze_PRO", "4 Channel Mode")]
    [InlineData("Clay_Paky@Arolla_Aqua_LT", null)]
    [InlineData("Briteq@BTI-BLIZZARD_WASH2", null)] // pan, tilt and zoom with placeholder ranges mapped onto the specs
    [InlineData("ADJ@Inno_Pocket_Z4", null)] // zoom range defined by its channel set
    [InlineData("Elation@Proteus_Brutus_FS", null)]
    public void Every_channel_function_round_trips(string prefix, string? mode)
    {
        var p = TestData.Profile(prefix, mode);
        int checkedValues = 0, failures = 0;
        var messages = new List<string>();
        foreach (var f in p.Functions)
        {
            foreach (float v in ValuesOf(f))
            {
                var buffer = new FixtureDmxBuffer(p);
                DmxEncoder.EncodeFunction(buffer, f.Index, v);
                var state = FixtureEvaluator.Evaluate(p, buffer);
                var cv = state.GetChannel(f.Channel);
                checkedValues++;
                var g = cv.Function >= 0 ? p.Functions[cv.Function] : null;
                float tolerance = Tolerance(f, v);
                bool ok = g is not null && string.Equals(g.Attribute, f.Attribute, StringComparison.OrdinalIgnoreCase) &&
                    (MathF.Abs(cv.Value - v) <= tolerance || (f.UsesSlotPosition && SameSlot(p, f, cv.Value, v)));
                if (ok) continue;
                failures++;
                if (messages.Count < 20)
                    messages.Add($"{f} value {v}: got {(g is null ? "no function" : g.ToString())} = {cv.Value} (dmx {cv.Dmx})");
            }
        }
        foreach (var m in messages) output.WriteLine(m);
        output.WriteLine($"{p}: {p.Functions.Count} functions, {checkedValues} values, {failures} failures");
        Assert.True(checkedValues > 0);
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Encoding_by_attribute_writes_coarse_and_fine_bytes()
    {
        var p = TestData.Profile("Martin_Professional@MAC_Viper_Profile", "Basic");
        var buffer = new FixtureDmxBuffer(p);
        Assert.Equal(1, DmxEncoder.Encode(buffer, "Pan", 0f));
        var pan = p.Channels[p.FindChannels("Pan")[0]];
        var slots = buffer.GetBreak(0);
        uint v = (uint)(slots[pan.Offsets[0] - 1] << 8 | slots[pan.Offsets[1] - 1]);
        Assert.InRange(v, 32767u, 32768u);
        Assert.Equal(v, buffer.GetValue(pan.Index));
        Assert.Equal(0, DmxEncoder.Encode(buffer, "NoSuchAttribute", 1f));
    }

    [Fact]
    public void Encoding_a_mode_dependent_function_sets_its_mode_master()
    {
        var p = TestData.Profile("Martin_Professional@MAC_Viper_Profile", "Basic");
        int pos = p.FindChannels("Gobo1PosRotate")[0];
        var rotate = p.FunctionsOf(pos).ToArray().First(f => f.Attribute == "Gobo1PosRotate");
        var buffer = new FixtureDmxBuffer(p);
        DmxEncoder.EncodeFunction(buffer, rotate.Index, 200f);
        uint master = buffer.GetValue(rotate.ModeMaster);
        Assert.InRange(master, rotate.ModeFrom, rotate.ModeTo);
        var state = FixtureEvaluator.Evaluate(p, buffer);
        Assert.Equal(rotate.Index, state.GetChannel(pos).Function);
        Assert.Equal(200f, state.GetChannel(pos).Value, 0.1f);
    }

    [Fact]
    public void Highlight_values_light_the_fixture()
    {
        var p = TestData.Profile("Prolights@ArcPod27Q", "17ch");
        var buffer = new FixtureDmxBuffer(p);
        foreach (var c in p.Channels) buffer.SetValue(c.Index, 0);
        Assert.Equal(0f, FixtureEvaluator.Evaluate(p, buffer).Beams[0].PeakCandela);
        Assert.Contains(p.Channels, c => c.HighlightValue is not null);
        buffer.ApplyHighlight();
        var lit = FixtureEvaluator.Evaluate(p, buffer).Beams[0];
        Assert.True(lit.PeakCandela > 0f);
        Assert.Equal(1f, lit.Dimmer, 1e-3f);
    }

    /// <summary>
    /// Achievable control values: what the function evaluates to at its range ends and middle and at every channel set's
    /// ends and middle (channel sets with their own physical values make other values unreachable).
    /// </summary>
    private static IEnumerable<float> ValuesOf(ProfileFunction f)
    {
        var dmx = new SortedSet<uint> { f.DmxFrom, f.DmxTo, f.DmxFrom + (f.DmxTo - f.DmxFrom) / 2 };
        foreach (var set in f.Sets)
        {
            dmx.Add(set.DmxFrom);
            dmx.Add(set.DmxTo);
            dmx.Add(set.DmxFrom + (set.DmxTo - set.DmxFrom) / 2);
        }
        var seen = new HashSet<float>();
        foreach (uint d in dmx.Take(60))
        {
            float v = f.UsesSlotPosition ? f.SlotPositionAt(d) : FixtureEvaluator.PhysicalAt(f, d, FixtureEvaluator.SetIndexAt(f, d));
            if (seen.Add(v)) yield return v;
        }
    }

    /// <summary>One DMX step of the steepest part of the function's mapping (plus rounding slack).</summary>
    private static float Tolerance(ProfileFunction f, float v)
    {
        double slope;
        if (f.UsesSlotPosition)
        {
            slope = 0;
            for (int i = 0; i + 1 < f.SlotDmx.Length; i++)
                if (f.SlotDmx[i + 1] > f.SlotDmx[i])
                    slope = Math.Max(slope, Math.Abs(f.SlotPosition[i + 1] - f.SlotPosition[i]) / (double)(f.SlotDmx[i + 1] - f.SlotDmx[i]));
            return (float)(slope * 1.01) + 1e-3f;
        }
        slope = Math.Abs(f.PhysicalTo - f.PhysicalFrom) / Math.Max(1.0, (double)f.DmxTo - f.DmxFrom);
        foreach (var set in f.Sets)
            if (set.HasPhysical && set.DmxTo > set.DmxFrom)
                slope = Math.Max(slope, Math.Abs(set.PhysicalTo - set.PhysicalFrom) / (double)(set.DmxTo - set.DmxFrom));
        if (f.Curve is not null) slope *= 8; // a curve can be steeper than the linear mapping
        return (float)(slope * 1.01) + 1e-4f * MathF.Max(1f, MathF.Abs(v));
    }

    private static bool SameSlot(FixtureProfile p, ProfileFunction f, float a, float b)
    {
        if (f.Wheel < 0) return false;
        int n = p.Wheels[f.Wheel].SlotCount;
        float d = MathF.Abs(a - b) % n;
        return MathF.Min(d, n - d) < 0.02f;
    }
}
