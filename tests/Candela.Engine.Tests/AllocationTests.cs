using System.Numerics;
using Candela.Core.Geometry;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>Steady-state frames (fixtures moving, nothing edited) do not allocate on the main thread.</summary>
public sealed class AllocationTests
{
    [Fact]
    public void Steady_state_ticks_with_a_render_world_do_not_allocate()
    {
        using var engine = TestEnvironment.CreateEngine("alloc", dmxThreads: 1);
        Assert.True(engine.Open(TestEnvironment.Fokina).Success);
        StageLooks.ApplyOpenWhite(engine, new OpenWhiteOptions { Undoable = false });
        var sync = engine.AttachRenderWorld(new NullRenderWorld());
        sync.LoadFixtureModelsInBackground = false;
        engine.Tick(0);
        sync.CompletePendingLoads(TimeSpan.FromMinutes(2));

        // a pan move keeps the motors (and so the poses and instances) busy for the measured frames
        foreach (var (_, fixture) in engine.Scene.Query<Core.SceneGraph.Components.FixtureComponent>())
            if (fixture.ManualAttributes.ContainsKey("Pan")) fixture.ManualAttributes["Pan"] += 90f;
        engine.Dmx.InvalidateAll();
        for (int i = 0; i < 20; i++) engine.Tick(1.0 / 60);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 30; i++) engine.Tick(1.0 / 60);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{allocated} bytes allocated in 30 steady frames");
        Assert.True(engine.Poses.Changed.Count > 0, "the motors should still be moving during the measured frames");
        Assert.True(engine.Lights.Length > 100);
    }

    /// <summary>A render world that accepts everything and keeps nothing.</summary>
    private sealed class NullRenderWorld : IRenderWorld
    {
        private int _next = 1;

        public MeshHandle CreateMesh(MeshData mesh) => new(_next++);
        public void DestroyMesh(MeshHandle mesh) { }
        public TextureHandle CreateTexture(ImageData image, bool srgb) => new(_next++);
        public void DestroyTexture(TextureHandle texture) { }
        public MaterialHandle CreateMaterial(MaterialData material, IImageSource? images) => new(_next++);
        public void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images) { }
        public void DestroyMaterial(MaterialHandle material) { }
        public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId) => new(_next++);
        public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags) { }
        public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials) { }
        public void DestroyInstance(InstanceHandle instance) { }
        public void SetLights(ReadOnlySpan<SpotLight> lights) { }
        public void SetLines(ReadOnlySpan<LineVertex> segments) { }
        public void SetAtmosphere(AtmosphereState atmosphere) { }
        public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources) { }
    }
}
