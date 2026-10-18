using System.Numerics;
using Candela.Core.Geometry;
using Candela.Photometry;

namespace Candela.Rendering.Vulkan;

/// <summary>
/// The procedural scene of <c>candela render-test</c> and the golden-image tests: a stage floor (top at Z = 0), a
/// back wall, boxes and spheres with typical PBR materials (painted wood, gold, white plastic, brushed steel, black
/// stage box), an emissive LED panel (150 cd/m²) on the wall, three coloured spot lights with different beam angles
/// and intensities from 6.5 m (casting soft shadows), and a cable drawn as a line. Every object has a fixed pick id
/// (<see cref="PickIds"/>).
/// </summary>
public sealed class ProceduralTestScene
{
    private readonly List<MeshHandle> _meshes = new();
    private readonly List<MaterialHandle> _materials = new();
    private readonly List<InstanceHandle> _instances = new();

    private ProceduralTestScene(IRenderWorld world)
    {
        World = world;
    }

    /// <summary>Pick ids of the scene's objects.</summary>
    public static class PickIds
    {
        /// <summary>Stage floor.</summary>
        public const uint Floor = 1;

        /// <summary>Back wall.</summary>
        public const uint BackWall = 2;

        /// <summary>White painted box in the red pool.</summary>
        public const uint WoodBox = 3;

        /// <summary>Gold sphere in the green pool.</summary>
        public const uint GoldSphere = 4;

        /// <summary>White plastic sphere in the blue pool.</summary>
        public const uint PlasticSphere = 5;

        /// <summary>Tall brushed-steel box.</summary>
        public const uint SteelBox = 6;

        /// <summary>Black stage box.</summary>
        public const uint StageBox = 7;

        /// <summary>Emissive LED panel on the back wall.</summary>
        public const uint LedPanel = 8;
    }

    /// <summary>The world the scene was built in.</summary>
    public IRenderWorld World { get; }

    /// <summary>The stage lights (red 18°, green 28°, blue 40° beams).</summary>
    public SpotLight[] Lights { get; private set; } = Array.Empty<SpotLight>();

    /// <summary>The cable polyline as line-list pairs.</summary>
    public LineVertex[] Lines { get; private set; } = Array.Empty<LineVertex>();

    /// <summary>Instances created by the scene.</summary>
    public IReadOnlyList<InstanceHandle> Instances => _instances;

    /// <summary>Recommended camera: front of house, looking at the stage (EV100 8).</summary>
    public static RenderCamera CreateCamera()
    {
        var camera = RenderCamera.LookAt(new Vector3(0f, -9f, 3.8f), new Vector3(0f, 1.5f, 1.0f), 50f);
        camera.Ev100 = 8f;
        return camera;
    }

    /// <summary>Builds the scene in <paramref name="world"/> and sets its lights and lines.</summary>
    public static ProceduralTestScene Build(IRenderWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var scene = new ProceduralTestScene(world);
        scene.Populate();
        return scene;
    }

    /// <summary>Removes every object the scene created (lights and lines are cleared).</summary>
    public void Destroy()
    {
        foreach (var i in _instances)
            World.DestroyInstance(i);
        foreach (var m in _materials)
            World.DestroyMaterial(m);
        foreach (var m in _meshes)
            World.DestroyMesh(m);
        _instances.Clear();
        _materials.Clear();
        _meshes.Clear();
        World.SetLights(ReadOnlySpan<SpotLight>.Empty);
        World.SetLines(ReadOnlySpan<LineVertex>.Empty);
    }

