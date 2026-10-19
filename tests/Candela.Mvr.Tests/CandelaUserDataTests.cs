using System.IO.Compression;
using System.Numerics;
using System.Xml.Linq;
using Candela.Core.Diagnostics;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Mvr.Tests;

/// <summary>
/// Walls, floors and ceilings travel through an MVR as Candela's UserData block: Candela imports them as walls with
/// their materials, other programs see a box geometry and keep the block.
/// </summary>
public sealed class CandelaUserDataTests
{
    private static Scene StageWithWalls(out Entity back, out Entity floor, out Entity ceiling, out Entity crate)
    {
        var scene = new Scene("Studio");
        back = scene.Add(new Entity("Back wall") { Local = new Transform(new Vector3(0, 6, 0)) });
        back.Add(new WallComponent { Kind = WallKind.Wall, Width = 12f, Height = 6f, Thickness = 0.2f, MaterialId = MaterialIds.MoltonBlack, UvScale = 2f });
        floor = scene.Add(new Entity("Floor"));
        floor.Add(new WallComponent { Kind = WallKind.Floor, Width = 14f, Height = 10f, Thickness = 0.1f, MaterialId = MaterialIds.StageFloorBlack });
        ceiling = scene.Add(new Entity("Ceiling") { Local = new Transform(new Vector3(0, 0, 9)) });
        ceiling.Add(new WallComponent { Kind = WallKind.Ceiling, Width = 14f, Height = 10f, Thickness = 0.3f, MaterialId = "asset:00000000-0000-0000-0000-000000000001/tex.png" });
        crate = scene.Add(new Entity("Crate"));
        crate.Add(new GroupComponent());
        return scene;
    }

    [Fact]
    public void Walls_floors_and_ceilings_come_back_as_walls_with_their_materials()
    {
        var scene = StageWithWalls(out var back, out var floor, out var ceiling, out _);
        using var ms = new MemoryStream();
        var export = MvrWriter.Write(scene, ms);
        Assert.Equal(4, export.ObjectCount);

        // other programs see SceneObjects with box geometry, and Candela's block in UserData
        XElement root;
        using (var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read))
        {
            using var s = zip.GetEntry(MvrReader.SceneDescriptionEntry)!.Open();
            root = XDocument.Load(s).Root!;
        }
        var data = root.Element("UserData")!.Elements("Data").Single(d => d.Attribute("provider")?.Value == CandelaUserData.Provider);
        Assert.Equal(CandelaUserData.Version, data.Attribute("ver")?.Value);
        Assert.Equal(3, data.Elements("Wall").Count());
        Assert.Equal(3, root.Descendants("SceneObject").Count(o => o.Element("Geometries")?.Element("Geometry3D") is not null));

        var imported = MvrImporter.Import(new MemoryStream(ms.ToArray()), "studio.mvr");
        var walls = imported.AllEntities().Where(e => e.Has<WallComponent>()).ToDictionary(e => e.Name);
        Assert.Equal(3, walls.Count);
        foreach (var (original, kind) in new[] { (back, WallKind.Wall), (floor, WallKind.Floor), (ceiling, WallKind.Ceiling) })
        {
            var copy = walls[original.Name];
            var w = copy.Get<WallComponent>()!;
            var o = original.Get<WallComponent>()!;
            Assert.Equal(kind, w.Kind);
            Assert.Equal((o.Width, o.Height, o.Thickness), (w.Width, w.Height, w.Thickness));
            Assert.Equal(o.UvScale, w.UvScale);
            Assert.False(copy.Has<MeshComponent>(), "the box written for other programs is not imported as a model");
            Assert.Empty(copy.Children);
            Assert.Equal(original.Local.Position, copy.Local.Position);
        }
        Assert.Equal(MaterialIds.MoltonBlack, walls["Back wall"].Get<WallComponent>()!.MaterialId);
        Assert.Equal(MaterialIds.StageFloorBlack, walls["Floor"].Get<WallComponent>()!.MaterialId);
        Assert.Equal(MaterialIds.Default, walls["Ceiling"].Get<WallComponent>()!.MaterialId); // a project texture does not travel
        Assert.Empty(imported.Assets); // no wall box became an asset
        Assert.DoesNotContain(imported.Issues, i => i.Severity >= IssueSeverity.Warning);

        // exported again: the same block (the walls survive any number of round trips)
        using var again = new MemoryStream();
        MvrWriter.Write(imported.Scene, again);
        var second = MvrImporter.Import(new MemoryStream(again.ToArray()), "again.mvr");
        Assert.Equal(3, second.AllEntities().Count(e => e.Has<WallComponent>()));
    }

    [Fact]
    public void A_damaged_block_is_reported_and_the_boxes_are_imported_as_models()
    {
        var scene = StageWithWalls(out _, out _, out _, out _);
        using var ms = new MemoryStream();
        MvrWriter.Write(scene, ms);
        var document = MvrReader.Read(ms.ToArray(), "studio.mvr");
        var block = document.UserData.Single(u => u.Provider == CandelaUserData.Provider);
        var records = block.Element!.Elements("Wall").ToList();
        records[0].Value = "{ not json";
        records[1].SetAttributeValue("object", "nonsense");
        records[2].Value = """{"$type":"wall","kind":"Wall","width":-3,"height":2,"thickness":0.1}""";
        block.Element.Add(new XElement("FutureThing", "kept for newer versions"));

        var imported = MvrImporter.Import(document);
        Assert.DoesNotContain(imported.AllEntities(), e => e.Has<WallComponent>());
        Assert.Equal(3, imported.AllEntities().Count(e => e.Has<MeshComponent>()));
        var issue = Assert.Single(imported.Issues, i => i.Code == MvrIssueCodes.ValueInvalid);
        Assert.Contains("3 wall(s)", issue.Message, StringComparison.Ordinal);

        // blocks of other programs are not read
        var foreign = MvrReader.Read(ms.ToArray(), "studio.mvr");
        foreach (var u in foreign.UserData) u.Provider = "SomeOtherApp";
        Assert.DoesNotContain(MvrImporter.Import(foreign).AllEntities(), e => e.Has<WallComponent>());
    }

    [Fact]
    public void Exports_without_walls_write_no_block()
    {
        var scene = new Scene("Empty");
        scene.Add(new Entity("Group")).Add(new GroupComponent());
        using var ms = new MemoryStream();
        MvrWriter.Write(scene, ms);
        var document = MvrReader.Read(ms.ToArray(), "empty.mvr");
        Assert.DoesNotContain(document.UserData, u => u.Provider == CandelaUserData.Provider);
        Assert.Empty(CandelaUserData.ReadWalls(document));
    }
}
