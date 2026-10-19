using System.Numerics;
using Candela.App.Diagnostics;
using Candela.App.Session;
using Candela.App.Settings;
using Candela.App.Ui;
using Candela.Core.Math;
using Candela.Core.SceneGraph.Components;

namespace Candela.App.Tests;

/// <summary>
/// Walls and floors drawn in the editor survive File → Export → MVR and opening that MVR again: kind, size, thickness
/// and material come back as walls (Candela's user-data block of the MVR), not as imported box models.
/// </summary>
[Collection(UiStateCollection.Name)]
public sealed class MvrWallsFollowUpTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = TestLog.Create();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Walls_and_floors_come_back_from_an_exported_mvr_with_kind_size_and_material()
    {
        Loc.SetLanguage("en");
        var settings = new SettingsStore(_log, _dir.File("settings.json"));
        settings.Load();
        using var session = EngineTestSupport.CreateSession(_log, settings, _dir);
        var engine = session.Engine!;
        var floor = StageTestSupport.AddWall(engine, "Stage floor", 10f, 6f, WallKind.Floor);
        floor.Get<WallComponent>()!.MaterialId = MaterialIds.WoodStage;
        var wall = StageTestSupport.AddWall(engine, "Back wall", 12f, 4f);
        wall.Get<WallComponent>()!.MaterialId = MaterialIds.MoltonBlack;
        wall.Local = new Transform(new Vector3(0f, 5f, 0f));
        StageTestSupport.AddFixture(engine, "Spot 1", 1, new Vector3(0f, 0f, 5f));

        string mvr = _dir.File("walls");
        var exported = session.Export(mvr, ExportKind.Mvr);
        Assert.True(exported.Succeeded, exported.Message);
        var opened = session.Open(mvr + ".mvr");
        Assert.True(opened.Succeeded, opened.Message);

        var walls = session.Scene.Query<WallComponent>().ToList();
        Assert.Equal(2, walls.Count);
        var backFloor = Assert.Single(walls, w => w.Component.Kind == WallKind.Floor).Component;
        Assert.Equal(10f, backFloor.Width, 3);
        Assert.Equal(6f, backFloor.Height, 3);
        Assert.Equal(MaterialIds.WoodStage, backFloor.MaterialId);
        var (backWallEntity, backWall) = Assert.Single(walls, w => w.Component.Kind == WallKind.Wall);
        Assert.Equal(12f, backWall.Width, 3);
        Assert.Equal(4f, backWall.Height, 3);
        Assert.Equal(MaterialIds.MoltonBlack, backWall.MaterialId);
        Assert.Equal(5f, backWallEntity.WorldPosition.Y, 3);
        // No box model was imported in their place.
        Assert.Empty(session.Scene.Query<MeshComponent>());
    }
}
