using System.Numerics;
using Assimp;
using Assimp.Configs;
using Candela.Core.Geometry;
using AiMatrix = Assimp.Matrix4x4;
using AiMesh = Assimp.Mesh;
using AiScene = Assimp.Scene;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace Candela.Assets;

/// <summary>
/// Reads OBJ, FBX, COLLADA and 3DS into a <see cref="RawModel"/> (source space).
/// Assimp reads through <see cref="VirtualIOSystem"/>, so companions come from the <see cref="ModelFileSet"/>.
/// Root-node conversions that Assimp applies on its own (COLLADA unit/up axis, 3DS Z→Y rotation) are removed so that
/// axis and unit handling happens in one place (<see cref="ModelImporter"/>) from the file's metadata.
/// </summary>
internal static class AssimpReader
{
    private const PostProcessSteps Steps =
        PostProcessSteps.Triangulate
        | PostProcessSteps.JoinIdenticalVertices
        | PostProcessSteps.SortByPrimitiveType
        | PostProcessSteps.FindDegenerates
        | PostProcessSteps.FindInvalidData
        | PostProcessSteps.GenerateUVCoords
        | PostProcessSteps.TransformUVCoords
        | PostProcessSteps.FlipUVs;

    public static RawModel Read(ImportContext ctx)
    {
        var error = AssimpNative.EnsureLoaded();
        if (error is not null) throw new AssetImportException(error);

        var files = ctx.Files;
        string ext = Path.GetExtension(files.MainFile).ToLowerInvariant();
        string alias = "__candela_main__" + ext;
        var main = ModelPreflight.Prepare(ctx, out var companionFilter);
        AiScene? scene;
        using (var io = new VirtualIOSystem(files, alias, main, companionFilter))
        using (var assimp = new AssimpContext())
        {
            assimp.SetIOSystem(io);
            assimp.SetConfig(new SortByPrimitiveTypeConfig(PrimitiveType.Point | PrimitiveType.Line));
            assimp.SetConfig(new RemoveDegeneratePrimitivesConfig(true));
            assimp.SetConfig(new ColladaIgnoreUpDirectionConfig(ctx.Format == ModelFormat.Collada));
            assimp.SetConfig(new FBXPreservePivotsConfig(false));
            assimp.SetConfig(new FBXConvertToMetersConfig(false));
            assimp.SetConfig(new FBXImportCamerasConfig(false));
            assimp.SetConfig(new FBXImportLightsConfig(false));
            assimp.SetConfig(new FBXImportAnimationsConfig(false));
            assimp.SetConfig(new FBXImportEmbeddedTexturesConfig(true));
            try
            {
                scene = assimp.ImportFile(alias, Steps);
            }
            catch (AssimpException e)
            {
                throw new AssetImportException(Describe(ctx.Format, e.Message), e);
            }
            ModelPreflight.ReportWarnings(ctx);
        }
        if (scene is null) throw new AssetImportException(Describe(ctx.Format, "no scene was produced"));
        if (!scene.HasMeshes || scene.RootNode is null) throw new AssetImportException("The file contains no surface geometry (only points, lines or empty objects).");

        var raw = new RawModel { Name = Path.GetFileNameWithoutExtension(files.MainFile) };
        ApplyFormatConventions(ctx, scene, raw, out bool resetRoot);

        var materials = new int[scene.MaterialCount];
        for (int i = 0; i < scene.MaterialCount; i++)
        {
            materials[i] = raw.Materials.Count;
            raw.Materials.Add(ConvertMaterial(ctx, scene, scene.Materials[i], i));
        }
        int defaultSlot = -1;
        int SlotOf(int materialIndex)
        {
            if (materialIndex >= 0 && materialIndex < materials.Length) return materials[materialIndex];
            if (defaultSlot < 0)
            {
                defaultSlot = raw.Materials.Count;
                raw.Materials.Add(ModelImporter.CreateDefaultMaterial());
            }
            return defaultSlot;
        }

        var meshes = new MeshData?[scene.MeshCount];
        var meshSlots = new int[scene.MeshCount];
        int droppedMeshes = 0;
        for (int i = 0; i < scene.MeshCount; i++)
        {
            meshes[i] = ConvertMesh(scene.Meshes[i], ctx.Options.SmoothingAngleDegrees);
            if (meshes[i] is null) droppedMeshes++;
            meshSlots[i] = SlotOf(scene.Meshes[i].MaterialIndex);
        }
        if (droppedMeshes > 0) ctx.Warn($"{droppedMeshes} mesh(es) without triangles were skipped.");
        if (scene.Meshes.Any(m => m.HasBones)) ctx.Warn("Skinned meshes are imported in their bind pose.");
        if (scene.Meshes.Any(m => m.VertexColorChannelCount > 0) && scene.Meshes.All(m => !m.HasTextureCoords(0)))
            ctx.Warn("Vertex colours are not supported and were ignored.");

        var nodeMeshCache = new Dictionary<string, MeshData>(StringComparer.Ordinal);
        var stack = new Stack<(Node Node, Matrix4x4 Parent)>();
        var rootLocal = resetRoot ? Matrix4x4.Identity : ToNumerics(scene.RootNode.Transform);
        stack.Push((scene.RootNode, Matrix4x4.Identity));
        bool isRoot = true;
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            var local = isRoot ? rootLocal : ToNumerics(node.Transform);
            isRoot = false;
            var world = local * parent;
            if (node.HasMeshes)
            {
                var mesh = NodeMesh(node, meshes, meshSlots, nodeMeshCache);
                if (mesh is not null) raw.Parts.Add(new RawPart(string.IsNullOrEmpty(node.Name) ? raw.Name : node.Name, mesh, world));
            }
            if (node.HasChildren)
            {
                for (int c = node.ChildCount - 1; c >= 0; c--) stack.Push((node.Children[c], world));
            }
        }
        if (raw.Parts.Count == 0) throw new AssetImportException("The file contains no surface geometry (only points, lines or empty objects).");
        return raw;
    }

    private static string Describe(ModelFormat format, string detail)
    {
        string name = format switch
        {
            ModelFormat.Obj => "OBJ",
            ModelFormat.Fbx => "FBX",
            ModelFormat.Collada => "COLLADA (.dae)",
            ModelFormat.ThreeDs => "3DS",
            ModelFormat.Gltf or ModelFormat.Glb => "glTF",
            _ => "model",
        };
        detail = detail.Replace("__candela_main__", "", StringComparison.Ordinal).Trim();
        return $"The {name} file could not be read: {detail}";
    }

    /// <summary>Up axis, units and whether Assimp's own root conversion must be discarded, per format.</summary>
    private static void ApplyFormatConventions(ImportContext ctx, AiScene scene, RawModel raw, out bool resetRoot)
    {
        resetRoot = false;
        switch (ctx.Format)
        {
            case ModelFormat.Fbx:
                {
                    raw.UpAxis = SourceUpAxis.Y;
                    var meta = scene.Metadata;
                    if (meta is not null)
                    {
                        int I(string key, int fallback) =>
                            meta.TryGetValue(key, out var e) && e.Data is not null && TryInt(e.Data, out int v) ? v : fallback;
                        raw.Basis = SourceMetadata.FbxBasis(I("UpAxis", -1), I("UpAxisSign", 1), I("FrontAxis", -1),
                            I("FrontAxisSign", 1), I("CoordAxis", -1), I("CoordAxisSign", 1));
                        if (raw.Basis is { } b)
                            raw.UpAxis = I("UpAxis", 1) == 2 ? SourceUpAxis.Z : SourceUpAxis.Y;
                        if (meta.TryGetValue("UnitScaleFactor", out var u) && u.Data is not null && TryDouble(u.Data, out double cm)
                            && cm > 0 && double.IsFinite(cm))
                        {
                            raw.UnitScale = (float)(cm / 100.0);
                            raw.UnitsKnown = true;
                        }
                    }
                    if (!raw.UnitsKnown)
                    {
                        // FBX without a unit is centimetres by definition of the format.
                        raw.UnitScale = 0.01f;
                        raw.UnitsKnown = true;
                    }
                    break;
                }
            case ModelFormat.Collada:
                {
                    var (meter, up, basis) = SourceMetadata.ReadCollada(ctx.Files.MainData);
                    raw.UnitScale = meter;
                    raw.UnitsKnown = true;
                    raw.UpAxis = up;
                    raw.Basis = basis;
                    resetRoot = true;
                    break;
                }
            case ModelFormat.Obj:
                raw.UpAxis = SourceUpAxis.Y;
                resetRoot = true;
                break;
            case ModelFormat.ThreeDs:
                raw.UpAxis = SourceUpAxis.Z;
                resetRoot = true;
                break;
            default:
                // Not reached for the supported formats (glTF and PLY have their own readers); Assimp's Y-up convention.
                raw.UpAxis = SourceUpAxis.Y;
                break;
        }
    }

    private static bool TryInt(object o, out int v)
    {
        try
        {
            v = Convert.ToInt32(o, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            v = 0;
            return false;
        }
    }

    private static bool TryDouble(object o, out double v)
    {
        try
        {
            v = Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            v = 0;
            return false;
        }
    }

    /// <summary>Assimp matrices are column-vector (translation in A4/B4/C4); System.Numerics uses row vectors.</summary>
    private static Matrix4x4 ToNumerics(AiMatrix m) => new(
        m.A1, m.B1, m.C1, m.D1,
        m.A2, m.B2, m.C2, m.D2,
        m.A3, m.B3, m.C3, m.D3,
        m.A4, m.B4, m.C4, m.D4);

    private static MeshData? NodeMesh(Node node, MeshData?[] meshes, int[] slots, Dictionary<string, MeshData> cache)
    {
        var indices = node.MeshIndices.Where(i => i >= 0 && i < meshes.Length && meshes[i] is not null).ToList();
        if (indices.Count == 0) return null;
        var key = string.Join(',', indices);
        if (cache.TryGetValue(key, out var cached)) return cached;
        MeshData mesh;
        if (indices.Count == 1)
        {
            var only = meshes[indices[0]]!;
            mesh = only;
            if (only.SubMeshes.Count == 0) only.SubMeshes = new List<SubMesh> { new(0, only.Indices.Length, slots[indices[0]]) };
        }
        else
        {
            var sources = indices.Select(i => (Copy(meshes[i]!), slots[i])).ToList();
            mesh = MeshTools.Merge(node.Name ?? "", sources);
        }
        cache[key] = mesh;
        return mesh;
    }

    private static MeshData Copy(MeshData m) => new()
    {
        Name = m.Name,
        Positions = m.Positions,
        Normals = m.Normals,
        TexCoords = m.TexCoords,
        Indices = m.Indices,
    };

    /// <summary>
    /// Converts one Assimp mesh (triangles only). Normals come from the file when it has valid ones; otherwise they are
    /// generated with <paramref name="smoothingAngle"/> (Assimp's own generator cannot split vertices that a format such
    /// as STL or 3DS already shares between faces, which smears normals around hard edges).
    /// </summary>
    private static MeshData? ConvertMesh(AiMesh m, float smoothingAngle)
    {
        if (!m.HasVertices || !m.HasFaces || (m.PrimitiveType & PrimitiveType.Triangle) == 0) return null;
        int n = m.VertexCount;
        var pos = new Vector3[n];
        var src = m.Vertices;
        for (int i = 0; i < n; i++) pos[i] = new Vector3(src[i].X, src[i].Y, src[i].Z);

        var indices = new uint[m.FaceCount * 3];
        int w = 0;
        foreach (var face in m.Faces)
        {
            if (face.IndexCount != 3) continue;
            var f = face.Indices;
            int a = f[0], b = f[1], c = f[2];
            if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) continue;
            indices[w++] = (uint)a;
            indices[w++] = (uint)b;
            indices[w++] = (uint)c;
        }
        if (w == 0) return null;
        if (w != indices.Length) Array.Resize(ref indices, w);

        var mesh = new MeshData { Name = m.Name ?? "", Positions = pos, Indices = indices };
        if (m.HasNormals && m.Normals.Count == n)
        {
            var nrm = new Vector3[n];
            bool valid = true;
            for (int i = 0; i < n; i++)
            {
                var v = new Vector3(m.Normals[i].X, m.Normals[i].Y, m.Normals[i].Z);
                if (!MeshTools.IsFinite(v) || v.LengthSquared() < 1e-12f)
                {
                    valid = false;
                    break;
                }
                nrm[i] = v;
            }
            if (valid) mesh.Normals = nrm;
        }
        if (m.HasTextureCoords(0) && m.TextureCoordinateChannels[0].Count == n)
        {
            var uv = new Vector2[n];
            var t = m.TextureCoordinateChannels[0];
            for (int i = 0; i < n; i++)
            {
                var v = new Vector2(t[i].X, t[i].Y);
                uv[i] = float.IsFinite(v.X) && float.IsFinite(v.Y) ? v : Vector2.Zero;
            }
            mesh.TexCoords = uv;
        }
        MeshTools.RemoveInvalidTriangles(mesh);
        if (mesh.Indices.Length == 0) return null;
        if (mesh.Normals.Length != mesh.Positions.Length) MeshTools.GenerateNormals(mesh, smoothingAngle);
        return mesh;
    }

    /// <summary>Names Assimp gives the material it invents for geometry without one (OBJ, 3DS, FBX readers).</summary>
    private static bool IsAssimpDefaultMaterial(Material am) =>
        am.HasName && am.Name is "DefaultMaterial" or "%%%DEFAULT" or "$$$DEFAULT" && am.GetAllMaterialTextures().Length == 0;

    private static MaterialData ConvertMaterial(ImportContext ctx, AiScene scene, Material am, int index)
    {
        if (IsAssimpDefaultMaterial(am)) return ModelImporter.CreateDefaultMaterial();
        string name = am.HasName && !string.IsNullOrWhiteSpace(am.Name) ? am.Name : $"Material {index + 1}";
        Vector3 C(Color4D c) => new(c.R, c.G, c.B);
        var phong = new PhongMaterial
        {
            Name = name,
            Diffuse = am.HasColorDiffuse ? C(am.ColorDiffuse) : new Vector3(0.8f),
            Specular = am.HasColorSpecular ? C(am.ColorSpecular) : Vector3.Zero,
            SpecularStrength = am.HasShininessStrength ? am.ShininessStrength : 1f,
            Shininess = am.HasShininess ? am.Shininess : null,
            Opacity = am.HasOpacity ? am.Opacity : 1f,
            Emissive = am.HasColorEmissive ? C(am.ColorEmissive) : Vector3.Zero,
            TwoSided = am.HasTwoSided && am.IsTwoSided,
        };
        // Some exporters write opacity 0 for "not set"; a fully invisible material is never intended.
        if (phong.Opacity <= 0.001f) phong = phong with { Opacity = 1f };
        var m = MaterialApproximation.FromPhong(phong);

        string? Texture(TextureType type)
        {
            if (!am.GetMaterialTexture(type, 0, out var slot) || string.IsNullOrWhiteSpace(slot.FilePath)) return null;
            return ResolveTexture(ctx, scene, slot.FilePath);
        }

        var pbr = am.PBR;
        string? baseTex = (pbr.HasTextureBaseColor ? ResolveSlot(ctx, scene, pbr.TextureBaseColor) : null) ?? Texture(TextureType.Diffuse);
        if (baseTex is not null)
        {
            m.BaseColorTexture = ctx.Key(baseTex);
            // A texture multiplies the factor; files usually leave Kd at a grey default next to a texture.
            if (m.Metallic == 0f) m.BaseColorFactor = new Vector4(1f, 1f, 1f, m.BaseColorFactor.W);
        }

        string? normalTex = (pbr.HasTextureNormalCamera ? ResolveSlot(ctx, scene, pbr.TextureNormalCamera) : null) ?? Texture(TextureType.Normals);
        if (normalTex is not null) m.NormalTexture = ctx.Key(normalTex);
        else if (Texture(TextureType.Height) is { } bump)
        {
            // OBJ "bump"/"map_bump" is often a normal map in practice; decide by looking at the pixels.
            if (ctx.TryDecode(bump, out var bumpImage))
            {
                if (TextureOps.LooksLikeNormalMap(bumpImage)) m.NormalTexture = ctx.Key(bump);
                else
                {
                    float scale = am.HasBumpScaling && am.BumpScaling > 0 && float.IsFinite(am.BumpScaling) ? am.BumpScaling : 1f;
                    var normal = TextureOps.NormalFromHeight(bumpImage, 4f * scale);
                    m.NormalTexture = ctx.Key(ctx.AddGenerated(Path.GetFileNameWithoutExtension(bump) + "_normal.png", normal));
                }
            }
        }

        string? metalTex = pbr.HasTextureMetalness ? ResolveSlot(ctx, scene, pbr.TextureMetalness) : null;
        string? roughTex = pbr.HasTextureRoughness ? ResolveSlot(ctx, scene, pbr.TextureRoughness) : null;
        if (metalTex is not null || roughTex is not null)
        {
            ImageData? metalImage = null, roughImage = null;
            bool okMetal = metalTex is null || ctx.TryDecode(metalTex, out metalImage);
            bool okRough = roughTex is null || ctx.TryDecode(roughTex, out roughImage);
            if (metalTex is not null && roughTex is not null && metalTex == roughTex && okMetal)
            {
                // Already packed (glTF exported through FBX): use as-is.
                m.MetallicRoughnessTexture = ctx.Key(metalTex);
                m.Metallic = 1f;
                m.Roughness = 1f;
            }
            else if (okMetal && okRough && (metalImage is not null || roughImage is not null))
            {
                var packed = TextureOps.PackMetallicRoughness(metalImage, m.Metallic, roughImage, m.Roughness);
                m.MetallicRoughnessTexture = ctx.Key(ctx.AddGenerated($"material{index}_metallicRoughness.png", packed));
                m.Metallic = 1f;
                m.Roughness = 1f;
            }
        }

        if (Texture(TextureType.Opacity) is { } opacityTex)
        {
            if (baseTex is not null && string.Equals(opacityTex, baseTex, StringComparison.Ordinal))
            {
                // Cut-out using the albedo's own alpha (foliage, fences, gobo-like cut-outs).
                m.AlphaMode = AlphaMode.Mask;
            }
            else if (ctx.TryDecode(opacityTex, out var opacityImage))
            {
                ImageData? albedo = null;
                if (baseTex is null || ctx.TryDecode(baseTex, out albedo))
                {
                    var merged = TextureOps.MergeOpacity(albedo, opacityImage);
                    m.BaseColorTexture = ctx.Key(ctx.AddGenerated($"material{index}_albedo.png", merged));
                    m.AlphaMode = AlphaMode.Mask;
                    m.Transmission = 0f;
                }
            }
        }

        string? emissiveTex = (pbr.HasTextureEmissionColor ? ResolveSlot(ctx, scene, pbr.TextureEmissionColor) : null) ?? Texture(TextureType.Emissive);
        if (emissiveTex is not null)
        {
            m.EmissiveTexture = ctx.Key(emissiveTex);
            if (m.EmissiveLuminance <= 0f && !am.HasColorEmissive)
            {
                m.EmissiveColor = Vector3.One;
                m.EmissiveLuminance = MaterialApproximation.EmissiveReferenceLuminance;
            }
        }

        if (Texture(TextureType.AmbientOcclusion) is { } ao) m.OcclusionTexture = ctx.Key(ao);
        return m;
    }

    private static string? ResolveSlot(ImportContext ctx, AiScene scene, TextureSlot slot) =>
        string.IsNullOrWhiteSpace(slot.FilePath) ? null : ResolveTexture(ctx, scene, slot.FilePath);

    /// <summary>Relative key of a texture: embedded ("*N" or a file name stored in the model) or an external file.</summary>
    private static string? ResolveTexture(ImportContext ctx, AiScene scene, string path)
    {
        EmbeddedTexture? embedded = null;
        int embeddedIndex = -1;
        if (path.StartsWith('*') && int.TryParse(path.AsSpan(1), out int idx) && idx >= 0 && idx < scene.TextureCount)
        {
            embedded = scene.Textures[idx];
            embeddedIndex = idx;
        }
        else if (scene.HasTextures)
        {
            var fileName = Path.GetFileName(path.Replace('\\', '/'));
            for (int i = 0; i < scene.TextureCount; i++)
            {
                var t = scene.Textures[i];
                if (!string.IsNullOrEmpty(t.Filename) && string.Equals(Path.GetFileName(t.Filename.Replace('\\', '/')), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    embedded = t;
                    embeddedIndex = i;
                    break;
                }
            }
        }
        if (embedded is null) return ctx.ResolveExternalImage(path);

        string cacheName = $"texture{embeddedIndex}";
        foreach (var (k, _) in ctx.Embedded)
            if (k.StartsWith("embedded/" + cacheName + ".", StringComparison.Ordinal)) return k;
        foreach (var (k, _) in ctx.Generated)
            if (k.StartsWith("generated/" + cacheName + ".", StringComparison.Ordinal)) return k;

        if (embedded.HasCompressedData)
        {
            var data = embedded.CompressedData;
            var format = ImageImporter.DetectFormat(data);
            var ext = format switch { "jpeg" => "jpg", null => (embedded.CompressedFormatHint ?? "bin").Trim('.').ToLowerInvariant(), _ => format };
            return ctx.AddEmbedded($"{cacheName}.{ext}", data);
        }
        if (embedded.HasNonCompressedData && embedded.Width > 0 && embedded.Height > 0)
        {
            var texels = embedded.NonCompressedData;
            if (texels.Length < embedded.Width * embedded.Height) return null;
            var pixels = new byte[embedded.Width * embedded.Height * 4];
            for (int i = 0; i < embedded.Width * embedded.Height; i++)
            {
                pixels[i * 4] = texels[i].R;
                pixels[i * 4 + 1] = texels[i].G;
                pixels[i * 4 + 2] = texels[i].B;
                pixels[i * 4 + 3] = texels[i].A;
            }
            var image = new ImageData { Width = embedded.Width, Height = embedded.Height, Format = PixelFormat.Rgba8, Pixels = pixels };
            return ctx.AddGenerated($"{cacheName}.png", image);
        }
        return null;
    }
}

