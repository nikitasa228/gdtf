using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Rendering;

/// <summary>Opaque handles into a renderer's retained scene. Value 0 is invalid.</summary>
public readonly record struct MeshHandle(int Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct MaterialHandle(int Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct TextureHandle(int Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct InstanceHandle(int Value)
{
    public bool IsValid => Value != 0;
}

[Flags]
public enum InstanceFlags
{
    None = 0,
    CastShadow = 1,

    /// <summary>Drawn with the selection outline.</summary>
    Selected = 2,

    Hidden = 4,

    /// <summary>Not written to the pick buffer (helpers, beams).</summary>
    NotPickable = 8,

    /// <summary>Highlighted by the cable overlay (devices connected to cables).</summary>
    CableHighlight = 16,

    /// <summary>Unlit, semi-transparent (reference drawings).</summary>
    Reference = 32,
}

/// <summary>
/// A spot light produced from one beam of a fixture's current state. Photometric: <see cref="PeakCandela"/> is the
/// on-axis luminous intensity including the dimmer; <see cref="Color"/> is linear Rec.2020 normalised to luminance 1.
/// The intensity distribution is the generalised Gaussian of Candela.Photometry.BeamDistribution given by
/// <see cref="BeamAngle"/> (50 %) and <see cref="FieldAngle"/> (10 %), both full angles in radians.
/// </summary>
public struct SpotLight
{
    public Vector3 Position;

    /// <summary>Beam axis (normalised).</summary>
    public Vector3 Direction;

    /// <summary>Orientation of gobos/blades around the axis (normalised, perpendicular to Direction).</summary>
    public Vector3 Up;

    public Vector3 Color;
    public float PeakCandela;
    public float BeamAngle;
    public float FieldAngle;

    /// <summary>Radius of the exit lens in metres (beam starts with this width; soft shadow size).</summary>
    public float SourceRadius;

    /// <summary>Gobo image (alpha = transmission), invalid = open.</summary>
    public TextureHandle Gobo;

    public float GoboRotation;

    /// <summary>0 = sharp projection, 1 = fully defocused.</summary>
    public float GoboBlur;

    /// <summary>Iris opening as a fraction of the field angle (1 = open).</summary>
    public float Iris;

    /// <summary>Framing blade insertion 0..1 for blades A,B,C,D (0 = out).</summary>
    public Vector4 BladeInsertion;

    /// <summary>Blade tilt angles in radians for blades A,B,C,D.</summary>
    public Vector4 BladeAngle;

    /// <summary>Rotation of the whole framing module in radians.</summary>
    public float BladeRotation;

    /// <summary>Prism facets (0 = no prism); each facet is a copy of the beam around the axis.</summary>
    public int PrismFacets;

    public float PrismRotation;

    /// <summary>Angle between the facet beams and the axis in radians.</summary>
    public float PrismSpread;

    /// <summary>0..1 diffusion that widens and softens the beam.</summary>
    public float Frost;

    public bool CastShadows;

    /// <summary>Entity id of the fixture (for picking and the light-meter breakdown).</summary>
    public Guid FixtureId;
}

/// <summary>A coloured polyline vertex (cables, gizmo guides, beam outlines). Width in pixels.</summary>
public struct LineVertex
{
    public Vector3 Position;
    public Vector4 Color;
    public float Width;
}

/// <summary>A 3D scalar field over a box (smoke density, flame temperature) for the volumetric renderer.</summary>
public sealed class VolumeGrid
{
    /// <summary>World-space box.</summary>
    public Vector3 Min { get; init; }

    public Vector3 Max { get; init; }
    public int SizeX { get; init; }
    public int SizeY { get; init; }
    public int SizeZ { get; init; }

    /// <summary>x fastest, then y, then z. Length = SizeX*SizeY*SizeZ.</summary>
    public float[] Values { get; init; } = Array.Empty<float>();
}

/// <summary>Participating media of the scene this frame.</summary>
public sealed class AtmosphereState
{
    /// <summary>Uniform haze scattering coefficient everywhere (1/m).</summary>
    public float BaseScattering { get; set; } = 0.02f;

    /// <summary>Henyey–Greenstein g.</summary>
    public float Anisotropy { get; set; } = 0.7f;

    /// <summary>Smoke density (0..1) from the simulation, or null.</summary>
    public VolumeGrid? SmokeDensity { get; set; }

    /// <summary>Scattering coefficient (1/m) at smoke density 1.</summary>
    public float SmokeScatteringAtFullDensity { get; set; } = 0.5f;

    /// <summary>Flame temperature in kelvin (0 = no flame), emissive as a blackbody, or null.</summary>
    public VolumeGrid? FlameTemperature { get; set; }

    /// <summary>Sound pressure level field in dB for the noise overlay's volumetric fog, or null.</summary>
    public VolumeGrid? NoiseField { get; set; }
}

/// <summary>Noise source for the noise overlay (fixture position + dB(A) at 1 m).</summary>
public readonly record struct NoiseSource(Vector3 Position, float DecibelsAt1m);

/// <summary>
/// The renderer's retained scene. The engine creates meshes/materials/instances when entities appear and updates
/// transforms/flags when they change; lights, lines, atmosphere and noise sources are replaced every frame.
/// All calls happen on the main thread.
/// </summary>
public interface IRenderWorld
{
    MeshHandle CreateMesh(MeshData mesh);
    void DestroyMesh(MeshHandle mesh);

    /// <summary>Creates a texture from an image. <paramref name="srgb"/> = colour data (albedo, gobos), false = data (normals).</summary>
    TextureHandle CreateTexture(ImageData image, bool srgb);

    void DestroyTexture(TextureHandle texture);

    /// <summary>Material whose texture keys are resolved through <paramref name="images"/>.</summary>
    MaterialHandle CreateMaterial(MaterialData material, IImageSource? images);

    void UpdateMaterial(MaterialHandle handle, MaterialData material, IImageSource? images);
    void DestroyMaterial(MaterialHandle material);

    /// <summary>
    /// Places a mesh. <paramref name="materials"/> maps the mesh's material slots (index = slot). <paramref name="pickId"/>
    /// identifies the entity under the cursor (0 = none).
    /// </summary>
    InstanceHandle CreateInstance(MeshHandle mesh, ReadOnlySpan<MaterialHandle> materials, Matrix4x4 world, InstanceFlags flags, uint pickId);

    void UpdateInstance(InstanceHandle instance, Matrix4x4 world, InstanceFlags flags);
    void SetInstanceMaterials(InstanceHandle instance, ReadOnlySpan<MaterialHandle> materials);
    void DestroyInstance(InstanceHandle instance);

    void SetLights(ReadOnlySpan<SpotLight> lights);

    /// <summary>Polylines as line-list pairs (two vertices per segment).</summary>
    void SetLines(ReadOnlySpan<LineVertex> segments);

    void SetAtmosphere(AtmosphereState atmosphere);
    void SetNoiseSources(ReadOnlySpan<NoiseSource> sources);
}
