using System.Numerics;
using System.Runtime.CompilerServices;
using Candela.Core.Geometry;
using Candela.Core.Math;

namespace Candela.Gdtf;

/// <summary>
/// One node of an instanced fixture geometry: a GDTF geometry (or an instance of a referenced geometry) with its
/// transform relative to the parent part and its mesh.
/// </summary>
public sealed class FixturePart
{
    internal FixturePart(int index, string name, string path, int parentIndex, Matrix4x4 local, GdtfGeometry geometry,
        GeometryReference[] references, GeometryReference? instancedBy, GdtfModel? modelDefinition, LoadedModel? model)
    {
        InstancedBy = instancedBy;
        Index = index;
        Name = name;
        Path = path;
        ParentIndex = parentIndex;
        LocalTransform = local;
        Geometry = geometry;
        References = references;
        ModelDefinition = modelDefinition;
        LoadedModel = model;
    }

    /// <summary>Index in <see cref="FixtureGeometry.Parts"/>.</summary>
    public int Index { get; }

    /// <summary>Geometry name, or the reference name for the top node of a referenced instance (e.g. "Pixel 12").</summary>
    public string Name { get; }

    /// <summary>Slash-separated path of names from the root, unique within the fixture (e.g. "Base/Yoke/Head").</summary>
    public string Path { get; }

    /// <summary>Index of the parent part, −1 for the root. Parents always come before their children.</summary>
    public int ParentIndex { get; }

    /// <summary>Transform relative to the parent part (row-vector convention, metres).</summary>
    public Matrix4x4 LocalTransform { get; }

    /// <summary>
    /// The GDTF geometry this part instantiates. For the top node of a referenced instance this is the referenced
    /// top-level geometry (its own Position is replaced by the reference's).
    /// </summary>
    public GdtfGeometry Geometry { get; }

    /// <summary>Element type of <see cref="Geometry"/> (Axis parts are animated, Beam parts emit light).</summary>
    public GeometryType Type => Geometry.Type;

    /// <summary>Geometry references from the root down to this part's instance (outermost first); empty outside references.</summary>
    public IReadOnlyList<GeometryReference> References { get; }

    /// <summary>The reference that created this part when it is the top node of an instance, else null.</summary>
    public GeometryReference? InstancedBy { get; }

    /// <summary>Model definition (declared size, primitive type, file), or null.</summary>
    public GdtfModel? ModelDefinition { get; }

    /// <summary>Loaded mesh with provenance, or null when the part has no visible model.</summary>
    public LoadedModel? LoadedModel { get; }

    /// <summary>Mesh in the part's local space (metres, Z up), shared between parts with the same model; null when none.</summary>
    public ModelData? Model => LoadedModel?.Model;

    /// <inheritdoc />
    public override string ToString() => Path;
}

/// <summary>
/// A fixture's geometry tree flattened into parts (parents before children) with GeometryReferences expanded, so the
/// renderer can draw it and the DMX layer can animate Axis parts and address each pixel.
/// </summary>
public sealed class FixtureGeometry
{
    private readonly Dictionary<PartKey, int> _byGeometry;
    private readonly Dictionary<string, int> _byPath;

    internal FixtureGeometry(GdtfGeometry root, DmxMode? mode, List<FixturePart> parts, Dictionary<PartKey, int> byGeometry, List<GdtfWarning> warnings)
    {
        Root = root;
        Mode = mode;
        Parts = parts;
        _byGeometry = byGeometry;
        _byPath = new Dictionary<string, int>(parts.Count, StringComparer.Ordinal);
        foreach (var p in parts) _byPath.TryAdd(p.Path, p.Index);
        Warnings = warnings;
    }

    /// <summary>Root geometry (the mode's geometry).</summary>
    public GdtfGeometry Root { get; }

    /// <summary>Mode the geometry was built for, if any.</summary>
    public DmxMode? Mode { get; }

    /// <summary>All parts; index 0 is the root.</summary>
    public IReadOnlyList<FixturePart> Parts { get; }

