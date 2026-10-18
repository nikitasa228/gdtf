using System.Collections.Concurrent;
using System.Diagnostics;
using Candela.Gdtf;
using Xunit.Abstractions;

namespace Candela.Dmx.Tests;

/// <summary>Every fixture of the repository library in every mode, and the per-frame cost of a large rig.</summary>
public class LibraryWideTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_fixture_in_every_mode_evaluates_random_dmx_to_finite_values()
    {
        var sw = Stopwatch.StartNew();
        var failures = new ConcurrentQueue<string>();
        long modes = 0, channels = 0, functions = 0, beams = 0, axes = 0, warnings = 0, files = 0, litModes = 0;
        Parallel.ForEach(TestData.Library.Entries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, entry =>
        {
            FixtureType ft;
            try
            {
                ft = GdtfReader.ReadFile(entry.FilePath);
            }
            catch (Exception e)
            {
                failures.Enqueue($"{entry.FileName}: cannot be read: {e.Message}");
                return;
            }
            Interlocked.Increment(ref files);
            uint seed = StableHash(entry.FileName);
            var rng = new Random((int)seed);
            foreach (var mode in ft.DmxModes)
            {
                FixtureProfile p;
                try
                {
                    p = FixtureProfile.Compile(ft, mode.Name, entry.Specs);
                }
                catch (Exception e)
                {
                    failures.Enqueue($"{entry.FileName} [{mode.Name}]: compile failed: {e}");
                    continue;
                }
                Interlocked.Increment(ref modes);
                Interlocked.Add(ref channels, p.Channels.Count);
                Interlocked.Add(ref functions, p.Functions.Count);
                Interlocked.Add(ref beams, p.Beams.Count);
                Interlocked.Add(ref axes, p.Axes.Count);
                Interlocked.Add(ref warnings, p.Warnings.Count);
                try
                {
                    var state = new FixtureState(p, seed);
                    var buffer = new FixtureDmxBuffer(p);
                    for (int frame = 0; frame < 4; frame++)
                    {
                        for (int b = 0; b < buffer.BreakCount; b++) rng.NextBytes(buffer.GetBreak(b));
                        foreach (var ch in p.Channels)
                            if (ch.IsVirtual) buffer.SetValue(ch.Index, (uint)rng.NextInt64(0, (long)ch.MaxValue + 1));
                        FixtureEvaluator.Evaluate(state, buffer, 0.01 + frame * 0.37, null, applyDynamics: true);
                        Check(p, state, $"{entry.FileName} [{mode.Name}] frame {frame}", failures);
                    }
                    // manual values for a few attributes (random values inside their functions' ranges)
                    var manual = new Dictionary<string, float>();
                    foreach (var f in p.Functions.Where((_, i) => i % 3 == 0).Take(20))
                        manual[f.Attribute] = f.MinValue + (float)rng.NextDouble() * (f.MaxValue - f.MinValue);
                    FixtureEvaluator.Evaluate(state, buffer, 2.5, AttributeOverrides.Compile(p, manual), applyDynamics: true);
                    Check(p, state, $"{entry.FileName} [{mode.Name}] manual", failures);
                    if (CheckOpenWhite(p, $"{entry.FileName} [{mode.Name}]", failures)) Interlocked.Increment(ref litModes);
                }
                catch (Exception e)
                {
                    failures.Enqueue($"{entry.FileName} [{mode.Name}]: evaluation threw: {e}");
                }
            }
        });
        output.WriteLine($"{files} files, {modes} modes, {channels} channel instances, {functions} functions, {beams} beams, {axes} axes, " +
            $"{warnings} compile warnings, {litModes} modes with beams lit at open white; {sw.Elapsed.TotalSeconds:F1} s");
        foreach (var f in failures.Take(30)) output.WriteLine(f);
        Assert.Equal(TestData.Library.Count, (int)files);
        Assert.True(modes >= 7000);
        Assert.True(litModes >= 7000, $"{litModes} modes lit at open white");
        Assert.Empty(failures);
    }

    /// <summary>
    /// Modes that the GDTF itself leaves dark (or partly dark) at open white, with the reason. Everything else must light
    /// every beam.
    /// </summary>
    private static readonly (string File, string Mode, string Reason)[] DarkAtOpenWhite =
    [
        ("ACME@ACME_MANA_PROFILE_", "Wash 22 DMX ", "the ColorSub_C channel carries a ColorAdd_R function; at full the beam is red, at 0 dark"),
        ("BeamZ_Pro@BS_1500_RGBW_IP65", "13 Ch Strobe + FX", "the shutter channel has only 'Shutter CLOSED' and strobe ranges"),
        ("BeamZ_Pro@Nereid_380B_IP65", "18 CH", "the LED ring's shutter has only 'Shutter CLOSED' and strobe ranges"),
        ("CLF_Lighting@LED_BAR_EFF", "30CH-FullMacro", "shutter channels share their slots with other channels"),
        ("CLF_Lighting@LED_BAR_EFF", "23CH-Simple + Macro", "shutter channels share their slots with other channels"),
        ("JB-Lighting@P18_MK2_Profile_", "Mode 1 (42 ch)", "the ColorSub_M channel carries an additive 'Color G' function"),
        ("Magmatic@SIDEWINDER", "4-CH", "the LED shutter is 'Off' up to the strobe ranges"),
        ("Magmatic@SIDEWINDER", "7-CH", "the LED shutter is 'Off' up to the strobe ranges"),
    ];

    /// <summary>
    /// <see cref="DmxLooks.OpenWhite"/> lights every beam, and every addressed master dimmer (its logical attribute is
    /// Dimmer) dims the beams it covers: half its range gives less light (relation chains and virtual dimmers included).
    /// Returns true when every beam is lit.
    /// </summary>
    private static bool CheckOpenWhite(FixtureProfile p, string where, ConcurrentQueue<string> failures)
    {
        if (p.Beams.Count == 0) return false;
        var buffer = new FixtureDmxBuffer(p);
        DmxLooks.OpenWhite(buffer);
        var state = FixtureEvaluator.Evaluate(p, buffer);
        int dark = state.Beams.Count(b => !(b.PeakCandela > 0f));
        bool excepted = Array.Exists(DarkAtOpenWhite, x => where.StartsWith(x.File, StringComparison.Ordinal) &&
            where.EndsWith($"[{x.Mode}]", StringComparison.Ordinal));
        if (dark > 0 && !excepted)
        {
            var b = state.Beams.First(x => !(x.PeakCandela > 0f));
            failures.Enqueue($"{where}: {dark} of {state.Beams.Count} beams dark at open white (first: {b.Name}, dimmer {b.Dimmer}, shutter {b.Shutter}, colour {b.ColorTransmission})");
        }
        if (dark > 0) return false;

        foreach (var ch in p.Channels)
        {
            if (ch.IsVirtual || ch.Class.Kind != AttributeKind.Dimmer || ch.Beams.Count == 0) continue;
            var active = state.GetChannel(ch.Index).Function;
            if (active < 0) continue;
            var f = p.Functions[active];
            if (f.Class.Kind != AttributeKind.Dimmer || f.IsConstant) continue;
            var half = new FixtureDmxBuffer(p);
            half.CopyFrom(buffer);
            DmxEncoder.EncodeFunction(half, f.Index, f.MinValue + 0.5f * (f.MaxValue - f.MinValue));
            var dimmed = FixtureEvaluator.Evaluate(p, half);
            double full = ch.Beams.Sum(b => (double)state.Beams[b].PeakCandela), less = ch.Beams.Sum(b => (double)dimmed.Beams[b].PeakCandela);
            if (!(less < 0.9 * full)) failures.Enqueue($"{where}: dimmer {ch} at half its range leaves {less:G4} of {full:G4} cd");
        }
        return true;
    }

    private static void Check(FixtureProfile p, FixtureState s, string where, ConcurrentQueue<string> failures)
    {
        void Bad(string what) => failures.Enqueue($"{where}: {what}");
        static bool F(float v) => float.IsFinite(v);
        for (int c = 0; c < s.ChannelCount; c++)
        {
            var v = s.GetChannel(c);
            if (!F(v.Physical) || !F(v.Value)) Bad($"channel {p.Channels[c]} = {v}");
        }
        foreach (var a in s.Axes)
            if (!F(a.Pan) || !F(a.Tilt) || !F(a.Rotation.X + a.Rotation.Y + a.Rotation.Z) || !F(a.Translation.X + a.Translation.Y + a.Translation.Z))
                Bad($"axis {a.PartIndex}: pan {a.Pan} tilt {a.Tilt}");
        foreach (var b in s.Beams)
        {
            float[] values = [b.Intensity,
                b.Dimmer,
                b.Shutter,
                b.ColorTransmission,
                b.PeakCandela,
                b.Flux,
                b.BeamAngle,
                b.FieldAngle,
                b.Focus,
                b.Iris,
                b.Frost,
                b.ShaperRotation,
                b.Color.Rgb.X,
                b.Color.Rgb.Y,
                b.Color.Rgb.Z,
                b.StrobeFrequency];
            if (values.Any(v => !F(v))) Bad($"beam {b.Name}: {string.Join(", ", values)}");
            if (b.Intensity is < 0f or > 1f || b.PeakCandela < 0f || b.ColorTransmission is < 0f or > 1f || b.Color.Rgb.X < 0f || b.Color.Rgb.Y < 0f ||
                b.Color.Rgb.Z < 0f || b.BeamAngle <= 0f || b.FieldAngle < b.BeamAngle * 0.999f)
                Bad($"beam {b.Name} out of range: I={b.Intensity} cd={b.PeakCandela} T={b.ColorTransmission} {b.Color} angles {b.BeamAngle}/{b.FieldAngle}");
            foreach (var w in b.ColorWheels.Concat(b.Gobos).Concat(b.Prisms).Concat(b.AnimationWheels).Concat(b.Effects).Concat(b.BeamShapers))
            {
                if (!F(w.Rotation) || !F(w.WheelAngle) || !F(w.Blend) || !F(w.RotationSpeed) || !F(w.Insertion) || (w.Wheel >= 0 && !F(w.Position)))
                    Bad($"beam {b.Name} wheel {w.Wheel}: pos {w.Position} rot {w.Rotation}");
            }
            foreach (var bl in b.Blades)
                if (!F(bl.A) || !F(bl.B) || !F(bl.Rotation)) Bad($"beam {b.Name} blade");
        }
        var o = s.Outputs;
        if (!F(o.Fog) || !F(o.Haze) || !F(o.FanSpeed) || !F(o.FanDirection) || !F(o.Flame)) Bad($"outputs {o.Fog} {o.Haze} {o.FanSpeed} {o.Flame}");
    }

    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        return h;
    }
}
