using System.Collections.Concurrent;
using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;

namespace Candela.Gdtf;

/// <summary>Where the mesh of a loaded GDTF model came from.</summary>
public enum ModelSourceKind
{
    /// <summary>A glTF/GLB file from <c>models/gltf*</c>.</summary>
    Gltf,

    /// <summary>A 3DS file from <c>models/3ds*</c>.</summary>
    ThreeDs,

    /// <summary>A procedural mesh for the model's <see cref="GdtfModel.PrimitiveType"/>.</summary>
    Primitive,

    /// <summary>A box of the declared size standing in for a model file that is missing or unreadable.</summary>
    Placeholder,
}

/// <summary>Which detail folder to prefer (GDTF 1.2 allows <c>models/gltf_low</c>, <c>models/gltf_high</c>, …).</summary>
public enum ModelDetailLevel
{
    /// <summary><c>models/gltf</c> / <c>models/3ds</c>.</summary>
    Default,

    /// <summary><c>*_low</c> folders first, then the default ones.</summary>
    Low,

    /// <summary><c>*_high</c> folders first, then the default ones.</summary>
    High,
}

/// <summary>Options of <see cref="GdtfModelLoader"/>.</summary>
public sealed class GdtfModelLoadOptions
{
    /// <summary>Detail folder preference.</summary>
    public ModelDetailLevel Detail { get; init; } = ModelDetailLevel.Default;

    /// <summary>Try glTF before 3DS (default); 3DS is the fallback either way.</summary>
    public bool PreferGltf { get; init; } = true;

    /// <summary>Scale file meshes per axis to the declared Length/Width/Height when they differ by more than <see cref="FitTolerance"/>.</summary>
    public bool FitToDeclaredSize { get; init; } = true;

    /// <summary>Relative per-axis difference below which a file mesh is left at its own size (default 2 %).</summary>
    public float FitTolerance { get; init; } = 0.02f;
}

/// <summary>How a model was loaded and transformed.</summary>
/// <param name="Source">Mesh source.</param>
/// <param name="Entry">Archive entry of the model file, or null for primitives.</param>
/// <param name="ConvertedFromYUp">True when the file was Y-up (glTF) and was rotated to Z-up.</param>
/// <param name="UnitScale">Uniform unit factor applied first (1 = metres, 0.001 = file in millimetres, …).</param>
/// <param name="AxisScale">Per-axis factor applied after the unit factor to reach the declared size (1,1,1 = none).</param>
/// <param name="FileSize">Bounding-box size of the file mesh in file units after axis conversion.</param>
/// <param name="FinalSize">Bounding-box size of the result in metres.</param>
public sealed record ModelLoadInfo(
    ModelSourceKind Source, string? Entry, bool ConvertedFromYUp, float UnitScale, Vector3 AxisScale, Vector3 FileSize, Vector3 FinalSize);

/// <summary>A loaded model mesh (shared and read-only by convention) and how it was produced.</summary>
/// <param name="Model">Mesh in GDTF geometry space: metres, Z up, origin = geometry origin, identity part transforms.</param>
/// <param name="Info">Provenance and applied transforms.</param>
public sealed record LoadedModel(ModelData Model, ModelLoadInfo Info);

/// <summary>
/// Loads the meshes of GDTF models from an archive: glTF/GLB (via SharpGLTF), 3DS (own reader) or procedural primitives,
/// converted to the engine convention (metres, Z up) and fitted to the declared model size.
/// </summary>
/// <remarks>
/// <para>
/// Axis conventions found in the reference library (1091 files): glTF models are Y-up as the glTF spec requires — in
/// 868 of 1260 GLB models the file's X/Y/Z extents equal the declared Length/Height/Width (e.g. Robe MegaPointe head:
/// file 0.278 × 0.453 × 0.254 vs declared L 0.285, W 0.254, H 0.453), and the lens of heads sits at −Y, matching GDTF's
/// beam direction −Z after rotating +90° about X: <c>(x, y, z) → (x, −z, y)</c>. 3DS models are Z-up like GDTF, but
/// more than half of them are in millimetres rather than the metres GDTF asks for.
/// </para>
/// <para>
/// Size: the declared Length (X), Width (Y), Height (Z) are authoritative (GDTF viewers scale models to them; several
/// manufacturers reuse one generic mesh, e.g. a 1 m tube scaled to 6 cm pixel segments). The mesh is first brought to
/// metres by a unit factor (1, 0.01, 0.001 or 0.0254) and then scaled per axis about the origin when it still differs
/// from the declared size by more than <see cref="GdtfModelLoadOptions.FitTolerance"/>. A declared size of exactly
/// 1 × 1 × 1 m is treated as an unset placeholder and only the unit factor is applied.
/// </para>
/// <para>Results are cached per model; the loader is thread-safe. Problems are collected in <see cref="Warnings"/>.</para>
/// </remarks>
public sealed class GdtfModelLoader
{
    private static readonly string[] GltfExtensions = { ".glb", ".gltf" };
    private static readonly string[] ThreeDsExtensions = { ".3ds" };
    private static readonly float[] UnitCandidates = { 1f, 0.001f, 0.01f, 0.0254f };

