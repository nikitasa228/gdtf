using Candela.Core.Math;

namespace Candela.App.Session;

/// <summary>
/// What the viewports show and how screen picks map back to scene entities: the engine's render synchronisation of the
/// real scene (<see cref="EngineViewportContent"/>), or — for diagnostics — the <see cref="PreviewStage"/> (the
/// renderer's procedural test scene as editable entities).
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

    /// <summary>
    /// Appends points whose hull is everything shown (box corners of the content, fixture parts as they hang), for
    /// framing a view tightly around the content rather than around its bounding sphere.
    /// </summary>
    void CollectFramingPoints(List<System.Numerics.Vector3> points);

    /// <summary>True once what the viewports show matches the scene (models loaded, synchronised after the last open).</summary>
    bool IsSettled { get; }
}