    /// <summary>A spot light aimed from <paramref name="position"/> at <paramref name="target"/>.</summary>
    /// <param name="position">Lens position, m.</param>
    /// <param name="target">Point the beam axis passes through, m.</param>
    /// <param name="color">Light colour (luminance 1).</param>
    /// <param name="peakCandela">On-axis intensity, cd.</param>
    /// <param name="beamDeg">Beam angle (50 %), degrees.</param>
    /// <param name="fieldDeg">Field angle (10 %), degrees.</param>
    public static SpotLight Spot(Vector3 position, Vector3 target, LightColor color, float peakCandela, float beamDeg, float fieldDeg)
    {
        var direction = Vector3.Normalize(target - position);
        var up = MathF.Abs(direction.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        return new SpotLight
        {
            Position = position,
            Direction = direction,
            Up = Vector3.Normalize(up - Vector3.Dot(up, direction) * direction),
            Color = color.Rgb,
            PeakCandela = peakCandela,
            BeamAngle = beamDeg * MathF.PI / 180f,
            FieldAngle = fieldDeg * MathF.PI / 180f,
            SourceRadius = 0.08f,
            Iris = 1f,
            CastShadows = true,
        };
    }

    private void Populate()
    {
        var floorMaterial = Material("stage floor", new Vector3(0.45f, 0.45f, 0.46f), 0f, 0.7f);
        var wallMaterial = Material("back wall", new Vector3(0.55f, 0.55f, 0.58f), 0f, 0.9f);
        var wood = Material("white painted wood", new Vector3(0.72f, 0.70f, 0.66f), 0f, 0.55f);
        var gold = Material("gold", new Vector3(1.0f, 0.77f, 0.34f), 1f, 0.3f);
        var plastic = Material("white plastic", new Vector3(0.8f, 0.8f, 0.8f), 0f, 0.25f);
        var steel = Material("brushed steel", new Vector3(0.56f, 0.57f, 0.58f), 1f, 0.45f);
        var black = Material("black stage box", new Vector3(0.04f, 0.04f, 0.04f), 0f, 0.6f);
        var led = World.CreateMaterial(new MaterialData
        {
            Name = "LED panel",
            BaseColorFactor = new Vector4(0.02f, 0.02f, 0.02f, 1f),
            Roughness = 0.35f,
            EmissiveColor = new Vector3(0.8f, 0.9f, 1.0f),
            EmissiveLuminance = 150f,
        }, null);
        _materials.Add(led);

        var plane = Mesh(ProceduralMeshes.Plane(14f, 10f));
        var unitBox = Mesh(ProceduralMeshes.Box(Vector3.One));
        var sphere = Mesh(ProceduralMeshes.Sphere(1f, 48, 24));

        Place(plane, floorMaterial, Matrix4x4.CreateTranslation(0f, 1f, 0f), PickIds.Floor);
        Place(unitBox, wallMaterial, Box(new Vector3(14f, 0.2f, 6f), new Vector3(0f, 5.1f, 3f)), PickIds.BackWall);
        Place(unitBox, wood, Box(new Vector3(1f, 1f, 1f), new Vector3(-3.2f, 1.6f, 0.5f), 20f), PickIds.WoodBox);
        Place(sphere, gold, Matrix4x4.CreateScale(0.6f) * Matrix4x4.CreateTranslation(0.3f, 1.6f, 0.6f), PickIds.GoldSphere);
        Place(sphere, plastic, Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(3.0f, 1.2f, 0.5f), PickIds.PlasticSphere);
        Place(unitBox, steel, Box(new Vector3(0.6f, 0.6f, 2.0f), new Vector3(1.8f, 3.0f, 1.0f)), PickIds.SteelBox);
        Place(unitBox, black, Box(new Vector3(0.8f, 0.8f, 0.8f), new Vector3(-1.2f, 3.2f, 0.4f)), PickIds.StageBox);
        Place(unitBox, led, Box(new Vector3(2.0f, 0.04f, 1.1f), new Vector3(-0.5f, 4.98f, 3.2f)), PickIds.LedPanel);

        Lights = new[]
        {
            Spot(new Vector3(-3f, 0.5f, 6.5f), new Vector3(-3f, 1.2f, 0f), LightColor.FromRec709(new Vector3(1f, 0.22f, 0.06f)), 60000f, 18f, 26f),
            Spot(new Vector3(0.4f, 1.0f, 6.5f), new Vector3(0.3f, 1.8f, 0f), LightColor.FromRec709(new Vector3(0.15f, 1f, 0.2f)), 45000f, 28f, 40f),
            Spot(new Vector3(3.2f, 0f, 6.5f), new Vector3(3.1f, 1.0f, 0f), LightColor.FromRec709(new Vector3(0.15f, 0.4f, 1f)), 25000f, 40f, 60f),
        };
        World.SetLights(Lights);

        var cable = new[]
        {
            new Vector3(-6f, -2f, 0.02f), new Vector3(-3f, -2.6f, 0.02f), new Vector3(0f, -2.2f, 0.02f), new Vector3(3f, -2.7f, 0.02f),
            new Vector3(5.5f, -1.5f, 0.02f), new Vector3(6f, 4.9f, 0.02f), new Vector3(6f, 4.9f, 4.5f),
        };
        var lines = new LineVertex[(cable.Length - 1) * 2];
        var color = new Vector4(1f, 0.82f, 0.1f, 1f);
        for (int i = 0; i + 1 < cable.Length; i++)
        {
            lines[2 * i] = new LineVertex { Position = cable[i], Color = color, Width = 3f };
            lines[2 * i + 1] = new LineVertex { Position = cable[i + 1], Color = color, Width = 3f };
        }

        Lines = lines;
        World.SetLines(lines);
    }

    /// <summary>Unit box scaled to <paramref name="size"/>, turned about Z by <paramref name="degrees"/>, centred at <paramref name="centre"/>.</summary>
    private static Matrix4x4 Box(Vector3 size, Vector3 centre, float degrees = 0f) =>
        Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180f) * Matrix4x4.CreateTranslation(centre);

    private MaterialHandle Material(string name, Vector3 baseColor, float metallic, float roughness)
    {
        var handle = World.CreateMaterial(new MaterialData
        {
            Name = name,
            BaseColorFactor = new Vector4(baseColor, 1f),
            Metallic = metallic,
            Roughness = roughness,
        }, null);
        _materials.Add(handle);
        return handle;
    }

    private MeshHandle Mesh(MeshData mesh)
    {
        var handle = World.CreateMesh(mesh);
        _meshes.Add(handle);
        return handle;
    }

    private void Place(MeshHandle mesh, MaterialHandle material, Matrix4x4 world, uint pickId)
    {
        Span<MaterialHandle> materials = stackalloc MaterialHandle[1];
        materials[0] = material;
        _instances.Add(World.CreateInstance(mesh, materials, world, InstanceFlags.CastShadow, pickId));
    }
}
