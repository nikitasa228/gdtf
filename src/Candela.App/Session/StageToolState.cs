using Candela.App.Files;
using Candela.Core.Editing;
using Candela.Core.Presets;
using Candela.Core.Settings;
using Candela.Engine;
using Candela.Rendering;
using SceneModel = Candela.Core.SceneGraph.Scene;

namespace Candela.App.Session;

/// <summary>
/// What the engine-backed panels work on: the stage engine (null while the editor runs without one) and the scene,
/// selection and undo history the user edits. In the editor these come from <see cref="EditorSession"/>; tests build
/// one around a headless <see cref="StageEngine"/>.
/// </summary>
internal sealed class StagePanelContext
{
    private readonly Func<StageEngine?> _engine;
    private readonly Func<SceneModel> _scene;
    private readonly Func<Selection> _selection;
    private readonly Func<UndoStack> _undo;

    /// <summary>Creates a context from accessors (evaluated on every use: the session may replace its scene).</summary>
    public StagePanelContext(Func<StageEngine?> engine, Func<SceneModel> scene, Func<Selection> selection, Func<UndoStack> undo,
        StageToolState tools, AppSettings settings)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The panels' context in the editor: the session's engine, scene, selection and undo history.</summary>
    public static StagePanelContext From(Shell.WorkspaceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = context.Session;
        return new StagePanelContext(() => session.Engine, () => session.Scene, () => session.Selection, () => session.Undo,
            context.Tools.Stage, context.Settings.Settings)
        {
            SettingsChanged = () => context.Settings.RequestSave(context.Timer.Elapsed),
            SelectedCableNode = () => context.Tools.CableNode,
        };
    }

    /// <summary>A context around an engine and its own scene, selection and undo history (tests, tools).</summary>
    public static StagePanelContext ForEngine(StageEngine engine, StageToolState tools, AppSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return new StagePanelContext(() => engine, () => engine.Scene, () => engine.Selection, () => engine.Undo, tools,
            settings ?? new AppSettings());
    }

    /// <summary>The stage engine, or null.</summary>
    public StageEngine? Engine => _engine();

    /// <summary>The scene being edited.</summary>
    public SceneModel Scene => _scene();

    /// <summary>The selection.</summary>
    public Selection Selection => _selection();

    /// <summary>The undo history.</summary>
    public UndoStack Undo => _undo();

    /// <summary>Presets, overlay legends, external control and the render job.</summary>
    public StageToolState Tools { get; }

    /// <summary>The user settings (network interface, Art-Net offset, units).</summary>
    public AppSettings Settings { get; }

    /// <summary>Called when a user setting was changed by a panel (the editor saves the settings file).</summary>
    public Action? SettingsChanged { get; set; }

    /// <summary>The selected cable point (the viewports' node selection), or null.</summary>
    public Func<Editing.CableNodeRef?>? SelectedCableNode { get; set; }
}

/// <summary>
/// State shared by the engine-backed panels and the viewports: the preset bindings of the Preview and Render settings
/// and of the lux and noise overlay gradients (one live object each, used by every viewport), their binding to the
/// open project's <c>view</c> settings section, external control (protocols, test console, DMX recorder), the final
/// render job, and the request to show a cable's link view. Main thread only.
/// </summary>
internal sealed class StageToolState : IDisposable
{
    /// <summary>Preset folder names (under the presets directory of the config folder).</summary>
    public const string PreviewKind = "preview", FinalRenderKind = "final-render", LuxKind = "lux-gradient", NoiseKind = "noise-gradient";

    private ViewSettings? _boundView;
    private bool _externalLoaded;
    private bool _disposed;

    /// <summary>Creates the state around the editor's live settings objects; presets live in <paramref name="store"/> (default: the config folder).</summary>
    public StageToolState(PreviewSettings preview, FinalRenderSettings finalRender, IPresetStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(finalRender);
        store ??= new FilePresetStore(ConfigPaths.PresetsDir);
        Preview = new PresetBinding<PreviewSettings>(PreviewKind, preview, static () => new PreviewSettings(), SettingsCopy.Copy,
            SettingsCopy.Sanitized, store);
        FinalRender = new PresetBinding<FinalRenderSettings>(FinalRenderKind, finalRender, static () => new FinalRenderSettings(),
            SettingsCopy.Copy, SettingsCopy.Sanitized, store);
        Lux = new PresetBinding<ColorGradient>(LuxKind, ColorGradient.DefaultLux(), ColorGradient.DefaultLux, SettingsCopy.Copy,
            static g => SettingsCopy.Sanitized(g, ColorGradient.DefaultLux), store);
        Noise = new PresetBinding<ColorGradient>(NoiseKind, ColorGradient.DefaultNoise(), ColorGradient.DefaultNoise, SettingsCopy.Copy,
            static g => SettingsCopy.Sanitized(g, ColorGradient.DefaultNoise), store);
        External = new ExternalControl();
        Render = new RenderJobRunner();
    }

    /// <summary>Real-time quality of every viewport (the Preview panel).</summary>
    public PresetBinding<PreviewSettings> Preview { get; }