    private readonly GdtfArchive _archive;
    private readonly GdtfModelLoadOptions _options;
    private readonly ConcurrentDictionary<GdtfModel, Lazy<LoadedModel?>> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly List<GdtfWarning> _warnings = new();
    private readonly string? _keyPrefix;

    /// <summary>Creates a loader for one archive (the archive must stay open while loading).</summary>
    public GdtfModelLoader(GdtfArchive archive, GdtfModelLoadOptions? options = null)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _options = options ?? new GdtfModelLoadOptions();
        _keyPrefix = archive.FileName is { } file ? GdtfImageSource.MakeKey(file, "") : null;
    }

    /// <summary>Problems found so far (missing files, unreadable meshes, …).</summary>
    public IReadOnlyList<GdtfWarning> Warnings
    {
        get
        {
            lock (_warnings) return _warnings.ToArray();
        }
    }

    /// <summary>Loads (or returns the cached) mesh of <paramref name="model"/>; null when the model has nothing to draw.</summary>
    public LoadedModel? Load(GdtfModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return _cache.GetOrAdd(model, m => new Lazy<LoadedModel?>(() => LoadUncached(m), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>Just the mesh of <see cref="Load"/>.</summary>
    public ModelData? LoadModelData(GdtfModel model) => Load(model)?.Model;

    private void Warn(string path, string message)
    {
        lock (_warnings) _warnings.Add(new GdtfWarning(path, message));
    }

    private LoadedModel? LoadUncached(GdtfModel model)
    {
        string path = ReadContext.ChildPath("Models", model.Name);
        if (model.File is null)
            return model.PrimitiveType == GdtfPrimitiveType.Undefined ? null : Primitive(model, ModelSourceKind.Primitive);

        var kinds = _options.PreferGltf ? new[] { ModelSourceKind.Gltf, ModelSourceKind.ThreeDs } : new[] { ModelSourceKind.ThreeDs, ModelSourceKind.Gltf };
        bool found = false;
        foreach (var kind in kinds)
        {
            var entry = _archive.FindModelFile(model.File, Folders(kind), kind == ModelSourceKind.Gltf ? GltfExtensions : ThreeDsExtensions);
            if (entry is null) continue;
            found = true;
            var data = _archive.TryReadBytes(entry);
            if (data is null)
            {
                Warn(path, $"Model file '{entry}' cannot be decompressed.");
                continue;
            }
            try
            {
                var mesh = kind == ModelSourceKind.Gltf
                    ? GltfModelReader.Read(data, entry, _archive.TryReadBytes, _keyPrefix, m => Warn(path, m))
                    : ThreeDsReader.Read(data, model.Name, m => Warn(path, m));
                if (kind == ModelSourceKind.ThreeDs) ResolveThreeDsTextures(mesh, entry);
                if (mesh.Parts.Count == 0 || mesh.Parts.All(p => p.Mesh.TriangleCount == 0))
                {
                    Warn(path, $"Model file '{entry}' contains no triangles.");
                    continue;
                }
                mesh.Name = model.Name;
                var info = Fit(mesh, model, kind, entry, convertYUp: kind == ModelSourceKind.Gltf);
                return new LoadedModel(mesh, info);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A model file is untrusted content of one geometry: whatever its decoder throws on it (SharpGLTF alone
                // throws schema, JSON, key, format and I/O exceptions on corrupt data) costs this model, not the fixture.
                Warn(path, $"Model file '{entry}' cannot be read ({e.GetType().Name}: {e.Message})");
            }
        }

        if (!found) Warn(path, $"Model file '{model.File}' is not in the archive.");
        if (model.PrimitiveType != GdtfPrimitiveType.Undefined) return Primitive(model, ModelSourceKind.Primitive);
        var placeholder = Primitive(model, ModelSourceKind.Placeholder);
        if (placeholder is not null) Warn(path, "A box of the declared size is used instead.");
        return placeholder;
    }

    private IReadOnlyList<string> Folders(ModelSourceKind kind)
    {
        string baseFolder = kind == ModelSourceKind.Gltf ? "models/gltf" : "models/3ds";
        return _options.Detail switch
        {
            ModelDetailLevel.Low => new[] { baseFolder + "_low", baseFolder },
            ModelDetailLevel.High => new[] { baseFolder + "_high", baseFolder },
            _ => new[] { baseFolder },
        };
    }

    private static LoadedModel? Primitive(GdtfModel model, ModelSourceKind kind)
    {
        var mesh = PrimitiveMeshes.Create(model.PrimitiveType, model.Size, model.Name);
        if (mesh is null) return null;
        var size = mesh.ComputeBounds().Size;
        return new LoadedModel(mesh, new ModelLoadInfo(kind, null, false, 1f, Vector3.One, size, size));
    }

    /// <summary>3DS materials name their texture file; turn that into a texture key when the archive has it.</summary>
    private void ResolveThreeDsTextures(ModelData mesh, string entry)
    {
        foreach (var mat in mesh.Materials)
        {
            if (mat.BaseColorTexture is not { } file) continue;
            string folder = entry[..(entry.LastIndexOf('/') + 1)];
            string? hit = _archive.ResolveEntryName(folder + file) ?? _archive.ResolveEntryName(file);
            if (hit is null)
            {
                string name = Path.GetFileName(file.Replace('\\', '/'));
                hit = _archive.FindInAnyFolder(name);
            }
            mat.BaseColorTexture = hit is not null && _keyPrefix is not null ? _keyPrefix + hit : null;
        }
    }

    private ModelLoadInfo Fit(ModelData mesh, GdtfModel model, ModelSourceKind kind, string entry, bool convertYUp)
    {
        if (convertYUp) Transform(mesh, static p => new Vector3(p.X, -p.Z, p.Y), static p => new Vector3(p.X, -p.Z, p.Y));
        var fileSize = mesh.ComputeBounds().Size;
        var declared = model.Size;
        float unit = ChooseUnit(fileSize, declared);
        var axis = Vector3.One;

        bool placeholder = declared == Vector3.One;
        if (_options.FitToDeclaredSize && !placeholder)
        {
            Span<float> s = stackalloc float[3];
            Span<bool> valid = stackalloc bool[3];
            double logSum = 0;
            int validCount = 0;
            for (int a = 0; a < 3; a++)
            {
                float d = declared[a], f = fileSize[a] * unit;
                valid[a] = d > 1e-5f && f > 1e-7f;
                s[a] = valid[a] ? d / f : 1f;
                if (valid[a])
                {
                    logSum += System.Math.Log(s[a]);
                    validCount++;
                }
            }
            if (validCount > 0)
            {
                float fill = (float)System.Math.Exp(logSum / validCount);
                for (int a = 0; a < 3; a++)
                    if (!valid[a] && fileSize[a] * unit > 1e-7f) s[a] = fill;
                bool within = true;
                for (int a = 0; a < 3; a++) within &= System.Math.Abs(s[a] - 1f) <= _options.FitTolerance;
                if (!within) axis = new Vector3(s[0], s[1], s[2]);
            }
        }

        var total = axis * unit;
        if (total != Vector3.One)
        {
            var inverse = new Vector3(1f / total.X, 1f / total.Y, 1f / total.Z);
            Transform(mesh, p => p * total, n => n * inverse);
        }
        return new ModelLoadInfo(kind, entry, convertYUp, unit, axis, fileSize, mesh.ComputeBounds().Size);
    }

    /// <summary>Unit factor that brings the file size closest to the declared size (or to a plausible fixture size).</summary>
    internal static float ChooseUnit(Vector3 fileSize, Vector3 declared)
    {
        float best = 1f;
        double bestError = double.MaxValue;
        bool anyDeclared = declared.X > 1e-5f || declared.Y > 1e-5f || declared.Z > 1e-5f;
        bool placeholder = declared == Vector3.One;
        foreach (float u in UnitCandidates)
        {
            double error = 0;
            int n = 0;
            if (anyDeclared && !placeholder)
            {
                for (int a = 0; a < 3; a++)
                {
                    float d = declared[a], f = fileSize[a] * u;
                    if (d <= 1e-5f || f <= 1e-7f) continue;
                    error += System.Math.Abs(System.Math.Log(f / d));
                    n++;
                }
            }
            if (n == 0)
            {
                // No usable declaration: prefer the unit that gives a plausible fixture size (largest extent ~0.3 m).
                float max = System.Math.Max(fileSize.X, System.Math.Max(fileSize.Y, fileSize.Z)) * u;
                if (max <= 0) return 1f;
                error = System.Math.Abs(System.Math.Log(max / 0.3)) + (u == 1f ? 0 : 0.5); // bias towards metres
            }
            if (error < bestError - 1e-9)
            {
                bestError = error;
                best = u;
            }
        }
        return best;
    }

    /// <summary>Applies a position map and a normal map (normals renormalised) to every part of a model.</summary>
    internal static void Transform(ModelData model, Func<Vector3, Vector3> position, Func<Vector3, Vector3> normal)
    {
        var seen = new HashSet<MeshData>(ReferenceEqualityComparer.Instance);
        foreach (var part in model.Parts)
        {
            var mesh = part.Mesh;
            if (!seen.Add(mesh)) continue;
            var p = mesh.Positions;
            for (int i = 0; i < p.Length; i++) p[i] = position(p[i]);
            var n = mesh.Normals;
            for (int i = 0; i < n.Length; i++)
            {
                var v = normal(n[i]);
                n[i] = v.LengthSquared() > 1e-30f && float.IsFinite(v.X) ? Vector3.Normalize(v) : Vector3.UnitZ;
            }
            if (mesh.Tangents is { } t)
            {
                // Tangents are directions: the (linear) position map applies, without the normal correction.
                for (int i = 0; i < t.Length; i++)
                {
                    var v = position(new Vector3(t[i].X, t[i].Y, t[i].Z));
                    v = v.LengthSquared() > 1e-30f ? Vector3.Normalize(v) : Vector3.UnitX;
                    t[i] = new Vector4(v, t[i].W);
                }
            }
        }
    }
}
