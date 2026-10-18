using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan.Tests;

/// <summary>Small scenes for the stage-2 tests: floors, boxes, walls and spot lights built into a renderer's world.</summary>
internal sealed class StageScene
{
    private readonly IRenderWorld _world;
    private readonly Dictionary<string, MeshHandle> _meshes = new();

    public StageScene(VulkanRenderer renderer)
    {
        Renderer = renderer;
        _world = renderer.World;
    }

    public VulkanRenderer Renderer { get; }

    public IRenderWorld World => _world;

    /// <summary>A Lambertian material (IOR 1: no specular) of the given linear Rec.709 albedo.</summary>
    public MaterialHandle Lambert(Vector3 albedo, string name = "lambert") => _world.CreateMaterial(new MaterialData
    {
        Name = name,
        BaseColorFactor = new Vector4(albedo, 1f),
        Metallic = 0f,
        Roughness = 1f,
        Ior = 1f,
    }, null);

    /// <summary>A floor plane at Z = 0 centred on the origin.</summary>
    public InstanceHandle Floor(float size, MaterialHandle material, InstanceFlags flags = InstanceFlags.CastShadow, uint pickId = 1) =>
        Place(Mesh($"floor {size}", () => ProceduralMeshes.Plane(size, size)), material, Matrix4x4.Identity, flags, pickId);

    /// <summary>An axis-aligned box of the given size centred at <paramref name="centre"/>.</summary>
    public InstanceHandle Box(Vector3 size, Vector3 centre, MaterialHandle material, InstanceFlags flags = InstanceFlags.CastShadow, uint pickId = 2) =>
        Place(Mesh("box", () => ProceduralMeshes.Box(Vector3.One)), material, Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(centre), flags, pickId);

    /// <summary>A sphere of radius <paramref name="radius"/> centred at <paramref name="centre"/>.</summary>
    public InstanceHandle Sphere(float radius, Vector3 centre, MaterialHandle material, InstanceFlags flags = InstanceFlags.CastShadow, uint pickId = 3) =>
        Place(Mesh("sphere", () => ProceduralMeshes.Sphere(1f, 32, 16)), material, Matrix4x4.CreateScale(radius) * Matrix4x4.CreateTranslation(centre),
            flags, pickId);

    public InstanceHandle Place(MeshHandle mesh, MaterialHandle material, Matrix4x4 world, InstanceFlags flags, uint pickId)
    {
        Span<MaterialHandle> materials = stackalloc MaterialHandle[1];
        materials[0] = material;
        return _world.CreateInstance(mesh, materials, world, flags, pickId);
    }

    /// <summary>A white (or coloured) spot light aimed from <paramref name="position"/> at <paramref name="target"/>.</summary>
    public static SpotLight Spot(Vector3 position, Vector3 target, float peakCandela, float beamDeg, float fieldDeg, LightColor color = default,
        bool shadows = true, BeamType type = BeamType.Wash)
    {
        var light = ProceduralTestScene.Spot(position, target, color, peakCandela, beamDeg, fieldDeg);
        light.CastShadows = shadows;
        light.BeamType = type;
        return light;
    }

    /// <summary>A gobo image: an opaque disc with a transparent four-leaf pattern and a bar (asymmetric, so orientation shows).</summary>
    public static ImageData GoboImage(int size = 128)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f, v = 1f - (y + 0.5f) / size * 2f;
                float r = MathF.Sqrt(u * u + v * v);
                float a = MathF.Atan2(v, u);
                bool open = r < 0.85f && (MathF.Cos(2f * a) * 0.5f + 0.5f > 0.55f || r < 0.15f);
                if (v > 0.55f && v < 0.7f && u > -0.1f && u < 0.6f)
                    open = true; // a bar at the top right: shows the orientation
                int i = (y * size + x) * 4;
                byte value = open ? (byte)255 : (byte)0;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = value;
            }
        }

        return new ImageData { Width = size, Height = size, Pixels = pixels };
    }

    /// <summary>Renders one frame (or several, for temporal effects) and returns the display image.</summary>
    public ImageData Render(IRenderTarget target, RenderCamera camera, ViewOptions view, PreviewSettings settings, int frames = 1, float dt = 1f / 30f)
    {
        for (int i = 0; i < frames; i++)
            Renderer.Render(target, camera, view, settings, dt);
        return Renderer.ReadPixels(target);
    }

    private MeshHandle Mesh(string key, Func<MeshData> create)
    {
        if (!_meshes.TryGetValue(key, out var mesh))
        {
            mesh = _world.CreateMesh(create());
            _meshes.Add(key, mesh);
        }

        return mesh;
    }
}
