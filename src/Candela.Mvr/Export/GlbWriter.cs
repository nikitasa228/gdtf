using System.Numerics;
using System.Runtime.InteropServices;
using Candela.Core.Geometry;
using SharpGLTF.Memory;
using SharpGLTF.Schema2;
using CoreAlphaMode = Candela.Core.Geometry.AlphaMode;
using GltfAlphaMode = SharpGLTF.Schema2.AlphaMode;

namespace Candela.Mvr;

/// <summary>
/// Writes a Core <see cref="ModelData"/> (metres, Z up) as a binary glTF 2.0 file (metres, Y up as glTF requires) with
/// SharpGLTF. Part transforms are baked into the vertices, so the file has one node and one mesh with a primitive per
/// sub-mesh. Materials keep their metallic-roughness factors and, when a texture source is given, their base colour,
/// metallic-roughness, normal, occlusion and emissive textures as images embedded in the GLB buffer (PNG or JPEG, the
/// formats core glTF holds), with a UV scale other than 1 as <c>KHR_texture_transform</c>. Emissive luminance (cd/m²),
/// transmission and index of refraction are written with <c>KHR_materials_emissive_strength</c>,
/// <c>KHR_materials_transmission</c> and <c>KHR_materials_ior</c> (optional extensions: other readers fall back to
/// the core material). Every primitive whose material binds a texture has <c>TEXCOORD_0</c> (zeros for a mesh without
/// texture coordinates, which Candela's renderer samples at (0, 0)), and accessors hold finite values only.
/// </summary>
public static class GlbWriter
{
    /// <summary>
    /// Encodes a model as GLB bytes without textures (material factors only). Parts with inconsistent arrays (see
    /// <see cref="MeshData.Validate"/>) are skipped and reported through <paramref name="warnings"/>. Output is
    /// deterministic for equal input.
    /// </summary>
    public static byte[] Write(ModelData model, Action<string>? warnings = null) => Write(model, null, warnings);

    /// <summary>
    /// Encodes a model as GLB bytes with its material textures. <paramref name="textures"/> returns the encoded image
    /// (PNG or JPEG bytes) of a texture key of the model's materials, or null when it is not available — the material is
    /// then written without that texture; it is called once per distinct key. Data that is not a PNG or JPEG image is
    /// left out and reported through <paramref name="warnings"/>, like parts with inconsistent arrays. Output is
    /// deterministic for equal input.
    /// </summary>
    public static byte[] Write(ModelData model, Func<string, byte[]?>? textures, Action<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var root = ModelRoot.CreateModel();
        var scene = root.UseScene("Scene");
        var materials = new Material?[model.Materials.Count];
        var images = new Dictionary<string, Image?>(StringComparer.Ordinal);
        Material? fallback = null;

        Image? ImageFor(string? key)
        {
            if (string.IsNullOrEmpty(key) || textures is null) return null;
            if (images.TryGetValue(key, out var known)) return known;
            Image? image = null;
            byte[]? data = textures(key);
            if (data is { Length: > 0 })
            {
                string? problem = null;
                if (IsPngOrJpeg(data))
                {
                    try
                    {
                        image = root.UseImage(new MemoryImage(data));
                        image.Name ??= ImageName(key, data);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                    {
                        problem = e.Message;
                    }
                }
                else
                {
                    problem = "it is not a PNG or JPEG image";
                }
                if (problem is not null) warnings?.Invoke($"Texture '{key}' cannot be embedded ({problem}); the material is written without it.");
            }
            images[key] = image;
            return image;
        }

        Material MaterialFor(int slot)
        {
            if (slot >= 0 && slot < materials.Length)
                return materials[slot] ??= CreateMaterial(root, model.Materials[slot], slot, ImageFor);
            return fallback ??= CreateMaterial(root, new MaterialData { Name = "Default" }, -1, ImageFor);
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
            Accessor? uv = m.TexCoords is { Length: > 0 } tc ? VertexAccessor(root, MemoryMarshal.AsBytes(FiniteTexCoords(tc).AsSpan()), tc.Length, DimensionType.VEC2) : null;

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
                var material = MaterialFor(sub.MaterialSlot);
                // a texture binding needs TEXCOORD_0 (glTF validator error MESH_PRIMITIVE_TOO_FEW_TEXCOORDS); a mesh without
                // texture coordinates samples the texture at (0, 0) in Candela's renderer, so zeros keep its look
                if (uv is null && HasTexture(material))
                {
                    var zeros = new Vector2[m.VertexCount];
                    uv = VertexAccessor(root, MemoryMarshal.AsBytes(zeros.AsSpan()), zeros.Length, DimensionType.VEC2);
                }
                if (uv is not null) prim.SetVertexAccessor("TEXCOORD_0", uv);
                prim.SetIndexAccessor(IndexAccessor(root, indices));
                prim.Material = material;
                written++;
            }
        }

        var node = scene.CreateNode(string.IsNullOrEmpty(model.Name) ? "Model" : model.Name);
        if (written > 0) node.Mesh = mesh;
        return root.WriteGLB().ToArray();
    }

