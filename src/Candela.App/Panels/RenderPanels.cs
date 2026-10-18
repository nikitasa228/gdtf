using System.Numerics;
using Candela.App.Docking;
using Candela.App.Session;
using Candela.App.Shell;
using Candela.App.Ui;
using Candela.Rendering;
using Hexa.NET.ImGui;

namespace Candela.App.Panels;

/// <summary>
/// The Render panel (requirement): final-render settings — output file, resolution, samples, bounces, format, a single
/// frame or a timecode range — and the "Render" button (also File → Render…). The path tracer arrives in a later
/// milestone: the button says so instead of failing.
/// </summary>
internal sealed unsafe class RenderPanel : Panel
{
    private readonly WorkspaceContext _ctx;
    private readonly Action<SessionResult> _report;
    private bool _range;
    private float _rangeStart;
    private float _rangeEnd = 10f;
    private string _output = "render.png";

    /// <summary>Creates the panel; <paramref name="report"/> shows the result of the Render button.</summary>
    public RenderPanel(WorkspaceContext context, Action<SessionResult> report)
        : base("Render", Icons.Clapperboard)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _output = _ctx.Tools.FinalRender.OutputPath;
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelRender);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(360, 420);

    /// <inheritdoc/>
    public override void Draw()
    {
        var s = _ctx.Tools.FinalRender;
        float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
        ImGui.PushItemWidth(-140 * scale);
        if (ImGui.InputText(Loc.Get(Str.RenderOutput), ref _output, 512))
            s.OutputPath = _output;
        int w = s.Width, h = s.Height;
        if (ImGui.InputInt(Loc.Get(Str.RenderResolution) + " W", ref w, 0))
            s.Width = Math.Clamp(w, 16, 16384);
        if (ImGui.InputInt(Loc.Get(Str.RenderResolution) + " H", ref h, 0))
            s.Height = Math.Clamp(h, 16, 16384);
        int spp = s.SamplesPerPixel;
        if (ImGui.SliderInt(Loc.Get(Str.RenderSamples), ref spp, 1, 4096, "%d", ImGuiSliderFlags.Logarithmic))
            s.SamplesPerPixel = spp;
        int bounces = s.MaxBounces;
        if (ImGui.SliderInt(Loc.Get(Str.RenderBounces), ref bounces, 1, 16))
            s.MaxBounces = bounces;
        int format = (int)s.Format;
        if (ImGui.Combo(Loc.Get(Str.RenderFormat), ref format, "PNG\0EXR\0"))
            s.Format = (ImageFileFormat)format;
        ImGui.PopItemWidth();

        ImGui.Spacing();
        if (ImGui.RadioButton(Loc.Get(Str.RenderFrame), !_range))
            _range = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(Loc.Get(Str.RenderRange), _range))
            _range = true;
        if (_range)
        {
            ImGui.PushItemWidth(-140 * scale);
            ImGui.DragFloat(Loc.Get(Str.RenderRangeStart), ref _rangeStart, 0.1f, 0f, 36000f, "%.2f s");
            ImGui.DragFloat(Loc.Get(Str.RenderRangeEnd), ref _rangeEnd, 0.1f, 0f, 36000f, "%.2f s");
            float fps = (float)s.FrameRate;
            if (ImGui.DragFloat(Loc.Get(Str.RenderFrameRate), ref fps, 0.1f, 1f, 120f, "%.2f fps"))
                s.FrameRate = fps;
            _rangeEnd = MathF.Max(_rangeEnd, _rangeStart);
            ImGui.PopItemWidth();
        }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(Theme.Accent, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Accent);
        if (ImGui.Button($"{Icons.Clapperboard}  {Loc.Get(Str.RenderStart)}", new Vector2(-1, 0)))
            _report(_ctx.Session.StartFinalRender(s));
        ImGui.PopStyleColor(2);
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMuted);
        ImGui.TextWrapped(Loc.Get(Str.RenderNotAvailable));
        ImGui.PopStyleColor();
    }
}

/// <summary>
/// The Preview panel (requirement "Превью"): real-time quality settings of all viewports — shadows, volumetric light,
/// indirect light, bloom, tone mapping, exposure and resolution scale (<see cref="PreviewSettings"/>).
/// </summary>
internal sealed class PreviewPanel : Panel
{
    private static readonly string[] ToneMappers = { "AgX", "ACES", "Neutral" };
    private static readonly string[] IndirectModes = { "Off", "Reflective shadow maps", "Ray traced" };
    private readonly WorkspaceContext _ctx;

    /// <summary>Creates the panel.</summary>
    public PreviewPanel(WorkspaceContext context)
        : base("Preview", Icons.Sparkles)
    {
        _ctx = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc/>
    public override string Title => Loc.Get(Str.PanelPreview);

    /// <inheritdoc/>
    public override Vector2 DefaultSize => new(340, 420);

    /// <inheritdoc/>
    public override void Draw()
    {
        var p = _ctx.Tools.Preview;
        float scale = ImGui.GetStyle().FontScaleMain * ImGui.GetStyle().FontScaleDpi;
        ImGui.SeparatorText(Loc.Get(Str.PreviewQuality));
        ImGui.PushItemWidth(-150 * scale);
        float resolution = p.ResolutionScale;
        if (ImGui.SliderFloat(Loc.Get(Str.PreviewResolutionScale), ref resolution, 0.5f, 1f, "%.2f"))
            p.ResolutionScale = resolution;
        bool shadows = p.Shadows;
        if (ImGui.Checkbox(Loc.Get(Str.PreviewShadows), ref shadows))
            p.Shadows = shadows;
        bool volumetrics = p.Volumetrics;
        if (ImGui.Checkbox(Loc.Get(Str.PreviewVolumetrics), ref volumetrics))
            p.Volumetrics = volumetrics;
        int indirect = (int)p.Indirect;
        if (ImGui.Combo(Loc.Get(Str.PreviewIndirect), ref indirect, IndirectModes, IndirectModes.Length))
            p.Indirect = (IndirectLighting)indirect;
        bool bloom = p.Bloom;
        if (ImGui.Checkbox(Loc.Get(Str.PreviewBloom), ref bloom))
            p.Bloom = bloom;
        int tone = (int)p.ToneMapper;
        if (ImGui.Combo(Loc.Get(Str.PreviewToneMapper), ref tone, ToneMappers, ToneMappers.Length))
            p.ToneMapper = (ToneMapper)tone;
        bool auto = p.AutoExposure;
        if (ImGui.Checkbox(Loc.Get(Str.PreviewAutoExposure), ref auto))
            p.AutoExposure = auto;
        float ev = p.ExposureCompensation;
        if (ImGui.SliderFloat(Loc.Get(Str.PreviewExposure), ref ev, -6f, 6f, "%+.1f EV"))
            p.ExposureCompensation = ev;
        ImGui.PopItemWidth();
    }
}
