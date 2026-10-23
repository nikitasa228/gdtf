using System.Numerics;
using Candela.Assets;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx;
using Candela.Rendering;
using Candela.Rendering.Vulkan;
using Xunit.Abstractions;

namespace Candela.Engine.Tests;

/// <summary>
/// The Rivale Profile of <c>stages/Фокина.mvr</c> (the library's Rivale Wash stands in for it) with gobo wheel 1
/// explicitly on its OPEN slot: no gobo, animation disc ("CMY Animation", a colour mosaic) or beam shaper is in the
/// beam — in the DMX state, in the lights the render world receives, and in the picture the Vulkan renderer draws
/// through the cookie, seen from the lens along the beam. The same picture with a gobo and with the animation disc
/// in proves that the measurement sees a pattern or a mosaic when there is one.
/// </summary>
[Collection(VulkanCollection.Name)]
public sealed class RivaleOpenGoboTests
{
    private const string RivaleFilePrefix = "Ayrton@Rivale_Wash@";

    /// <summary>Gobo wheel 1 slot positions of the Rivale (1-based, as the fixture controls write them).</summary>
    private const float OpenSlot = 1f, DotLineSlot = 2f;

    private readonly ITestOutputHelper _output;

    public RivaleOpenGoboTests(ITestOutputHelper output) => _output = output;

    private static bool Inserted(in WheelState w) => (w.Insertion > 0f && !w.IsOpen) || w.Facets > 0;

    private static bool IsRivale(StageEngine engine, Guid id) =>
        engine.Dmx.GetResolution(id)?.Entry?.FileName.StartsWith(RivaleFilePrefix, StringComparison.Ordinal) == true;

    private static void SetOnRivales(StageEngine engine, string attribute, float value)
    {
        foreach (var (entity, fixture) in engine.Scene.Query<FixtureComponent>())
        {
            if (!IsRivale(engine, entity.Id)) continue;
            fixture.ManualAttributes[attribute] = value;
            fixture.NotifyChanged();
        }
    }

    /// <summary>Ticks long enough for the wheel motors to reach their slots (3 s).</summary>
    private static void Settle(StageEngine engine)
    {
        for (int i = 0; i < 90; i++) engine.Tick(1.0 / 30);
    }

    private static void AssertNothingInserted(StageEngine engine, IReadOnlyList<SpotLight> lights, string context)
    {
        int rivales = 0;
        foreach (var (entity, _) in engine.Scene.Query<FixtureComponent>())
        {
            if (!IsRivale(engine, entity.Id)) continue;
            rivales++;
            foreach (var b in engine.Dmx.GetState(entity.Id)!.Beams)
            {
                foreach (var w in b.Gobos.Concat(b.AnimationWheels).Concat(b.Prisms).Concat(b.BeamShapers).Concat(b.Effects))
                    Assert.False(Inserted(w), $"{context}: {b.Name} wheel {w.Wheel} slot {w.Slot} insertion {w.Insertion}");
                Assert.All(b.Gobos, w => Assert.True(w.IsOpen, $"{context}: gobo wheel {w.Wheel} on slot {w.Slot}, not open"));
            }

            var own = lights.Where(l => l.FixtureId == entity.Id).ToList();
            Assert.NotEmpty(own);
            Assert.All(own, l => Assert.False(l.Gobo.IsValid, $"{context}: a cookie image reaches the renderer"));
            Assert.All(own, l => Assert.True(l.PrismFacets <= 1, $"{context}: {l.PrismFacets} prism facets"));
        }

        Assert.Equal(27, rivales);
    }

    [Fact]
    public void Fokina_rivales_with_gobo_wheel_1_at_open_insert_nothing()
    {
        using var engine = TestEnvironment.CreateEngine("rivale-open-gobo");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.Equal(91, StageLooks.ApplyOpenWhite(engine));
        var world = new RecordingRenderWorld();
        var sync = engine.AttachRenderWorld(world);
        sync.LoadFixtureModelsInBackground = false;
        engine.Tick(0);
        Assert.True(sync.CompletePendingLoads(TimeSpan.FromMinutes(2)));
        Settle(engine);
        AssertNothingInserted(engine, world.Lights, "open white");

        // the wheel driven to another slot and back to OPEN, as an operator does
        SetOnRivales(engine, "Gobo1", DotLineSlot);
        Settle(engine);
        Assert.All(world.Lights.Where(l => IsRivale(engine, l.FixtureId)), l => Assert.True(l.Gobo.IsValid));
        SetOnRivales(engine, "Gobo1", OpenSlot);
        Settle(engine);
        AssertNothingInserted(engine, world.Lights, "gobo wheel 1 at OPEN");
    }

