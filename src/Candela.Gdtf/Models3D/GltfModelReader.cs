using System.Buffers.Binary;
using System.Numerics;
using Candela.Core.Geometry;
using SharpGLTF.Schema2;
using GltfAlphaMode = SharpGLTF.Schema2.AlphaMode;
using GltfPrimitiveType = SharpGLTF.Schema2.PrimitiveType;

namespace Candela.Gdtf;

/// <summary>
/// Reads glTF 2.0 models (<c>.glb</c>, or <c>.gltf</c> with resources in the same archive) through SharpGLTF into a
/// <see cref="ModelData"/>: positions, normals, first UV set, indices and PBR materials. Node transforms are baked
/// into the vertices (every part has an identity transform); coordinates stay in glTF's Y-up frame — the GDTF model
/// loader converts them to Z-up.
/// </summary>
/// <remarks>
/// Texture references become keys <c>gdtf:{fixture file}#{entry}</c> for images stored as files, or
/// <c>gdtf:{fixture file}#{model entry}?image={index}</c> for images embedded in the GLB buffer; see
/// <see cref="GdtfImageSource"/>. GLB files whose chunks are not 4-byte aligned (written by some exporters) are
/// re-packed before parsing.
/// </remarks>
public static class GltfModelReader
{
    /// <summary>
    /// Parses a GLB or glTF file. <paramref name="entryPath"/> is the archive entry (for texture keys and relative
    /// URIs), <paramref name="resolveFile"/> returns other archive entries by path (or null), and
    /// <paramref name="textureKeyPrefix"/> is <c>gdtf:{fixture file}#</c> (null: texture keys are left unset).
    /// </summary>
    /// <exception cref="InvalidDataException">The data cannot be parsed as glTF.</exception>
    public static ModelData Read(
        byte[] data, string entryPath, Func<string, byte[]?>? resolveFile = null, string? textureKeyPrefix = null,
        Action<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var root = Parse(data, entryPath, resolveFile);
        var model = new ModelData { Name = Path.GetFileNameWithoutExtension(entryPath) };

        var materialSlots = new Dictionary<int, int>();
        int defaultSlot = -1;
        int Slot(Material? m)
        {
            if (m is null)
            {
                if (defaultSlot < 0)
                {
                    defaultSlot = model.Materials.Count;
                    model.Materials.Add(ThreeDsReader.DefaultMaterial());
                }
                return defaultSlot;
            }
            if (!materialSlots.TryGetValue(m.LogicalIndex, out int slot))
            {
                slot = model.Materials.Count;
                materialSlots[m.LogicalIndex] = slot;
                model.Materials.Add(ConvertMaterial(m, entryPath, textureKeyPrefix, resolveFile));
            }
            return slot;
        }

        foreach (var node in EnumerateNodes(root))
        {
            if (node.Mesh is null) continue;
            var mesh = ReadMesh(node, Slot, warnings);
            if (mesh is not null) model.Parts.Add(new ModelPart { Name = node.Name ?? node.Mesh.Name ?? "", Mesh = mesh });
        }
        return model;
    }

