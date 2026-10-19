using System.Text.Json;
using System.Xml.Linq;
using Candela.Core.Diagnostics;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Mvr;

/// <summary>
/// Candela's own block in an MVR's <c>GeneralSceneDescription/UserData</c> (<c>&lt;Data provider="Candela" ver="1"&gt;</c>):
/// what MVR cannot hold, so it survives an export and import through MVR. Other programs keep the block and ignore it
/// (MVR 1.6: application data in <c>UserData/Data</c> must be preserved, not interpreted).
/// </summary>
/// <remarks>
/// <para>Version 1 holds walls, floors and ceilings: one <c>&lt;Wall object="{uuid}" geometry="{file}"&gt;</c> per
/// exported <see cref="WallComponent"/>, whose text is the component as Candela JSON (kind, size in metres, material id,
/// UV scale). <c>object</c> is the uuid of the MVR SceneObject that carries the wall's box geometry (written for other
/// programs) and <c>geometry</c> the file of that box. The importer gives such an object its <see cref="WallComponent"/>
/// back and skips the box (it would otherwise become an imported model); a block it cannot read is reported and the
/// box is imported as a model instead, so nothing is lost.</para>
/// <para>It also marks Candela's generic devices (DMX splitters, Art-Net / sACN nodes, dimmer packs, whose GDTF files
/// Candela generates): one <c>&lt;Device object="{uuid}" gdtf="{file}" mode="{mode}" type="{FixtureTypeID}"/&gt;</c> per
/// exported device. The object itself is an ordinary MVR <c>Fixture</c> with its GDTF embedded, which is what other
/// programs read; the record tells Candela that <c>gdtf</c> is the file name of one of its own generic devices, so the
/// import resolves it to that device (the library's generated file, or the embedded copy under that name) without
/// reporting the channel-less GDTF as a placeholder stub. <c>type</c> (optional) is the GDTF <c>FixtureTypeID</c> of
/// the device as exported: a record whose object now carries a GDTF of another fixture type (another program kept the
/// block but changed the object) — or, without an embedded GDTF, references another file — is stale and ignored, so the
/// object is imported as the fixture it now is. Readers that do not know the element (older Candela versions) skip
/// it.</para>
/// <para>And it joins objects split across layers: MVR keeps an object's children on the object's layer, so a child on
/// another layer is exported under a GroupObject of its own layer that repeats its parent's place; one
/// <c>&lt;LayerPart object="{group uuid}" original="{parent uuid}"/&gt;</c> per such group lets the importer put the
/// group's children back under the parent (other programs see two groups of the same name, one per layer). Older
/// readers skip the element and import the group as a plain group.</para>
/// </remarks>
public static class CandelaUserData
{
    /// <summary>The <c>provider</c> of Candela's block.</summary>
    public const string Provider = "Candela";

    /// <summary>The version of the block's format written by this version.</summary>
    public const string Version = "1";

    /// <summary>Most wall records read from one block (a hostile file cannot make the import spend unbounded work).</summary>
    public const int MaxRecords = 100_000;

    private const string WallElement = "Wall";
    private const string DeviceElement = "Device";
    private const string LayerPartElement = "LayerPart";
    private const string OriginalAttribute = "original";
    private const string ObjectAttribute = "object";
    private const string GeometryAttribute = "geometry";
    private const string GdtfAttribute = "gdtf";
    private const string ModeAttribute = "mode";
    private const string TypeAttribute = "type";

    /// <summary>Longest GDTF file name accepted in a device record (characters).</summary>
    private const int MaxFileNameLength = 255;

    /// <summary>The asset-key scheme of Candela.Assets (<c>asset:{id}/…</c>): textures of the project's own assets.</summary>
    private const string AssetScheme = "asset:";

    /// <summary>A wall of the block: the MVR object it belongs to, the file of its box geometry and the component.</summary>
    /// <param name="ObjectId">Uuid of the MVR scene object.</param>
    /// <param name="GeometryFile">File name of the box geometry written for other programs, or null.</param>
    /// <param name="Wall">The wall, floor or ceiling.</param>
    public sealed record WallRecord(Guid ObjectId, string? GeometryFile, WallComponent Wall);

    /// <summary>A generic device of the block: the MVR object and the device's GDTF file name, DMX mode and fixture type.</summary>
    /// <param name="ObjectId">Uuid of the MVR fixture.</param>
    /// <param name="GdtfFile">File name of the generic device's GDTF (no folder, ending in <c>.gdtf</c>).</param>
    /// <param name="Mode">DMX mode name, or null for the GDTF's first mode.</param>
    /// <param name="FixtureTypeId">
    /// GDTF <c>FixtureTypeID</c> of the device's file as exported, or null when it was not known (the file was not
    /// embedded); the importer uses it to tell the device from a GDTF another program put on the object since.
    /// </param>
    public sealed record DeviceRecord(Guid ObjectId, string GdtfFile, string? Mode, Guid? FixtureTypeId = null);

