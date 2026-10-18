using System.Numerics;
using System.Runtime.InteropServices;
using Candela.Core.Geometry;
using SharpGLTF.Schema2;
using CoreAlphaMode = Candela.Core.Geometry.AlphaMode;
using GltfAlphaMode = SharpGLTF.Schema2.AlphaMode;

namespace Candela.Mvr;

/// <summary>
/// Writes a Core <see cref="ModelData"/> (metres, Z up) as a binary glTF 2.0 file (metres, Y up as glTF requires) with
/// SharpGLTF. Part transforms are baked into the vertices, so the file has one node and one mesh with a primitive per
/// sub-mesh; materials keep their metallic-roughness factors. Texture keys are not exported (see docs/mvr.md).
/// </summary>
public static class GlbWriter
{
    /// <summary>
    /// Encodes a model as GLB bytes. Parts with inconsistent arrays (see <see cref="MeshData.Validate"/>) are skipped and
    /// reported through <paramref name="warnings"/>. Output is deterministic for equal input.
    /// </summary>
    public static byte[] Write(ModelData model, Action<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var root = ModelRoot.CreateModel();
        var scene = root.UseScene("Scene");
        var materials = new Material?[model.Materials.Count];
        Material? fallback = null;

        Material MaterialFor(int slot)
        {
            if (slot >= 0 && slot < materials.Length)
                return materials[slot] ??= CreateMaterial(root, model.Materials[slot], slot);
            return fallback ??= CreateMaterial(root, new MaterialData { Name = "Default" }, -1);
        }

        var mesh = root.CreateMesh(string.IsNullOrEmpty(model.Name) ? "Model" : model.Name);
        int written = 0;
        foreach (var part in model.Parts)
        {
            var m = part.Mesh;
            try
            {
                m.Validate();
            }
            catch (InvalidDataException e)
            {
                warnings?.Invoke($"Part '{part.Name}' skipped: {e.Message}");
                continue;
            }
            if (m.VertexCount == 0 || m.Indices.Length < 3) continue;

            var (positions, normals, flip) = Bake(m, part.Transform);
            var position = VertexAccessor(root, MemoryMarshal.AsBytes(positions.AsSpan()), positions.Length, DimensionType.VEC3);
            var normal = VertexAccessor(root, MemoryMarshal.AsBytes(normals.AsSpan()), normals.Length, DimensionType.VEC3);
            Accessor? uv = m.TexCoords is { Length: > 0 } tc ? VertexAccessor(root, MemoryMarshal.AsBytes(tc.AsSpan()), tc.Length, DimensionType.VEC2) : null;

            var ranges = m.SubMeshes.Count > 0 ? m.SubMeshes : new List<SubMesh> { new(0, m.Indices.Length, 0) };
            foreach (var sub in ranges)
            {
                int start = Math.Clamp(sub.IndexStart, 0, m.Indices.Length);
                int count = Math.Clamp(sub.IndexCount, 0, m.Indices.Length - start) / 3 * 3;
                if (count == 0) continue;
                var indices = new uint[count];
                m.Indices.AsSpan(start, count).CopyTo(indices);
                if (flip)
                    for (int i = 0; i + 2 < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);

                var prim = mesh.CreatePrimitive();
                prim.SetVertexAccessor("POSITION", position);
                prim.SetVertexAccessor("NORMAL", normal);
                if (uv is not null) prim.SetVertexAccessor("TEXCOORD_0", uv);
                prim.SetIndexAccessor(IndexAccessor(root, indices));
                prim.Material = MaterialFor(sub.MaterialSlot);
                written++;
            }
        }

        var node = scene.CreateNode(string.IsNullOrEmpty(model.Name) ? "Model" : model.Name);
        if (written > 0) node.Mesh = mesh;
        return root.WriteGLB().ToArray();
    }

    /// <summary>Transforms vertices by the part transform and converts Z-up → glTF Y-up: (x, y, z) → (x, z, −y).</summary>
    private static (Vector3[] Positions, Vector3[] Normals, bool Flip) Bake(MeshData m, Matrix4x4 transform)
    {
        bool identity = transform.IsIdentity;
        var normalMatrix = Matrix4x4.Identity;
        if (!identity && Matrix4x4.Invert(transform, out var inv)) normalMatrix = Matrix4x4.Transpose(inv);
        float det = identity ? 1f : MvrMatrix.Determinant3(transform);

        var positions = new Vector3[m.Positions.Length];
        var normals = new Vector3[m.Positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            var p = identity ? m.Positions[i] : Vector3.Transform(m.Positions[i], transform);
            positions[i] = new Vector3(p.X, p.Z, -p.Y);
            var n = identity ? m.Normals[i] : Vector3.TransformNormal(m.Normals[i], normalMatrix);
            float len = n.Length();
            n = len > 1e-12f && float.IsFinite(len) ? n / len : Vector3.UnitZ;
            normals[i] = new Vector3(n.X, n.Z, -n.Y);
        }
        return (positions, normals, det < 0);
    }

    private static Accessor VertexAccessor(ModelRoot root, ReadOnlySpan<byte> data, int count, DimensionType dimension)
    {
        var view = root.UseBufferView(data.ToArray(), 0, null, 0, BufferMode.ARRAY_BUFFER);
        var accessor = root.CreateAccessor();
        accessor.SetData(view, 0, count, new SharpGLTF.Memory.AttributeFormat(dimension, EncodingType.FLOAT, false));
        return accessor;
    }

    private static Accessor IndexAccessor(ModelRoot root, uint[] indices)
    {
        var bytes = MemoryMarshal.AsBytes(indices.AsSpan()).ToArray();
        var view = root.UseBufferView(bytes, 0, null, 0, BufferMode.ELEMENT_ARRAY_BUFFER);
        var accessor = root.CreateAccessor();
        accessor.SetIndexData(view, 0, indices.Length, IndexEncodingType.UNSIGNED_INT);
        return accessor;
    }

    private static Material CreateMaterial(ModelRoot root, MaterialData data, int slot)
    {
        var material = root.CreateMaterial(string.IsNullOrEmpty(data.Name) ? $"Material {slot}" : data.Name);
        material.InitializePBRMetallicRoughness();
        material.DoubleSided = data.DoubleSided;
        material.Alpha = data.AlphaMode switch
        {
            CoreAlphaMode.Mask => GltfAlphaMode.MASK,
            CoreAlphaMode.Blend => GltfAlphaMode.BLEND,
            _ => GltfAlphaMode.OPAQUE,
        };
        if (data.AlphaMode == CoreAlphaMode.Mask) material.AlphaCutoff = Clamp01(data.AlphaCutoff);
        if (material.FindChannel("BaseColor") is { } baseColor)
        {
            var c = data.BaseColorFactor;
            Set(baseColor, "RGBA", new Vector4(Clamp01(c.X), Clamp01(c.Y), Clamp01(c.Z), Clamp01(c.W)));
        }
        if (material.FindChannel("MetallicRoughness") is { } mr)
        {
            Set(mr, "MetallicFactor", Clamp01(data.Metallic));
            Set(mr, "RoughnessFactor", Clamp01(data.Roughness));
        }
        if (data.EmissiveLuminance > 0 && material.FindChannel("Emissive") is { } emissive)
        {
            var e = data.EmissiveColor;
            Set(emissive, "RGB", new Vector3(Clamp01(e.X), Clamp01(e.Y), Clamp01(e.Z)));
        }
        return material;
    }

    private static void Set(MaterialChannel channel, string name, object value)
    {
        foreach (var p in channel.Parameters)
        {
            if (p.Name != name) continue;
            p.Value = value;
            return;
        }
    }

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;
}
