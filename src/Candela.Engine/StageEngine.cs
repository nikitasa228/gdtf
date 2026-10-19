using Candela.Assets;
using Candela.Core.Diagnostics;
using Candela.Core.Editing;
using Candela.Core.SceneGraph;
using Candela.Core.SceneGraph.Components;
using Candela.Core.Settings;
using Candela.Dmx;
using Candela.Dmx.Input;
using Candela.Electrical;
using Candela.Gdtf;
using Candela.Project;
using Candela.Protocols;
using Candela.Rendering;

namespace Candela.Engine;

/// <summary>
/// The running world: one open scene with everything that brings it to life — the fixture library and researched specs,
/// the project's assets, protocol input (Art-Net, sACN …) into the DMX input hub, DMX evaluation with motor dynamics,
/// smoke, fire and sound simulation, the cable/power/rigging solvers, the issue list, undo and selection. The editor
/// and the command-line tool are thin layers over it; everything works without a window.
/// </summary>
/// <remarks>
/// <para><b>Frame.</b> <see cref="Tick"/> advances the world by one frame: protocol input → <see cref="DmxEngine"/>
/// (console-driven fixtures from the data network) → fixture poses → device outputs into the simulations → simulation
/// step → light list → <see cref="RenderSync"/>. The result depends only on the scene, the DMX received and the
/// sequence of frame times.</para>
/// <para><b>Files.</b> <see cref="New"/>, <see cref="Open"/>, <see cref="Save"/>, <see cref="SaveAs"/>,
/// <see cref="Import"/> and <see cref="ExportMvr"/> return an <see cref="EngineResult"/>; content problems never throw.</para>
/// <para><b>Threading.</b> Main thread only, like the scene (protocol receivers write the hub from their own threads).</para>
/// </remarks>
public sealed partial class StageEngine : IDisposable
{
    private readonly StageLightCollector _lights = new();
    private readonly List<IIssueProvider> _providers = new();
    private readonly IReadOnlyList<Issue> _libraryIssues = Array.Empty<Issue>();
    private ProjectData _project;
    private Scene _scene;
    private readonly CableAnchorIndex _cableAnchors = new();
    private readonly HashSet<Entity> _movedForNetwork = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Guid> _movedCables = new();
    private NetworkSolution? _network;
    private bool _networkDirty = true;
    private bool _hasConsoles;
    private bool _disposed;
    private RenderSync? _render;
    private EngineSettings _settings = new();
    private ViewSettings _view = new();

    /// <summary>
    /// Creates an engine with an empty scene. The fixture library is scanned from <see cref="StageEngineOptions.LibraryFolders"/>
    /// (or taken from <see cref="StageEngineOptions.Library"/>); problems with the library are reported in
    /// <see cref="Issues"/>, never thrown.
    /// </summary>
    public StageEngine(StageEngineOptions? options = null)
    {
        Options = options ?? new StageEngineOptions();
        CacheDirectory = Options.CacheDirectory ?? ConfigPaths.CacheDir;
        Issues = new IssueLog();
        Library = Options.Library ?? LoadLibrary(Options, out _libraryIssues);
        Profiles = new FixtureProfileCache(Library);
        _project = new ProjectData(new Scene());
        _scene = _project.Scene;
        Models = new EngineModels(() => _project.Assets, CacheDirectory, () => ImportWorker.FindWorker(Options.ImportWorkerPath),
            Options.IsolateNativeImports, Options.ImportWorkerTimeout);
        Images = new EngineImageSource(() => _project.Assets, Models, () => Library);
        Input = new DmxInputHub();
        Protocols = new ProtocolManager(Input);
        ElectricalProfiles = new LibraryFixtureProfileProvider(Library);
        Network = new NetworkSolver(ElectricalProfiles);
        Rigging = new RiggingAnalyzer(ElectricalProfiles);
        Simulation = new StageSimulation(Options.SimulationParallelism);
        Poses = new FixturePoses();
        Undo = new UndoStack();
        Selection = new Selection();
        LightMeter = new LightMeterService(this);
        Recorder = new DmxRecorder(Input);

        _providers.Add(new PatchIssueProvider(Profiles) { Substitutions = SubstitutionOf });
        _providers.Add(new ElectricalIssueProvider(Network));
        _providers.Add(new RiggingIssueProvider(Rigging));
        _providers.Add(new EngineIssueProvider(this));
        foreach (var p in _providers) Issues.Register(p);

        Dmx = CreateDmx(_scene);
        _scene.Changed += OnSceneChanged;
        _project.Assets.AssetChanged += OnAssetChanged;
        foreach (var issue in _libraryIssues) Issues.Report(issue);
    }