/// <summary>
/// Assimp file system over a <see cref="ModelFileSet"/> (read-only). The main file is served from
/// <paramref name="mainData"/> (possibly sanitised); companions pass through <paramref name="companionFilter"/>
/// (e.g. MTL sanitising) before Assimp sees them. The file set itself keeps the original bytes.
/// </summary>
internal sealed class VirtualIOSystem(ModelFileSet files, string mainAlias, byte[] mainData, Func<string, byte[], byte[]>? companionFilter)
    : IOSystem
{
    public override IOStream OpenFile(string pathToFile, FileIOMode fileMode)
    {
        if (fileMode is not (FileIOMode.Read or FileIOMode.ReadBinary or FileIOMode.ReadText)) return null!;
        var normalized = (pathToFile ?? "").Replace('\\', '/');
        if (normalized.EndsWith(mainAlias, StringComparison.OrdinalIgnoreCase)) return new MemoryIOStream(pathToFile!, fileMode, mainData);
        if (!files.TryRead(normalized, out var key, out var data)) return null!;
        return new MemoryIOStream(pathToFile!, fileMode, companionFilter is null ? data : companionFilter(key, data));
    }
}

/// <summary>Read-only Assimp stream over a byte array.</summary>
internal sealed class MemoryIOStream : IOStream
{
    private readonly byte[] _data;
    private long _position;

    public MemoryIOStream(string path, FileIOMode mode, byte[] data) : base(path, mode)
    {
        _data = data;
    }

    public override bool IsValid => true;

    public override long Read(byte[] dataRead, long count)
    {
        long available = System.Math.Max(0, _data.LongLength - _position);
        long n = System.Math.Min(System.Math.Min(count, available), dataRead.LongLength);
        if (n <= 0) return 0;
        Array.Copy(_data, _position, dataRead, 0, n);
        _position += n;
        return n;
    }

    public override long Write(byte[] dataToWrite, long count) => 0;

    public override ReturnCode Seek(long offset, Origin seekOrigin)
    {
        long target = seekOrigin switch
        {
            Origin.Set => offset,
            Origin.Current => _position + offset,
            Origin.End => _data.LongLength + offset,
            _ => -1,
        };
        if (target < 0 || target > _data.LongLength) return ReturnCode.Failure;
        _position = target;
        return ReturnCode.Success;
    }

    public override long GetPosition() => _position;

    public override long GetFileSize() => _data.LongLength;

    public override void Flush()
    {
    }
}
