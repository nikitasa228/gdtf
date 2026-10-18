namespace Candela.Core.SceneGraph;

/// <summary>
/// What kind of change a <see cref="SceneChange"/> reports. New kinds are only ever appended, so a consumer that
/// switches over the kinds it cares about keeps working when more are added.
/// </summary>
public enum SceneChangeKind
{
    /// <summary>An entity (with its subtree) joined the scene. <see cref="SceneChange.Entity"/> is the subtree root.</summary>
    Added,

    /// <summary>
    /// An entity (with its subtree) left the scene. <see cref="SceneChange.Entity"/> is the subtree root,
    /// <see cref="SceneChange.FormerParent"/> its parent before the removal.
    /// </summary>
    Removed,

    /// <summary>An entity moved under another parent or to another sibling position (<see cref="SceneChange.FormerParent"/>).</summary>
    Reparented,

    /// <summary><see cref="SceneGraph.Entity.Name"/> changed.</summary>
    Renamed,

    /// <summary>The local transform of an entity changed; the world transforms of its whole subtree changed with it.</summary>
    TransformChanged,

    /// <summary>A component was attached to <see cref="SceneChange.Entity"/>.</summary>
    ComponentAdded,

    /// <summary>A component was detached from <see cref="SceneChange.Entity"/>.</summary>
    ComponentRemoved,

    /// <summary>The data of <see cref="SceneChange.Component"/> changed (raised by <see cref="Component.NotifyChanged"/>).</summary>
    ComponentChanged,

    /// <summary>
    /// A value of <see cref="Scene.Settings"/> changed, or the settings object was replaced. <see cref="SceneChange.Entity"/>
    /// is null.
    /// </summary>
    SettingsChanged,

    /// <summary>
    /// <see cref="SceneGraph.Entity.Visible"/> changed. The effective visibility of the entity's whole subtree
    /// (<see cref="SceneGraph.Entity.IsVisibleInHierarchy"/>) changed with it.
    /// </summary>
    VisibilityChanged,

    /// <summary><see cref="SceneGraph.Entity.Locked"/> changed.</summary>
    LockedChanged,

    /// <summary>
    /// <see cref="SceneGraph.Entity.LayerId"/> changed: the entity moved to another layer
    /// (<see cref="SceneChange.FormerLayerId"/> is the layer it left).
    /// </summary>
    LayerChanged,

    /// <summary>
    /// The scene's layer list changed (a layer was added, removed, replaced or moved) or a property of one layer changed
    /// (<see cref="SceneGraph.Layer.Name"/>, <see cref="SceneGraph.Layer.Visible"/>, <see cref="SceneGraph.Layer.Locked"/>).
    /// <see cref="SceneChange.Layer"/> is the layer concerned, or null when several layers changed at once (cleared,
    /// added in bulk). <see cref="SceneChange.Entity"/> is null.
    /// </summary>
    LayersChanged,

    /// <summary><see cref="Scene.Name"/> changed. <see cref="SceneChange.Entity"/> is null.</summary>
    SceneRenamed,
}

/// <summary>
/// One change of a <see cref="Scene"/>, delivered through <see cref="Scene.Changed"/> after the change was applied
/// (every change also increments <see cref="Scene.Version"/>). <see cref="Entity"/> is null only for scene-level changes
/// (<see cref="SceneChangeKind.SettingsChanged"/>, <see cref="SceneChangeKind.LayersChanged"/>,
/// <see cref="SceneChangeKind.SceneRenamed"/>).
/// </summary>
/// <param name="Kind">What changed.</param>
/// <param name="Entity">The entity concerned (the subtree root for structural changes), or null for scene-level changes.</param>
/// <param name="Component">The component concerned by component changes, otherwise null.</param>
public sealed record SceneChange(SceneChangeKind Kind, Entity? Entity, Component? Component = null)
{
    /// <summary>Parent before the change, for <see cref="SceneChangeKind.Removed"/> and <see cref="SceneChangeKind.Reparented"/>.</summary>
    public Entity? FormerParent { get; init; }

    /// <summary>Layer id the entity was on before a <see cref="SceneChangeKind.LayerChanged"/>, otherwise null.</summary>
    public Guid? FormerLayerId { get; init; }

    /// <summary>The layer a <see cref="SceneChangeKind.LayersChanged"/> concerns (null when several changed at once).</summary>
    public Layer? Layer { get; init; }
}