    /// <summary>Parses the container, repairing unaligned GLB chunks.</summary>
    internal static ModelRoot Parse(byte[] data, string entryPath, Func<string, byte[]?>? resolveFile)
    {
        var settings = new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip };
        try
        {
            if (IsGlb(data))
            {
                try
                {
                    return ModelRoot.ParseGLB(new ArraySegment<byte>(data), settings);
                }
                catch (Exception e) when (e is SharpGLTF.Validation.SchemaException or SharpGLTF.Validation.DataException)
                {
                    var repaired = RepackGlb(data) ?? throw new InvalidDataException($"GLB '{entryPath}' is malformed: {e.Message}", e);
                    return ModelRoot.ParseGLB(new ArraySegment<byte>(repaired), settings);
                }
            }

            string folder = entryPath.Contains('/') ? entryPath[..(entryPath.LastIndexOf('/') + 1)] : "";
            var context = SharpGLTF.Schema2.ReadContext.Create(uri =>
            {
                var bytes = resolveFile?.Invoke(folder + Uri.UnescapeDataString(uri));
                return bytes is null ? default : new ArraySegment<byte>(bytes);
            });
            context.Validation = SharpGLTF.Validation.ValidationMode.Skip;
            using var ms = new MemoryStream(data, writable: false);
            return context.ReadTextSchema2(ms);
        }
        catch (Exception e) when (e is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException($"glTF '{entryPath}' cannot be read: {e.Message.Split('\n')[0]}", e);
        }
    }

    private static bool IsGlb(byte[] data) => data.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 0x46546C67;

    /// <summary>Rewrites a GLB with 4-byte aligned chunks (JSON padded with spaces, BIN with zeros); null if hopeless.</summary>
    internal static byte[]? RepackGlb(byte[] data)
    {
        if (!IsGlb(data)) return null;
        var chunks = new List<(uint Type, ArraySegment<byte> Body)>();
        int at = 12;
        while (at + 8 <= data.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            if (at + 8 + (long)len > data.Length) return null;
            chunks.Add((type, new ArraySegment<byte>(data, at + 8, (int)len)));
            at += 8 + (int)len;
            while (at < data.Length && at % 4 != 0 && data[at] is 0 or 0x20) at++; // tolerate partial padding
        }
        if (chunks.Count == 0) return null;
        using var ms = new MemoryStream(data.Length + 16);
        Span<byte> header = stackalloc byte[12];
        ms.Write(header); // placeholder
        Span<byte> ch = stackalloc byte[8];
        foreach (var (type, body) in chunks)
        {
            int pad = (4 - body.Count % 4) % 4;
            BinaryPrimitives.WriteUInt32LittleEndian(ch, (uint)(body.Count + pad));
            BinaryPrimitives.WriteUInt32LittleEndian(ch[4..], type);
            ms.Write(ch);
            ms.Write(body);
            byte padByte = type == 0x4E4F534A ? (byte)0x20 : (byte)0;
            for (int i = 0; i < pad; i++) ms.WriteByte(padByte);
        }
        var result = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)result.Length);
        return result;
    }

    private static IEnumerable<Node> EnumerateNodes(ModelRoot root)
    {
        var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault();
        IEnumerable<Node> roots = scene is not null
            ? scene.VisualChildren
            : root.LogicalNodes.Where(n => n.VisualParent is null);
        var stack = new Stack<Node>(roots.Reverse());
        var seen = new HashSet<int>();
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n.LogicalIndex)) continue;
            yield return n;
            foreach (var c in n.VisualChildren.Reverse()) stack.Push(c);
        }
    }

    private static MeshData? ReadMesh(Node node, Func<Material?, int> slotOf, Action<string>? warnings)
    {
        var world = node.WorldMatrix;
        if (!Matrix4x4.Invert(world, out var inverse)) inverse = Matrix4x4.Identity;
        var normalMatrix = Matrix4x4.Transpose(inverse);
        bool mirrored = world.GetDeterminant() < 0;

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMesh>();
        bool anyUv = false, missingNormals = false;

        foreach (var prim in node.Mesh.Primitives)
        {
            if (prim.DrawPrimitiveType is not (GltfPrimitiveType.TRIANGLES or GltfPrimitiveType.TRIANGLE_STRIP or GltfPrimitiveType.TRIANGLE_FAN))
                continue;
            var posAccessor = prim.GetVertexAccessor("POSITION");
            if (posAccessor is null) continue;
            IList<Vector3> pos;
            IList<Vector3>? nrm = null;
            IList<Vector2>? tex = null;
            try
            {
                pos = posAccessor.AsVector3Array();
                var na = prim.GetVertexAccessor("NORMAL");
                if (na is not null && na.Count == pos.Count) nrm = na.AsVector3Array();
                var ta = prim.GetVertexAccessor("TEXCOORD_0");
                if (ta is not null && ta.Count == pos.Count) tex = ta.AsVector2Array();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                warnings?.Invoke($"glTF primitive of node '{node.Name}' has unreadable vertex data ({e.Message}); skipped.");
                continue;
            }

            int baseVertex = positions.Count;
            for (int i = 0; i < pos.Count; i++)
            {
                var p = Vector3.Transform(pos[i], world);
                positions.Add(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) ? p : Vector3.Zero);
                if (nrm is not null)
                {
                    var n = Vector3.TransformNormal(nrm[i], normalMatrix);
                    normals.Add(n.LengthSquared() > 1e-20f && float.IsFinite(n.X) ? Vector3.Normalize(n) : Vector3.UnitZ);
                }
                else
                {
                    normals.Add(Vector3.Zero);
                }
                uvs.Add(tex is not null ? tex[i] : Vector2.Zero);
            }
            missingNormals |= nrm is null;
            anyUv |= tex is not null;

            int start = indices.Count;
            try
            {
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    if ((uint)a >= pos.Count || (uint)b >= pos.Count || (uint)c >= pos.Count) continue;
                    indices.Add((uint)(baseVertex + a));
                    if (mirrored)
                    {
                        indices.Add((uint)(baseVertex + c));
                        indices.Add((uint)(baseVertex + b));
                    }
                    else
                    {
                        indices.Add((uint)(baseVertex + b));
                        indices.Add((uint)(baseVertex + c));
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                warnings?.Invoke($"glTF primitive of node '{node.Name}' has unreadable indices ({e.Message}); skipped.");
                indices.RemoveRange(start, indices.Count - start);
            }
            if (indices.Count > start) subMeshes.Add(new SubMesh(start, indices.Count - start, slotOf(prim.Material)));
        }

        if (indices.Count == 0) return null;
        var mesh = new MeshData
        {
            Name = node.Name ?? "",
            Positions = positions.ToArray(),
            Normals = normals.ToArray(),
            TexCoords = anyUv ? uvs.ToArray() : null,
            Indices = indices.ToArray(),
            SubMeshes = subMeshes,
        };
        if (missingNormals) FillMissingNormals(mesh);
        return mesh;
    }

    /// <summary>Computes area-weighted normals for vertices that came without one, keeping the given ones.</summary>
    private static void FillMissingNormals(MeshData mesh)
    {
        var given = mesh.Normals;
        mesh.ComputeNormals();
        var computed = mesh.Normals;
        for (int i = 0; i < given.Length; i++)
            if (given[i] != Vector3.Zero) computed[i] = given[i];
    }

    private static MaterialData ConvertMaterial(Material m, string entryPath, string? keyPrefix, Func<string, byte[]?>? resolveFile)
    {
        var mat = new MaterialData { Name = m.Name ?? $"material{m.LogicalIndex}", DoubleSided = m.DoubleSided };
        mat.AlphaMode = m.Alpha switch
        {
            GltfAlphaMode.MASK => Candela.Core.Geometry.AlphaMode.Mask,
            GltfAlphaMode.BLEND => Candela.Core.Geometry.AlphaMode.Blend,
            _ => Candela.Core.Geometry.AlphaMode.Opaque,
        };
        mat.AlphaCutoff = m.AlphaCutoff;
        if (m.IndexOfRefraction > 0 && float.IsFinite(m.IndexOfRefraction)) mat.Ior = m.IndexOfRefraction;

        var baseColor = m.FindChannel("BaseColor") ?? m.FindChannel("Diffuse");
        if (baseColor is { } bc)
        {
            mat.BaseColorFactor = Sanitize(VectorParameter(bc, "RGBA") ?? Vector4.One, Vector4.One);
            mat.BaseColorTexture = TextureKey(bc, entryPath, keyPrefix, resolveFile);
            if (bc.TextureTransform is { } tt && float.IsFinite(tt.Scale.X) && float.IsFinite(tt.Scale.Y)) mat.UvScale = tt.Scale;
        }
        if (m.FindChannel("MetallicRoughness") is { } mr)
        {
            mat.Metallic = Clamp01(ParameterValue(mr, "MetallicFactor") ?? 1f);
            mat.Roughness = Clamp01(ParameterValue(mr, "RoughnessFactor") ?? 1f);
            mat.MetallicRoughnessTexture = TextureKey(mr, entryPath, keyPrefix, resolveFile);
        }
        else if (m.FindChannel("SpecularGlossiness") is { } sg)
        {
            mat.Metallic = 0f;
            mat.Roughness = Clamp01(1f - (ParameterValue(sg, "GlossinessFactor") ?? 0.4f));
        }
        if (m.FindChannel("Normal") is { } normal) mat.NormalTexture = TextureKey(normal, entryPath, keyPrefix, resolveFile);
        if (m.FindChannel("Occlusion") is { } occ) mat.OcclusionTexture = TextureKey(occ, entryPath, keyPrefix, resolveFile);
        if (m.FindChannel("Emissive") is { } em)
        {
            var c = Sanitize(VectorParameter(em, "RGB") ?? Vector4.Zero, Vector4.Zero);
            float strength = ParameterValue(em, "EmissiveStrength") ?? 1f;
            mat.EmissiveColor = new Vector3(c.X, c.Y, c.Z) * strength;
            mat.EmissiveTexture = TextureKey(em, entryPath, keyPrefix, resolveFile);
        }
        if (m.FindChannel("Transmission") is { } tr) mat.Transmission = Clamp01(ParameterValue(tr, "TransmissionFactor") ?? 0f);
        return mat;
    }

    private static Vector4? VectorParameter(MaterialChannel channel, string name)
    {
        foreach (var p in channel.Parameters)
        {
            if (p.Name != name) continue;
            return p.Value switch
            {
                Vector4 v4 => v4,
                Vector3 v3 => new Vector4(v3, 1f),
                _ => null,
            };
        }
        return null;
    }

    private static float? ParameterValue(MaterialChannel channel, string name)
    {
        foreach (var p in channel.Parameters)
            if (p.Name == name && p.Value is float f && float.IsFinite(f)) return f;
        return null;
    }

    private static string? TextureKey(MaterialChannel channel, string entryPath, string? keyPrefix, Func<string, byte[]?>? resolveFile)
    {
        var image = channel.Texture?.PrimaryImage;
        if (image is null || keyPrefix is null) return null;
        var source = image.Content.SourcePath;
        if (!string.IsNullOrEmpty(source) && resolveFile is not null)
        {
            string folder = entryPath.Contains('/') ? entryPath[..(entryPath.LastIndexOf('/') + 1)] : "";
            string candidate = folder + Path.GetFileName(source);
            if (resolveFile(candidate) is not null) return keyPrefix + candidate;
        }
        return $"{keyPrefix}{entryPath}?image={image.LogicalIndex}";
    }

    private static Vector4 Sanitize(Vector4 v, Vector4 fallback) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W) ? v : fallback;

    private static float Clamp01(float f) => float.IsFinite(f) ? System.Math.Clamp(f, 0f, 1f) : 0f;
}
