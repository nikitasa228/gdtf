using System.Numerics;
using Candela.Core.Geometry;

namespace Candela.Assets;

/// <summary>A mesh instance in source space: the mesh (possibly shared) and its node's world matrix (row vectors).</summary>
internal readonly record struct RawPart(string Name, MeshData Mesh, Matrix4x4 World);

/// <summary>What a reader produces before axis/unit conversion: geometry and materials in the file's own space.</summary>
internal sealed class RawModel
{
    public string Name { get; set; } = "";
    public List<RawPart> Parts { get; } = new();
    public List<MaterialData> Materials { get; } = new();

    /// <summary>Up axis by format convention or metadata.</summary>
    public SourceUpAxis UpAxis { get; set; } = SourceUpAxis.Y;

    /// <summary>
    /// Exact source → Candela basis from file metadata (FBX axis system), used instead of <see cref="UpAxis"/> when set.
    /// Rotation/reflection only (no scale).
    /// </summary>
    public Matrix4x4? Basis { get; set; }

    /// <summary>Metres per file unit from metadata or convention.</summary>
    public float UnitScale { get; set; } = 1f;

    /// <summary>True when <see cref="UnitScale"/> comes from the file.</summary>
    public bool UnitsKnown { get; set; }
}
