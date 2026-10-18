using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Candela.Core.Geometry;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using AlphaMode = Candela.Core.Geometry.AlphaMode;
using GltfAlphaMode = SharpGLTF.Schema2.AlphaMode;

namespace Candela.Assets;

/// <summary>
/// Reads glTF 2.0 (.gltf with external or data-URI buffers, .glb) with SharpGLTF into a <see cref="RawModel"/>.
/// PBR metallic-roughness materials map one-to-one (factors, textures, alpha, double-sided, emissive strength,
/// transmission, IOR, texture-transform scale); specular-glossiness is approximated. glTF is Y-up metres.
/// </summary>
internal static class GltfReader
{
    private static readonly byte[] s_placeholderPng = ImageImporter.EncodePng(
        new ImageData { Width = 1, Height = 1, Format = PixelFormat.Rgba8, Pixels = new byte[] { 255, 255, 255, 255 } });

    public static RawModel Read(ImportContext ctx)
    {
        var files = ctx.Files;
        var missingImages = new HashSet<string>(StringComparer.Ordinal);
        var context = ReadContext.Create(uri => Satellite(ctx, uri, missingImages));
        context.Validation = ValidationMode.TryFix;
        ModelRoot model;
        try
        {
            using var stream = new MemoryStream(files.MainData, writable: false);
            model = context.ReadSchema2(stream);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not AssetImportException)
        {
            string detail = e.GetBaseException().Message;
            if (detail.Contains("draco", StringComparison.OrdinalIgnoreCase) || detail.Contains("meshopt", StringComparison.OrdinalIgnoreCase))
                detail = "the file uses mesh compression (Draco/meshopt), which is not supported; export it without compression";
            throw new AssetImportException($"The glTF file could not be read: {detail}", e);
        }

        var raw = new RawModel
        {
            Name = Path.GetFileNameWithoutExtension(files.MainFile),
            UpAxis = SourceUpAxis.Y,
            UnitScale = 1f,
            UnitsKnown = true,
        };

        var uris = ReadImageUris(files.MainData);
        var imageKeys = new string?[model.LogicalImages.Count];
        for (int i = 0; i < imageKeys.Length; i++)
            imageKeys[i] = ImageKey(ctx, model.LogicalImages[i], i, i < uris.Length ? uris[i] : null, missingImages);

        var slots = new int[model.LogicalMaterials.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = raw.Materials.Count;
            raw.Materials.Add(ConvertMaterial(ctx, model.LogicalMaterials[i], i, imageKeys));
        }
        int defaultSlot = -1;

        var meshCache = new Dictionary<int, MeshData?>();
        var scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault();
        IEnumerable<Node> nodes = scene is not null
            ? Node.Flatten(scene)
            : model.LogicalNodes.Where(n => n.VisualParent is null).SelectMany(n => Node.Flatten(n));
        bool skinned = false;
        foreach (var node in nodes)
        {
            if (node.Mesh is null) continue;
            skinned |= node.Skin is not null;
            if (!meshCache.TryGetValue(node.Mesh.LogicalIndex, out var mesh))
            {
                mesh = ConvertMesh(ctx, node.Mesh, slots, ref defaultSlot, raw);
                meshCache[node.Mesh.LogicalIndex] = mesh;
            }
            if (mesh is null) continue;
            var world = node.WorldMatrix;
            if (!IsFinite(world)) world = Matrix4x4.Identity;
            raw.Parts.Add(new RawPart(string.IsNullOrEmpty(node.Name) ? (node.Mesh.Name ?? raw.Name) : node.Name, mesh, world));
        }
        if (skinned) ctx.Warn("Skinned meshes are imported in their bind pose.");
        if (raw.Parts.Count == 0) throw new AssetImportException("The glTF file contains no triangle geometry.");
        return raw;
    }

    private static ArraySegment<byte> Satellite(ImportContext ctx, string uri, HashSet<string> missingImages)
    {
        var path = Unescape(uri);
        if (ctx.Files.TryRead(path, out _, out var data)) return new ArraySegment<byte>(data);
        if (AssetFormats.IsImageFile(path) || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            // A missing texture must not fail the model; a 1×1 placeholder keeps SharpGLTF happy and is dropped later.
            missingImages.Add(uri);
            ctx.Warn($"Texture '{path}' was not found next to the model; the material is used without it.");
            return new ArraySegment<byte>(s_placeholderPng);
        }
        throw new AssetImportException($"The glTF file refers to '{path}', which is missing; import the .gltf together with its .bin and texture files.");
    }

