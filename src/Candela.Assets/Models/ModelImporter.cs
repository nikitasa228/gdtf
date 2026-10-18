using System.Diagnostics;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;

namespace Candela.Assets;

/// <summary>
/// Imports 3D models into Core <see cref="ModelData"/> in Candela space: right-handed, Z up, metres.
/// <list type="bullet">
/// <item>glTF / GLB through SharpGLTF (PBR materials map directly); PLY and STL through Candela's own readers; OBJ(+MTL),
/// FBX, COLLADA and 3DS through Assimp after a managed check/clean-up (<see cref="ModelPreflight"/>) that keeps damaged
/// files from crashing the native library (classic materials approximated, see <see cref="MaterialApproximation"/>).</item>
/// <item>Axes: glTF/OBJ/FBX(Y-up) are rotated so +Y becomes +Z and the front (+Z) faces the audience (−Y); FBX axis
/// metadata, COLLADA &lt;up_axis&gt; are honoured; STL/PLY/3DS are taken as Z-up. Units: FBX UnitScaleFactor,
/// COLLADA &lt;unit meter&gt;; unit-less formats are metres unless they are implausibly large (then millimetres, with a
/// warning). <see cref="ImportOptions"/> overrides both.</item>
/// <item>Node transforms become part transforms in Candela space; mirroring transforms are baked into a mirrored copy of
/// the mesh so every part transform is a proper rotation (renderers may cull by winding).</item>
/// <item>Normals are generated when missing (smooth up to the smoothing angle; flat for glTF as its specification
/// requires) and tangents are always computed from the final UVs.</item>
/// <item>Textures are referenced by keys (<see cref="ImportOptions.TextureKeyPrefix"/> + relative path) that resolve to
/// the files, embedded images or generated images listed in the <see cref="ImportedModel"/>.</item>
/// </list>
/// SketchUp files are rejected with <see cref="AssetFormats.SketchUpMessage"/>. Failures throw
/// <see cref="AssetImportException"/> with a user-facing message; recoverable problems become warnings. Thread-safe.
/// </summary>
public static class ModelImporter
{
    /// <summary>Mirror across the YZ plane (used to keep part transforms free of reflections).</summary>
    private static readonly Matrix4x4 s_mirrorX = Matrix4x4.CreateScale(-1f, 1f, 1f);

