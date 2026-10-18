using System.Numerics;
using Candela.Core.Geometry;
using Candela.Rendering;

namespace Candela.Engine.Tests;

/// <summary>
/// An <see cref="IRenderWorld"/> that keeps what it is told in memory and checks the contract: every handle used was
/// created and not destroyed, instances reference live meshes and materials, matrices are finite.
/// </summary>
internal sealed class RecordingRenderWorld : IRenderWorld
{
    private int _next = 1;

    public Dictionary<int, MeshData> Meshes { get; } = new();
    public Dictionary<int, MaterialData> Materials { get; } = new();
    public Dictionary<int, ImageData> Textures { get; } = new();
    public Dictionary<int, Instance> Instances { get; } = new();
    public SpotLight[] Lights { get; private set; } = Array.Empty<SpotLight>();
    public LineVertex[] Lines { get; private set; } = Array.Empty<LineVertex>();
    public NoiseSource[] NoiseSources { get; private set; } = Array.Empty<NoiseSource>();
    public AtmosphereState? Atmosphere { get; private set; }
    public int AtmosphereUpdates { get; private set; }
    public int InstanceUpdates { get; private set; }
    public List<string> Violations { get; } = new();

    public sealed class Instance
    {
        public required MeshHandle Mesh { get; init; }
        public required MaterialHandle[] Materials { get; set; }
        public required Matrix4x4 World { get; set; }
        public required InstanceFlags Flags { get; set; }
        public required uint PickId { get; init; }
    }

    public MeshHandle CreateMesh(MeshData mesh)
    {
        try
        {
            mesh.Validate();
        }
        catch (InvalidDataException e)
        {
            Violations.Add($"invalid mesh '{mesh.Name}': {e.Message}");
        }
        int h = _next++;
        Meshes[h] = mesh;
        return new MeshHandle(h);
    }

    public void DestroyMesh(MeshHandle mesh)
    {
        if (!Meshes.Remove(mesh.Value)) Violations.Add($"destroyed unknown mesh {mesh.Value}");
        foreach (var i in Instances.Values)
            if (i.Mesh == mesh) Violations.Add($"destroyed mesh {mesh.Value} while an instance still draws it");
    }

    public TextureHandle CreateTexture(ImageData image, bool srgb)
    {
        if (image.Width <= 0 || image.Height <= 0) Violations.Add("empty texture");
        int h = _next++;
        Textures[h] = image;
        return new TextureHandle(h);
    }

    public void DestroyTexture(TextureHandle texture)
    {
        if (!Textures.Remove(texture.Value)) Violations.Add($"destroyed unknown texture {texture.Value}");
        foreach (var l in Lights)
            if (l.Gobo == texture) Violations.Add($"destroyed texture {texture.Value} while a light still projects it");
    }

    public MaterialHandle CreateMaterial(MaterialData material, IImageSource? images)
    {
        int h = _next++;
        Materials[h] = material;
        return new MaterialHandle(h);
    }

    public void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images)
    {
        if (!Materials.ContainsKey(handle.Value)) Violations.Add($"updated unknown material {handle.Value}");
        Materials[handle.Value] = material;
    }

    public void DestroyMaterial(MaterialHandle material)
    {
        if (!Materials.Remove(material.Value)) Violations.Add($"destroyed unknown material {material.Value}");
        foreach (var i in Instances.Values)
            if (Array.IndexOf(i.Materials, material) >= 0) Violations.Add($"destroyed material {material.Value} while an instance still uses it");
    }

    public InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId)
    {
        if (!Meshes.ContainsKey(mesh.Value)) Violations.Add($"instance of unknown mesh {mesh.Value}");
        foreach (var m in materials)
            if (!Materials.ContainsKey(m.Value)) Violations.Add($"instance with unknown material {m.Value}");
        CheckMatrix(world);
        int h = _next++;
        Instances[h] = new Instance { Mesh = mesh, Materials = materials.ToArray(), World = world, Flags = flags, PickId = pickId };
        return new InstanceHandle(h);
    }

    public void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags)
    {
        InstanceUpdates++;
        CheckMatrix(world);
        if (!Instances.TryGetValue(instance.Value, out var i))
        {
            Violations.Add($"updated unknown instance {instance.Value}");
            return;
        }
        i.World = world;
        i.Flags = flags;
    }

    public void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials)
    {
        if (!Instances.TryGetValue(instance.Value, out var i)) Violations.Add($"materials of unknown instance {instance.Value}");
        else i.Materials = materials.ToArray();
    }

    public void DestroyInstance(InstanceHandle instance)
    {
        if (!Instances.Remove(instance.Value)) Violations.Add($"destroyed unknown instance {instance.Value}");
    }

    public void SetLights(ReadOnlySpan<SpotLight> lights)
    {
        foreach (var l in lights)
        {
            if (!float.IsFinite(l.PeakCandela) || !float.IsFinite(l.Position.X) || !float.IsFinite(l.Direction.Z)) Violations.Add("non-finite light");
            if (l.Gobo.IsValid && !Textures.ContainsKey(l.Gobo.Value)) Violations.Add($"light projects unknown texture {l.Gobo.Value}");
        }
        Lights = lights.ToArray();
    }

    public void SetLines(ReadOnlySpan<LineVertex> segments)
    {
        if (segments.Length % 2 != 0) Violations.Add("odd number of line vertices");
        Lines = segments.ToArray();
    }

    public void SetAtmosphere(AtmosphereState atmosphere)
    {
        Atmosphere = atmosphere;
        AtmosphereUpdates++;
    }

    public void SetNoiseSources(ReadOnlySpan<NoiseSource> sources) => NoiseSources = sources.ToArray();

    private void CheckMatrix(in Matrix4x4 m)
    {
        if (!float.IsFinite(m.M11 + m.M22 + m.M33 + m.M41 + m.M42 + m.M43)) Violations.Add("non-finite instance matrix");
    }
}