    private static string Unescape(string uri)
    {
        try
        {
            return Uri.UnescapeDataString(uri);
        }
        catch (UriFormatException)
        {
            return uri;
        }
    }

    /// <summary>
    /// The <c>uri</c> of every entry of the glTF <c>images</c> array (null for buffer-view images and data URIs), read
    /// from the JSON or the JSON chunk of a GLB. SharpGLTF does not expose the URI of satellite images it read through
    /// a callback, and the URI is what maps an image to its companion file. Malformed JSON yields an empty array.
    /// </summary>
    internal static string?[] ReadImageUris(byte[] data)
    {
        ReadOnlySpan<byte> json = data;
        if (AssetFormats.IsGlb(data))
        {
            if (data.Length < 20) return Array.Empty<string?>();
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4));
            if (type != 0x4E4F534A || length > (uint)(data.Length - 20)) return Array.Empty<string?>();
            json = data.AsSpan(20, (int)length);
        }
        if (json.StartsWith("\uFEFF"u8)) json = json[3..];
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("images", out var images)
                || images.ValueKind != JsonValueKind.Array)
                return Array.Empty<string?>();
            var result = new string?[images.GetArrayLength()];
            int i = 0;
            foreach (var image in images.EnumerateArray())
            {
                if (image.ValueKind == JsonValueKind.Object && image.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String
                    && uri.GetString() is { Length: > 0 } text && !text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    result[i] = text;
                i++;
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<string?>();
        }
    }

    private static string? ImageKey(ImportContext ctx, Image image, int index, string? uri, HashSet<string> missing)
    {
        var content = image.Content;
        if (content.IsEmpty) return null;
        var source = uri ?? content.SourcePath;
        if (!string.IsNullOrEmpty(source) && !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (missing.Contains(source)) return null;
            if (ctx.Files.TryRead(Unescape(source), out var key, out var bytes))
            {
                if (ImageImporter.DetectFormat(bytes) is null)
                {
                    ctx.Warn($"Texture '{key}' is in a format Candela cannot read (WebP/KTX2/DDS); convert it to PNG or JPEG.");
                    return null;
                }
                return key;
            }
        }
        var data = content.Content.ToArray();
        if (ImageImporter.DetectFormat(data) is null)
        {
            ctx.Warn($"An embedded texture (image {index}) is in a format Candela cannot read (WebP/KTX2/DDS); it is ignored.");
            return null;
        }
        string name = string.IsNullOrWhiteSpace(image.Name) ? $"image{index}" : image.Name;
        string ext = content.IsJpg ? ".jpg" : content.IsPng ? ".png" : "." + (ImageImporter.DetectFormat(data) ?? "bin");
        if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) name += ext;
        return ctx.AddEmbedded(name, data);
    }

    private static MeshData? ConvertMesh(ImportContext ctx, Mesh mesh, int[] slots, ref int defaultSlot, RawModel raw)
    {
        var parts = new List<(MeshData Mesh, int Slot)>();
        foreach (var prim in mesh.Primitives)
        {
            if (prim.DrawPrimitiveType is not (PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN))
            {
                ctx.Warn("Point and line primitives were skipped.");
                continue;
            }
            MeshData? converted;
            try
            {
                converted = ConvertPrimitive(prim);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NotSupportedException)
            {
                ctx.Warn($"A primitive of mesh '{mesh.Name}' has invalid data and was skipped ({e.Message}).");
                continue;
            }
            if (converted is null) continue;
            int slot;
            if (prim.Material is not null && prim.Material.LogicalIndex < slots.Length) slot = slots[prim.Material.LogicalIndex];
            else
            {
                if (defaultSlot < 0)
                {
                    defaultSlot = raw.Materials.Count;
                    raw.Materials.Add(ModelImporter.CreateDefaultMaterial());
                }
                slot = defaultSlot;
            }
            parts.Add((converted, slot));
        }
        if (parts.Count == 0) return null;
        return MeshTools.Merge(mesh.Name ?? "", parts);
    }

    private static MeshData? ConvertPrimitive(MeshPrimitive prim)
    {
        var posAccessor = prim.GetVertexAccessor("POSITION");
        if (posAccessor is null || posAccessor.Count == 0) return null;
        var positions = Copy(posAccessor.AsVector3Array());
        int n = positions.Length;

        var tri = prim.GetTriangleIndices();
        var indices = new List<uint>(prim.IndexAccessor?.Count ?? n);
        foreach (var (a, b, c) in tri)
        {
            if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) continue;
            indices.Add((uint)a);
            indices.Add((uint)b);
            indices.Add((uint)c);
        }
        if (indices.Count == 0) return null;
        var mesh = new MeshData { Positions = positions, Indices = indices.ToArray() };

        int uvSet = prim.Material?.FindChannel("BaseColor")?.TextureCoordinate ?? 0;
        var uvAccessor = prim.GetVertexAccessor($"TEXCOORD_{uvSet}") ?? prim.GetVertexAccessor("TEXCOORD_0");
        if (uvAccessor is not null && uvAccessor.Count == n)
        {
            var uv = Copy(uvAccessor.AsVector2Array());
            for (int i = 0; i < uv.Length; i++)
                if (!float.IsFinite(uv[i].X) || !float.IsFinite(uv[i].Y)) uv[i] = Vector2.Zero;
            mesh.TexCoords = uv;
        }

        var nAccessor = prim.GetVertexAccessor("NORMAL");
        bool normalsOk = false;
        if (nAccessor is not null && nAccessor.Count == n)
        {
            var normals = Copy(nAccessor.AsVector3Array());
            normalsOk = true;
            for (int i = 0; i < normals.Length; i++)
            {
                if (!MeshTools.IsFinite(normals[i]) || normals[i].LengthSquared() < 1e-12f)
                {
                    normalsOk = false;
                    break;
                }
                normals[i] = Vector3.Normalize(normals[i]);
            }
            if (normalsOk) mesh.Normals = normals;
        }
        MeshTools.RemoveInvalidTriangles(mesh);
        if (mesh.Indices.Length == 0) return null;
        // glTF: "When normals are not specified, client implementations MUST calculate flat normals."
        if (!normalsOk) MeshTools.ComputeFlatNormals(mesh);
        return mesh;
    }

    private static T[] Copy<T>(IList<T> source)
    {
        var result = new T[source.Count];
        source.CopyTo(result, 0);
        return result;
    }

    private static MaterialData ConvertMaterial(ImportContext ctx, Material gm, int index, string?[] images)
    {
        var m = new MaterialData
        {
            Name = string.IsNullOrWhiteSpace(gm.Name) ? $"Material {index + 1}" : gm.Name,
            DoubleSided = gm.DoubleSided,
            AlphaMode = gm.Alpha switch { GltfAlphaMode.MASK => AlphaMode.Mask, GltfAlphaMode.BLEND => AlphaMode.Blend, _ => AlphaMode.Opaque },
            AlphaCutoff = gm.AlphaCutoff,
            Metallic = 1f,
            Roughness = 1f,
        };
        float ior = gm.IndexOfRefraction;
        if (float.IsFinite(ior) && ior >= 1f) m.Ior = ior;

        string? Tex(MaterialChannel ch)
        {
            var image = ch.Texture?.PrimaryImage ?? ch.Texture?.FallbackImage;
            if (image is null || image.LogicalIndex >= images.Length) return null;
            return images[image.LogicalIndex] is { } key ? ctx.Key(key) : null;
        }

        if (gm.FindChannel("BaseColor") is { } baseColor && !(gm.FindChannel("Diffuse") is { } && gm.FindChannel("SpecularGlossiness") is { }))
        {
            m.BaseColorFactor = Sanitize(baseColor.Color, Vector4.One);
            m.BaseColorTexture = Tex(baseColor);
            if (baseColor.TextureTransform is { } tt)
            {
                if (tt.Scale.X > 0 && tt.Scale.Y > 0 && float.IsFinite(tt.Scale.X) && float.IsFinite(tt.Scale.Y)) m.UvScale = tt.Scale;
                if (tt.Offset != Vector2.Zero || tt.Rotation != 0f)
                    ctx.Warn($"Material '{m.Name}': texture offset/rotation (KHR_texture_transform) is not supported; only the scale is used.");
            }
        }
        if (gm.FindChannel("MetallicRoughness") is { } mr)
        {
            m.Metallic = Clamp01(mr.GetFactor("MetallicFactor"), 1f);
            m.Roughness = Clamp01(mr.GetFactor("RoughnessFactor"), 1f);
            m.MetallicRoughnessTexture = Tex(mr);
        }
        if (gm.FindChannel("Diffuse") is { } diffuse && gm.FindChannel("SpecularGlossiness") is { } sg)
        {
            // KHR_materials_pbrSpecularGlossiness → metallic-roughness approximation.
            m.BaseColorFactor = Sanitize(diffuse.Color, Vector4.One);
            m.BaseColorTexture = Tex(diffuse);
            var specular = Vector3.One;
            float gloss = 1f;
            foreach (var parameter in sg.Parameters)
            {
                if (parameter.Name == "SpecularFactor" && parameter.Value is Vector3 sv) specular = sv;
                else if (parameter.Name == "GlossinessFactor" && parameter.Value is float gv) gloss = Clamp01(gv, 1f);
            }
            m.Roughness = System.Math.Max(MaterialApproximation.MinimumRoughness, 1f - gloss);
            float s = MathF.Max(specular.X, MathF.Max(specular.Y, specular.Z));
            m.Metallic = s > 0.5f ? 1f : 0f;
            if (m.Metallic > 0f && m.BaseColorTexture is null) m.BaseColorFactor = new Vector4(Vector3.Clamp(specular, Vector3.Zero, Vector3.One), m.BaseColorFactor.W);
            m.MetallicRoughnessTexture = null;
            if (Tex(sg) is not null) ctx.Warn($"Material '{m.Name}': specular-glossiness textures are approximated by constant values.");
        }
        if (gm.FindChannel("Normal") is { } normal) m.NormalTexture = Tex(normal);
        if (gm.FindChannel("Occlusion") is { } occlusion) m.OcclusionTexture = Tex(occlusion);
        if (gm.FindChannel("Emissive") is { } emissive)
        {
            var c = emissive.Color;
            var rgb = new Vector3(MathF.Max(0, c.X), MathF.Max(0, c.Y), MathF.Max(0, c.Z));
            float strength = emissive.Parameters.Any(p => p.Name == "EmissiveStrength") ? emissive.GetFactor("EmissiveStrength") : 1f;
            if (!float.IsFinite(strength) || strength < 0) strength = 1f;
            float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
            string? tex = Tex(emissive);
            if (float.IsFinite(max) && max > 1e-4f)
            {
                m.EmissiveColor = rgb / max;
                m.EmissiveLuminance = max * strength * MaterialApproximation.EmissiveReferenceLuminance;
                m.EmissiveTexture = tex;
            }
        }
        if (gm.FindChannel("Transmission") is { } transmission)
        {
            float t = Clamp01(transmission.GetFactor("TransmissionFactor"), 0f);
            m.Transmission = t;
        }
        if (gm.Unlit)
        {
            // Unlit (baked-lighting) assets are shown lit on a stage: a rough dielectric with the same colours.
            m.Metallic = 0f;
            m.Roughness = 1f;
            m.MetallicRoughnessTexture = null;
            ctx.Warn($"Material '{m.Name}' is unlit in the file; it is imported as a matte lit surface.");
        }
        return m;
    }

    private static Vector4 Sanitize(Vector4 v, Vector4 fallback) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W)
            ? Vector4.Clamp(v, Vector4.Zero, Vector4.One)
            : fallback;

    private static float Clamp01(float v, float fallback) => float.IsFinite(v) ? System.Math.Clamp(v, 0f, 1f) : fallback;

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M21) && float.IsFinite(m.M22)
        && float.IsFinite(m.M23) && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M41)
        && float.IsFinite(m.M42) && float.IsFinite(m.M43);
}
