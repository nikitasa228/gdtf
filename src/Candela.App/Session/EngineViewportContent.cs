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
/// fixture. World bounds come from the <see cref="TrackedRenderWorld"/> between the binding and the renderer, so box
/// selection and framing use what is really drawn; entities whose models are still loading fall back to the extents the
/// engine knows from the components (<see cref="EngineSceneBounds"/>). Children of a selected group are outlined too.
/// </summary>
internal sealed class EngineViewportContent : IViewportContent
{
    private readonly StageEngine _engine;
    private readonly TrackedRenderWorld _world;
    private readonly RenderSync _sync;
    private readonly HashSet<uint> _inherited = new();
    private readonly Stack<Entity> _stack = new();
    private readonly Action _onSelectionChanged;
    private readonly Action<Scene> _onSceneReplaced;
    private readonly Func<Guid, Aabb?> _modelBounds;
    private bool _selectionDirty = true;
    private bool _tickedSinceReplace;
    private long _inheritedVersion = -1;
    private int _inheritedInstances = -1;
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
        _world = new TrackedRenderWorld(renderer.World);
        _modelBounds = id => _engine.Models.GetBounds(id);
        _sync = engine.AttachRenderWorld(_world, loadFixtureModelsInBackground);
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

    /// <summary>The bounds-tracking world between the binding and the renderer.</summary>
    public TrackedRenderWorld World => _world;

    /// <summary>Fixture types whose 3D models are still loading in the background.</summary>
    public int PendingFixtureTypes => _sync.PendingFixtureTypes;

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
        // Edits made by the UI after this frame's engine tick (gizmo drags, new walls, drops) reach the render world
        // before the viewports render, so a drag never lags a frame behind the mouse.
        long version = _engine.Scene.Version;
        if (version != _syncedVersion || _selectionDirty)
        {
            _syncedVersion = version;
            _sync.Update();
        }

        UpdateInheritedSelection();
    }

    /// <summary>Marks the render world as up to date after the engine's tick (no second update this frame).</summary>
    public void MarkTicked()
    {
        _syncedVersion = _engine.Scene.Version;
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
            if (!_world.IncludeBounds(_sync.PickIdOf(e.Id), ref bounds))
            {
                // Not drawn (yet): a fixture whose model is loading, an undecodable model — the component extents.
                var own = EngineSceneBounds.Of(e, _modelBounds);
                if (!own.IsEmpty && e.Components.Count > 0 && !e.Has<GroupComponent>())
                    bounds.Include(own);
            }
        }

        return !bounds.IsEmpty;
    }

    /// <inheritdoc/>
    public Aabb SceneBounds
    {
        get
        {
            var bounds = _world.VisibleBounds();
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
        _world.CollectVisibleCorners(points);
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

    /// <summary>
    /// Outlines the descendants of selected entities (a selected group shows its members): recomputed when the selection,
    /// the scene or the set of drawn instances changed (models finishing their background load get new instances).
    /// </summary>
    private void UpdateInheritedSelection()
    {
        long version = _engine.Scene.Version;
        int instances = _world.InstanceCount;
        if (!_selectionDirty && version == _inheritedVersion && instances == _inheritedInstances)
            return;
        _selectionDirty = false;
        _inheritedVersion = version;
        _inheritedInstances = instances;
        _inherited.Clear();
        var scene = _engine.Scene;
        var ids = _engine.Selection.Ids;
        for (int i = 0; i < ids.Count; i++)
        {
            if (scene.Find(ids[i]) is not { } selected)
                continue;
            _stack.Clear();
            for (int c = 0; c < selected.Children.Count; c++)
                _stack.Push(selected.Children[c]);
            while (_stack.Count > 0)
            {
                var e = _stack.Pop();
                uint pick = _sync.PickIdOf(e.Id);
                if (pick != 0)
                    _inherited.Add(pick);
                for (int c = 0; c < e.Children.Count; c++)
                    _stack.Push(e.Children[c]);
            }
        }

        _world.SetInheritedSelection(_inherited);
    }
}
