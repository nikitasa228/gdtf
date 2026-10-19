using System.Numerics;
using Candela.App.Editing;
using Candela.App.Session;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using Candela.Engine;

namespace Candela.App.Tests;

/// <summary>
/// A small rig for the cabling tests on a headless engine with the repository's library: a DMX console in front of the
/// stage, a wall behind it, a truss at 5 m with three MAC Viper Profiles under it (patched 1.001, 1.035, 1.069), and a
/// three-phase distro on the floor. Positions in metres, Z up.
/// </summary>
internal sealed class CablingScene : IDisposable
{
    public CablingScene(TempDir dir)
    {
        Engine = StageTestSupport.CreateEngine(dir);
        Tools = StageTestSupport.CreateTools();
        Stage = StagePanelContext.ForEngine(Engine, Tools);
        var scene = Engine.Scene;
        Console = Add("Console 1", DeviceCatalog.Find("console-dmx-4")!.CreateComponent(), new Vector3(0f, -6f, 0f));
        Wall = new Entity("Wall 1") { Local = new Transform(new Vector3(0f, 4f, 0f)) };
        Wall.Add(new WallComponent { Kind = WallKind.Wall, Width = 12f, Height = 6f, Thickness = 0.1f });
        scene.Add(Wall);
        Truss = new Entity("Truss 1") { Local = new Transform(new Vector3(-4f, 0f, 5.2f)) };
        Truss.Add(new TrussComponent { Length = 8f, Profile = "F34" });
        scene.Add(Truss);
        Spots = new[]
        {
            StageTestSupport.AddFixture(Engine, "Spot 1", 1, new Vector3(-2f, 0f, 5f), 1, 1),
            StageTestSupport.AddFixture(Engine, "Spot 2", 2, new Vector3(0f, 0f, 5f), 1, 35),
            StageTestSupport.AddFixture(Engine, "Spot 3", 3, new Vector3(2f, 0f, 5f), 1, 69),
        };
        Distro = Add("Distro 1", DeviceCatalog.Find("distro-32-true1")!.CreateComponent(), new Vector3(6f, 0f, 0f));
        Tool = new CableTool(Stage);
        Engine.Undo.Clear();
    }

    public StageEngine Engine { get; }

    public StageToolState Tools { get; }

    public StagePanelContext Stage { get; }

    public CableTool Tool { get; }

    public Entity Console { get; }

    public Entity Wall { get; }

    public Entity Truss { get; }

    public Entity[] Spots { get; }

    public Entity Distro { get; }

    /// <summary>Adds an entity with <paramref name="component"/> at <paramref name="position"/>.</summary>
    public Entity Add(string name, Component component, Vector3 position)
    {
        var e = new Entity(name) { Local = new Transform(position) };
        e.Add(component);
        Engine.Scene.Add(e);
        return e;
    }

    /// <summary>The device target of an entity (its origin).</summary>
    public static CableTarget DeviceAt(Entity e) => CableTarget.Device(e.Id, e.WorldPosition);

    /// <summary>A point on the ground (free).</summary>
    public static CableTarget Ground(float x, float y) => CableTarget.Surface(new Vector3(x, y, 0f), Vector3.UnitZ, Guid.Empty);

    /// <summary>A point on the front face of the wall (anchored to it).</summary>
    public CableTarget OnWall(float x, float z) => CableTarget.Surface(new Vector3(x, 3.95f, z), -Vector3.UnitY, Wall.Id);

    /// <summary>A point under the truss (anchored to it).</summary>
    public CableTarget OnTruss(float x) => CableTarget.Surface(new Vector3(x, 0f, 5.05f), -Vector3.UnitZ, Truss.Id);

    /// <summary>The cables of the scene in creation order.</summary>
    public List<(Entity Entity, CableComponent Cable)> Cables() => Engine.Scene.Query<CableComponent>().ToList();

    public void Dispose()
    {
        Tools.Dispose();
        Engine.Dispose();
    }
}
