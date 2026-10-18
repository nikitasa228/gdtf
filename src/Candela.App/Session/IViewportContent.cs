using Candela.Core.Math;

namespace Candela.App.Session;

/// <summary>
/// What the viewports show and how screen picks map back to scene entities. In this build it is the
/// <see cref="PreviewStage"/> (the renderer's procedural test scene as editable entities); with the stage engine it is
/// the engine's render synchronisation of the real scene.
/// </summary>
internal interface IViewportContent : IDisposable
{
    /// <summary>Short description shown in the viewport corner (empty = none).</summary>
    string Caption { get; }

    /// <summary>Brings scene changes (added/removed/moved entities, selection highlight) into the render world.</summary>
    void Sync();

    /// <summary>The selectable entity for a renderer pick id (null = background or not selectable).</summary>
    Guid? EntityForPick(uint pickId);

    /// <summary>World-space bounds of an entity (for framing and box selection); false when it has no geometry.</summary>
    bool TryGetBounds(Guid entityId, out Aabb bounds);

    /// <summary>Bounds of everything shown (empty when nothing is shown).</summary>
    Aabb SceneBounds { get; }
}
