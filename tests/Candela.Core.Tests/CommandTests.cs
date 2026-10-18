using System.Numerics;
using Candela.Core.Editing;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;

namespace Candela.Core.Tests;

public class CommandTests
{
    [Fact]
    public void Descriptions_are_fixed_at_creation_and_reading_them_does_not_allocate()
    {
        var scene = new Scene();
        var truss = scene.Add(new Entity("Truss"));
        var spot = scene.Add(new Entity("Spot"));
        var wall = spot.Add(new WallComponent());
        var commands = new IUndoableCommand[]
        {
            new AddEntityCommand(scene, new Entity("New")),
            new AddEntitiesCommand(scene, new[] { new Entity("P1"), new Entity("P2") }),
            new RemoveEntityCommand(scene, spot),
            new ReparentCommand(scene, spot, truss),
            new AddComponentCommand(spot, new GroupComponent()),
            new RemoveComponentCommand(spot, wall),
            new SetTransformCommand(new[] { spot }, new[] { new Transform(Vector3.One) }),
            new SetValueCommand<bool>("Hide Spot", () => spot.Visible, v => spot.Visible = v, false),
            ModifyComponentCommand.Create(wall, w => w.Width = 3),
            new DuplicateEntitiesCommand(scene, new[] { truss }),
            new CompositeCommand("Group"),
        };
        var expected = new[]
        {
            "Add New", "Add 2 objects", "Delete Spot", "Move Spot into Truss", "Add Group to Spot", "Remove Wall from Spot", "Move",
            "Hide Spot", "Edit Wall", "Duplicate Truss", "Group",
        };
        Assert.Equal(expected, commands.Select(c => c.Description));

        // Renaming afterwards does not change what the history shows for the step that was made.
        spot.Name = "Renamed";
        truss.Name = "Renamed truss";
        Assert.Equal(expected, commands.Select(c => c.Description));

        foreach (var c in commands) _ = c.Description; // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        int length = 0;
        for (int i = 0; i < 1000; i++)
            foreach (var c in commands)
                length += c.Description.Length;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(length > 0);
    }

    [Fact]
    public void Undo_stack_descriptions_do_not_allocate()
    {
        var scene = new Scene();
        var undo = new UndoStack();
        undo.Execute(new AddEntityCommand(scene, new Entity("A")));
        undo.Execute(new AddEntityCommand(scene, new Entity("B")));
        undo.Undo();
        _ = undo.UndoDescription;
        _ = undo.RedoDescription;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = undo.UndoDescription;
            _ = undo.RedoDescription;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal("Add A", undo.UndoDescription);
        Assert.Equal("Add B", undo.RedoDescription);
    }

    [Fact]
    public void Reparent_undo_restores_the_sibling_position()
    {
        var scene = new Scene();
        var a = scene.Add(new Entity("a"));
        var b = scene.Add(new Entity("b"));
        var c = scene.Add(new Entity("c"));
        var undo = new UndoStack();
        undo.Execute(new ReparentCommand(scene, b, c));
        Assert.Equal(new[] { a, c }, scene.Roots);
        undo.Undo();
        Assert.Equal(new[] { a, b, c }, scene.Roots);
        undo.Redo();
        Assert.Same(c, b.Parent);
    }

    [Fact]
    public void Add_entities_command_adds_and_removes_all_roots_as_one_step()
    {
        var scene = new Scene();
        var parent = scene.Add(new Entity("Parent"));
        var undo = new UndoStack();
        var first = new Entity("1");
        first.AddChild(new Entity("1.1"));
        var command = new AddEntitiesCommand(scene, new[] { first, new Entity("2") }, parent);
        undo.Execute(command);
        Assert.Equal(4, scene.Count);
        Assert.Equal(new[] { "1", "2" }, parent.Children.Select(e => e.Name));
        Assert.Equal(command.Entities, parent.Children);
        undo.Undo();
        Assert.Equal(1, scene.Count);
        undo.Redo();
        Assert.Equal(4, scene.Count);

        var placed = new AddEntitiesCommand(scene, new (Entity, Entity?, int)[] { (new Entity("root"), null, 0), (new Entity("child"), parent, 0) });
        placed.Do();
        Assert.Equal("root", scene.Roots[0].Name);
        Assert.Equal("child", parent.Children[0].Name);
        placed.Undo();
        Assert.Equal(4, scene.Count);
    }

    [Fact]
    public void Modify_component_undo_and_redo_are_exact_for_null_values_with_non_null_defaults()
    {
        var scene = new Scene();
        var e = scene.Add(new Entity("Console"));
        var console = e.Add(new ConsoleComponent());
        Assert.Equal("artnet", console.ProtocolId);
        var undo = new UndoStack();

        undo.Execute(ModifyComponentCommand.Create(console, c => c.ProtocolId = null));
        Assert.Null(console.ProtocolId);
        undo.Undo();
        Assert.Equal("artnet", console.ProtocolId);
        undo.Redo();
        Assert.Null(console.ProtocolId); // was "artnet" when nulls were dropped from the snapshot

        var copy = (ConsoleComponent)console.Clone();
        Assert.Null(copy.ProtocolId);
        Assert.Null(copy.Owner);
    }

    [Fact]
    public void Duplicate_command_adds_copies_next_to_originals_and_undoes_them()
    {
        var scene = new Scene();
        var group = scene.Add(new Entity("Group"));
        var truss = scene.Add(new Entity("Truss") { Local = new Transform(new Vector3(0, 4, 6)) }, group);
        truss.Add(new TrussComponent());
        var spot = scene.Add(new Entity("Spot"), truss);
        spot.Add(new FixtureComponent { MountedOn = truss.Id });
        var undo = new UndoStack();

        var command = new DuplicateEntitiesCommand(scene, new[] { truss, spot }); // spot is inside truss: copied once
        undo.Execute(command);
        var copy = Assert.Single(command.Copies);
        Assert.Equal(5, scene.Count);
        Assert.Same(group, copy.Parent);
        Assert.Equal(truss.WorldPosition, copy.WorldPosition);
        var spotCopy = Assert.Single(copy.Children);
        Assert.NotEqual(spot.Id, spotCopy.Id);
        Assert.Equal(copy.Id, spotCopy.Get<FixtureComponent>()!.MountedOn);
        Assert.Equal(truss.Id, spot.Get<FixtureComponent>()!.MountedOn); // original untouched

        undo.Undo();
        Assert.Equal(3, scene.Count);
        Assert.Null(copy.Scene);
        undo.Redo();
        Assert.Same(copy, scene.Find(copy.Id));
        Assert.Throws<ArgumentException>(() => new DuplicateEntitiesCommand(scene, new[] { new Entity("Loose") }));
    }
}