    /// <summary>Problems found while building (e.g. reference cycles, part limit).</summary>
    public IReadOnlyList<GdtfWarning> Warnings { get; }

    /// <summary>
    /// Part of a geometry inside the instance given by its reference chain, or −1. A <see cref="GeometryReference"/>
    /// itself (DMX channels may name one, e.g. per-layer controls of a multi-layer fixture) stands for the top part of
    /// the instance it creates.
    /// </summary>
    public int FindPart(GdtfGeometry? geometry, IReadOnlyList<GeometryReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (geometry is null) return -1;
        var chain = references as GeometryReference[] ?? references.ToArray();
        if (_byGeometry.TryGetValue(new PartKey(geometry, chain), out int index)) return index;
        if (geometry is GeometryReference { ReferencedGeometry: { } template } reference)
        {
            var extended = new GeometryReference[chain.Length + 1];
            chain.CopyTo(extended, 0);
            extended[^1] = reference;
            if (_byGeometry.TryGetValue(new PartKey(template, extended), out index)) return index;
        }
        return -1;
    }

    /// <summary>Part a DMX channel instance controls, or −1 (e.g. when the channel's geometry is not in this tree).</summary>
    public int FindPart(DmxChannelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return FindPart(instance.Geometry, instance.References);
    }

    /// <summary>Part by path (<see cref="FixturePart.Path"/>), or −1.</summary>
    public int FindPartByPath(string path) => _byPath.TryGetValue(path, out int i) ? i : -1;

    /// <summary>
    /// Computes world transforms without allocating: <c>world[i] = animation[i] · local[i] · world[parent]</c>, with the root's
    /// parent being <paramref name="fixtureTransform"/>. <paramref name="animation"/> (optional, one per part) is the
    /// rotation/translation of an Axis about its own origin from the DMX state; pass an empty span for the rest pose.
    /// </summary>
    public void ComputeWorldTransforms(Span<Matrix4x4> world, Matrix4x4 fixtureTransform, ReadOnlySpan<Matrix4x4> animation = default)
    {
        if (world.Length < Parts.Count) throw new ArgumentException($"Need {Parts.Count} matrices.", nameof(world));
        bool animated = animation.Length >= Parts.Count;
        for (int i = 0; i < Parts.Count; i++)
        {
            var p = Parts[i];
            var local = animated ? animation[i] * p.LocalTransform : p.LocalTransform;
            world[i] = local * (p.ParentIndex < 0 ? fixtureTransform : world[p.ParentIndex]);
        }
    }

    /// <summary>Bounds of all part meshes under the given world transforms (metres).</summary>
    public Aabb ComputeBounds(ReadOnlySpan<Matrix4x4> world)
    {
        var bounds = Aabb.Empty;
        for (int i = 0; i < Parts.Count && i < world.Length; i++)
            if (Parts[i].Model is { } model) bounds.Include(model.ComputeBounds().Transform(world[i]));
        return bounds;
    }

    /// <summary>Key of a part: geometry plus the reference chain it was instanced through (compared by identity).</summary>
    internal readonly struct PartKey : IEquatable<PartKey>
    {
        private readonly int _hash;

        public PartKey(GdtfGeometry geometry, GeometryReference[] chain)
        {
            Geometry = geometry;
            Chain = chain;
            var h = new HashCode();
            h.Add(RuntimeHelpers.GetHashCode(geometry));
            foreach (var r in chain) h.Add(RuntimeHelpers.GetHashCode(r));
            _hash = h.ToHashCode();
        }

        public GdtfGeometry Geometry { get; }
        public GeometryReference[] Chain { get; }

        public bool Equals(PartKey other)
        {
            if (_hash != other._hash || !ReferenceEquals(Geometry, other.Geometry) || Chain.Length != other.Chain.Length) return false;
            for (int i = 0; i < Chain.Length; i++)
                if (!ReferenceEquals(Chain[i], other.Chain[i])) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is PartKey k && Equals(k);

        public override int GetHashCode() => _hash;
    }
}

/// <summary>
/// Walks a mode's geometry tree and returns its parts with models: GeometryReferences are expanded into copies of
/// the referenced geometry (placed at the reference's position, with the reference's model replacing the top
/// node's model when given), recursively for nested references.
/// </summary>
public sealed class FixtureGeometryBuilder
{
    /// <summary>Maximum nesting of geometry references that is expanded.</summary>
    public const int MaxReferenceDepth = 16;

