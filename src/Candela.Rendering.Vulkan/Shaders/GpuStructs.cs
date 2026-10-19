using System.Numerics;
using System.Runtime.InteropServices;

namespace Candela.Rendering.Vulkan;

// C# mirrors of the GLSL structs in Shaders/common.glsl. All GPU blocks use the scalar block layout
// (GL_EXT_scalar_block_layout), so a field is aligned to its component size and these sequential structs match the
// GLSL declarations byte for byte (sizes are asserted by the tests). Matrices are System.Numerics matrices uploaded
// verbatim: GLSL reads them as the transpose, so `M * v` in GLSL equals `v * M` in C#.

/// <summary>Interleaved mesh vertex (48 bytes): position, normal, uv, tangent (xyz + bitangent sign).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector4 Tangent;
}

/// <summary>PBR material (112 bytes), see <c>Material</c> in common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMaterial
{
    public Vector4 BaseColor;
    public Vector4 BaseColorFactor709;
    public Vector3 Emissive;
    public float Metallic;
    public float Roughness;
    public float F0;
    public float AlphaCutoff;
    public float Transmission;
    public Vector2 UvScale;
    public uint BaseColorTexture;
    public uint NormalTexture;
    public uint MetallicRoughnessTexture;
    public uint OcclusionTexture;
    public uint EmissiveTexture;
    public uint Flags;
    public Vector3 Emissive709;
    public float Ior;
}

/// <summary>A placed mesh (192 bytes), see <c>Instance</c> in common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuInstance
{
    public Matrix4x4 World;
    public Matrix4x4 NormalMatrix;
    public uint Flags;
    public uint PickId;
    public uint MaterialSlotOffset;
    public uint MaterialSlotCount;
    public Vector4 BoundingSphere;

    /// <summary>Additive emitted luminance, linear Rec.2020 cd/m² (lens glow), seen along <see cref="EmissionAxis"/>.</summary>
    public Vector3 Emission;

    /// <summary>1 / (half-angle)² of the directional falloff of <see cref="Emission"/> (rad⁻²); 0 = uniform.</summary>
    public float EmissionSharpness;

    /// <summary>World direction of the directional emission (unit; unused when uniform).</summary>
    public Vector3 EmissionAxis;

    public float Pad0;
}

/// <summary>A spot light (224 bytes), see <c>Light</c> in common.glsl and <see cref="LightPacker"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLight
{
    public Vector3 Position;
    public float MaxIntensity;
    public Vector3 Direction;
    public float CosCutoff;
    public Vector3 Intensity;
    public float InvHalfBeam;
    public float Exponent;
    public float HalfField;
    public float InvWindow;
    public float Cutoff;
    public float IrisAngle;
    public float IrisSoftStart;
    public uint Shape;
    public uint Flags;
    public Vector3 Up;
    public float SourceRadius;
    public Vector3 Right;
    public float GateScale;
    public uint GoboTexture;
    public float GoboBlur;
    public float EdgeSoftness;
    public uint PrismFacets;
    public Vector2 GoboRotation;
    public float PrismCosSpread;
    public float PrismSinSpread;
    public float PrismRotation;
    public float BoundingAngle;
    public float SolidAngle;

    /// <summary>Layer coordinate of the profile in <see cref="BeamScatteringTable"/> (family × exponent steps + exponent index).</summary>
    public float BeamLayer;

    public Vector4 BladeA;
    public Vector4 BladeB;
    public Vector4 BladeC;
    public Vector4 BladeD;
}

/// <summary>Per-view data of a light (16 bytes), see <c>LightView</c> in common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLightView
{
    /// <summary>Shadow atlas tile, or <see cref="ShaderConstants.NoShadow"/>.</summary>
    public uint ShadowSlot;

    /// <summary>LIGHT_VIEW_* bits.</summary>
    public uint Flags;

    /// <summary>First virtual point light of the light (reflective shadow maps).</summary>
    public uint VplOffset;

    /// <summary>Number of virtual point lights.</summary>
    public uint VplCount;
}

/// <summary>A tile of the shadow atlas (192 bytes), see <c>ShadowSlot</c> in common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuShadowSlot
{
    /// <summary>World → light clip space (reverse-Z perspective with an infinite far plane).</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>Light clip space → world.</summary>
    public Matrix4x4 InverseViewProjection;

    /// <summary>Atlas UV of the tile: offset (xy) and size (zw).</summary>
    public Vector4 AtlasRect;

    public Vector3 Position;
    public float Near;
    public Vector3 Axis;
    public float TanHalfAngle;

    /// <summary>World size of one shadow texel per metre of depth along the axis (2·tan(half angle)/resolution).</summary>
    public float TexelScale;

    public float Resolution;

    /// <summary>1 / atlas size (UV of one atlas texel).</summary>
    public float AtlasTexel;

    /// <summary>Bit 0: the tile holds reflective-shadow-map data.</summary>
    public uint Flags;
}

/// <summary>A virtual point light of the reflective shadow maps (48 bytes), see <c>Vpl</c> in common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuVpl
{
    public Vector3 Position;
    public float Pad0;
    public Vector3 Normal;
    public float Pad1;

    /// <summary>Reflected flux of the surface element (linear Rec.2020, lm): the VPL radiates Φ/π·cos θ cd.</summary>
    public Vector3 Flux;

    public float Pad2;
}