    /// <summary>An engine set up from the user's settings (see <see cref="StageEngineOptions.FromSettings"/>).</summary>
    public static StageEngine FromSettings(AppSettings settings) => new(StageEngineOptions.FromSettings(settings));

    /// <summary>How the engine was set up.</summary>
    public StageEngineOptions Options { get; }

    /// <summary>Folder of the engine's caches (decoded models, extracted GDTF files).</summary>
    public string CacheDirectory { get; }

    /// <summary>The open scene. Replaced by <see cref="New"/> and <see cref="Open"/> (see <see cref="SceneReplaced"/>).</summary>
    public Scene Scene => _scene;

    /// <summary>Raised after the scene was replaced (new, opened); views rebind to <see cref="Scene"/>.</summary>
    public event Action<Scene>? SceneReplaced;

    /// <summary>The fixture library (GDTF files of the library folders, plus GDTFs brought in by projects and MVR files).</summary>
    public FixtureLibrary Library { get; }

    /// <summary>Researched physical data of the library's fixtures (<c>_specs/fixtures_index.json</c>).</summary>
    public FixtureSpecsIndex Specs => Library.Specs;

    /// <summary>Compiled DMX profiles (GDTF type + mode) shared by every fixture of a type.</summary>
    public FixtureProfileCache Profiles { get; }

    /// <summary>Imported models, images, textures and GDTF copies of the project (replaced with the project).</summary>
    public AssetStore Assets => _project.Assets;

    /// <summary>
    /// The open project: scene, assets, settings sections, GDTF copies and thumbnail, as <c>Candela.Project</c> saves it.
    /// Replaced by <see cref="New"/> and <see cref="Open"/>.
    /// </summary>
    public ProjectData Project => _project;

    /// <summary>Decoded models of the model assets (native formats decoded in the isolated import worker).</summary>
    public EngineModels Models { get; }

    /// <summary>Image source for every texture key of the scene's materials (fixtures, assets, built-in materials).</summary>
    public EngineImageSource Images { get; }

    /// <summary>Merged DMX input from every protocol (and from recordings played back).</summary>
    public DmxInputHub Input { get; }

    /// <summary>The external protocols (Art-Net, sACN, OSC …) feeding <see cref="Input"/>; none runs until started.</summary>
    public ProtocolManager Protocols { get; }

    /// <summary>DMX evaluation of every fixture of the scene (recreated with the scene).</summary>
    public DmxEngine Dmx { get; private set; }

    /// <summary>World matrices of every fixture part for the current DMX state.</summary>
    public FixturePoses Poses { get; }

    /// <summary>Smoke, fire and sound.</summary>
    public StageSimulation Simulation { get; }

    /// <summary>Electrical data of fixtures (ports, power, weight) for the solvers and reports.</summary>
    public LibraryFixtureProfileProvider ElectricalProfiles { get; }

    /// <summary>Data and power network solver (console-driven fixtures, circuits).</summary>
    public NetworkSolver Network { get; }

    /// <summary>Truss and rigging-point loads.</summary>
    public RiggingAnalyzer Rigging { get; }

    /// <summary>Issues of the scene (patch, electrical, rigging, assets, simulation) and of the last operations.</summary>
    public IssueLog Issues { get; }

