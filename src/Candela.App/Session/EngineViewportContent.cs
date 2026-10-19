using System.Globalization;
using System.Numerics;
using Candela.App.Ui;
using Candela.Core.Math;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Engine;
using Candela.Rendering;
using EngineSceneBounds = Candela.Engine.SceneBounds;

namespace Candela.App.Session;

/// <summary>
/// What the viewports show with the stage engine: the engine's real scene, kept in one shared render world by the
/// engine's <see cref="RenderSync"/> (fixtures in their DMX pose, walls, floors, models, images, trusses, cables, lights,
/// haze). Every viewport renders that world into its own render target. Picks map back to entities through the binding's
/// pick ids — all parts of a fixture carry the fixture entity's id, so clicking a moving head's lens selects the
/// fixture. World bounds come from the binding (<see cref="RenderSync.TryGetWorldBounds(Guid, out Aabb, bool)"/>,
/// <see cref="RenderSync.VisibleWorldBounds"/>, <see cref="RenderSync.CollectVisibleCorners"/>), so box selection,
/// framing and drop targets use what is really drawn; entities whose models are still loading fall back to the extents
/// the engine knows from the components (<see cref="EngineSceneBounds"/>). The binding outlines the members of a selected
/// group (<see cref="RenderSync.OutlineSelectedDescendants"/>) and adds the work light of the project's view
/// (<see cref="ViewSettings.WorkLight"/>) unless a viewport measures stage light with the lux overlay.
/// </summary>
internal sealed class EngineViewportContent : IViewportContent
{
    private readonly StageEngine _engine;
    private readonly RenderSync _sync;
    private readonly Stack<Entity> _stack = new();
    private readonly Action _onSelectionChanged;
    private readonly Action<Scene> _onSceneReplaced;
    private readonly Func<Guid, Aabb?> _modelBounds;
    private bool _selectionDirty = true;
    private bool _tickedSinceReplace;
    private long _syncedVersion = -1;
    private int _captionPending = -1;
    private string _caption = "";
    private string _captionLanguage = "";
    private bool _disposed;

