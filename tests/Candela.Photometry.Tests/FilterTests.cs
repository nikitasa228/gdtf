using System.Numerics;
using Candela.Core.SceneGraph.Components;

namespace Candela.Photometry.Tests;

public class FilterTests
{
    public static TheoryData<string> Sources => new() { "D65", "A", "6500K", "3200K" };

    private static Spectrum SourceSpectrum(string name) => name switch
    {
        "D65" => Spectrum.IlluminantD65,
        "A" => Spectrum.IlluminantA,
        "6500K" => Spectrum.ReferenceIlluminant(6500f),
        _ => Spectrum.Blackbody(3200f),
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void FullCmy_TransmitsLessThanTwoPercent(string source)
    {
        var model = new SubtractiveFilterModel(SourceSpectrum(source));
        var full = model.Evaluate(1f, 1f, 1f);
        Assert.InRange(full.LuminousTransmission, 0f, 0.02f);
        var spectral = Filters.ApplyCmy(SourceSpectrum(source), 1f, 1f, 1f);
        Assert.InRange(spectral.LuminousTransmission, 0f, 0.02f);
    }

    [Fact]
    public void ZeroLevels_LeaveTheSourceUnchanged()
    {
        var model = new SubtractiveFilterModel(Spectrum.IlluminantA);
        var open = model.Evaluate(0f, 0f, 0f);
        Assert.Equal(1.0, open.LuminousTransmission, 1e-6);
        Assert.True(Vector3.Distance(model.SourceColor.Rgb, open.Color.Rgb) < 1e-5f);
        Assert.Equal(2856.0, model.SourceKelvin, 1.5);
        var nan = model.Evaluate(float.NaN, float.NaN, float.NaN);
        Assert.Equal(1.0, nan.LuminousTransmission, 1e-6);
    }

    [Fact]
    public void Transmission_DecreasesMonotonicallyWithLevel()
    {
        var model = SubtractiveFilterModel.ForCct(6500f);
        for (int flag = 0; flag < 3; flag++)
        {
            float previous = 1.0001f;
            for (int i = 0; i <= 20; i++)
            {
                float level = i / 20f;
                var r = flag switch
                {
                    0 => model.Evaluate(level, 0f, 0f),
                    1 => model.Evaluate(0f, level, 0f),
                    _ => model.Evaluate(0f, 0f, level),
                };
                Assert.True(r.LuminousTransmission < previous, $"flag {flag} level {level}");
                previous = r.LuminousTransmission;
            }
        }
    }

    [Fact]
    public void SingleFlags_ProduceTheirHues()
    {
        var model = new SubtractiveFilterModel(Spectrum.IlluminantD65);
        var cyan = model.Evaluate(1f, 0f, 0f);
        var magenta = model.Evaluate(0f, 1f, 0f);
        var yellow = model.Evaluate(0f, 0f, 1f);

        var c = cyan.Color.ToXy();
        Assert.True(c.X < 0.25f && c.Y > 0.25f, $"cyan {c}");
        var m = magenta.Color.ToXy();
        Assert.True(m.Y < 0.25f && m.X > 0.25f, $"magenta {m}");
        var y = yellow.Color.ToXy();
        Assert.True(y.X > 0.40f && y.Y > 0.45f, $"yellow {y}");

        // Typical CMY engine transmissions: yellow high, cyan medium, magenta low.
        Assert.InRange(yellow.LuminousTransmission, 0.80f, 0.97f);
        Assert.InRange(cyan.LuminousTransmission, 0.55f, 0.85f);
        Assert.InRange(magenta.LuminousTransmission, 0.15f, 0.40f);

        // Cyan + yellow = green, magenta + yellow = red, cyan + magenta = blue.
        Assert.True(model.Evaluate(1f, 0f, 1f).Color.ToXy().Y > 0.55f);
        Assert.True(model.Evaluate(0f, 1f, 1f).Color.ToXy().X > 0.6f);
        Assert.True(model.Evaluate(1f, 1f, 0f).Color.ToXy().Y < 0.1f);
    }

    [Fact]
    public void FlagTransmissionSpectra_HaveTheDocumentedBands()
    {
        var cyan = Filters.CyanTransmission(1f);
        Assert.InRange(cyan.Sample(650f), 0f, 0.01f);
        Assert.InRange(cyan.Sample(450f), 0.99f, 1f);
        var magenta = Filters.MagentaTransmission(1f);
        Assert.InRange(magenta.Sample(540f), 0f, 0.01f);
        Assert.InRange(magenta.Sample(440f), 0.99f, 1f);
        Assert.InRange(magenta.Sample(650f), 0.99f, 1f);
        var yellow = Filters.YellowTransmission(1f);
        Assert.InRange(yellow.Sample(450f), 0f, 0.01f);
        Assert.InRange(yellow.Sample(600f), 0.99f, 1f);

        // Absorption depth proportional to the level.
        Assert.Equal(1.0 - 0.5 * Filters.PeakAbsorption, Filters.CyanTransmission(0.5f).Sample(700f), 1e-3);
        Assert.Equal(1.0, Filters.CmyTransmission(0f, 0f, 0f).MaxValue(), 1e-6);
    }

    [Fact]
    public void SpectralApply_AgreesWithTheFastModel()
    {
        var source = Spectrum.ReferenceIlluminant(5600f);
        var model = new SubtractiveFilterModel(source);
        foreach (var (c, m, y) in new[] { (0.3f, 0.6f, 0.1f), (1f, 0f, 0.5f), (0.2f, 0.2f, 0.9f) })
        {
            var fast = model.Evaluate(c, m, y);
            var slow = Filters.Apply(source, Filters.CmyTransmission(c, m, y));
            Assert.Equal(slow.LuminousTransmission, fast.LuminousTransmission, 1e-4);
            Assert.True(Vector2.Distance(slow.Color.ToXy(), fast.Color.ToXy()) < 1e-4f);
        }
    }

    [Fact]
    public void Apply_HandlesDarkSources()
    {
        var dark = Filters.Apply(Spectrum.Constant(0f), Filters.CyanTransmission(1f));
        Assert.Equal(0f, dark.LuminousTransmission);
        var blocked = Filters.Apply(Spectrum.IlluminantD65, Spectrum.Constant(0f));
        Assert.Equal(0f, blocked.LuminousTransmission);
        var model = new SubtractiveFilterModel(Spectrum.Constant(0f));
        Assert.Equal(0f, model.Evaluate(0f, 0f, 0f).LuminousTransmission);
    }

    [Fact]
    public void Cto_TurnsAPlanckianSourceIntoTheTargetTemperature()
    {
        var cto = Filters.ConvertBlackbody(6500f, 3200f);
        var cct = cto.Color.ToCct();
        Assert.Equal(3200.0, cct.Kelvin, 1.0);
        Assert.Equal(0.0, cct.Duv, 1e-4);
        Assert.InRange(cto.LuminousTransmission, 0.2f, 0.6f);

        var ctb = Filters.ConvertBlackbody(3200f, 5600f);
        Assert.Equal(5600.0, ctb.Color.ToCct().Kelvin, 1.0);
        Assert.InRange(ctb.LuminousTransmission, 0.1f, 0.5f);

        var none = Filters.ConvertBlackbody(4000f, 4000f);
        Assert.Equal(1.0, none.LuminousTransmission, 1e-6);
    }

    [Fact]
    public void Cto_SpectralAndModelPathsAgree()
    {
        var bb = Spectrum.Blackbody(6500f);
        var spectral = Filters.ApplyConversion(bb, 6500f, 3200f);
        Assert.Equal(3200.0, spectral.Color.ToCct().Kelvin, 2.0);
        Assert.Equal(Filters.ConvertBlackbody(6500f, 3200f).LuminousTransmission, spectral.LuminousTransmission, 2e-3);

        var transmission = Filters.ConversionFilterTransmission(6500f, 3200f);
        float peak = 0f;
        for (float w = 380f; w <= 780f; w += 5f)
            peak = MathF.Max(peak, transmission.Sample(w));
        Assert.Equal(1.0, peak, 1e-5);

        // A daylight source of nominal 6500 K lands close to the target as well.
        var model = SubtractiveFilterModel.ForCct(6500f);
        var warmed = model.Evaluate(0f, 0f, 0f, 3200f);
        Assert.Equal(3200.0, warmed.Color.ToCct().Kelvin, 100.0);
        var unchanged = model.Evaluate(0f, 0f, 0f, float.NaN);
        Assert.Equal(1.0, unchanged.LuminousTransmission, 1e-6);
        var cmyAndCto = model.Evaluate(0f, 0f, 0.3f, 3200f);
        Assert.True(cmyAndCto.LuminousTransmission < warmed.LuminousTransmission);
    }

    [Fact]
    public void WheelSlot_ReproducesItsXyYForAWhiteSource()
    {
        var open = Filters.FromWheelSlot(0.3127f, 0.3290f, 100f);
        Assert.Equal(1.0, open.LuminousTransmission, 1e-4);
        Assert.True(Vector3.Distance(Vector3.One, open.Color.Rgb) < 1e-3f);

        var red = Filters.FromWheelSlot(0.6401f, 0.3300f, 21.26f);
        Assert.Equal(0.2126, red.LuminousTransmission, 1e-4);
        Assert.Equal(0.6401, red.Color.ToXy().X, 1e-4);
        Assert.Equal(0.3300, red.Color.ToXy().Y, 1e-4);

        var mvr = Filters.FromWheelSlot(new CieColor(0.6401f, 0.3300f, 21.26f));
        Assert.Equal(red, mvr);
    }

    [Fact]
    public void WheelSlot_CapsImpossibleTransmissions()
    {
        // GDTF files often state Y = 100 % for saturated slots; a deep blue filter cannot pass that much light.
        var blue = Filters.FromWheelSlot(0.15f, 0.06f, 100f);
        Assert.InRange(blue.LuminousTransmission, 0.01f, 0.15f);
        Assert.Equal(0.15, blue.Color.ToXy().X, 1e-4);
        var nan = Filters.FromWheelSlot(0.3127f, 0.3290f, float.NaN);
        Assert.Equal(1.0, nan.LuminousTransmission, 1e-4);
        var negative = Filters.FromWheelSlot(0.5f, 0.4f, -20f);
        Assert.Equal(0f, negative.LuminousTransmission);
    }

    [Fact]
    public void ChromaticityFilter_OnColoredSourceStaysPhysical()
    {
        var warm = LightColor.FromCct(3000f);
        var red = Filters.ApplyChromaticityFilter(warm, new Vector2(0.64f, 0.33f), 0.2f);
        Assert.InRange(red.LuminousTransmission, 0.2f, 0.5f); // warm light is red-rich, so more passes than under D65
        Assert.True(red.Color.ToXy().X > 0.6f);
        // Red light through a deep blue filter is almost blocked. Without a spectrum the per-channel model keeps a small
        // leak (an sRGB-blue filter has some Rec.2020 red), which real blue gels also show in the deep red.
        var redLight = LightColor.FromXy(0.7f, 0.3f);
        var blueFilter = Filters.ApplyChromaticityFilter(redLight, new Vector2(0.15f, 0.06f), 0.05f);
        Assert.InRange(blueFilter.LuminousTransmission, 0f, 0.05f);
        Assert.True(blueFilter.LuminousTransmission < 0.6f * Filters.FromWheelSlot(0.15f, 0.06f, 5f).LuminousTransmission);
    }

    [Fact]
    public void EverySlotAndFilterColorInTheBundledGdtfFiles_GivesAPhysicalResult()
    {
        int count = 0;
        foreach (string element in new[] { "Slot", "Filter" })
        {
            foreach (var (file, raw, x, y, bigY) in RepoData.Colors(element))
            {
                var r = Filters.FromWheelSlot(x, y, bigY);
                Assert.True(r.LuminousTransmission is >= 0f and <= 1f, $"{file} {raw}: {r}");
                var rgb = r.Color.Rgb;
                Assert.True(float.IsFinite(rgb.X + rgb.Y + rgb.Z) && rgb.X >= 0f && rgb.Y >= 0f && rgb.Z >= 0f, $"{file} {raw}");
                count++;
            }
        }

        Assert.True(count > 5000, $"only {count} slots");
    }
}