    /// <summary>Final-render settings (the Render panel).</summary>
    public PresetBinding<FinalRenderSettings> FinalRender { get; }

    /// <summary>The lux overlay gradient every viewport uses.</summary>
    public PresetBinding<ColorGradient> Lux { get; }

    /// <summary>The noise overlay gradient every viewport uses.</summary>
    public PresetBinding<ColorGradient> Noise { get; }

    /// <summary>Protocols, test console and DMX recorder of the external-control panel.</summary>
    public ExternalControl External { get; }

    /// <summary>The final render job (Render panel, File → Render).</summary>
    public RenderJobRunner Render { get; }

    /// <summary>The editor's file dialog (set by the workspace; null in tests).</summary>
    public FileBrowserDialog? Browser { get; set; }

    /// <summary>The viewport whose camera a render uses: the active one, recorded while viewports are drawn.</summary>
    public Viewport.ViewportPanel? ActiveViewport { get; set; }

    /// <summary>
    /// Settings applied to every viewport once when they change (the Preview panel's EV100 and grid switches): the
    /// viewports compare <see cref="ViewportApplyVersion"/> with the version they applied last.
    /// </summary>
    public int ViewportApplyVersion { get; private set; }

    /// <summary>Camera exposure (EV100) to apply to every viewport, or null to leave each viewport's own value.</summary>
    public float? ViewportEv100 { get; private set; }

    /// <summary>Grid switch to apply to every viewport, or null to leave each viewport's own switch.</summary>
    public bool? ViewportGrid { get; private set; }

    /// <summary>Raised when the user asks to see how a cable links its devices (properties panel → connected cable).</summary>
    public event Action<Guid>? CableLinkRequested;

    /// <summary>Asks for the cable link view of <paramref name="cableEntityId"/>; false when nothing shows it yet.</summary>
    public bool RequestCableLink(Guid cableEntityId)
    {
        var handler = CableLinkRequested;
        if (handler is null)
            return false;
        handler(cableEntityId);
        return true;
    }

    /// <summary>Applies an exposure to every viewport camera.</summary>
    public void ApplyEv100ToViewports(float ev100)
    {
        if (!float.IsFinite(ev100))
            return;
        ViewportEv100 = Math.Clamp(ev100, -6f, 24f);
        ViewportApplyVersion++;
    }

    /// <summary>Turns the grid of every viewport on or off.</summary>
    public void ApplyGridToViewports(bool on)
    {
        ViewportGrid = on;
        ViewportApplyVersion++;
    }

    /// <summary>
    /// Connects the live settings to the open project's <c>view</c> section: when another project was opened (the
    /// engine's <see cref="ViewSettings"/> object changed), its stored preview, render and overlay settings become the live
    /// values; afterwards the section refers to the live objects, so saving the project stores what the panels show.
    /// Cheap enough for every frame.
    /// </summary>
    public void BindProject(StageEngine? engine)
    {
        if (engine is null)
        {
            _boundView = null;
            return;
        }

        var view = engine.View;
        if (view is null)
            return;
        if (!ReferenceEquals(view, _boundView))
        {
            _boundView = view;
            Preview.Adopt(view.Preview);
            FinalRender.Adopt(view.FinalRender);
            view.View ??= new ViewOptions();
            Lux.Adopt(view.View.LuxGradient);
            Noise.Adopt(view.View.NoiseGradient);
        }

        if (!ReferenceEquals(view.Preview, Preview.Live))
            view.Preview = Preview.Live;
        if (!ReferenceEquals(view.FinalRender, FinalRender.Live))
            view.FinalRender = FinalRender.Live;
        view.View ??= new ViewOptions();
        if (!ReferenceEquals(view.View.LuxGradient, Lux.Live))
            view.View.LuxGradient = Lux.Live;
        if (!ReferenceEquals(view.View.NoiseGradient, Noise.Live))
            view.View.NoiseGradient = Noise.Live;
    }

    /// <summary>
    /// Per-frame work outside the UI and GPU frames (the editor calls it before building the UI): project binding,
    /// external control (reading its settings file once, protocol binding, status polling) and one step of a running
    /// render job.
    /// </summary>
    /// <param name="engine">The stage engine, or null.</param>
    /// <param name="renderer">The renderer of the viewports (render jobs), or null.</param>
    /// <param name="settings">User settings (network interface, Art-Net offset).</param>
    /// <param name="readOnly">True in headless runs: no settings files are written.</param>
    /// <param name="now">Seconds since start.</param>
    /// <param name="deferDispose">Releases GPU objects after the frames that use them (null: at once).</param>
    public void Update(StageEngine? engine, IRenderer? renderer, AppSettings settings, bool readOnly, double now,
        Action<IDisposable>? deferDispose = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (!_externalLoaded)
        {
            _externalLoaded = true;
            External.Load(ConfigPaths.ConfigDir);
        }

        External.CanSave = !readOnly;
        BindProject(engine);
        External.Bind(engine, settings);
        External.Update(engine, now);
        Render.DisposeTarget = deferDispose;
        if (renderer is not null)
            Render.Step(engine, renderer, now);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Render.Dispose();
        External.Dispose();
    }
}