    /// <summary>Maximum number of parts of one fixture (guards against reference explosions in broken files).</summary>
    public const int MaxParts = 200_000;

    /// <summary>
    /// Longest <see cref="FixturePart.Path"/> (characters). A part whose path would be longer gets the path
    /// <c>…#{index}</c> instead, so hostile names cannot make path memory grow with depth × name length.
    /// </summary>
    public const int MaxPathLength = 4096;

    /// <summary>
    /// Most characters of all <see cref="FixturePart.Path"/>s of one build together (the library's largest needs 113 000).
    /// Past it parts get the indexed path <c>…#{index}</c>: a 50 kB description of a 250-level template instanced 800
    /// times otherwise made the paths alone take 800 MB.
    /// </summary>
    public const int MaxTotalPathCharacters = 4 << 20;

    /// <summary>
    /// Most expansion steps (parts, references, refused references) of one build: bounds the work for files whose
    /// references are refused over and over, which <see cref="MaxParts"/> alone does not.
    /// </summary>
    private const long MaxSteps = 4L * MaxParts;

    private readonly FixtureType _fixtureType;
    private readonly Func<GdtfModel, LoadedModel?>? _models;

    /// <summary>Creates a builder; <paramref name="models"/> loads meshes (null: parts get no meshes).</summary>
    public FixtureGeometryBuilder(FixtureType fixtureType, Func<GdtfModel, LoadedModel?>? models = null)
    {
        _fixtureType = fixtureType ?? throw new ArgumentNullException(nameof(fixtureType));
        _models = models;
    }

    /// <summary>Creates a builder that loads meshes with <paramref name="loader"/>.</summary>
    public FixtureGeometryBuilder(FixtureType fixtureType, GdtfModelLoader loader)
        : this(fixtureType, (loader ?? throw new ArgumentNullException(nameof(loader))).Load)
    {
    }

    /// <summary>
    /// Builds the geometry of a mode (null: the first mode; without modes the first top-level geometry). A fixture type
    /// without any geometry yields a single empty root part named after the fixture.
    /// </summary>
    public FixtureGeometry Build(DmxMode? mode = null)
    {
        mode ??= _fixtureType.DmxModes.FirstOrDefault();
        var root = mode?.Geometry ?? _fixtureType.Geometries.FirstOrDefault();
        if (root is null)
        {
            var empty = Build(new GdtfGeometry { Name = string.IsNullOrEmpty(_fixtureType.Name) ? "Fixture" : _fixtureType.Name }, mode);
            return new FixtureGeometry(empty.Root, mode, empty.Parts.ToList(), new Dictionary<FixtureGeometry.PartKey, int>(),
                new List<GdtfWarning>(empty.Warnings) { new("Geometries", "Fixture type has no geometry; an empty root is used.") });
        }
        return Build(root, mode);
    }

