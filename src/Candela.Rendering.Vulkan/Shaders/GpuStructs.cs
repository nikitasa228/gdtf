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

/// <summary>A placed mesh (160 bytes), see <c>Instance</c> in common.glsl.</summary>
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
}

/// <summary>A spot light (96 bytes), see <c>Light</c> in common.glsl and <see cref="LightPacker"/>.</summary>
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
}

/// <summary>Per-view constants (uniform buffer, 592 bytes), see <c>FrameData</c> in common.glsl.</summary>
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
