using System.Numerics;
using Candela.Core.Geometry;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Engine.Tests;

/// <summary>Builds small test scenes with real library fixtures.</summary>
internal static class SceneBuilder
{
    public const string MacViper = "Martin_Professional@MAC_Viper_Profile@20230516NoMeas.gdtf";
    public const string Naja = "Ayrton@Naja@V1.0_First_Release.gdtf";
    public const string EntourHaze = "ADJ@Entour_Haze_PRO@2023-09-08_First_Release.gdtf";

    public static Entity Fixture(Scene scene, string gdtf, string? mode, Vector3 position, int universe = 0, int address = 0,
        string? name = null, int fixtureId = 0, Quaternion? rotation = null)
    {
        var entity = new Entity(name ?? Path.GetFileNameWithoutExtension(gdtf).Split('@')[1])
        {
            Local = new Transform(position, rotation ?? Quaternion.Identity, Vector3.One),
        };
        var fixture = new FixtureComponent { GdtfSpec = gdtf, GdtfMode = mode, FixtureId = fixtureId };
        if (universe > 0) fixture.Addresses.Add(new DmxAddress(universe, address));
        entity.Add(fixture);
        scene.Add(entity);
        return entity;
    }

    public static Entity Floor(Scene scene, float size = 20f, string material = MaterialIds.StageFloorBlack)
    {
        var floor = new Entity("Floor");
        floor.Add(new WallComponent { Kind = WallKind.Floor, Width = size, Height = size, Thickness = 0.1f, MaterialId = material });
        scene.Add(floor);
        return floor;
    }

    public static Entity Console(Scene scene, Vector3 position)
    {
        var console = new Entity("Console") { Local = new Transform(position) };
        console.Add(new ConsoleComponent());
        scene.Add(console);
        return console;
    }

    /// <summary>A two-node DMX cable from <paramref name="from"/>'s port to <paramref name="to"/>'s port.</summary>
    public static Entity Cable(Scene scene, Entity from, string fromPort, Entity to, string toPort, string type = "dmx-xlr5")
    {
        var entity = new Entity($"{from.Name} → {to.Name}");
        var cable = new CableComponent { CableTypeId = type, Label = "D1" };
        var a = new CableNode { Anchor = new CableAnchor(from.Id, Vector3.Zero) };
        var b = new CableNode { Anchor = new CableAnchor(to.Id, Vector3.Zero) };
        cable.Nodes.AddRange([a, b]);
        cable.Segments.Add(new CableSegment(a.Id, b.Id));
        cable.Endpoints.Add(new CableEndpoint { NodeId = a.Id, EntityId = from.Id, PortName = fromPort });
        cable.Endpoints.Add(new CableEndpoint { NodeId = b.Id, EntityId = to.Id, PortName = toPort });
        entity.Add(cable);
        scene.Add(entity);
        return entity;
    }

    /// <summary>Writes a textured unit cube as OBJ + MTL + PNG into <paramref name="folder"/> and returns the OBJ path.</summary>
    public static string WriteCubeObj(string folder, string name = "cube")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".mtl"), "newmtl painted\nKd 0.8 0.2 0.1\nmap_Kd tex.png\n");
        var png = Assets.ImageImporter.EncodePng(new ImageData
        {
            Width = 2,
            Height = 2,
            Pixels = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255],
        });
        File.WriteAllBytes(Path.Combine(folder, "tex.png"), png);
        var obj = new System.Text.StringBuilder();
        obj.AppendLine($"mtllib {name}.mtl");
        foreach (var (x, y, z) in new[] { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1) })
            obj.AppendLine($"v {x} {y} {z}");
        obj.AppendLine("vt 0 0\nvt 1 0\nvt 1 1\nvt 0 1");
        obj.AppendLine("usemtl painted");
        foreach (var (a, b, c, d) in new[] { (1, 4, 3, 2), (5, 6, 7, 8), (1, 2, 6, 5), (2, 3, 7, 6), (3, 4, 8, 7), (4, 1, 5, 8) })
            obj.AppendLine($"f {a}/1 {b}/2 {c}/3 {d}/4");
        string path = Path.Combine(folder, name + ".obj");
        File.WriteAllText(path, obj.ToString());
        return path;
    }
}