    /// <summary>
    /// Attaches <paramref name="renderer"/>'s world to <paramref name="engine"/> (replacing an earlier binding).
    /// <paramref name="loadFixtureModelsInBackground"/> keeps opening a large rig from stalling frames (false for tests
    /// that need every model at once).
    /// </summary>
    public EngineViewportContent(StageEngine engine, IRenderer renderer, bool loadFixtureModelsInBackground = true)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(renderer);
        _modelBounds = id => _engine.Models.GetBounds(id);
        _sync = engine.AttachRenderWorld(renderer.World, loadFixtureModelsInBackground);
        _sync.OutlineSelectedDescendants = true;
        _sync.WorkLight = EffectiveWorkLight();
        _onSelectionChanged = () => _selectionDirty = true;
        _onSceneReplaced = _ =>
        {
            _selectionDirty = true;
            _tickedSinceReplace = false;
        };
        engine.Selection.Changed += _onSelectionChanged;
        engine.SceneReplaced += _onSceneReplaced;
        _syncedVersion = engine.Scene.Version;
    }

    /// <summary>The engine's render binding.</summary>
    public RenderSync Binding => _sync;

    /// <summary>Fixture types whose 3D models are still loading in the background.</summary>
    public int PendingFixtureTypes => _sync.PendingFixtureTypes;

    /// <summary>
    /// True while a viewport shows the lux overlay: the work light is left out, since the overlay measures stage light
    /// and every viewport shares one light list. Set by the workspace before each frame's <see cref="Sync"/>.
    /// </summary>
    public bool LuxOverlayShown { get; set; }

    /// <summary>
    /// The work light a final render asks for while it runs (it decides alone: renders get no work light unless asked),
    /// or null to follow the project's view (<see cref="ViewSettings.WorkLight"/>).
    /// </summary>
    public WorkLightMode? WorkLightOverride { get; set; }

    /// <summary>The work light mode the binding gets now: the render's, or the view's (off under the lux overlay).</summary>
    public WorkLightMode EffectiveWorkLight() =>
        WorkLightOverride ?? (LuxOverlayShown ? WorkLightMode.Off : _engine.View.WorkLight);

    /// <inheritdoc/>
    public string Caption
    {
        get
        {
            int pending = _sync.PendingFixtureTypes;
            if (pending != _captionPending || !string.Equals(_captionLanguage, Loc.Language, StringComparison.Ordinal))
            {
                _captionPending = pending;
                _captionLanguage = Loc.Language;
                _caption = pending > 0 ? Loc.Format(Str.ViewportLoadingModels, pending.ToString(CultureInfo.InvariantCulture)) : "";
            }

            return _caption;
        }
    }

    /// <inheritdoc/>
    public void Sync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_engine.Render, _sync))
            return;
        // The work light follows the view setting (and the lux overlay) at once, not only at the next engine tick.
        var workLight = EffectiveWorkLight();
        bool lightsChanged = _sync.WorkLight != workLight;
        if (lightsChanged)
            _sync.WorkLight = workLight;
        // Edits made by the UI after this frame's engine tick (gizmo drags, new walls, drops) reach the render world
        // before the viewports render, so a drag never lags a frame behind the mouse.
        long version = _engine.Scene.Version;
        if (version != _syncedVersion || _selectionDirty || lightsChanged)
        {
            _syncedVersion = version;
            _selectionDirty = false;
            _sync.Update();
        }
    }

    /// <summary>Marks the render world as up to date after the engine's tick (no second update this frame).</summary>
    public void MarkTicked()
    {
        _syncedVersion = _engine.Scene.Version;
        _selectionDirty = false;
        _tickedSinceReplace = true;
    }

    /// <inheritdoc/>
    public bool IsSettled => _tickedSinceReplace && _sync.PendingFixtureTypes == 0;

    /// <inheritdoc/>
    public Guid? EntityForPick(uint pickId)
    {
        if (!_sync.TryGetEntity(pickId, out var id))
            return null;
        var entity = _engine.Scene.Find(id);
        return entity is null || entity.Locked ? null : id;
    }

    /// <summary>The entity drawn with a pick id, also when it is locked (drop targets, wall handles).</summary>
    public Entity? EntityOfPick(uint pickId) => _sync.TryGetEntity(pickId, out var id) ? _engine.Scene.Find(id) : null;

    /// <inheritdoc/>
    public bool TryGetBounds(Guid entityId, out Aabb bounds)
    {
        bounds = Aabb.Empty;
        var root = _engine.Scene.Find(entityId);
        if (root is null)
            return false;
        _stack.Clear();
        _stack.Push(root);
        while (_stack.Count > 0)
        {
            var e = _stack.Pop();
            for (int i = 0; i < e.Children.Count; i++)
                _stack.Push(e.Children[i]);
            if (_sync.TryGetWorldBounds(e.Id, out var drawn))
            {
                bounds.Include(drawn);
                continue;
            }

            // Not drawn (yet): a fixture whose model is loading, an undecodable model — the component extents.
            var own = EngineSceneBounds.Of(e, _modelBounds);
            if (!own.IsEmpty && e.Components.Count > 0 && !e.Has<GroupComponent>())
                bounds.Include(own);
        }

        return !bounds.IsEmpty;
    }

    /// <inheritdoc/>
    public Aabb SceneBounds
    {
        get
        {
            var bounds = _sync.VisibleWorldBounds();
            bounds.Include(EngineSceneBounds.Compute(_engine.Scene, _modelBounds));
            return bounds;
        }
    }

    /// <inheritdoc/>
    public void CollectFramingPoints(List<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        EngineSceneBounds.CollectContentCorners(_engine.Scene, points, _modelBounds);
        EngineSceneBounds.CollectFixtureParts(_engine.Poses, points);
        // What is really drawn (fixture models in their pose, decoded models) — the component boxes above cover what
        // is still loading.
        _sync.CollectVisibleCorners(points);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _engine.Selection.Changed -= _onSelectionChanged;
        _engine.SceneReplaced -= _onSceneReplaced;
        if (ReferenceEquals(_engine.Render, _sync))
            _engine.DetachRenderWorld();
    }
}