    /// <summary>Builds the geometry below an explicit root.</summary>
    public FixtureGeometry Build(GdtfGeometry root, DmxMode? mode)
    {
        ArgumentNullException.ThrowIfNull(root);
        var parts = new List<FixturePart>();
        var keys = new Dictionary<FixtureGeometry.PartKey, int>();
        var warnings = new List<GdtfWarning>();
        var active = new HashSet<GdtfGeometry>(ReferenceEqualityComparer.Instance) { root.TopLevel };

        // Depth first in file order with an explicit stack: trees may be 256 levels deep inside each of 16 nested
        // references, which recursion could not survive on small thread stacks.
        var stack = new Stack<Work>();
        stack.Push(new Work(root, root.Name, root.Position, root.Model, -1, "", Array.Empty<GeometryReference>(), null, null));
        int refused = 0;
        long steps = 0, pathCharacters = 0;
        bool pathBudgetSpent = false;
        while (stack.Count > 0)
        {
            if (++steps > MaxSteps)
            {
                // Only references that are refused over and over (a geometry referencing itself thousands of times,
                // instanced thousands of times) get here: the parts built so far are kept.
                warnings.Add(new GdtfWarning(root.Name, $"The geometry tree takes more than {MaxSteps} steps to expand; the rest is omitted."));
                break;
            }
            var w = stack.Pop();
            if (w.Leave is { } finished)
            {
                active.Remove(finished);
                continue;
            }
            if (w.Reference is { } r)
            {
                // A reference is expanded when it is reached, so the active set holds exactly the templates above it.
                var template = r.ReferencedGeometry!;
                if (w.Chain.Length + 1 > MaxReferenceDepth || active.Contains(template))
                {
                    if (refused++ == 0)
                        warnings.Add(new GdtfWarning(w.ParentPath, $"Reference '{r.Name}' to '{template.Name}' is recursive or nested too deeply; not expanded."));
                    continue;
                }
                var next = new GeometryReference[w.Chain.Length + 1];
                w.Chain.CopyTo(next, 0);
                next[^1] = r;
                active.Add(template);
                stack.Push(new Work(null, "", default, null, -1, "", Array.Empty<GeometryReference>(), null, null, template));
                stack.Push(new Work(template, r.Name, r.Position, r.Model ?? template.Model, w.Parent, w.ParentPath, next, r, null));
                continue;
            }
            if (parts.Count >= MaxParts)
            {
                warnings.Add(new GdtfWarning(root.Name, $"More than {MaxParts} parts; the rest are omitted."));
                break;
            }

            var g = w.Geometry!;
            int index = parts.Count;
            // Only absurdly long names exceed the limit (real paths stay below 300 characters): an indexed path keeps every
            // part addressable without making the memory for paths grow with depth × name length.
            int length = w.ParentPath.Length == 0 ? w.Name.Length : w.ParentPath.Length + 1 + w.Name.Length;
            if (!pathBudgetSpent && pathCharacters + length > MaxTotalPathCharacters)
            {
                pathBudgetSpent = true;
                warnings.Add(new GdtfWarning(root.Name, $"Part paths exceed {MaxTotalPathCharacters} characters in all; further parts are named by index."));
            }
            bool indexed = pathBudgetSpent || length > MaxPathLength;
            string path = indexed ? $"…#{index}" : w.ParentPath.Length == 0 ? w.Name : $"{w.ParentPath}/{w.Name}";
            if (!indexed) pathCharacters += length;
            parts.Add(new FixturePart(index, w.Name, path, w.Parent, w.Local, g, w.Chain, w.InstancedBy, w.Model,
                w.Model is null ? null : _models?.Invoke(w.Model)));
            keys.TryAdd(new FixtureGeometry.PartKey(g, w.Chain), index);

            for (int i = g.Children.Count - 1; i >= 0; i--)
            {
                var child = g.Children[i];
                if (child is GeometryReference reference)
                {
                    if (reference.ReferencedGeometry is not null)
                        stack.Push(new Work(null, "", default, null, index, path, w.Chain, null, reference));
                }
                else
                {
                    stack.Push(new Work(child, child.Name, child.Position, child.Model, index, path, w.Chain, null, null));
                }
            }
        }
        if (refused > 1)
            warnings.Add(new GdtfWarning(root.Name, $"{refused} recursive or too deeply nested references were not expanded in all."));
        return new FixtureGeometry(root, mode, parts, keys, warnings);
    }

    /// <summary>
    /// One step of the walk: a geometry to add as a part, a reference to expand (<see cref="Reference"/>), or the end of
    /// a referenced template's subtree (<see cref="Leave"/>).
    /// </summary>
    private readonly record struct Work(
        GdtfGeometry? Geometry, string Name, Matrix4x4 Local, GdtfModel? Model, int Parent, string ParentPath,
        GeometryReference[] Chain, GeometryReference? InstancedBy, GeometryReference? Reference, GdtfGeometry? Leave = null);
}
