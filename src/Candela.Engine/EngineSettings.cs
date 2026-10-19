using System.Numerics;
using Candela.Rendering;

namespace Candela.Engine;

/// <summary>
/// Engine behaviour stored with a project (the <c>engine</c> section of the project settings): what is simulated and
/// how. Plain data; changed on the main thread, read by <see cref="StageEngine.Tick"/>.
/// </summary>
public sealed class EngineSettings
{
    /// <summary>Name of the section in the project's settings file.</summary>
    public const string SectionName = "engine";

    /// <summary>Freezes smoke, fire and sound (DMX and moving lights keep running).</summary>
    public bool SimulationPaused { get; set; }

    /// <summary>Simulates haze and smoke from HAZE/FOG/FAN devices and flames.</summary>
    public bool SimulateSmoke { get; set; } = true;

    /// <summary>Simulates the flames of GENERATOR FIRE devices.</summary>
    public bool SimulateFire { get; set; } = true;

    /// <summary>Computes the sound-pressure sources of the noise overlay.</summary>
    public bool SimulateNoise { get; set; } = true;

    /// <summary>
    /// When the scene has no atmosphere volume, a smoke box around the scene is created as soon as an atmospheric device
    /// emits, so hazers work without setting up a volume first.
    /// </summary>
    public bool AutomaticAtmosphere { get; set; } = true;

    /// <summary>Cell size of the automatic smoke box, m (coarse: it covers the whole scene).</summary>
    public float AutomaticAtmosphereCellSize { get; set; } = 0.4f;

    /// <summary>Moving parts travel at their motor speed (pan, tilt, zoom, wheels…); false makes them jump.</summary>
    public bool MotorDynamics { get; set; } = true;

    /// <summary>
    /// Share of a light's specified noise (cooling fans) at zero intensity, 0…1: the sound power follows the lamp load
    /// between this share and 1 (fans of LED fixtures keep turning when dark).
    /// </summary>
    public float IdleNoiseShare { get; set; } = 0.25f;

    /// <summary>
    /// Fixtures that stand in for models the library had no GDTF of when the scene was imported (an MVR import chose a
    /// similar model of the same manufacturer). Kept with the project so the patch judges them as stand-ins and the
    /// engine switches them to the real GDTF once it is added to the library (<see cref="StageEngine.SubstitutionOf"/>).
    /// </summary>
    public List<SubstitutedFixture> Substitutes { get; set; } = new();

    /// <summary>A copy with every value made finite and in range (for settings read from a file).</summary>
    public EngineSettings Sanitized() => new()
    {
        Substitutes = (Substitutes ?? new List<SubstitutedFixture>())
            .Where(s => s is not null && s.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(s.RequestedSpec) && !string.IsNullOrWhiteSpace(s.StandIn))
            .Select(s => new SubstitutedFixture
            {
                EntityId = s.EntityId,
                RequestedSpec = s.RequestedSpec.Trim(),
                RequestedMode = s.RequestedMode,
                StandIn = s.StandIn.Trim(),
                PatchSpacing = s.PatchSpacing is > 0 and <= 512 ? s.PatchSpacing : null,
            })
            .DistinctBy(s => s.EntityId)
            .ToList(),
        SimulationPaused = SimulationPaused,
        SimulateSmoke = SimulateSmoke,
        SimulateFire = SimulateFire,
        SimulateNoise = SimulateNoise,
        AutomaticAtmosphere = AutomaticAtmosphere,
        AutomaticAtmosphereCellSize = float.IsFinite(AutomaticAtmosphereCellSize) ? Math.Clamp(AutomaticAtmosphereCellSize, 0.05f, 5f) : 0.4f,
        MotorDynamics = MotorDynamics,
        IdleNoiseShare = float.IsFinite(IdleNoiseShare) ? Math.Clamp(IdleNoiseShare, 0f, 1f) : 0.25f,
    };
}