    /// <summary>True for PNG and JPEG data (by signature), the image formats a binary glTF embeds without extensions.</summary>
    public static bool IsPngOrJpeg(ReadOnlySpan<byte> data) =>
        (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G'
         && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        || (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF);

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
            // glTF accessors must hold finite values; the renderer draws a non-finite vertex at the origin
            if (!(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))) p = Vector3.Zero;
            positions[i] = new Vector3(p.X, p.Z, -p.Y);
            var n = identity ? m.Normals[i] : Vector3.TransformNormal(m.Normals[i], normalMatrix);
            float len = n.Length();
            n = len > 1e-12f && float.IsFinite(len) ? n / len : Vector3.UnitZ;
            normals[i] = new Vector3(n.X, n.Z, -n.Y);
        }
        return (positions, normals, det < 0);
    }

    /// <summary>The texture coordinates with non-finite values replaced by 0 (glTF accessors must be finite); the array itself when all are finite.</summary>
    private static Vector2[] FiniteTexCoords(Vector2[] uv)
    {
        int bad = Array.FindIndex(uv, t => !(float.IsFinite(t.X) && float.IsFinite(t.Y)));
        if (bad < 0) return uv;
        var copy = (Vector2[])uv.Clone();
        for (int i = bad; i < copy.Length; i++)
            if (!(float.IsFinite(copy[i].X) && float.IsFinite(copy[i].Y))) copy[i] = Vector2.Zero;
        return copy;
    }

    /// <summary>True when any channel of <paramref name="material"/> binds a texture.</summary>
    private static bool HasTexture(Material material)
    {
        foreach (var channel in material.Channels)
            if (channel.Texture is not null) return true;
        return false;
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

    private static Material CreateMaterial(ModelRoot root, MaterialData data, int slot, Func<string?, Image?> imageFor)
    {
        var material = root.CreateMaterial(string.IsNullOrEmpty(data.Name) ? $"Material {slot}" : data.Name);
        float transmission = Clamp01(data.Transmission);
        // KHR_materials_transmission only when the surface transmits (glass), so opaque materials stay core glTF
        if (transmission > 0) material.InitializePBRMetallicRoughness("Transmission");
        else material.InitializePBRMetallicRoughness();
        if (transmission > 0 && material.FindChannel("Transmission") is { } transmissive) Set(transmissive, "TransmissionFactor", transmission);
        // KHR_materials_ior: glTF's default is 1.5, like Core's; other indices (water 1.33, …) are written
        if (float.IsFinite(data.Ior) && data.Ior >= 1f && MathF.Abs(data.Ior - DefaultIor) > 1e-6f) material.IndexOfRefraction = data.Ior;
        material.DoubleSided = data.DoubleSided;
        material.Alpha = data.AlphaMode switch
        {
            CoreAlphaMode.Mask => GltfAlphaMode.MASK,
            CoreAlphaMode.Blend => GltfAlphaMode.BLEND,
            _ => GltfAlphaMode.OPAQUE,
        };
        if (data.AlphaMode == CoreAlphaMode.Mask) material.AlphaCutoff = Clamp01(data.AlphaCutoff);
        var uvScale = data.UvScale;
        if (!(float.IsFinite(uvScale.X) && float.IsFinite(uvScale.Y)) || uvScale.X == 0 || uvScale.Y == 0) uvScale = Vector2.One;
        if (material.FindChannel("BaseColor") is { } baseColor)
        {
            var c = data.BaseColorFactor;
            Set(baseColor, "RGBA", new Vector4(Clamp01(c.X), Clamp01(c.Y), Clamp01(c.Z), Clamp01(c.W)));
            Texture(baseColor, imageFor(data.BaseColorTexture), uvScale);
        }
        if (material.FindChannel("MetallicRoughness") is { } mr)
        {
            Set(mr, "MetallicFactor", Clamp01(data.Metallic));
            Set(mr, "RoughnessFactor", Clamp01(data.Roughness));
            Texture(mr, imageFor(data.MetallicRoughnessTexture), uvScale);
        }
        if (data.NormalTexture is not null && material.FindChannel("Normal") is { } normal) Texture(normal, imageFor(data.NormalTexture), uvScale);
        if (data.OcclusionTexture is not null && material.FindChannel("Occlusion") is { } occlusion)
            Texture(occlusion, imageFor(data.OcclusionTexture), uvScale);
        if (data.EmissiveLuminance > 0 && float.IsFinite(data.EmissiveLuminance) && material.FindChannel("Emissive") is { } emissive)
        {
            // glTF's emissive colour is 0 … 1; the luminance goes into KHR_materials_emissive_strength relative to
            // EmissiveReferenceLuminance, so emitted colour × luminance survives (core readers see the colour only)
            var e = data.EmissiveColor;
            var rgb = new Vector3(float.IsFinite(e.X) ? MathF.Max(e.X, 0) : 0, float.IsFinite(e.Y) ? MathF.Max(e.Y, 0) : 0, float.IsFinite(e.Z) ? MathF.Max(e.Z, 0) : 0);
            float peak = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
            if (peak > 0 && float.IsFinite(peak))
            {
                // a colour above 1 is scaled into range and its peak moves into the strength
                float scale = MathF.Max(peak, 1f);
                Set(emissive, "RGB", rgb / scale);
                float strength = scale * data.EmissiveLuminance / EmissiveReferenceLuminance;
                if (float.IsFinite(strength) && MathF.Abs(strength - 1f) > 1e-6f) Set(emissive, "EmissiveStrength", strength);
                Texture(emissive, imageFor(data.EmissiveTexture), uvScale);
            }
        }
        return material;
    }

    /// <summary>
    /// Luminance in cd/m² of a glTF emissive colour of 1 at emissive strength 1: <see cref="MaterialData.EmissiveLuminance"/>
    /// is written as <c>KHR_materials_emissive_strength</c> relative to it. It is the reference Candela's glTF import
    /// gives emissive colours (<c>Candela.Assets.MaterialApproximation.EmissiveReferenceLuminance</c>), so an exported
    /// LED screen comes back with its luminance.
    /// </summary>
    public const float EmissiveReferenceLuminance = 100f;

    /// <summary>glTF's index of refraction when <c>KHR_materials_ior</c> is absent (and Core's default).</summary>
    private const float DefaultIor = 1.5f;

    /// <summary>Binds an image to a material channel (texture coordinate set 0, repeat), with a UV scale other than 1 as a texture transform.</summary>
    private static void Texture(MaterialChannel channel, Image? image, Vector2 uvScale)
    {
        if (image is null) return;
        channel.SetTexture(0, image, null, TextureWrapMode.REPEAT, TextureWrapMode.REPEAT, TextureMipMapFilter.DEFAULT, TextureInterpolationFilter.DEFAULT);
        if (uvScale != Vector2.One) channel.SetTransform(Vector2.Zero, uvScale, 0f, null);
    }

    /// <summary>A readable image name from a texture key: its last path segment, with the extension of the image's format.</summary>
    private static string ImageName(string key, ReadOnlySpan<byte> data)
    {
        string name = key;
        int query = name.IndexOf('?');
        if (query > 0) name = name[..query];
        int cut = name.LastIndexOfAny(['/', '\\', ':', '#']);
        if (cut >= 0 && cut < name.Length - 1) name = name[(cut + 1)..];
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem)) stem = "texture";
        return stem + (data[0] == 0x89 ? ".png" : ".jpg");
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
