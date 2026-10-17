namespace Candela.Gdtf;

/// <summary>
/// A parsed GDTF fixture type (DIN SPEC 15800, GDTF 1.0 – 1.2): identity, attribute definitions, wheels, physical
/// descriptions, models, the geometry tree, DMX modes, revisions, presets and protocols. Names given in the file are
/// kept, and every cross reference (attribute, wheel, emitter, filter, model, geometry, channel …) is also resolved
/// into an object link; links that cannot be resolved stay <c>null</c> and produce a <see cref="Warnings"/> entry.
/// </summary>
/// <remarks>
/// Units are those of GDTF: lengths in metres, angles in degrees, time in seconds, power in watts, luminous flux in
/// lumen, luminous intensity in candela, colour as CIE 1931 xyY. Matrices are already converted to the
/// System.Numerics row-vector convention (see <see cref="GdtfMatrix"/>). An instance is not modified after
/// <see cref="GdtfReader"/> returns it and can be shared between threads.
/// </remarks>
public sealed class FixtureType
{
    private Dictionary<string, GdtfAttribute>? _attributes;
    private Dictionary<string, Wheel>? _wheels;
    private Dictionary<string, Emitter>? _emitters;
    private Dictionary<string, Filter>? _filters;
    private Dictionary<string, GdtfModel>? _models;
    private Dictionary<string, GdtfGeometry>? _geometries;
    private Dictionary<string, DmxMode>? _modes;

    /// <summary>GDTF <c>DataVersion</c> of the file ("1.0", "1.1", "1.2").</summary>
    public string DataVersion { get; set; } = "1.2";

    /// <summary>Name of the fixture type (as shown in libraries; without the manufacturer).</summary>
    public string Name { get; set; } = "";

    /// <summary>Short name for small displays (GDTF <c>ShortName</c>).</summary>
    public string ShortName { get; set; } = "";

    /// <summary>Detailed name (GDTF <c>LongName</c>).</summary>
    public string LongName { get; set; } = "";

    /// <summary>Manufacturer as written in the file.</summary>
    public string Manufacturer { get; set; } = "";

    /// <summary>Free-text description.</summary>
    public string Description { get; set; } = "";

    /// <summary>Unique id of the fixture type (GDTF <c>FixtureTypeID</c>); <see cref="Guid.Empty"/> when missing or invalid.</summary>
    public Guid FixtureTypeId { get; set; }

    /// <summary>Optional fixture type this one is derived from (GDTF <c>RefFT</c>).</summary>
    public Guid? RefFixtureTypeId { get; set; }

    /// <summary>Thumbnail resource name without extension (<c>{Thumbnail}.png</c> / <c>.svg</c> in the archive root).</summary>
    public string? Thumbnail { get; set; }

    /// <summary>Horizontal pixel offset of the thumbnail's insertion point.</summary>
    public int ThumbnailOffsetX { get; set; }

    /// <summary>Vertical pixel offset of the thumbnail's insertion point.</summary>
    public int ThumbnailOffsetY { get; set; }

    /// <summary>Whether other fixtures may be mounted on this one (GDTF <c>CanHaveChildren</c>, default yes).</summary>
    public bool CanHaveChildren { get; set; } = true;

    /// <summary>Activation groups, feature groups and attributes.</summary>
    public AttributeDefinitions AttributeDefinitions { get; } = new();

    /// <summary>Colour, gobo, prism and animation wheels.</summary>
    public List<Wheel> Wheels { get; } = new();

    /// <summary>Emitters, filters, colour spaces, gamuts, DMX profiles, CRIs, connectors and fixture properties.</summary>
    public PhysicalDescriptions PhysicalDescriptions { get; } = new();

    /// <summary>Model definitions referenced by geometries.</summary>
    public List<GdtfModel> Models { get; } = new();

    /// <summary>Top-level geometries. A DMX mode picks one as its root; the others are templates for references.</summary>
    public List<GdtfGeometry> Geometries { get; } = new();

    /// <summary>DMX modes in file order.</summary>
    public List<DmxMode> DmxModes { get; } = new();

    /// <summary>Revision history.</summary>
    public List<Revision> Revisions { get; } = new();