    [SkippableFact]
    public void Fokina_rivale_with_gobo_wheel_1_at_open_projects_an_even_white_field()
    {
        using var renderer = CreateRendererOrSkip();
        using var engine = TestEnvironment.CreateEngine("rivale-open-gobo-render");
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        Assert.Equal(91, StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false }));

        // one Rivale alone: the others would light the same floor in other whites
        engine.Tick(0);
        var fixtures = engine.Scene.Query<FixtureComponent>().Select(t => t.Item1).ToList();
        var rivale = fixtures.Where(e => IsRivale(engine, e.Id)).OrderBy(e => e.Get<FixtureComponent>()!.FixtureId).First();
        foreach (var other in fixtures.Where(e => e != rivale))
            engine.Scene.Remove(other);
        var fixture = rivale.Get<FixtureComponent>()!;
        fixture.ManualAttributes["Gobo1"] = OpenSlot;
        fixture.NotifyChanged();

        var sync = engine.AttachRenderWorld(renderer.World);
        sync.LoadFixtureModelsInBackground = false;
        engine.Tick(0);
        Assert.True(sync.CompletePendingLoads(TimeSpan.FromMinutes(2)));
        Settle(engine);

        var open = Measure(engine, renderer, rivale.Id, "rivale-gobo-open.png");

        fixture.ManualAttributes["Gobo1"] = DotLineSlot;
        fixture.NotifyChanged();
        Settle(engine);
        var gobo = Measure(engine, renderer, rivale.Id, "rivale-gobo-dot-line.png");

        fixture.ManualAttributes["Gobo1"] = OpenSlot;
        fixture.ManualAttributes["AnimationWheelSpin"] = 0f;
        fixture.NotifyChanged();
        Settle(engine);
        var mosaic = Measure(engine, renderer, rivale.Id, "rivale-animation-disc.png");

        // and back to open: no stale cookie stays in the renderer
        fixture.ManualAttributes.Remove("AnimationWheelSpin");
        fixture.ManualAttributes["AnimationWheel1"] = 0f;
        fixture.NotifyChanged();
        Settle(engine);
        var reopened = Measure(engine, renderer, rivale.Id, "rivale-gobo-reopened.png");

        _output.WriteLine($"open:     {open}");
        _output.WriteLine($"gobo:     {gobo}");
        _output.WriteLine($"mosaic:   {mosaic}");
        _output.WriteLine($"reopened: {reopened}");

        // the measurement sees a gobo (luminance pattern) and the animation disc (colour mosaic) when they are in
        Assert.True(gobo.LuminanceSpread > 0.3, $"gobo not visible: {gobo}");
        Assert.True(mosaic.ChromaSpread > 0.03, $"animation disc not visible: {mosaic}");
        // gobo wheel 1 at OPEN: an even white field (only the beam's own fall-off), before and after the wheels moved
        foreach (var (field, label) in new[] { (open, "open"), (reopened, "reopened") })
        {
            Assert.True(field.MeanLuminance > 0.005, $"{label}: the beam does not light the view: {field}");
            Assert.True(field.LuminanceSpread < 0.1, $"{label}: a pattern in the open beam: {field}");
            Assert.True(field.ChromaSpread < 0.01, $"{label}: a colour mosaic in the open beam: {field}");
        }
    }

    private readonly record struct FieldStats(double MeanLuminance, double LuminanceSpread, double ChromaSpread)
    {
        public override string ToString() => $"mean {MeanLuminance:0.000}, luminance spread {LuminanceSpread:0.000}, chroma spread {ChromaSpread:0.0000}";
    }

    /// <summary>
    /// Renders the view from just in front of the lens along the beam axis (a quarter of the beam angle wide, so the whole
    /// picture lies in the beam's hot spot) and measures how even it is: the relative spread of the luminance (gobo
    /// pattern) and the spread of the chromaticity (colour mosaic) over the lit pixels.
    /// </summary>
    private FieldStats Measure(StageEngine engine, VulkanRenderer renderer, Guid fixtureId, string name)
    {
        engine.Tick(1.0 / 30);
        var light = engine.Lights.ToArray().Single(l => l.FixtureId == fixtureId);
        var direction = Vector3.Normalize(light.Direction);
        var eye = light.Position + direction * 0.5f;
        var camera = RenderCamera.LookAt(eye, eye + direction, MathF.Max(light.BeamAngle * 0.25f * 180f / MathF.PI, 1f));
        var view = new ViewOptions { ShowGrid = false, ShowSelectionOutline = false };
        var settings = new PreviewSettings { AutoExposure = true, Volumetrics = true };
        using var target = renderer.CreateTarget(128, 96);
        for (int i = 0; i < 24; i++) renderer.Render(target, camera, view, settings, 0.5f);
        var image = renderer.ReadPixels(target);
        string dir = Environment.GetEnvironmentVariable("CANDELA_TEST_OUTPUT") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "render-output");
        Directory.CreateDirectory(dir);
        ImageImporter.SavePng(image, Path.Combine(dir, name));

        int n = image.Width * image.Height;
        var luminance = new double[n];
        var chroma = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            double r = Linear(image.Pixels[4 * i]), g = Linear(image.Pixels[4 * i + 1]), b = Linear(image.Pixels[4 * i + 2]);
            luminance[i] = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            double sum = r + g + b;
            chroma[i] = sum > 1e-6 ? new Vector2((float)(r / sum), (float)(g / sum)) : new Vector2(1f / 3f);
        }

        double mean = luminance.Average();
        double spread = Math.Sqrt(luminance.Select(y => (y - mean) * (y - mean)).Average()) / Math.Max(mean, 1e-9);
        var lit = Enumerable.Range(0, n).Where(i => luminance[i] > 0.25 * mean).ToList();
        var meanChroma = lit.Aggregate(Vector2.Zero, (s, i) => s + chroma[i]) / Math.Max(lit.Count, 1);
        double chromaSpread = lit.Count == 0 ? 0 : Math.Sqrt(lit.Average(i => (double)Vector2.DistanceSquared(chroma[i], meanChroma)));
        return new FieldStats(mean, spread, chromaSpread);

        static double Linear(byte v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    private static VulkanRenderer CreateRendererOrSkip()
    {
        try
        {
            return VulkanRenderer.Create(new VulkanContextOptions
            {
                CacheDirectory = Path.Combine(AppContext.BaseDirectory, "vk-cache"),
                MinimumLogLevel = VulkanLogLevel.Error,
            });
        }
        catch (VulkanUnavailableException e)
        {
            Skip.If(true, $"No Vulkan device available: {e.Message}");
            throw;
        }
    }
}