    private static readonly Dictionary<string, string> s_foreignFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".blend"] = "Blender files cannot be read directly; export from Blender as .glb or .fbx",
        [".max"] = "3ds Max scenes cannot be read directly; export from 3ds Max as .fbx",
        [".c4d"] = "Cinema 4D files cannot be read directly; export from Cinema 4D as .fbx or .glb",
        [".ma"] = "Maya scenes cannot be read directly; export from Maya as .fbx",
        [".mb"] = "Maya scenes cannot be read directly; export from Maya as .fbx",
        [".3dm"] = "Rhino files cannot be read directly; export from Rhino as .obj or .fbx",
        [".dwg"] = "DWG drawings cannot be read; export the 3D model as .dae, .fbx or .obj",
        [".dxf"] = "DXF drawings cannot be read; export the 3D model as .dae, .fbx or .obj",
        [".usd"] = "USD files are not supported; export as .glb",
        [".usdz"] = "USDZ files are not supported; export as .glb",
        [".usda"] = "USD files are not supported; export as .glb",
        [".usdc"] = "USD files are not supported; export as .glb",
        [".step"] = "CAD (STEP) files cannot be read; export a mesh as .stl, .obj or .glb",
        [".stp"] = "CAD (STEP) files cannot be read; export a mesh as .stl, .obj or .glb",
        [".igs"] = "CAD (IGES) files cannot be read; export a mesh as .stl, .obj or .glb",
        [".iges"] = "CAD (IGES) files cannot be read; export a mesh as .stl, .obj or .glb",
        [".3mf"] = "3MF files are not supported; export the model as .stl, .obj or .glb",
        [".x"] = "DirectX .x files are not supported; export the model as .glb, .fbx or .obj",
        [".x3d"] = "X3D files are not supported; export the model as .glb, .dae or .obj",
        [".zae"] = "Zipped COLLADA (.zae) is not supported; extract the .dae file and import that",
        [".ase"] = "ASE files are not supported; export the model as .fbx, .dae or .obj",
        [".lwo"] = "LightWave objects are not supported; export the model as .fbx, .glb or .obj",
        [".lws"] = "LightWave scenes are not supported; export the model as .fbx, .glb or .obj",
        [".off"] = "OFF files are not supported; convert the model to .ply, .obj or .stl",
        [".ac"] = "AC3D files are not supported; export the model as .obj or .dae",
        [".ms3d"] = "MilkShape files are not supported; export the model as .obj or .fbx",
    };

    /// <summary>True when <paramref name="fileName"/> has an importable model extension.</summary>
    public static bool IsSupported(string fileName) => AssetFormats.IsModelFile(fileName);

    /// <summary>Imports a model file from disk, reading companions (MTL, .bin, textures) from around it.</summary>
    public static ImportedModel Import(string path, ImportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        CheckExtension(path);
        return Import(ModelFileSet.FromFile(path), options);
    }

    /// <summary>
    /// Imports a model held in memory (e.g. from a project archive). <paramref name="companions"/> are the other files of
    /// the model keyed by relative path.
    /// </summary>
    public static ImportedModel Import(string fileName, byte[] data, IReadOnlyDictionary<string, byte[]>? companions = null, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        CheckExtension(fileName);
        return Import(ModelFileSet.FromMemory(fileName, data, companions), options);
    }

    /// <summary>Imports the main file of a file set.</summary>
    public static ImportedModel Import(ModelFileSet files, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        options ??= ImportOptions.Default;
        var data = files.MainData;
        if (AssetFormats.IsSketchUp(files.MainFile, data)) throw new AssetImportException(AssetFormats.SketchUpMessage);
        var format = AssetFormats.GetModelFormat(files.MainFile);
        if ((format is ModelFormat.Unknown or ModelFormat.Gltf) && AssetFormats.IsGlb(data)) format = ModelFormat.Glb;
        if (format == ModelFormat.Unknown) CheckExtension(files.MainFile);
        if (data.Length == 0) throw new AssetImportException($"'{files.MainFile}' is empty.");

        var ctx = new ImportContext(files, options, format);
        // glTF is never handed to Assimp: its glTF reader crashes the process on damaged files that SharpGLTF rejects
        // cleanly. PLY and STL are read by Candela's own readers for the same reason (Assimp hangs or crashes on
        // truncated files); OBJ and COLLADA are checked/cleaned before Assimp sees them (ModelPreflight).
        RawModel raw = format switch
        {
            ModelFormat.Gltf or ModelFormat.Glb => GltfReader.Read(ctx),
            ModelFormat.Ply => PlyReader.Read(ctx),
            ModelFormat.Stl => StlReader.Read(ctx),
            _ => AssimpReader.Read(ctx),
        };

        return Finish(raw, ctx);
    }

    /// <summary>Neutral material used for geometry without a material (light grey, matte).</summary>
    public static MaterialData CreateDefaultMaterial() => new()
    {
        Name = "Default",
        BaseColorFactor = new Vector4(0.8f, 0.8f, 0.8f, 1f),
        Metallic = 0f,
        Roughness = 0.6f,
    };

    internal static void CheckExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        if (string.Equals(ext, ".skp", StringComparison.OrdinalIgnoreCase)) throw new AssetImportException(AssetFormats.SketchUpMessage);
        if (s_foreignFormats.TryGetValue(ext, out var advice)) throw new AssetImportException(advice + ".");
        if (!AssetFormats.IsModelFile(ext))
        {
            throw new AssetImportException(
                $"'{Path.GetFileName(fileName)}' is not a supported 3D model. Supported: {string.Join(", ", AssetFormats.ModelExtensions)}.");
        }
    }

    private static ImportedModel Finish(RawModel raw, ImportContext ctx)
    {
        var options = ctx.Options;
        Matrix4x4 basis = options.UpAxis switch
        {
            SourceUpAxis.Y => SourceMetadata.YUpToZUp,
            SourceUpAxis.Z => Matrix4x4.Identity,
            _ => raw.Basis ?? (raw.UpAxis == SourceUpAxis.Y ? SourceMetadata.YUpToZUp : Matrix4x4.Identity),
        };
        var up = options.UpAxis != SourceUpAxis.Auto ? options.UpAxis : raw.UpAxis;

        float unit;
        bool known;
        if (options.UnitScale is { } u && float.IsFinite(u) && u > 0)
        {
            unit = u;
            known = true;
        }
        else
        {
            unit = float.IsFinite(raw.UnitScale) && raw.UnitScale > 0 ? raw.UnitScale : 1f;
            known = raw.UnitsKnown;
            if (!known && options.GuessUnits)
            {
                var size = SourceBounds(raw).Size;
                float extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * unit;
                if (extent > ImportOptions.GuessMillimetresAbove)
                {
                    unit = 0.001f;
                    ctx.Warn($"The file has no units and would be {extent:0} m large; it was read as millimetres. Set the unit in the import options if this is wrong.");
                }
            }
        }

        var conversion = basis * Matrix4x4.CreateScale(unit);
        if (!Matrix4x4.Invert(conversion, out var inverse)) throw new AssetImportException("The model's axis conversion is degenerate.");
        var transformed = new HashSet<MeshData>(ReferenceEqualityComparer.Instance);
        var mirrored = new Dictionary<MeshData, MeshData>(ReferenceEqualityComparer.Instance);
        var parts = new List<ModelPart>(raw.Parts.Count);
        foreach (var rp in raw.Parts)
        {
            if (transformed.Add(rp.Mesh)) MeshTools.Transform(rp.Mesh, conversion);
            var t = rp.World.IsIdentity ? Matrix4x4.Identity : Snap(inverse * rp.World * conversion);
            var mesh = rp.Mesh;
            if (MeshTools.Determinant3(t) < 0f)
            {
                // A mirroring node (negative scale, common for symmetric parts) would turn the instance inside out for a
                // renderer that culls by winding. Bake the mirror into a copy of the mesh (winding flipped back) and keep a
                // proper rotation in the transform: mesh·S·(S·T) = mesh·T with S = diag(−1, 1, 1).
                if (!mirrored.TryGetValue(rp.Mesh, out var copy))
                {
                    copy = MeshTools.Clone(rp.Mesh);
                    MeshTools.Transform(copy, s_mirrorX);
                    mirrored[rp.Mesh] = copy;
                    transformed.Add(copy);
                }
                mesh = copy;
                t = s_mirrorX * t;
            }
            parts.Add(new ModelPart { Name = rp.Name, Mesh = mesh, Transform = t });
        }
        if (mirrored.Count > 0)
        {
            // Meshes that are only used mirrored are no longer referenced; do not process them further.
            var used = new HashSet<MeshData>(parts.Select(p => p.Mesh), ReferenceEqualityComparer.Instance);
            transformed.RemoveWhere(m => !used.Contains(m));
        }

        if (parts.Count > System.Math.Max(1, options.MaxParts))
        {
            ctx.Warn($"The model has {parts.Count} objects; they were merged into one to keep it fast to draw.");
            parts = new List<ModelPart> { Flatten(raw.Name, parts, options.SmoothingAngleDegrees) };
            transformed.Clear();
            transformed.Add(parts[0].Mesh);
        }

        foreach (var mesh in transformed)
        {
            if (mesh.Normals.Length != mesh.Positions.Length) MeshTools.GenerateNormals(mesh, options.SmoothingAngleDegrees);
            MeshTools.GenerateTangents(mesh);
        }
        var valid = new List<ModelPart>(parts.Count);
        foreach (var p in parts)
        {
            try
            {
                p.Mesh.Validate();
                valid.Add(p);
            }
            catch (InvalidDataException e)
            {
                ctx.Warn($"Part '{p.Name}' was skipped: {e.Message}");
            }
        }
        if (valid.Count == 0) throw new AssetImportException("The model contains no valid geometry.");

        var model = new ModelData { Name = raw.Name, Parts = valid, Materials = CompactMaterials(valid, raw.Materials) };
        var missing = ctx.Files.MissingFiles
            .Where(m => !m.Contains("__candela_main__", StringComparison.Ordinal) && Path.HasExtension(m))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new ImportedModel
        {
            Model = model,
            Format = ctx.Format,
            MainFile = ctx.Files.MainFile,
            Files = ctx.Files.Files,
            EmbeddedImages = ctx.Embedded,
            GeneratedImages = ctx.Generated,
            MissingFiles = missing,
            Warnings = ctx.Warnings,
            SourceUpAxis = up,
            UnitScale = unit,
            UnitsKnown = known,
        };
    }

    /// <summary>
    /// Drops materials that no part uses (Assimp adds a default material to every OBJ; files often carry unused library
    /// materials) and renumbers the sub-mesh slots, keeping the file's material order so slot indices are stable across
    /// re-imports. A mesh without sub-meshes uses slot 0; slots without a material get the default material.
    /// </summary>
    private static List<MaterialData> CompactMaterials(List<ModelPart> parts, List<MaterialData> materials)
    {
        var meshes = new List<MeshData>(parts.Count);
        var seen = new HashSet<MeshData>(ReferenceEqualityComparer.Instance);
        foreach (var p in parts)
            if (seen.Add(p.Mesh)) meshes.Add(p.Mesh);
        var used = new SortedSet<int>();
        foreach (var m in meshes)
        {
            if (m.SubMeshes.Count == 0) used.Add(0);
            else foreach (var sm in m.SubMeshes) used.Add(System.Math.Max(sm.MaterialSlot, 0));
        }
        var remap = new Dictionary<int, int>(used.Count);
        var result = new List<MaterialData>(used.Count);
        foreach (int slot in used)
        {
            remap[slot] = result.Count;
            result.Add(slot < materials.Count ? materials[slot] : CreateDefaultMaterial());
        }
        foreach (var m in meshes)
        {
            if (m.SubMeshes.Count == 0)
            {
                if (remap[0] != 0) m.SubMeshes = new List<SubMesh> { new(0, m.Indices.Length, remap[0]) };
                continue;
            }
            for (int i = 0; i < m.SubMeshes.Count; i++)
                m.SubMeshes[i] = m.SubMeshes[i] with { MaterialSlot = remap[System.Math.Max(m.SubMeshes[i].MaterialSlot, 0)] };
        }
        return result;
    }

    private static Aabb SourceBounds(RawModel raw)
    {
        var meshBounds = new Dictionary<MeshData, Aabb>(ReferenceEqualityComparer.Instance);
        var total = Aabb.Empty;
        foreach (var p in raw.Parts)
        {
            if (!meshBounds.TryGetValue(p.Mesh, out var b)) meshBounds[p.Mesh] = b = p.Mesh.ComputeBounds();
            total.Include(b.Transform(p.World));
        }
        return total;
    }

    /// <summary>Rounds matrix entries within 1e-6 of 0 or ±1 (removes noise from the conjugation C⁻¹·T·C).</summary>
    private static Matrix4x4 Snap(Matrix4x4 m)
    {
        static float S(float v)
        {
            if (MathF.Abs(v) < 1e-6f) return 0f;
            if (MathF.Abs(v - 1f) < 1e-6f) return 1f;
            if (MathF.Abs(v + 1f) < 1e-6f) return -1f;
            return v;
        }
        return new Matrix4x4(
            S(m.M11), S(m.M12), S(m.M13), S(m.M14),
            S(m.M21), S(m.M22), S(m.M23), S(m.M24),
            S(m.M31), S(m.M32), S(m.M33), S(m.M34),
            m.M41, m.M42, m.M43, S(m.M44));
    }

    private static ModelPart Flatten(string name, List<ModelPart> parts, float smoothingAngle)
    {
        var sources = new List<(MeshData Mesh, int MaterialSlot)>(parts.Count);
        foreach (var p in parts)
        {
            var src = p.Mesh;
            var copy = new MeshData
            {
                Name = src.Name,
                Positions = (Vector3[])src.Positions.Clone(),
                Normals = (Vector3[])src.Normals.Clone(),
                TexCoords = src.TexCoords is null ? null : (Vector2[])src.TexCoords.Clone(),
                Indices = src.Indices,
                SubMeshes = new List<SubMesh>(src.SubMeshes),
            };
            if (copy.Normals.Length != copy.Positions.Length) MeshTools.GenerateNormals(copy, smoothingAngle);
            if (!p.Transform.IsIdentity)
            {
                copy.Indices = (uint[])src.Indices.Clone();
                MeshTools.Transform(copy, p.Transform);
            }
            int slot = copy.SubMeshes.Count > 0 ? copy.SubMeshes[0].MaterialSlot : 0;
            sources.Add((copy, slot));
        }
        var merged = MeshTools.Merge(name, sources);
        Debug.Assert(merged.Positions.Length == merged.Normals.Length);
        return new ModelPart { Name = name, Mesh = merged, Transform = Matrix4x4.Identity };
    }
}