    /// <summary>Fixture type presets (GDTF leaves their content open; kept as name/attribute bags).</summary>
    public List<FtPreset> Presets { get; } = new();

    /// <summary>RDM, Art-Net, sACN, PosiStageNet, OSC and CITP information.</summary>
    public GdtfProtocols Protocols { get; } = new();

    /// <summary>Non-fatal problems found while reading.</summary>
    public List<GdtfWarning> Warnings { get; } = new();

    /// <summary>File name of the source archive, when read from a file (used for texture keys).</summary>
    public string? SourceFileName { get; set; }

    /// <summary>Major.minor of <see cref="DataVersion"/> (1.0 when unparsable).</summary>
    public Version Version => System.Version.TryParse(DataVersion, out var v) ? v : new Version(1, 0);

    /// <summary>Attribute by exact name, or null.</summary>
    public GdtfAttribute? FindAttribute(string? name) => Lookup(ref _attributes, AttributeDefinitions.Attributes, a => a.Name, name);

    /// <summary>Wheel by exact name, or null.</summary>
    public Wheel? FindWheel(string? name) => Lookup(ref _wheels, Wheels, w => w.Name, name);

    /// <summary>Emitter by exact name, or null.</summary>
    public Emitter? FindEmitter(string? name) => Lookup(ref _emitters, PhysicalDescriptions.Emitters, e => e.Name, name);

    /// <summary>Filter by exact name, or null.</summary>
    public Filter? FindFilter(string? name) => Lookup(ref _filters, PhysicalDescriptions.Filters, f => f.Name, name);

    /// <summary>Model by exact name, or null.</summary>
    public GdtfModel? FindModel(string? name) => Lookup(ref _models, Models, m => m.Name, name);

    /// <summary>Geometry anywhere in the tree by name (first in depth-first order when names repeat), or null.</summary>
    public GdtfGeometry? FindGeometry(string? name) => Lookup(ref _geometries, AllGeometries(), g => g.Name, name);

    /// <summary>DMX mode by name; <c>null</c> or empty returns the first mode (MVR convention).</summary>
    public DmxMode? FindMode(string? name)
    {
        if (string.IsNullOrEmpty(name)) return DmxModes.Count > 0 ? DmxModes[0] : null;
        var mode = Lookup(ref _modes, DmxModes, m => m.Name, name);
        if (mode is not null) return mode;
        foreach (var m in DmxModes)
            if (string.Equals(m.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    /// <summary>Every geometry of every top-level tree, depth first (GeometryReferences are not expanded).</summary>
    public IEnumerable<GdtfGeometry> AllGeometries()
    {
        foreach (var top in Geometries)
            foreach (var g in top.DescendantsAndSelf())
                yield return g;
    }

    /// <summary>Drops the name lookup caches; call after changing collections of a fixture type built in code.</summary>
    public void InvalidateLookups()
    {
        _attributes = null;
        _wheels = null;
        _emitters = null;
        _filters = null;
        _models = null;
        _geometries = null;
        _modes = null;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Manufacturer} {Name}";

    private static T? Lookup<T>(ref Dictionary<string, T>? cache, IEnumerable<T> source, Func<T, string> key, string? name)
        where T : class
    {
        if (name is null) return null;
        var dict = cache;
        if (dict is null)
        {
            dict = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in source) dict.TryAdd(key(item), item);
            cache = dict; // benign race: every thread builds the same content
        }
        return dict.TryGetValue(name, out var found) ? found : null;
    }
}

/// <summary>One entry of the revision history.</summary>
public sealed class Revision
{
    /// <summary>What changed.</summary>
    public string Text { get; set; } = "";

    /// <summary>When (UTC), if the date could be parsed.</summary>
    public DateTime? Date { get; set; }

    /// <summary>GDTF Share user id of the author (0 = unknown).</summary>
    public uint UserId { get; set; }

    /// <summary>Name of the software or person that made the change (GDTF 1.1+).</summary>
    public string? ModifiedBy { get; set; }
}

/// <summary>A fixture type preset. GDTF does not define its content yet, so it is kept as an attribute bag.</summary>
public sealed class FtPreset
{
    /// <summary>XML element name.</summary>
    public string ElementName { get; set; } = "FTPreset";

    /// <summary>All XML attributes as written.</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
}
