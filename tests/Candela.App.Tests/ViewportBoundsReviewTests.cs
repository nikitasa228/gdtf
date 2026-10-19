using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Dmx.Input;
using Candela.Rendering;
using EngineSceneBounds = Candela.Engine.SceneBounds;

namespace Candela.App.Tests;

/// <summary>
/// The viewports' bounds, framing points and group outline now come from the engine's render binding instead of the
/// editor's own bounds-tracking world. These tests keep that old bookkeeping as a recording world between the binding
/// and a memory renderer (every mesh's bounds, every instance's world matrix, flags and pick id exactly as the renderer
/// receives them) and check on Фокина — with the heads moved by DMX — that box selection, framing and drop targets see
/// the same boxes as before, and that a selected group outlines exactly its members.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class ViewportBoundsReviewTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();
    private readonly SettingsStore _settings;

    public ViewportBoundsReviewTests()
    {
        Loc.SetLanguage("en");
        _settings = new SettingsStore(_log, _dir.File("settings.json"));
        _settings.Load();
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Fokina_bounds_framing_and_group_outline_match_what_the_renderer_was_sent()
    {
        using var session = EngineTestSupport.CreateSession(_log, _settings, _dir);
        Assert.True(session.Open(EngineTestSupport.Fokina).Succeeded);
        using var renderer = new RecordingRenderer();
        session.AttachRenderer(renderer);
        var content = Assert.IsType<EngineViewportContent>(session.Content);
        var engine = session.Engine!;

        // Every channel of the first universes at half: the moving heads pan and tilt away from their rest pose, so the
        // drawn boxes are not the placement boxes any more.
        var half = new byte[DmxInputHub.SlotCount];
        Array.Fill(half, (byte)128);
        var source = new DmxSourceId("test", "review");
        for (int frame = 0; frame < 90; frame++)
        {
            for (int universe = 1; universe <= 8; universe++)
                engine.Input.Submit(source, universe, half);
            session.Update(1 / 30.0);
            session.SyncRender();
        }

        Assert.True(content.IsSettled);
        var world = renderer.Recorder;
        Assert.True(world.InstanceCount > 91);
        var scene = session.Scene;
        var all = scene.All().ToList();
        int compared = 0, moved = 0;
        foreach (var e in all)
        {
            // Drop targets, framing a selection and box selection: the box of an entity and its subtree.
            bool had = OldBounds(content, world, e, engine.Models.GetBounds, out var expected);
            bool has = content.TryGetBounds(e.Id, out var actual);
            Assert.Equal(had, has);
            if (!had)
                continue;
            compared++;
            AssertSame(expected, actual, e.Name);
            if (e.Has<FixtureComponent>() && !EngineSceneBounds.Of(e, engine.Models.GetBounds).IsEmpty)
            {
                var placed = EngineSceneBounds.Of(e, engine.Models.GetBounds);
                if (Vector3.Distance(placed.Min, actual.Min) > 1e-3f || Vector3.Distance(placed.Max, actual.Max) > 1e-3f)
                    moved++;
            }
        }

        Assert.True(compared >= 91, $"only {compared} entities had bounds");
        Assert.True(moved > 10, $"only {moved} fixtures were drawn away from their rest box");

        // The scene's box (camera limits, grid) and the framing points.
        var oldScene = world.VisibleBounds();
        oldScene.Include(EngineSceneBounds.Compute(scene, engine.Models.GetBounds));
        AssertSame(oldScene, content.SceneBounds, "scene");
        var oldPoints = new List<Vector3>();
        EngineSceneBounds.CollectContentCorners(scene, oldPoints, engine.Models.GetBounds);
        EngineSceneBounds.CollectFixtureParts(engine.Poses, oldPoints);
        world.CollectVisibleCorners(oldPoints);
        var newPoints = new List<Vector3>();
        content.CollectFramingPoints(newPoints);
        Assert.Equal(oldPoints.Count, newPoints.Count);
        Assert.Equal(Sorted(oldPoints), Sorted(newPoints));

        // A selected group outlines exactly itself and its members (a nested group's too); nothing else is outlined.
        var fixtures = all.Where(e => e.Has<FixtureComponent>()).ToList();
        var outer = scene.Add(new Entity("outer group"));
        var inner = scene.Add(new Entity("inner group"));
        scene.Reparent(inner, outer);
        for (int i = 0; i < 12; i++)
            scene.Reparent(fixtures[i], i < 5 ? inner : outer);
        session.SyncRender();
        Assert.True(OldBounds(content, world, outer, engine.Models.GetBounds, out var outerBefore));
        Assert.True(content.TryGetBounds(outer.Id, out var outerNow));
        AssertSame(outerBefore, outerNow, outer.Name);
        var groups = new List<Entity> { outer, inner, fixtures[0], fixtures[20] };
        foreach (var group in groups)
        {
            session.Selection.Set(group.Id);
            session.SyncRender();
            var members = group.SelfAndDescendants().Select(d => content.Binding.PickIdOf(d.Id)).Where(p => p != 0).ToHashSet();
            foreach (var (pick, selected) in world.SelectedByPick())
                Assert.True(members.Contains(pick) == selected, $"pick {pick} outlined {selected} with '{group.Name}' selected");
        }

        session.Selection.Clear();
        session.SyncRender();
        Assert.DoesNotContain(world.SelectedByPick(), p => p.Selected);
    }

    /// <summary>
    /// The bounds as the editor computed them before: the instances drawn with each pick id of the subtree, from the
    /// mesh bounds and world matrices the renderer received; the component extents for what is not drawn (yet).
    /// </summary>
    private static bool OldBounds(EngineViewportContent content, RecordingRenderWorld world, Entity root, Func<Guid, Aabb?> modelBounds, out Aabb bounds)
    {
        bounds = Aabb.Empty;
        var stack = new Stack<Entity>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            foreach (var child in e.Children)
                stack.Push(child);
            if (!world.IncludeBounds(content.Binding.PickIdOf(e.Id), ref bounds))
            {
                var own = EngineSceneBounds.Of(e, modelBounds);
                if (!own.IsEmpty && e.Components.Count > 0 && !e.Has<GroupComponent>())
                    bounds.Include(own);
            }
        }

        return !bounds.IsEmpty;
    }

    private static void AssertSame(Aabb expected, Aabb actual, string what)
    {
        Assert.True(Vector3.Distance(expected.Min, actual.Min) < 1e-4f && Vector3.Distance(expected.Max, actual.Max) < 1e-4f,
            $"{what}: {expected.Min}–{expected.Max} before, {actual.Min}–{actual.Max} now");
    }

    private static List<(int, int, int)> Sorted(List<Vector3> points) =>
        points.Select(p => ((int)MathF.Round(p.X * 1000f), (int)MathF.Round(p.Y * 1000f), (int)MathF.Round(p.Z * 1000f))).Order().ToList();

    /// <summary>A memory renderer whose world records what the binding sends.</summary>
    private sealed class RecordingRenderer : IRenderer
    {
        private readonly MemoryRenderer _inner = new();

        public RecordingRenderer() => Recorder = new RecordingRenderWorld(_inner.World);

        public RecordingRenderWorld Recorder { get; }

        public RendererInfo Info => _inner.Info;

        public IRenderWorld World => Recorder;

        public IRenderTarget CreateTarget(int width, int height) => _inner.CreateTarget(width, height);

        public void Render(IRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, float deltaTime) =>
            _inner.Render(target, camera, view, settings, deltaTime);

        public ImageData ReadPixels(IRenderTarget target) => _inner.ReadPixels(target);

        public PickResult? Pick(IRenderTarget target, int x, int y) => null;

        public IFinalRender StartFinalRender(RenderCamera camera, FinalRenderSettings settings) => _inner.StartFinalRender(camera, settings);

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>The editor's former bounds bookkeeping, kept as a reference: per mesh its bounds, per instance its state.</summary>
    private sealed class RecordingRenderWorld(IRenderWorld inner) : IRenderWorld
    {
        private readonly Dictionary<MeshHandle, Aabb> _meshBounds = new();
        private readonly Dictionary<InstanceHandle, (MeshHandle Mesh, Matrix4x4 World, InstanceFlags Flags, uint Pick)> _instances = new();

        public int InstanceCount => _instances.Count;

        public bool IncludeBounds(uint pick, ref Aabb bounds)
        {
            if (pick == 0)
                return false;
            bool any = false;
            foreach (var state in _instances.Values)
            {
                if (state.Pick != pick || !_meshBounds.TryGetValue(state.Mesh, out var local) || local.IsEmpty)
                    continue;
                var box = local.Transform(state.World);
                if (!Finite(box))
                    continue;
                bounds.Include(box);
                any = true;
            }

            return any;
        }

        public Aabb VisibleBounds()
        {
            var bounds = Aabb.Empty;
            foreach (var box in VisibleBoxes())
                bounds.Include(box);
            return bounds;
        }

        public void CollectVisibleCorners(List<Vector3> corners)
        {
            foreach (var b in VisibleBoxes())
            {
                for (int i = 0; i < 8; i++)
                    corners.Add(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
            }
        }

        /// <summary>Per pick id: whether every instance drawn with it carries the Selected flag (they must agree).</summary>
        public IEnumerable<(uint Pick, bool Selected)> SelectedByPick()
        {
            foreach (var group in _instances.Values.Where(s => s.Pick != 0).GroupBy(s => s.Pick))
            {
                bool any = group.Any(s => (s.Flags & InstanceFlags.Selected) != 0);
                bool every = group.All(s => (s.Flags & InstanceFlags.Selected) != 0);
                Assert.True(any == every, $"pick {group.Key}: only some instances are outlined");
                yield return (group.Key, any);
            }
        }

        private IEnumerable<Aabb> VisibleBoxes()
        {
            foreach (var state in _instances.Values)
            {
                if ((state.Flags & InstanceFlags.Hidden) != 0 || !_meshBounds.TryGetValue(state.Mesh, out var local) || local.IsEmpty)
                    continue;
                var box = local.Transform(state.World);
                if (Finite(box) && box.Size.Length() <= 10_000f)
                    yield return box;
            }
        }

        private static bool Finite(Aabb b) =>
            float.IsFinite(b.Min.X) && float.IsFinite(b.Min.Y) && float.IsFinite(b.Min.Z) &&
            float.IsFinite(b.Max.X) && float.IsFinite(b.Max.Y) && float.IsFinite(b.Max.Z);

        public MeshHandle CreateMesh(MeshData mesh)
        {
            var handle = inner.CreateMesh(mesh);
            if (handle.IsValid)
                _meshBounds[handle] = mesh.ComputeBounds();
            return handle;
        }

        public void DestroyMesh(MeshHandle mesh)
        {
            _meshBounds.Remove(mesh);
            inner.DestroyMesh(mesh);
        }

        public TextureHandle CreateTexture(ImageData image, bool srgb) => inner.CreateTexture(image, srgb);

        public void DestroyTexture(TextureHandle texture) => inner.DestroyTexture(texture);

        public MaterialHandle CreateMaterial(MaterialData material, IImageSource? images) => inner.CreateMaterial(material, images);

        public void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images) => inner.UpdateMaterial(handle, material, images);

        public void DestroyMaterial(MaterialHandle material) => inner.DestroyMaterial(material);

        public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId)
        {
            var handle = inner.CreateInstance(mesh, materials, world, flags, pickId);
            if (handle.IsValid)
                _instances[handle] = (mesh, world, flags, pickId);
            return handle;
        }

        public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags)
        {
            if (_instances.TryGetValue(instance, out var state))
                _instances[instance] = state with { World = world, Flags = flags };
            inner.UpdateInstance(instance, world, flags);
        }

        public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials) => inner.SetInstanceMaterials(instance, materials);

        public void DestroyInstance(InstanceHandle instance)
        {
            _instances.Remove(instance);
            inner.DestroyInstance(instance);
        }

        public void SetInstanceEmission(InstanceHandle instance, Vector3 luminance) => inner.SetInstanceEmission(instance, luminance);

        public void SetLights(ReadOnlySpan<SpotLight> lights) => inner.SetLights(lights);

        public void SetLines(ReadOnlySpan<LineVertex> segments) => inner.SetLines(segments);

        public void SetAtmosphere(AtmosphereState atmosphere) => inner.SetAtmosphere(atmosphere);

        public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources) => inner.SetNoiseSources(sources);
    }
}