    /// <summary>
    /// A part of an object on another layer: the GroupObject the export wrote on a second layer to hold the children of
    /// <paramref name="OriginalId"/> that are on that layer (MVR keeps children on their parent's layer).
    /// </summary>
    /// <param name="ObjectId">Uuid of the GroupObject written for the layer.</param>
    /// <param name="OriginalId">Uuid of the object it repeats (on its own layer).</param>
    public sealed record LayerPartRecord(Guid ObjectId, Guid OriginalId);

    /// <summary>Builds Candela's <c>UserData/Data</c> block for <paramref name="walls"/>.</summary>
    public static MvrUserData Create(IEnumerable<WallRecord> walls) => Create(walls, Array.Empty<DeviceRecord>());

    /// <summary>Builds Candela's <c>UserData/Data</c> block for <paramref name="walls"/> and generic <paramref name="devices"/>.</summary>
    public static MvrUserData Create(IEnumerable<WallRecord> walls, IEnumerable<DeviceRecord> devices) =>
        Create(walls, devices, Array.Empty<LayerPartRecord>());

    /// <summary>
    /// Builds Candela's <c>UserData/Data</c> block for <paramref name="walls"/>, generic <paramref name="devices"/> and
    /// the <paramref name="layerParts"/> of objects whose children are on other layers.
    /// </summary>
    public static MvrUserData Create(IEnumerable<WallRecord> walls, IEnumerable<DeviceRecord> devices, IEnumerable<LayerPartRecord> layerParts)
    {
        ArgumentNullException.ThrowIfNull(walls);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(layerParts);
        var data = new XElement("Data", new XAttribute("provider", Provider), new XAttribute("ver", Version));
        foreach (var w in walls)
        {
            var element = new XElement(WallElement, new XAttribute(ObjectAttribute, w.ObjectId.ToString("D")));
            if (!string.IsNullOrEmpty(w.GeometryFile)) element.Add(new XAttribute(GeometryAttribute, w.GeometryFile));
            element.Value = CandelaJson.SerializeComponent(w.Wall);
            data.Add(element);
        }
        foreach (var d in devices)
        {
            var element = new XElement(DeviceElement, new XAttribute(ObjectAttribute, d.ObjectId.ToString("D")), new XAttribute(GdtfAttribute, d.GdtfFile));
            if (!string.IsNullOrEmpty(d.Mode)) element.Add(new XAttribute(ModeAttribute, d.Mode));
            if (d.FixtureTypeId is { } type && type != Guid.Empty) element.Add(new XAttribute(TypeAttribute, type.ToString("D")));
            data.Add(element);
        }
        foreach (var p in layerParts)
        {
            data.Add(new XElement(LayerPartElement, new XAttribute(ObjectAttribute, p.ObjectId.ToString("D")),
                new XAttribute(OriginalAttribute, p.OriginalId.ToString("D"))));
        }
        return new MvrUserData { Provider = Provider, Version = Version, Element = data };
    }