    /// <summary>Undo history of the open scene.</summary>
    public UndoStack Undo { get; }

    /// <summary>Selected entities.</summary>
    public Selection Selection { get; }

    /// <summary>
    /// What is simulated and how (stored with the project's <c>engine</c> section). Assigning another object counts as
    /// an unsaved change; after changing the object in place call <see cref="MarkSettingsChanged"/>.
    /// </summary>
    public EngineSettings Settings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_settings, value)) return;
            _settings = value;
            MarkSettingsChanged();
        }
    }

    /// <summary>
    /// View settings stored with the project's <c>view</c> section (preview quality, final-render settings, shading,
    /// overlay and its legends, work light, camera). Assigning another object counts as an unsaved change; the editor
    /// changes the object in place and then calls <see cref="MarkViewSettingsChanged"/>.
    /// </summary>
    public ViewSettings View
    {
        get => _view;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_view, value)) return;
            _view = value;
            MarkViewSettingsChanged();
        }
    }

    /// <summary>
    /// Counts the changes of <see cref="Settings"/> and <see cref="View"/> (assignments and the <c>Mark…Changed</c>
    /// calls); views compare it to notice a change without comparing the settings themselves.
    /// </summary>
    public long SettingsVersion { get; private set; }

    /// <summary>
    /// Records that <see cref="View"/> was changed in place (Preview/Render panel, overlay legend, work light, saved
    /// camera …): the project has unsaved changes (<see cref="IsModified"/>) and the next autosave stores them.
    /// </summary>
    public void MarkViewSettingsChanged()
    {
        IsModified = true;
        SettingsVersion++;
    }

    /// <summary>
    /// Records that <see cref="Settings"/> was changed in place (simulation switches, motor dynamics …): the project has
    /// unsaved changes and the next autosave stores them.
    /// </summary>
    public void MarkSettingsChanged()
    {
        IsModified = true;
        SettingsVersion++;
    }

    /// <summary>Illuminance queries and the light-plot report.</summary>
    public LightMeterService LightMeter { get; }

    /// <summary>Records the live DMX input to a file.</summary>
    public DmxRecorder Recorder { get; }

    /// <summary>The recording being played into <see cref="Input"/>, or null.</summary>
    public DmxPlayer? Player { get; private set; }

    /// <summary>Engine time, s (advanced by <see cref="Tick"/>; 0 after New/Open).</summary>
    public double Time { get; private set; }

    /// <summary>Number of <see cref="Tick"/> calls since the scene was created or opened.</summary>
    public long Frame { get; private set; }

    /// <summary>The photometric lights of the last frame (one per lit beam, two per flame sample).</summary>
    public ReadOnlySpan<SpotLight> Lights => _lights.Lights;

    /// <summary>The light collector (light counts of the last frame).</summary>
    public StageLightCollector LightCollector => _lights;

    /// <summary>The renderer binding, or null when no render world is attached.</summary>
    public RenderSync? Render => _render;

    /// <summary>True when the scene contains a lighting console (fixtures are then console-driven only when cabled to one).</summary>
    public bool HasConsoles
    {
        get
        {
            UpdateNetwork();
            return _hasConsoles;
        }
    }

    /// <summary>
    /// The data/power network of the scene as it is now: solved again after structural edits (cables, consoles,
    /// fixtures, power sources, entities added, removed, renamed or reparented), and renewed when entities that cable
    /// nodes are anchored to — or cable entities themselves — moved, so cable geometry, lengths, weights, run lengths and
    /// the length checks always describe the current positions. A move recomputes only the cables that moved with it
    /// (<see cref="NetworkSolver.UpdateGeometry"/>), when the solution is next read; moving anything else keeps the
    /// solution as it is.
    /// </summary>
    public NetworkSolution NetworkSolution
    {
        get
        {
            UpdateNetwork();
            if (_network is null)
            {
                _movedForNetwork.Clear();
                _network = Network.GetOrSolve(_scene);
            }
            else
            {
                ApplyCableMoves(_network);
            }
            return _network;
        }
    }

    /// <summary>
    /// Advances the world by <paramref name="deltaTime"/> seconds (≤ 0 or non-finite: re-evaluates without advancing time).
    /// </summary>
    public void Tick(double deltaTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double dt = double.IsFinite(deltaTime) && deltaTime > 0 ? deltaTime : 0;
        Time += dt;
        Frame++;

        // the recorder captures the hub's input at packet rate on the receive threads; the frame time it is given here is
        // the earliest time of what arrives from now on (playback submitted below included)
        if (Recorder.IsRecording) Recorder.Capture(Time);
        Player?.PlayTo(Time);
        _autosave?.Update();

        UpdateNetwork();
        Dmx.ApplyDynamics = Settings.MotorDynamics;
        Dmx.Update(Time);
        Poses.Update(_scene, Dmx);
        Simulation.Step(dt, _scene, Poses, Settings, Settings.SimulationPaused);
        _lights.Collect(Poses, Simulation.Fire.LightSamples, _render?.GoboResolver);
        LightMeter.Invalidate();
        _render?.Update();
    }

    /// <summary>Re-runs every issue provider for the current scene (call after edits; not every frame).</summary>
    public void RefreshIssues()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Issues.Refresh(_scene);
    }

    /// <summary>
    /// Connects a renderer's world: from now on <see cref="Tick"/> keeps it in sync with the scene. Replaces a previous
    /// binding (whose renderer objects are destroyed). <paramref name="loadFixtureModelsInBackground"/> sets
    /// <see cref="RenderSync.LoadFixtureModelsInBackground"/> and <see cref="RenderSync.LoadModelsInBackground"/> before
    /// the first update (false for the CLI, tests and thumbnails, which need every fixture and imported model at once).
    /// </summary>
    public RenderSync AttachRenderWorld(IRenderWorld world, bool loadFixtureModelsInBackground = true)
    {
        ArgumentNullException.ThrowIfNull(world);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _render?.Dispose();
        _render = new RenderSync(this, world)
        {
            LoadFixtureModelsInBackground = loadFixtureModelsInBackground,
            LoadModelsInBackground = loadFixtureModelsInBackground,
        };
        _render.Update();
        return _render;
    }

    /// <summary>Disconnects the render world and destroys everything the engine created in it.</summary>
    public void DetachRenderWorld()
    {
        _render?.Dispose();
        _render = null;
    }

    /// <summary>
    /// Starts the given protocols (<see cref="ProtocolIds"/>) with the engine's network settings, subscribing to the
    /// universes the scene's patch uses (at least universe 1). Returns the protocols' status.
    /// </summary>
    public IReadOnlyList<ProtocolStatus> StartProtocols(IEnumerable<string> protocolIds, ProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(protocolIds);
        var universes = Dmx.BuildPatch().Universes;
        var o = (options ?? new ProtocolOptions
        {
            NetworkInterface = Options.NetworkInterface,
            ArtNetUniverseOffset = Options.ArtNetUniverseOffset,
        }) with
        {
            SubscribedUniverses = universes.Count > 0 ? universes : ProtocolOptions.DefaultUniverses,
        };
        return Protocols.Apply(protocolIds, o);
    }

    /// <summary>
    /// Plays a recording into the input hub from <paramref name="startSeconds"/> of the recording at the current engine
    /// time (null stops playback). The fixtures follow it like a console.
    /// </summary>
    public void Play(DmxRecording? recording, double startSeconds = 0)
    {
        Player?.Dispose();
        Player = recording is null ? null : new DmxPlayer(recording, Input, Time - startSeconds);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _render?.Dispose();
        _render = null;
        Player?.Dispose();
        Recorder.Stop();
        _autosave?.Dispose();
        _autosave = null;
        Protocols.Dispose();
        _scene.Changed -= OnSceneChanged;
        _project.Assets.AssetChanged -= OnAssetChanged;
        Dmx.Dispose();
        Images.Dispose();
    }

    /// <summary>
    /// Makes <paramref name="project"/> the open project: DMX, poses, simulations, undo and selection start afresh.
    /// <paramref name="recovered"/> is the crashed autosave session the project was recovered from (its files are then
    /// continued by the new autosave session and removed by the next save).
    /// </summary>
    internal void ReplaceProject(ProjectData project, RecoverableSession? recovered = null)
    {
        CloseAutosaveSession();
        _recovered = recovered;
        _scene.Changed -= OnSceneChanged;
        _project.Assets.AssetChanged -= OnAssetChanged;
        Dmx.Dispose();
        _project = project;
        var scene = project.Scene;
        _scene = scene;
        Dmx = CreateDmx(scene);
        _scene.Changed += OnSceneChanged;
        _project.Assets.AssetChanged += OnAssetChanged;
        Poses.Clear();
        Simulation.Reset();
        Undo.Clear();
        Selection.Clear();
        Models.Clear();
        Images.Clear();
        Time = 0;
        Frame = 0;
        _networkDirty = true;
        _network = null;
        _movedForNetwork.Clear();
        _cableAnchors.Invalidate();
        IsModified = false;
        SceneReplaced?.Invoke(scene);
        StartAutosaveSession();
    }

    /// <summary>Clears the messages of earlier operations (a new file is opened); library problems stay listed.</summary>
    private void ResetProgramMessages()
    {
        Issues.ClearProgramMessages();
        foreach (var issue in _libraryIssues) Issues.Report(issue);
    }

    /// <summary>
    /// Recompiles every fixture after GDTF files were added to the library: the profile cache is cleared and the DMX
    /// engine re-resolves every fixture (<see cref="DmxEngine.ReloadProfiles"/>), so a fixture that was unresolved — or
    /// resolved to a substitute — picks up the new file, and fixtures an MVR import drew with a stand-in switch to the real
    /// GDTF of their model when the library now has it. Fixtures keep their motor positions (a moving head does not
    /// travel to its position again); the poses and the renderer binding rebuild the fixtures whose profile changed.
    /// </summary>
    internal void ReloadFixtureTypes()
    {
        Profiles.Clear();
        ElectricalProfiles.Invalidate();
        ReplaceStandInsWithRealGdtfs();
        Dmx.ReloadProfiles();
        _networkDirty = true;
    }

    private DmxEngine CreateDmx(Scene scene) => new(scene, Profiles, Input)
    {
        MaxDegreeOfParallelism = Math.Max(1, Options.DmxParallelism),
        ConsoleDriven = IsConsoleDriven,
    };

    /// <summary>
    /// Console-driven status: with consoles in the scene, a fixture is driven when a data path connects it to a console
    /// output; without consoles, when every universe it is patched in is live.
    /// </summary>
    private bool IsConsoleDriven(Entity entity, FixtureComponent fixture, bool universesLive) =>
        _hasConsoles && _network is { } network ? network.IsConsoleDriven(entity.Id) : universesLive;

    /// <summary>After structural edits: whether the scene has consoles, and (with consoles) the solution.</summary>
    private void UpdateNetwork()
    {
        if (!_networkDirty) return;
        _networkDirty = false;
        _movedForNetwork.Clear();
        _cableAnchors.Invalidate();
        _hasConsoles = false;
        foreach (var _ in _scene.Query<ConsoleComponent>())
        {
            _hasConsoles = true;
            break;
        }
        _network = _hasConsoles ? Network.Solve(_scene) : null;
    }

    /// <summary>
    /// Renews the geometry of the cables that moved with the entities moved since the solution was made (anchors and
    /// cable entities, with their subtrees); a move that carries no cable leaves the solution as it is.
    /// </summary>
    private void ApplyCableMoves(NetworkSolution network)
    {
        if (_movedForNetwork.Count == 0) return;
        _cableAnchors.EnsureBuilt(_scene);
        _movedCables.Clear();
        foreach (var moved in _movedForNetwork)
            if (ReferenceEquals(moved.Scene, _scene)) _cableAnchors.CollectMoved(moved, _movedCables);
        _movedForNetwork.Clear();
        if (_movedCables.Count == 0) return;
        _network = Network.UpdateGeometry(network, _scene, _movedCables);
        _movedCables.Clear();
    }

    /// <summary>An asset was replaced or removed (import options changed …): the project has unsaved changes.</summary>
    private void OnAssetChanged(Guid assetId)
    {
        IsModified = true;
        _assetEdits++;
        Models.Invalidate(assetId);
    }

    private void OnSceneChanged(SceneChange change)
    {
        IsModified = true;
        switch (change.Kind)
        {
            case SceneChangeKind.TransformChanged or SceneChangeKind.Reparented:
                if (change.Entity is { } moved)
                {
                    Poses.MarkMoved(moved);
                    // cables anchored in the moved subtree get their geometry renewed when the solution is next read
                    if (change.Kind == SceneChangeKind.TransformChanged && !_networkDirty && _network is not null) _movedForNetwork.Add(moved);
                }
                // the scene order (which orders lines, cables and issues) may have changed: solve again
                if (change.Kind == SceneChangeKind.Reparented) _networkDirty = true;
                break;
            case SceneChangeKind.Renamed:
                // device and cable names are part of the solution (labels, messages)
                if (change.Entity is { } renamed && (renamed.Has<CableComponent>() || renamed.Has<ConsoleComponent>() ||
                                                     renamed.Has<FixtureComponent>() || renamed.Has<PowerSourceComponent>()))
                    _networkDirty = true;
                break;
            case SceneChangeKind.Added or SceneChangeKind.Removed:
                _networkDirty = true;
                if (change.Kind == SceneChangeKind.Removed && change.Entity is { } removed)
                    foreach (var e in removed.SelfAndDescendants()) Selection.Remove(e.Id);
                break;
            case SceneChangeKind.ComponentAdded or SceneChangeKind.ComponentRemoved or SceneChangeKind.ComponentChanged:
                if (change.Component is CableComponent or ConsoleComponent or FixtureComponent or PowerSourceComponent) _networkDirty = true;
                break;
        }
        Simulation.OnSceneChanged(change);
    }

    private static FixtureLibrary LoadLibrary(StageEngineOptions options, out IReadOnlyList<Issue> issues)
    {
        var found = new List<Issue>();
        issues = found;
        var folders = options.LibraryFolders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        FixtureSpecsIndex? specs = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.SpecsIndexPath))
                specs = FixtureSpecsIndex.Load(options.SpecsIndexPath);
            else
                foreach (var folder in folders)
                    if ((specs = FixtureSpecsIndex.TryLoadNear(folder)) is not null) break;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            found.Add(new Issue(IssueSeverity.Warning, IssueCategory.Program, EngineIssueCodes.LibraryMissing,
                $"The researched fixture data could not be read ({e.Message}); power, weight and candela values fall back to the GDTF files."));
        }

        var existing = folders.Where(Directory.Exists).ToList();
        if (existing.Count == 0)
        {
            found.Add(new Issue(IssueSeverity.Warning, IssueCategory.Program, EngineIssueCodes.LibraryMissing,
                folders.Count == 0
                    ? "No fixture library folder is set; only GDTF files inside projects and MVR files are available."
                    : $"The fixture library folder(s) {string.Join(", ", folders.Select(f => $"'{f}'"))} do not exist."));
        }
        try
        {
            return FixtureLibrary.Scan(existing, specs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            found.Add(new Issue(IssueSeverity.Error, IssueCategory.Program, EngineIssueCodes.LibraryMissing,
                $"The fixture library could not be scanned: {e.Message}"));
            return new FixtureLibrary(specs);
        }
    }
}
