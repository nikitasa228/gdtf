using System.Numerics;
using Candela.App.Ui;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Electrical;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Editing;

/// <summary>A node of a cable: the cable entity and the node id (cable nodes are not entities of their own).</summary>
/// <param name="CableId">The cable entity.</param>
/// <param name="NodeId">The node (<see cref="CableNode.Id"/>).</param>
internal readonly record struct CableNodeRef(Guid CableId, Guid NodeId);

/// <summary>
/// Editing single cable nodes (requirement: "точки кабелей можно передвигать как обычные объекты по трём осям стрелками
/// или перетягиванием"): their world position (an anchored node follows its entity at its anchor offset; a free node
/// sits in the cable's space), moving one live while a gizmo or mouse drag is under way and committing the whole drag as
/// one undo step, and deleting a node (a route point between two segments joins them; an end point goes with its
/// segment and plug; a branching node stays).
/// </summary>
internal static class CableNodeEdits
{
    /// <summary>The cable component and node of <paramref name="node"/>, or null when either is gone.</summary>
    public static (Entity Entity, CableComponent Cable, CableNode Node)? Resolve(SceneModel scene, CableNodeRef node)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Find(node.CableId) is not { } entity || entity.Get<CableComponent>() is not { } cable || cable.FindNode(node.NodeId) is not { } n)
            return null;
        return (entity, cable, n);
    }

    /// <summary>World position of a node in metres (anchors resolved).</summary>
    public static Vector3 WorldPosition(SceneModel scene, Entity cableEntity, CableNode node) =>
        CableGeometry.NodeWorldPosition(scene, cableEntity.WorldMatrix, node, out _);

    /// <summary>
    /// Puts a node at <paramref name="world"/> without an undo step (live drag): an anchored node keeps its anchor and
    /// gets the offset in its entity's space, a free node the position in the cable's space.
    /// </summary>
    public static void SetWorldPosition(SceneModel scene, Entity cableEntity, CableComponent cable, CableNode node, Vector3 world)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(cableEntity);
        ArgumentNullException.ThrowIfNull(cable);
        ArgumentNullException.ThrowIfNull(node);
        if (!float.IsFinite(world.X) || !float.IsFinite(world.Y) || !float.IsFinite(world.Z))
            return;
        Apply(scene, cableEntity, node, world);
        cable.NotifyChanged();
    }

    /// <summary>The state of a node, to restore before committing a drag (<see cref="CommitMove"/>).</summary>
    public static (Vector3 Position, CableAnchor? Anchor) Capture(CableNode node) => (node.Position, node.Anchor);

    /// <summary>
    /// Commits a drag as one undo step: the node goes back to <paramref name="before"/> and a command moves it to its
    /// current place. Returns false when the node did not move.
    /// </summary>
    public static bool CommitMove(UndoStack undo, CableComponent cable, CableNode node, (Vector3 Position, CableAnchor? Anchor) before, bool breakMerge = true)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(cable);
        ArgumentNullException.ThrowIfNull(node);
        var after = (node.Position, node.Anchor);
        if (after == before)
            return false;
        node.Position = before.Position;
        node.Anchor = before.Anchor;
        Guid id = node.Id;
        undo.Execute(ModifyComponentCommand.Create(cable, c =>
        {
            if (c.FindNode(id) is { } n)
            {
                n.Position = after.Position;
                n.Anchor = after.Anchor;
            }
        }, CableLoc.English_(CableStr.NodeMoved)));
        if (breakMerge)
            undo.BreakMerge();
        return true;
    }

    /// <summary>
    /// Moves a node to <paramref name="world"/> as one undo step (numeric entry, tests); with
    /// <paramref name="breakMerge"/> false consecutive moves of the node merge into one step (a drag on a field).
    /// </summary>
    public static bool Move(SceneModel scene, UndoStack undo, CableNodeRef reference, Vector3 world, bool breakMerge = true)
    {
        if (Resolve(scene, reference) is not var (entity, cable, node))
            return false;
        var before = Capture(node);
        Apply(scene, entity, node, world);
        return CommitMove(undo, cable, node, before, breakMerge);
    }

    /// <summary>
    /// Deletes a node (one undo step): a route point between two segments joins its neighbours, an end point is removed
    /// with its segment and its plug. A node where the cable branches (three or more segments) is kept (false). A cable
    /// left with fewer than two nodes is deleted.
    /// </summary>
    public static bool Delete(SceneModel scene, UndoStack undo, CableNodeRef reference)
    {
        ArgumentNullException.ThrowIfNull(undo);
        if (Resolve(scene, reference) is not var (entity, cable, _))
            return false;
        Guid id = reference.NodeId;
        int degree = 0;
        foreach (var s in cable.Segments)
        {
            if (s.FromNode == id || s.ToNode == id)
                degree++;
        }

        if (degree > 2)
            return false;
        int remaining = cable.Nodes.Count(n => n is not null && n.Id != id);
        if (remaining < 2)
        {
            undo.Execute(new RemoveEntityCommand(scene, entity));
            return true;
        }

        undo.Execute(ModifyComponentCommand.Create(cable, c =>
        {
            var neighbours = new List<Guid>(2);
            for (int i = c.Segments.Count - 1; i >= 0; i--)
            {
                var s = c.Segments[i];
                if (s.FromNode != id && s.ToNode != id)
                    continue;
                neighbours.Add(s.FromNode == id ? s.ToNode : s.FromNode);
                c.Segments.RemoveAt(i);
            }

            if (neighbours.Count == 2)
                c.Segments.Add(new CableSegment(neighbours[1], neighbours[0]));
            c.Nodes.RemoveAll(n => n is not null && n.Id == id);
            c.Endpoints.RemoveAll(e => e is not null && e.NodeId == id);
        }, CableLoc.English_(CableStr.NodeDeleted)));
        undo.BreakMerge();
        return true;
    }

    private static void Apply(SceneModel scene, Entity cableEntity, CableNode node, Vector3 world)
    {
        if (node.Anchor is { } anchor && scene.Find(anchor.EntityId) is { } target && Matrix4x4.Invert(target.WorldMatrix, out var toAnchor))
        {
            node.Anchor = anchor with { LocalOffset = Vector3.Transform(world, toAnchor) };
            // The stored position is the fallback for a missing anchor: keep it at the same place.
            if (Matrix4x4.Invert(cableEntity.WorldMatrix, out var toCableFallback))
                node.Position = Vector3.Transform(world, toCableFallback);
            return;
        }

        if (Matrix4x4.Invert(cableEntity.WorldMatrix, out var toCable))
            node.Position = Vector3.Transform(world, toCable);
    }
}
