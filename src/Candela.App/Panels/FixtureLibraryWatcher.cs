using Candela.Engine;
using Candela.Gdtf;

namespace Candela.App.Panels;

/// <summary>
/// Follows <see cref="StageEngine.FixtureLibraryChanged"/> of whichever engine the session has at the moment, so the
/// Library panel learns about GDTF files that reached the fixture library — an imported <c>.gdtf</c>, the GDTFs embedded
/// in an MVR, the GDTF copies of a project — and rebuilds its grid, category counts and hover facts. The engine raises
/// the event on the main thread; the watcher only counts it, and the panel takes the news once per frame with
/// <see cref="TakeChanged"/>, so a burst of changes costs one rebuild.
/// </summary>
internal sealed class FixtureLibraryWatcher : IDisposable
{
    private readonly Action<FixtureLibraryChange> _handler;
    private StageEngine? _engine;
    private bool _pending;

    /// <summary>Creates a watcher that follows no engine yet (see <see cref="Follow"/>).</summary>
    public FixtureLibraryWatcher() => _handler = OnChanged;

    /// <summary>The engine whose library is followed, or null.</summary>
    public StageEngine? Engine => _engine;

    /// <summary>Library changes reported since the watcher was created (diagnostics and tests).</summary>
    public int Changes { get; private set; }

    /// <summary>The most recent change, or null before the first.</summary>
    public FixtureLibraryChange? LastChange { get; private set; }

    /// <summary>
    /// Follows <paramref name="engine"/>: the previous engine's event is left and the new one's joined. Calling it every
    /// frame with the same engine does nothing. A change of engine counts as a library change (the panel rebuilds).
    /// </summary>
    public void Follow(StageEngine? engine)
    {
        if (ReferenceEquals(engine, _engine))
            return;
        if (_engine is not null)
            _engine.FixtureLibraryChanged -= _handler;
        _engine = engine;
        if (engine is not null)
            engine.FixtureLibraryChanged += _handler;
        _pending = true;
    }

    /// <summary>True once after one or more library changes (or a change of engine) since the last call.</summary>
    public bool TakeChanged()
    {
        bool changed = _pending;
        _pending = false;
        return changed;
    }

    /// <summary>
    /// The tile the Library grid shows for <paramref name="entry"/>: the grid lists the newest revision of each model,
    /// so an older revision (an imported GDTF the library already had in a newer version) selects its model's tile.
    /// </summary>
    public static FixtureLibraryEntry TileFor(FixtureLibrary? library, FixtureLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return library is not null && library.GetRevisions(entry) is { Count: > 0 } revisions ? revisions[0] : entry;
    }

    /// <summary>Leaves the followed engine's event.</summary>
    public void Dispose()
    {
        if (_engine is not null)
            _engine.FixtureLibraryChanged -= _handler;
        _engine = null;
    }

    private void OnChanged(FixtureLibraryChange change)
    {
        Changes++;
        LastChange = change;
        _pending = true;
    }
}