    /// <summary>
    /// The layer parts of Candela's blocks in <paramref name="document"/>: GroupObject uuid → uuid of the object it
    /// repeats on its layer. A record without two valid, different uuids is reported in <paramref name="issues"/>
    /// (<see cref="MvrIssueCodes.ValueInvalid"/>) and left out (its GroupObject is then imported as a plain group);
    /// blocks of other providers are ignored.
    /// </summary>
    public static IReadOnlyDictionary<Guid, LayerPartRecord> ReadLayerParts(MvrDocument document, ICollection<Issue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var parts = new Dictionary<Guid, LayerPartRecord>();
        int damaged = 0;
        foreach (var block in document.UserData)
        {
            if (!string.Equals(block.Provider, Provider, StringComparison.Ordinal) || block.Element is null) continue;
            foreach (var element in block.Element.Elements())
            {
                if (element.Name.LocalName != LayerPartElement) continue;
                if (parts.Count >= MaxRecords) break;
                if (!Guid.TryParse((string?)element.Attribute(ObjectAttribute), out var objectId) || objectId == Guid.Empty ||
                    !Guid.TryParse((string?)element.Attribute(OriginalAttribute), out var originalId) || originalId == Guid.Empty ||
                    objectId == originalId)
                {
                    damaged++;
                    continue;
                }
                parts.TryAdd(objectId, new LayerPartRecord(objectId, originalId));
            }
        }
        if (damaged > 0)
        {
            issues?.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ValueInvalid,
                $"{damaged} layer part record(s) in Candela's data of the MVR could not be read; those groups are imported as plain groups."));
        }
        return parts;
    }

    /// <summary>
    /// The generic devices of Candela's blocks in <paramref name="document"/> by MVR object uuid. A record without a
    /// valid object uuid, with a GDTF name that is not a plain <c>.gdtf</c> file name or with a <c>type</c> that is not a
    /// uuid is reported in <paramref name="issues"/> (<see cref="MvrIssueCodes.ValueInvalid"/>) and left out (the object
    /// is then imported as the fixture it is); blocks of other providers are ignored.
    /// </summary>
    public static IReadOnlyDictionary<Guid, DeviceRecord> ReadDevices(MvrDocument document, ICollection<Issue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var devices = new Dictionary<Guid, DeviceRecord>();
        int damaged = 0;
        foreach (var block in document.UserData)
        {
            if (!string.Equals(block.Provider, Provider, StringComparison.Ordinal) || block.Element is null) continue;
            foreach (var element in block.Element.Elements())
            {
                if (element.Name.LocalName != DeviceElement) continue;
                if (devices.Count >= MaxRecords) break;
                string? file = ((string?)element.Attribute(GdtfAttribute))?.Trim();
                string? mode = ((string?)element.Attribute(ModeAttribute))?.Trim();
                string? typeText = ((string?)element.Attribute(TypeAttribute))?.Trim();
                Guid? type = null;
                bool typeValid = true;
                if (!string.IsNullOrEmpty(typeText))
                {
                    typeValid = Guid.TryParse(typeText, out var parsed);
                    if (typeValid && parsed != Guid.Empty) type = parsed;
                }
                if (!Guid.TryParse((string?)element.Attribute(ObjectAttribute), out var objectId) || objectId == Guid.Empty || !IsPlainGdtfName(file) ||
                    !typeValid)
                {
                    damaged++;
                    continue;
                }
                devices.TryAdd(objectId, new DeviceRecord(objectId, file!, string.IsNullOrEmpty(mode) ? null : mode, type));
            }
        }
        if (damaged > 0)
        {
            issues?.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ValueInvalid,
                $"{damaged} device record(s) in Candela's data of the MVR could not be read; those objects are imported as the fixtures they are."));
        }
        return devices;
    }

    /// <summary>A file name without folders or reserved characters that ends in <c>.gdtf</c>.</summary>
    private static bool IsPlainGdtfName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFileNameLength || !name.EndsWith(".gdtf", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.'))
            return false;
        foreach (char c in name)
            if (c < ' ' || c is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*') return false;
        return name.Length > ".gdtf".Length;
    }

    /// <summary>
    /// The walls of Candela's blocks in <paramref name="document"/> by MVR object uuid. Damaged records are reported in
    /// <paramref name="issues"/> (<see cref="MvrIssueCodes.ValueInvalid"/>) and left out; blocks of other providers are ignored.
    /// </summary>
    public static IReadOnlyDictionary<Guid, WallRecord> ReadWalls(MvrDocument document, ICollection<Issue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var walls = new Dictionary<Guid, WallRecord>();
        int damaged = 0;
        string? firstProblem = null;
        foreach (var block in document.UserData)
        {
            if (!string.Equals(block.Provider, Provider, StringComparison.Ordinal) || block.Element is null) continue;
            foreach (var element in block.Element.Elements())
            {
                if (element.Name.LocalName != WallElement) continue; // records of newer versions are kept by the document, not read
                if (walls.Count >= MaxRecords) break;
                if (TryReadWall(element, out var record, out var problem))
                {
                    walls.TryAdd(record!.ObjectId, record);
                    continue;
                }
                damaged++;
                firstProblem ??= problem;
            }
        }
        if (damaged > 0)
        {
            issues?.Add(new Issue(IssueSeverity.Warning, IssueCategory.Scene, MvrIssueCodes.ValueInvalid,
                $"{damaged} wall(s) in Candela's data of the MVR could not be read ({firstProblem}); they are imported as plain models."));
        }
        return walls;
    }

    private static bool TryReadWall(XElement element, out WallRecord? record, out string? problem)
    {
        record = null;
        if (!Guid.TryParse((string?)element.Attribute(ObjectAttribute), out var objectId) || objectId == Guid.Empty)
        {
            problem = "a wall names no object";
            return false;
        }
        WallComponent? wall;
        try
        {
            wall = JsonSerializer.Deserialize<Component>(element.Value, CandelaJson.Options) as WallComponent;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            problem = e.Message;
            return false;
        }
        if (wall is null)
        {
            problem = "a wall record holds no wall";
            return false;
        }
        if (!(float.IsFinite(wall.Width) && float.IsFinite(wall.Height) && float.IsFinite(wall.Thickness)
              && wall.Width > 0 && wall.Height > 0 && wall.Thickness > 0) || !Enum.IsDefined(wall.Kind))
        {
            problem = "a wall has an invalid kind or size";
            return false;
        }
        if (!float.IsFinite(wall.UvScale) || wall.UvScale <= 0) wall.UvScale = 1f;
        // textures of the exporting project's own assets do not travel with the MVR
        if (string.IsNullOrWhiteSpace(wall.MaterialId) || wall.MaterialId.StartsWith(AssetScheme, StringComparison.Ordinal))
            wall.MaterialId = MaterialIds.Default;
        string? geometry = (string?)element.Attribute(GeometryAttribute);
        record = new WallRecord(objectId, string.IsNullOrWhiteSpace(geometry) ? null : geometry, wall);
        problem = null;
        return true;
    }
}
