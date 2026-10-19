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
    private const string ObjectAttribute = "object";
    private const string GeometryAttribute = "geometry";

    /// <summary>The asset-key scheme of Candela.Assets (<c>asset:{id}/…</c>): textures of the project's own assets.</summary>
    private const string AssetScheme = "asset:";

    /// <summary>A wall of the block: the MVR object it belongs to, the file of its box geometry and the component.</summary>
    /// <param name="ObjectId">Uuid of the MVR scene object.</param>
    /// <param name="GeometryFile">File name of the box geometry written for other programs, or null.</param>
    /// <param name="Wall">The wall, floor or ceiling.</param>
    public sealed record WallRecord(Guid ObjectId, string? GeometryFile, WallComponent Wall);

    /// <summary>Builds Candela's <c>UserData/Data</c> block for <paramref name="walls"/>.</summary>
    public static MvrUserData Create(IEnumerable<WallRecord> walls)
    {
        ArgumentNullException.ThrowIfNull(walls);
        var data = new XElement("Data", new XAttribute("provider", Provider), new XAttribute("ver", Version));
        foreach (var w in walls)
        {
            var element = new XElement(WallElement, new XAttribute(ObjectAttribute, w.ObjectId.ToString("D")));
            if (!string.IsNullOrEmpty(w.GeometryFile)) element.Add(new XAttribute(GeometryAttribute, w.GeometryFile));
            element.Value = CandelaJson.SerializeComponent(w.Wall);
            data.Add(element);
        }
        return new MvrUserData { Provider = Provider, Version = Version, Element = data };
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
