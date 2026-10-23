using System.Numerics;

namespace Candela.Rendering.Tests;

/// <summary>The smoke overlay's part of the rendering contract: its default gradient, its markers and the world call.</summary>
public class SmokeOverlayContractTests
{
    [Fact]
    public void Default_smoke_gradient_spans_haze_to_fog_on_a_log_scale_and_leaves_clear_air_uncoloured()
    {
        var g = ColorGradient.DefaultSmoke();
        Assert.True(g.Logarithmic);
        Assert.Equal(0.005f, g.RangeMin);
        Assert.Equal(2f, g.RangeMax);
        Assert.Equal(0f, g.OutOfRangeColor.W);
        Assert.Equal(0f, g.Evaluate(0.001f).W); // clearer than the range: no colour
        // denser air is more opaque and the stops run blue → magenta
        float previous = -1f;
        foreach (var stop in g.Stops)
        {
            Assert.True(stop.Color.W > previous, $"alpha at {stop.Value}");
            previous = stop.Color.W;
        }

        var haze = g.Evaluate(0.1f);
        Assert.True(haze.Y > haze.X && haze.Y > haze.Z, $"0.1 /m is green: {haze}");
        Assert.All(g.Bake(256), c => Assert.True(float.IsFinite(c.X + c.Y + c.Z + c.W)));
        Assert.Same(g.GetType(), new ViewOptions().SmokeGradient.GetType());
        Assert.Equal(g.RangeMax, new ViewOptions().SmokeGradient.RangeMax);
    }

    [Fact]
    public void Only_gradients_with_stops_and_an_increasing_finite_range_are_drawable()
    {
        Assert.True(ColorGradient.IsDrawable(ColorGradient.DefaultLux()));
        Assert.True(ColorGradient.IsDrawable(ColorGradient.DefaultNoise()));
        Assert.True(ColorGradient.IsDrawable(ColorGradient.DefaultSmoke()));
        Assert.True(ColorGradient.IsDrawable(new ColorGradient())); // no stops yet: every value is out of range, still drawable
        Assert.False(ColorGradient.IsDrawable(null));
        Assert.False(ColorGradient.IsDrawable(new ColorGradient { Stops = null! }));
        Assert.False(ColorGradient.IsDrawable(new ColorGradient { RangeMin = 2f, RangeMax = 2f }));
        Assert.False(ColorGradient.IsDrawable(new ColorGradient { RangeMin = 5f, RangeMax = 1f }));
        Assert.False(ColorGradient.IsDrawable(new ColorGradient { RangeMin = float.NaN }));
        Assert.False(ColorGradient.IsDrawable(new ColorGradient { RangeMax = float.PositiveInfinity }));
        // what the check protects: baking an inverted range throws
        Assert.ThrowsAny<ArgumentException>(() => new ColorGradient { RangeMin = 5f, RangeMax = 1f, Stops = { new GradientStop(1f, Vector4.One) } }.Bake(4));
    }

    [Fact]
    public void Every_emitter_kind_has_its_own_colour_and_off_is_grey()
    {
        var colours = Enum.GetValues<SmokeEmitterMarkerKind>().Select(SmokeEmitterMarker.ColorOf).ToList();
        Assert.Equal(colours.Count, colours.Distinct().Count());
        Assert.DoesNotContain(SmokeEmitterMarker.OffColor, colours);
        var off = SmokeEmitterMarker.OffColor;
        Assert.Equal(off.X, off.Y);
        Assert.Equal(off.Y, off.Z);
    }

    [Fact]
    public void A_world_without_smoke_overlay_support_ignores_the_emitters()
    {
        IRenderWorld world = new MinimalWorld();
        world.SetSmokeEmitters(new[] { new SmokeEmitterMarker(Vector3.Zero, Vector3.UnitZ, 1f, SmokeEmitterMarkerKind.Hazer, Guid.NewGuid()) });
        world.SetSmokeEmitters(ReadOnlySpan<SmokeEmitterMarker>.Empty);
    }

    /// <summary>A render world that implements only the members it must (renderers written before the smoke overlay).</summary>
    private sealed class MinimalWorld : IRenderWorld
    {
        public MeshHandle CreateMesh(Core.Geometry.MeshData mesh) => default;
        public void DestroyMesh(MeshHandle mesh) { }
        public TextureHandle CreateTexture(Core.Geometry.ImageData image, bool srgb) => default;
        public void DestroyTexture(TextureHandle texture) { }
        public MaterialHandle CreateMaterial(Core.Geometry.MaterialData material, Core.Geometry.IImageSource? images) => default;
        public void UpdateMaterial(MaterialHandle handle, Core.Geometry.MaterialData material, Core.Geometry.IImageSource? images) { }
        public void DestroyMaterial(MaterialHandle material) { }
        public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId) => default;
        public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags) { }
        public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials) { }
        public void DestroyInstance(InstanceHandle instance) { }
        public void SetLights(ReadOnlySpan<SpotLight> lights) { }
        public void SetLines(ReadOnlySpan<LineVertex> segments) { }
        public void SetAtmosphere(AtmosphereState atmosphere) { }
        public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources) { }
    }
}