/// <summary>Per-view constants (uniform buffer, 992 bytes), see <c>FrameData</c> in common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuFrameData
{
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseView;
    public Matrix4x4 InverseProjection;
    public Matrix4x4 InverseViewProjection;
    public Vector4 CameraPosition;
    public Vector4 CameraForward;
    public Vector4 RenderSize;
    public Vector4 Background;
    public Vector4 GridColorMinor;
    public Vector4 GridColorMajor;
    public Vector4 GridAxisX;
    public Vector4 GridAxisY;
    public float Exposure;
    public float NearPlane;
    public float FarPlane;
    public float ClusterLogScale;
    public float ClusterLogBias;
    public uint ClusterCountX;
    public uint ClusterCountY;
    public uint ClusterCountZ;
    public uint LightCount;
    public uint ViewMode;
    public uint Flags;
    public float GridSpacing;
    public float GridMajorEvery;
    public float GridFadeDistance;
    public float RangeEpsilon;
    public uint ClusterIndexCapacity;
    public Vector4 Headlight;
    public Matrix4x4 PreviousViewProjection;
    public Vector4 DisplaySize;
    public Vector4 VolumeGrid;
    public Vector4 VolumeDepth;
    public Vector4 VolumeJitter;
    public Vector4 Media;
    public Vector4 SmokeMin;
    public Vector4 SmokeMax;
    public Vector4 FlameMin;
    public Vector4 FlameMax;
    public Vector4 NoiseMin;
    public Vector4 NoiseMax;
    public Vector4 FlameLut;
    public Vector4 OverlayRange;
    public Vector4 OverlayOutOfRange;
    public Vector4 SelectionColor;
    public Vector4 PixelFootprint;
    public float LightRangeScale;
    public uint BeamCount;
    public uint Features;
    public uint OverlayMode;
    public uint VplCount;
    public uint FrameIndex;
    public uint NoiseSourceCount;
    public float BloomIntensity;
    public float IndirectIntensity;
    public float RenderScale;
    public uint ShadowTileCount;
    public float VolumeHistoryWeight;

    /// <summary>Beam culling tiles along x and y (<see cref="ShaderConstants.BeamTileSize"/> half-resolution pixels each).</summary>
    public uint BeamTilesX;

    public uint BeamTilesY;

    /// <summary>Mask words per tile (one bit per beam).</summary>
    public uint BeamWordsPerTile;

    /// <summary>BEAM_FLAG_* bits.</summary>
    public uint BeamFlags;

    /// <summary>Weight of the accumulated beam modulation ratio (0 = none).</summary>
    public float BeamHistoryWeight;

    /// <summary>0.25 … 1: scales the segments and cookie samples per beam crossing.</summary>
    public float BeamQuality;

    /// <summary>Scattering coefficient of the beam envelope image, 1/m.</summary>
    public float BeamSigmaRef;

    public float BeamPad0;
}

/// <summary>Push constants of every draw (32 bytes), see <c>DrawConstants</c> in common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDrawConstants
{
    public uint InstanceIndex;
    public uint MaterialIndex;
    public uint Mode;
    public float Width;
    public Vector4 Color;
}

/// <summary>Counter block of the clustered light lists (16 bytes), see <c>ClusterCounterBuffer</c> in view.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuClusterCounter
{
    public uint IndexCount;
    public uint IndexRequired;
    public uint OverflowedClusters;
    public uint Pad;
}

/// <summary>Auto-exposure result written by the GPU (16 bytes), see <c>ExposureResult</c> in exposure.comp.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuExposureResult
{
    /// <summary>Average log2 luminance (cd/m²) of the frame (percentile-trimmed histogram mean).</summary>
    public float AverageLog2Luminance;

    /// <summary>Pixels counted.</summary>
    public uint Count;

    public uint Pad0;
    public uint Pad1;
}

/// <summary>Path tracer parameters (uniform buffer, 112 bytes), see <c>PtParams</c> in pt_common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuPtParams
{
    /// <summary>Box the haze fills (xyz), m.</summary>
    public Vector4 MediaMin;

    /// <summary>Box the haze fills (xyz), m; w = haze scattering coefficient, 1/m.</summary>
    public Vector4 MediaMax;

    /// <summary>x = smoke scattering majorant (1/m), y = flame absorption (1/m), z = Henyey–Greenstein g, w = 1 / exposure.</summary>
    public Vector4 Majorants;

    /// <summary>rgb = background luminance (Rec.2020, cd/m²), w = firefly clamp (cd/m², 0 = off).</summary>
    public Vector4 Background;

    /// <summary>x = lens clearance of shadow rays (m), y = lens polygon offset in front of the light (m), z = polygon radius factor.</summary>
    public Vector4 Geometry;

    public uint LightCount;
    public uint MaxBounces;
    public uint MaxVolumeBounces;

    /// <summary>PT_FLAG_* bits.</summary>
    public uint Flags;

    public uint Seed;
    public uint Width;
    public uint Height;
    public uint Pad0;
}
