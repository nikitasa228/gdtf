using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Serialization;

namespace Candela.Core.Tests;

public class SceneTests
{
    private static bool Near(Vector3 a, Vector3 b, float eps = 1e-4f) => Vector3.Distance(a, b) < eps;

    [Fact]
    public void World_matrix_follows_parent_and_updates_after_parent_moves()
    {
        var scene = new Scene();
        var truss = scene.Add(new Entity("Truss") { Local = new Transform(new Vector3(0, 5, 6)) });
        var fixture = scene.Add(new Entity("Spot") { Local = new Transform(new Vector3(1, 0, -0.3f)) }, truss);

        Assert.True(Near(fixture.WorldPosition, new Vector3(1, 5, 5.7f)));
        truss.Local = new Transform(new Vector3(0, 5, 8));
        Assert.True(Near(fixture.WorldPosition, new Vector3(1, 5, 7.7f)));

        truss.Local = new Transform(new Vector3(0, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Vector3.One);
        Assert.True(Near(fixture.WorldPosition, new Vector3(0, 1, -0.3f)));
    }

    [Fact]
    public void Reparent_keeps_world_position()
    {
        var scene = new Scene();
        var a = scene.Add(new Entity("A") { Local = new Transform(new Vector3(10, 0, 0)) });
        var b = scene.Add(new Entity("B") { Local = new Transform(new Vector3(0, 3, 0)) });
        scene.Reparent(b, a);
        Assert.Same(a, b.Parent);
        Assert.True(Near(b.WorldPosition, new Vector3(0, 3, 0)));
        Assert.True(Near(b.Local.Position, new Vector3(-10, 3, 0)));
        Assert.Throws<InvalidOperationException>(() => scene.Reparent(a, b));
    }

    [Fact]
    public void Remove_and_undo_restore_subtree_and_index()
    {
        var scene = new Scene();
        var undo = new UndoStack();
        var first = scene.Add(new Entity("First"));
        var group = scene.Add(new Entity("Group"));
        scene.Add(new Entity("Child"), group);
        var last = scene.Add(new Entity("Last"));

        undo.Execute(new RemoveEntityCommand(scene, group));
        Assert.Equal(2, scene.Count);
        Assert.Null(scene.Find(group.Children[0].Id));

        undo.Undo();
        Assert.Equal(4, scene.Count);
        Assert.Equal(new[] { first, group, last }, scene.Roots);
        Assert.NotNull(scene.Find(group.Children[0].Id));
    }

    [Fact]
    public void Gizmo_drag_merges_into_one_undo_step()
    {
        var scene = new Scene();
        var undo = new UndoStack();
        var e = scene.Add(new Entity("E"));
        for (int i = 1; i <= 10; i++)
            undo.Execute(new SetTransformCommand(new[] { e }, new[] { new Transform(new Vector3(i, 0, 0)) }));
        undo.BreakMerge();
        undo.Execute(new SetTransformCommand(new[] { e }, new[] { new Transform(new Vector3(0, 0, 5)) }));

        undo.Undo();
        Assert.True(Near(e.WorldPosition, new Vector3(10, 0, 0)));
        undo.Undo();
        Assert.True(Near(e.WorldPosition, Vector3.Zero));
        Assert.False(undo.CanUndo);
        undo.Redo();
        Assert.True(Near(e.WorldPosition, new Vector3(10, 0, 0)));
    }

    [Fact]
    public void Modify_component_command_undoes_in_place_edit()
    {
        var scene = new Scene();
        var undo = new UndoStack();
        var e = scene.Add(new Entity("Wall"));
        var wall = e.Add(new WallComponent { Width = 4 });

        undo.Execute(ModifyComponentCommand.Create(wall, w => { w.Width = 7; w.MaterialId = MaterialIds.Concrete; }));
        Assert.Equal(7, wall.Width);
        undo.Undo();
        Assert.Equal(4, wall.Width);
        Assert.Equal(MaterialIds.PaintedWallWhite, wall.MaterialId);
        undo.Redo();
        Assert.Equal(MaterialIds.Concrete, wall.MaterialId);
    }

    [Fact]
    public void Group_executes_as_single_step()
    {
        var scene = new Scene();
        var undo = new UndoStack();
        using (undo.Group("Add two"))
        {
            undo.Execute(new AddEntityCommand(scene, new Entity("1")));
            undo.Execute(new AddEntityCommand(scene, new Entity("2")));
        }
        Assert.Equal(2, scene.Count);
        undo.Undo();
        Assert.Equal(0, scene.Count);
        Assert.Equal("Add two", undo.RedoDescription);
    }

    [Fact]
    public void Scene_json_round_trip_keeps_hierarchy_transforms_and_every_component_type()
    {
        var scene = new Scene("Test stage");
        scene.Settings.BaseHazeScattering = 0.05f;
        var truss = scene.Add(new Entity("Truss 1") { Local = new Transform(new Vector3(0, 4, 7), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f), Vector3.One) });
        truss.Add(new TrussComponent { Length = 12, Profile = "F44", MaxUniformLoadKg = 800 });
        var spot = scene.Add(new Entity("Spot 101"), truss);
        spot.Add(new FixtureComponent
        {
            GdtfSpec = "Robe_Lighting@Robin_T1_Profile@x.gdtf",
            GdtfMode = "Mode 1",
            Addresses = { new DmxAddress(2, 101) },
            FixtureId = 101,
            Color = new CieColor(0.3127f, 0.329f, 100f),
            MountedOn = truss.Id,
            ManualAttributes = { ["Pan"] = 45f, ["Dimmer"] = 0.5f },
        });
        var cable = scene.Add(new Entity("DMX 1"));
        var n1 = new CableNode { Position = new Vector3(1, 2, 3) };
        var n2 = new CableNode { Anchor = new CableAnchor(spot.Id, new Vector3(0, 0, 0.1f)) };
        cable.Add(new CableComponent
        {
            CableTypeId = "dmx-xlr5",
            Nodes = { n1, n2 },
            Segments = { new CableSegment(n1.Id, n2.Id) },
            Endpoints = { new CableEndpoint { NodeId = n2.Id, EntityId = spot.Id, PortName = "DMX In" } },
        });
        scene.Add(new Entity("Console")).Add(new ConsoleComponent());
        scene.Add(new Entity("Distro")).Add(new PowerSourceComponent());
        scene.Add(new Entity("Floor")).Add(new WallComponent { Kind = WallKind.Floor, Width = 20, Height = 15 });
        scene.Add(new Entity("Plan")).Add(new ImageComponent { Mode = ImageMode.Poster });
        scene.Add(new Entity("Model")).Add(new MeshComponent { ModelAssetId = Guid.NewGuid(), MaterialOverrides = { [0] = MaterialIds.WoodStage } });
        scene.Add(new Entity("Hoist")).Add(new RiggingPointComponent { Carries = { truss.Id } });
        scene.Add(new Entity("Atmosphere")).Add(new AtmosphereVolumeComponent());
        scene.Add(new Entity("Cam")).Add(new CameraComponent { Orthographic = true });
        scene.Add(new Entity("Folder")).Add(new GroupComponent());

        var json = SceneSerializer.ToJson(scene);
        var back = SceneSerializer.FromJson(json);

        Assert.Equal(scene.Count, back.Count);
        Assert.Equal("Test stage", back.Name);
        Assert.Equal(0.05f, back.Settings.BaseHazeScattering);
        var spot2 = back.Find(spot.Id)!;
        Assert.Equal(truss.Id, spot2.Parent!.Id);
        Assert.True(Near(spot2.WorldPosition, spot.WorldPosition));
        var fx = spot2.Get<FixtureComponent>()!;
        Assert.Equal(new DmxAddress(2, 101), fx.Addresses[0]);
        Assert.Equal(45f, fx.ManualAttributes["Pan"]);
        Assert.Equal(new CieColor(0.3127f, 0.329f, 100f), fx.Color);
        var cab = back.Find(cable.Id)!.Get<CableComponent>()!;
        Assert.Equal(spot.Id, cab.Nodes[1].Anchor!.Value.EntityId);
        Assert.Equal("DMX In", cab.Endpoints[0].PortName);
        Assert.Equal(WallKind.Floor, back.All().Single(e => e.Name == "Floor").Get<WallComponent>()!.Kind);
        Assert.Equal(SceneSerializer.ToJson(back), json);
    }

    [Fact]
    public void Dmx_absolute_address_conversion()
    {
        Assert.Equal(new DmxAddress(2, 1), DmxAddress.FromAbsolute(513));
        Assert.Equal(741, new DmxAddress(2, 229).Absolute);
        Assert.Equal(new DmxAddress(1, 512), DmxAddress.FromAbsolute(512));
    }

    [Fact]
    public void Component_clone_is_deep_and_detached()
    {
        var e = new Entity("E");
        var c = e.Add(new CableComponent { Nodes = { new CableNode { Position = Vector3.One } } });
        var copy = (CableComponent)c.Clone();
        Assert.Null(copy.Owner);
        copy.Nodes[0].Position = Vector3.Zero;
        Assert.Equal(Vector3.One, c.Nodes[0].Position);
    }

    [Fact]
    public void Aabb_ray_intersection()
    {
        var box = new Aabb(new Vector3(-1), new Vector3(1));
        Assert.Equal(4f, box.Intersect(new Ray(new Vector3(-5, 0, 0), Vector3.UnitX))!.Value, 4);
        Assert.Null(box.Intersect(new Ray(new Vector3(-5, 3, 0), Vector3.UnitX)));
    }
}