/// <summary>A fixture drawn with a stand-in GDTF (see <see cref="EngineSettings.Substitutes"/>).</summary>
public sealed class SubstitutedFixture
{
    /// <summary>The fixture's entity.</summary>
    public Guid EntityId { get; set; }

    /// <summary>The fixture type the scene asks for (the MVR <c>GDTFSpec</c>, e.g. <c>Ayrton@MagicBlade R.gdtf</c>).</summary>
    public string RequestedSpec { get; set; } = "";

    /// <summary>The DMX mode the scene asks for, or null.</summary>
    public string? RequestedMode { get; set; }

    /// <summary>GDTF file name of the stand-in (the fixture's <c>GdtfSpec</c> while it stands in).</summary>
    public string StandIn { get; set; } = "";

    /// <summary>Channels between the start addresses of the fixtures of this type, or null when unknown.</summary>
    public int? PatchSpacing { get; set; }
}

/// <summary>
/// How the project is looked at (the <c>view</c> section of the project settings): preview quality, shading and
/// overlay, and the last camera. The editor keeps its viewports in sync with it; the CLI uses it as defaults.
/// </summary>
public sealed class ViewSettings
{
    /// <summary>Name of the section in the project's settings file.</summary>
    public const string SectionName = "view";

    /// <summary>Real-time quality (the Preview panel).</summary>
    public PreviewSettings Preview { get; set; } = new();

    /// <summary>Shading, overlay and grid of the main view.</summary>
    public ViewOptions View { get; set; } = new();

    /// <summary>Final-render settings (the Render panel).</summary>
    public FinalRenderSettings FinalRender { get; set; } = new();

    /// <summary>Camera of the main view when the project was saved, or null.</summary>
    public SavedCamera? Camera { get; set; }

    /// <summary>
    /// The work light of the editor's preview (<see cref="RenderSync.WorkLight"/>): by default it lights the set dimly
    /// while no fixture emits, so an unlit scene is not just black. Final renders do not use it unless asked.
    /// </summary>
    public WorkLightMode WorkLight { get; set; } = WorkLightMode.Auto;
}

/// <summary>A camera position stored with a project (world space, metres, degrees at this boundary).</summary>
public sealed class SavedCamera
{
    /// <summary>Eye position, m.</summary>
    public Vector3 Eye { get; set; } = new(0f, -15f, 6f);

    /// <summary>Point looked at, m.</summary>
    public Vector3 Target { get; set; } = new(0f, 0f, 2f);

    /// <summary>Vertical field of view, degrees (perspective).</summary>
    public float FovYDegrees { get; set; } = 50f;

    /// <summary>Orthographic projection instead of perspective.</summary>
    public bool Orthographic { get; set; }

    /// <summary>Visible height, m (orthographic).</summary>
    public float OrthoHeight { get; set; } = 10f;

    /// <summary>Exposure value at ISO 100.</summary>
    public float Ev100 { get; set; } = 6f;

    /// <summary>The renderer camera for this view.</summary>
    public RenderCamera ToRenderCamera()
    {
        var eye = Finite(Eye, new Vector3(0f, -15f, 6f));
        var target = Finite(Target, Vector3.Zero);
        if (Vector3.DistanceSquared(eye, target) < 1e-8f) target = eye + Vector3.UnitY;
        var camera = RenderCamera.LookAt(eye, target, float.IsFinite(FovYDegrees) ? Math.Clamp(FovYDegrees, 1f, 170f) : 50f);
        camera.Projection = Orthographic ? ProjectionKind.Orthographic : ProjectionKind.Perspective;
        camera.OrthoHeight = float.IsFinite(OrthoHeight) && OrthoHeight > 0f ? OrthoHeight : 10f;
        camera.Ev100 = float.IsFinite(Ev100) ? Ev100 : 6f;
        return camera;
    }

    private static Vector3 Finite(Vector3 v, Vector3 fallback) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : fallback;
}
